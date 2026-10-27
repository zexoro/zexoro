#!/usr/bin/env bash
# End-to-end test of zexoro's portable Linux tar.gz (docs/design/0006-desktop-formats.md): extract
# with the system's tar, run, and automatic updates with zexoro-updater (strategy replace from the
# archive) against a local feed server, plus the verification failures and a directory that is not
# writable. The Linux counterpart of tests/e2e/windows/Run-ZipE2E.ps1.
#
#   tests/e2e/unix/run-tar-e2e.sh <directory written by tests/e2e/pack.sh>
#
# Consumes linux/ZexoroE2ETar-{1.0.0,2.0.0}-linux-<arch>.tar.gz of the machine's architecture
# (x64 or arm64), whose [updates] point at http://localhost:8080/tar/stable/, the feeds pack.sh
# signed (feeds/tar/seq1, seq2 and other-key) and linux/no-updates/ZexoroE2ETar-1.0.0-linux-<arch>.tar.gz.
# Needs bash, GNU tar, python3 (the feed server and JSON), the .NET runtime (the fixture app is
# framework-dependent) and a free port 8080.
#
# Shared update scenario (0006 section 5): extract 1.0.0; check against seq1 (up to date); a
# tampered feed, a feed signed with an untrusted key, seq1 replayed after seq2 and a download whose
# bytes do not match the feed each exit 4 with nothing changed; then download and apply 2.0.0
# while the application runs (apply waits for it, then restarts it); check again (up to date).
# Finally a copy in a directory the user may not write to: apply exits 7 and changes nothing.
#
# Each check prints PASS or FAIL; the script exits 1 when any check failed. It removes everything
# it extracted, also after a failure.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <directory written by tests/e2e/pack.sh>" >&2
  exit 2
fi

packages="$(cd "$1" && pwd)"
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 2 ;;
esac

app_id=dev.zexoro.e2e.tar
folder=ZexoroE2ETar
v1="$packages/linux/$folder-1.0.0-linux-$arch.tar.gz"
v2="$packages/linux/$folder-2.0.0-linux-$arch.tar.gz"
plain="$packages/linux/no-updates/$folder-1.0.0-linux-$arch.tar.gz"
for f in "$v1" "$v2" "$v2.release.json" "$plain" "$packages"/feeds/tar/{seq1,seq2,other-key}/feed.json; do
  [ -f "$f" ] || { echo "$f not found; pack with the current tests/e2e/pack.sh" >&2; exit 2; }
done
update_key="$(tr -d '\r\n' < "$packages/zexoro-update.pub")"

work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-tar-e2e.XXXXXX")"
www="$work/www"
served="$www/tar/stable"
# Isolated per-user directories: the updater keeps its state in $XDG_STATE_HOME, the fixture app
# writes its marker to $XDG_DATA_HOME (.NET's LocalApplicationData), copies go to $TMPDIR.
export XDG_STATE_HOME="$work/state" XDG_DATA_HOME="$work/data" TMPDIR="$work/tmp"
state="$XDG_STATE_HOME/zexoro/updater/$app_id"
marker="$XDG_DATA_HOME/ZexoroE2E/ran.txt"
mkdir -p "$served" "$XDG_STATE_HOME" "$XDG_DATA_HOME" "$TMPDIR"
server=
pids=()
cleanup() {
  for pid in ${pids[@]+"${pids[@]}"} $server; do kill "$pid" 2>/dev/null || true; done
  chmod -R u+w "$work" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

passed=0
failed=0
pass() { passed=$((passed + 1)); echo "PASS  $*"; }
fail() { failed=$((failed + 1)); echo "FAIL  $*"; }
check() { # <message> <command>...: passes when the command succeeds
  local message=$1
  shift
  if "$@"; then pass "$message"; else fail "$message"; fi
}
equal() { # <actual> <expected> <message>
  if [ "$1" = "$2" ]; then pass "$3"; else fail "$3 (got '$1', expected '$2')"; fi
}

# json <file> <field>: a top-level field of the last JSON object line of <file> (arrays joined by ',').
json() {
  python3 -I - "$1" "$2" <<'PY'
import json, sys
lines = [l for l in open(sys.argv[1], encoding="utf-8").read().splitlines() if l.strip().startswith("{")]
text = lines[-1] if lines else open(sys.argv[1], encoding="utf-8").read()
try:
    value = json.loads(text).get(sys.argv[2])
except ValueError:
    value = None
print("" if value is None else ",".join(value) if isinstance(value, list) else value)
PY
}

flip_byte() {
  python3 -I - "$1" <<'PY'
import sys
path = sys.argv[1]
data = bytearray(open(path, "rb").read())
data[len(data) // 2] ^= 0x01
open(path, "wb").write(bytes(data))
PY
}

mode() { stat -c %a "$1"; }

wait_for() { # <seconds> <command>...: polls until the command succeeds
  local deadline=$((SECONDS + $1))
  shift
  until "$@"; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.2
  done
}

# extract <archive> <parent>: extracts as a user would, after checking the single top-level directory.
extract() {
  local tops
  tops="$(tar -tzf "$1" | cut -d/ -f1 | sort -u | tr '\n' ' ')"
  equal "$tops" "$folder " "$(basename "$1") has the single top-level directory $folder/"
  mkdir -p "$2"
  tar -xzf "$1" -C "$2"
}

# listing <archive>: tar's own view (an independent reader): owner 0/0, the fixed date, the modes.
listing() {
  local archive=$1 list
  list="$(tar -tzvf "$archive" --numeric-owner --utc)"
  echo "$list" | sed 's/^/      /'
  check "$(basename "$archive"): every entry is owned by 0/0" bash -c '! grep -v " 0/0 " <<< "$1" | grep -q .' _ "$list"
  check "$(basename "$archive"): every entry is dated 1980-02-01 00:00" bash -c '! grep -v " 1980-02-01 00:00 " <<< "$1" | grep -q .' _ "$list"
  check "$(basename "$archive"): app/FixtureApp is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* $folder/app/FixtureApp\$" <<< "$list"
  check "$(basename "$archive"): app/FixtureApp.dll is -rw-r--r--" grep -Eq "^-rw-r--r-- .* $folder/app/FixtureApp.dll\$" <<< "$list"
  check "$(basename "$archive"): app/bin/helper.sh (#!) is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* $folder/app/bin/helper.sh\$" <<< "$list"
}

# run_app <root>: runs the extracted application and checks it ran from <root>/app/.
run_app() {
  rm -rf "$XDG_DATA_HOME/ZexoroE2E"
  local status=0
  "$1/app/FixtureApp" || status=$?
  equal "$status" 0 "the extracted app exits with 0"
  equal "$(cat "$marker" 2>/dev/null)" "$1/app/" "the extracted app ran (marker written)"
}

# test_extracted <root> <version> <with updates: yes|no>: layout, modes and install record.
test_extracted() {
  local root=$1 version=$2 updates=$3 expected record="$1/zexoro-install.json"
  for file in FixtureApp FixtureApp.dll FixtureApp.runtimeconfig.json FixtureApp.deps.json version.txt bin/helper.sh; do
    check "app/$file exists" test -e "$root/app/$file"
  done
  equal "$(cat "$root/app/version.txt")" "$version" "app/version.txt is $version"
  expected="app icon.png zexoro-install.json"
  [ "$updates" = yes ] && expected="$expected zexoro-updater"
  equal "$(cd "$root" && ls -A | sort | tr '\n' ' ' | sed 's/ $//')" "$expected" "the root holds app/, icon.png, the record and (with [updates]) the updater, nothing else"
  equal "$(mode "$root/app/FixtureApp")" 755 "app/FixtureApp has mode 755"
  equal "$(mode "$root/app/bin/helper.sh")" 755 "app/bin/helper.sh has mode 755"
  equal "$(mode "$root/app/FixtureApp.dll")" 644 "app/FixtureApp.dll has mode 644"
  check "icon.png is a PNG file" bash -c 'head -c 8 "$1" | od -An -tx1 | tr -d " \n" | grep -qx 89504e470d0a1a0a' _ "$root/icon.png"
  if [ -f "$packages/linux/links" ]; then
    check "the symbolic link app/bin/version.txt -> ../version.txt is kept" test "$(readlink "$root/app/bin/version.txt")" = ../version.txt
    equal "$(cat "$root/app/bin/version.txt")" "$version" "app/bin/version.txt reads through the link"
  fi
  for field in schema:1 app:$app_id version:$version os:linux arch:$arch format:tar-gz scope:user strategy:replace; do
    equal "$(json "$record" "${field%%:*}")" "${field#*:}" "zexoro-install.json ${field%%:*}"
  done
  if [ "$updates" = yes ]; then
    equal "$(mode "$root/zexoro-updater")" 755 "zexoro-updater has mode 755"
    equal "$(json "$record" channel)" stable "zexoro-install.json channel"
    equal "$(json "$record" feed)" "http://localhost:8080/tar/stable/feed.json" "zexoro-install.json feed"
    equal "$(json "$record" keys)" "$update_key" "zexoro-install.json keys"
    equal "$(json "$record" main)" FixtureApp "zexoro-install.json main"
  else
    equal "$(json "$record" feed)" "" "zexoro-install.json has no feed without [updates]"
  fi
  run_app "$root"
}

# test_version <root> <version> <context>: the files and the record tell <version>, nothing is staged.
test_version() {
  equal "$(cat "$1/app/version.txt")" "$2" "$3: app/version.txt is $2"
  equal "$(json "$1/zexoro-install.json" version)" "$2" "$3: zexoro-install.json version is $2"
  equal "$(cd "$1" && ls -A | grep -E '\.(new|old)$' | tr '\n' ' ')" "" "$3: nothing staged or old in the root"
}

publish_feed() { cp "$packages/feeds/tar/$1/feed.json" "$packages/feeds/tar/$1/feed.json.sig" "$served/"; }
publish_packages() { cp "$packages"/linux/$folder-*-linux-*.tar.gz "$served/"; }
downloads() { if [ -d "$state/downloads" ]; then (cd "$state/downloads" && ls -A | sort | tr '\n' ' ' | sed 's/ $//'); fi; }

# updater <root> <command> <exit> <status> <message> [<error phrase>]: runs the installed updater.
updater() {
  local root=$1 command=$2 exit=$3 status=$4 message=$5 phrase=${6:-} code=0
  (cd "$work" && "$root/zexoro-updater" "$command" --json) > "$work/out.json" 2> "$work/err.txt" || code=$?
  echo "      zexoro-updater $command --json -> $code: $(cat "$work/out.json" "$work/err.txt" | tr '\n' ' ')"
  equal "$code" "$exit" "$command exits with $exit: $message"
  equal "$(json "$work/out.json" status)" "$status" "$command reports status '$status'"
  if [ -n "$phrase" ]; then
    check "$command error names the cause ('$phrase')" grep -qi -- "$phrase" <<< "$(json "$work/out.json" error)"
  fi
}

python3 -I -m http.server 8080 --directory "$www" > "$work/server.log" 2>&1 &
server=$!
wait_for 15 curl -fsS -o /dev/null "http://localhost:8080/" || { echo "the feed server did not start" >&2; cat "$work/server.log" >&2; exit 1; }
echo "Serving $www at http://localhost:8080/ (linux-$arch)"

echo
echo "== tar.gz without [updates]"
check "no release fragment without [updates]" test ! -e "$plain.release.json"
listing "$plain"
extract "$plain" "$work/plain"
test_extracted "$work/plain/$folder" 1.0.0 no

echo
root="$work/my apps/$folder"
echo "== $folder (tar.gz, strategy replace, $root)"
equal "$(python3 -I -c 'import json,sys; a=json.load(open(sys.argv[1]))["release"]["artifacts"][0]; print("/".join([a["os"],a["arch"],a["format"],a["scope"]]))' "$v2.release.json")" \
  "linux/$arch/tar-gz/user" "the release fragment describes a linux/$arch/tar-gz/user artifact"
listing "$v1"

echo "-- extract 1.0.0"
extract "$v1" "$(dirname "$root")"
test_extracted "$root" 1.0.0 yes
check "app/obsolete.txt exists in 1.0.0" test -f "$root/app/obsolete.txt"
publish_packages

echo "-- a valid feed without a newer version"
publish_feed seq1
updater "$root" check 1 up-to-date "the feed with sequence 1 has only 1.0.0"

echo "-- verification failures (exit 4)"
publish_feed seq2
flip_byte "$served/feed.json"
updater "$root" check 4 error "a feed byte was flipped" signature
publish_feed other-key
updater "$root" check 4 error "the feed is signed with an untrusted key" signature

echo "-- the feed with 2.0.0"
publish_feed seq2
updater "$root" check 0 available "the feed with sequence 2 has 2.0.0"
equal "$(json "$work/out.json" installed)" 1.0.0 "check reports installed 1.0.0"
equal "$(json "$work/out.json" version)" 2.0.0 "check reports version 2.0.0"
equal "$(json "$work/out.json" size)" "$(wc -c < "$v2" | tr -d ' ')" "check reports the size of the 2.0.0 archive"

echo "-- replay of the older feed (exit 4)"
publish_feed seq1
updater "$root" check 4 error "the validly signed feed with sequence 1 is served after sequence 2" sequence

echo "-- an artifact that does not match its sha256 (exit 4)"
publish_feed seq2
flip_byte "$served/$(basename "$v2")"
updater "$root" download 4 error "the served 2.0.0 archive has one byte changed" SHA-256
equal "$(downloads)" "" "nothing was kept in the downloads"
updater "$root" apply 1 up-to-date "nothing is downloaded, so there is nothing to apply"
test_version "$root" 1.0.0 "after the verification failures"
check "after the verification failures: app/obsolete.txt is still there" test -f "$root/app/obsolete.txt"

echo "-- download 2.0.0"
publish_packages
: > "$work/server.log"
updater "$root" download 0 downloaded "the 2.0.0 archive matches the feed"
equal "$(json "$work/out.json" version)" 2.0.0 "download reports version 2.0.0"
check "the updater fetched /tar/stable/$(basename "$v2")" grep -q "GET /tar/stable/$(basename "$v2") " "$work/server.log"
equal "$(downloads)" "$(basename "$v2") staged.json" "the verified archive is staged in the downloads"

echo "-- apply while the application runs, then restart it"
rm -rf "$XDG_DATA_HOME/ZexoroE2E"
"$root/app/FixtureApp" --wait &
app_pid=$!
pids+=("$app_pid")
wait_for 60 test -f "$marker" || fail "the application started (with --wait)"
rm -f "$marker" "$state/last-apply.json"
(cd "$work" && exec "$root/zexoro-updater" apply --wait-pid "$app_pid" --restart --json) > "$work/apply.out" 2> "$work/apply.err" &
apply_pid=$!
pids+=("$apply_pid")
sleep 3
check "apply waits while the application runs" kill -0 "$apply_pid"
equal "$(cat "$root/app/version.txt")" 1.0.0 "nothing changed while the application runs"
kill "$app_pid"
# Reap it, or the zombie would still count as running for apply's kill(pid, 0).
wait "$app_pid" 2>/dev/null || true
code=0
wait "$apply_pid" || code=$?
echo "      apply stdout: $(cat "$work/apply.out")"
echo "      apply stderr: $(cat "$work/apply.err" | tr '\n' ' ')"
equal "$code" 0 "apply exits with 0"
check "the update finished (last-apply.json written)" test -f "$state/last-apply.json"
equal "$(json "$state/last-apply.json" status)" applied "last-apply.json status is applied"
equal "$(json "$state/last-apply.json" installed)" 1.0.0 "last-apply.json installed is 1.0.0"
equal "$(json "$state/last-apply.json" version)" 2.0.0 "last-apply.json version is 2.0.0"
check "the application was restarted" wait_for 60 test -f "$marker"
equal "$(cat "$marker" 2>/dev/null)" "$root/app/" "the restarted application runs from the extracted directory"

echo "-- 2.0.0 is in place"
test_version "$root" 2.0.0 updated
check "app/obsolete.txt is gone" test ! -e "$root/app/obsolete.txt"
equal "$(json "$root/zexoro-install.json" format)/$(json "$root/zexoro-install.json" scope)/$(json "$root/zexoro-install.json" strategy)" \
  tar-gz/user/replace "zexoro-install.json is still tar-gz/user/replace"
equal "$(mode "$root/app/FixtureApp")" 755 "app/FixtureApp kept mode 755"
equal "$(mode "$root/app/bin/helper.sh")" 755 "app/bin/helper.sh kept mode 755"
equal "$(mode "$root/app/FixtureApp.dll")" 644 "app/FixtureApp.dll kept mode 644"
equal "$(mode "$root/zexoro-updater")" 755 "zexoro-updater kept mode 755"
if [ -f "$packages/linux/links" ]; then
  check "the symbolic link app/bin/version.txt was recreated" test "$(readlink "$root/app/bin/version.txt")" = ../version.txt
  equal "$(cat "$root/app/bin/version.txt")" 2.0.0 "app/bin/version.txt reads 2.0.0 through the link"
fi
equal "$(downloads)" "" "the downloads were cleared"
equal "$(ls -A "$TMPDIR")" "" "no updater copy is left in \$TMPDIR"

echo "-- up to date afterwards"
updater "$root" check 1 up-to-date "2.0.0 is the newest release"
equal "$(json "$work/out.json" installed)" 2.0.0 "check reports installed 2.0.0"

echo "-- a 1.0.0 copy in a directory the user may not write to (exit 7)"
rm -rf "$root"
extract "$v1" "$(dirname "$root")"
updater "$root" download 0 downloaded "2.0.0 is downloaded for the read-only copy"
chmod a-w "$root"
updater "$root" apply 7 error "the directory is not writable" "not writable"
test_version "$root" 1.0.0 "the read-only copy"
check "the read-only copy: app/obsolete.txt is still there" test -f "$root/app/obsolete.txt"
chmod u+w "$root"
echo "-- the same copy once it is writable again"
updater "$root" apply 0 applied "the kept download is applied"
test_version "$root" 2.0.0 "after making the directory writable"

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ]

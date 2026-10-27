#!/usr/bin/env bash
# End-to-end test of zexoro's deb packages (docs/design/0006-desktop-formats.md): independent
# checks with dpkg-deb (and lintian when installed), install with apt, run through /usr/bin, the
# notify update scenario against a local feed server, upgrade with dpkg, remove and purge.
#
#   tests/e2e/unix/run-deb-e2e.sh <directory written by tests/e2e/pack.sh>
#
# Consumes deb/zexoro-e2e_{1.0.0,2.0.0}_<arch>.deb of the machine's architecture (amd64 or arm64),
# whose [updates] point at http://localhost:8080/deb/stable/, the feeds pack.sh signed
# (feeds/deb/seq1, seq2 and other-key) and deb/no-updates/zexoro-e2e_1.0.0_<arch>.deb.
# Runs as root or with password-less sudo (a CI runner, a container); installs and finally purges
# the package zexoro-e2e. Needs dpkg, apt-get, python3 (the feed server and JSON), curl, GNU find,
# the .NET runtime (the fixture app is framework-dependent; DOTNET_ROOT when it is not in a default
# location) and a free port 8080. lintian is optional: its findings are printed, and errors other
# than the accepted ones below fail the test.
#
# Notify scenario (0006 section 5): with 1.0.0 installed, check against seq1 (up to date); a
# tampered feed, a feed signed with an untrusted key and seq1 replayed after seq2 exit 4; check
# against seq2 reports 2.0.0 and the package's url; download and apply exit 7, fetch nothing and
# change nothing (files, modes, dpkg --verify, the updater's state). The upgrade is then done with
# dpkg, as a user would.
#
# Each check prints PASS or FAIL; the script exits 1 when any check failed.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <directory written by tests/e2e/pack.sh>" >&2
  exit 2
fi

packages="$(cd "$1" && pwd)"
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 debarch=amd64 ;;
  aarch64 | arm64) arch=arm64 debarch=arm64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 2 ;;
esac

pkg=zexoro-e2e
app_id=dev.zexoro.e2e.deb
root=/opt/$pkg
v1="$packages/deb/${pkg}_1.0.0_$debarch.deb"
v2="$packages/deb/${pkg}_2.0.0_$debarch.deb"
plain="$packages/deb/no-updates/${pkg}_1.0.0_$debarch.deb"
for f in "$v1" "$v2" "$v2.release.json" "$plain" "$packages"/feeds/deb/{seq1,seq2,other-key}/feed.json; do
  [ -f "$f" ] || { echo "$f not found; pack with the current tests/e2e/pack.sh" >&2; exit 2; }
done

if [ "$(id -u)" -eq 0 ]; then as_root() { "$@"; }; else as_root() { sudo "$@"; }; fi
export DEBIAN_FRONTEND=noninteractive

work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-deb-e2e.XXXXXX")"
chmod 755 "$work"
www="$work/www"
served="$www/deb/stable"
export XDG_STATE_HOME="$work/state" XDG_DATA_HOME="$work/data" TMPDIR="$work/tmp"
state="$XDG_STATE_HOME/zexoro/updater/$app_id"
marker="$XDG_DATA_HOME/ZexoroE2E/ran.txt"
mkdir -p "$served" "$XDG_STATE_HOME" "$XDG_DATA_HOME" "$TMPDIR"
server=
installed() { dpkg-query -W -f='${db:Status-Status}' "$pkg" 2>/dev/null || true; }
cleanup() {
  [ -n "$server" ] && kill "$server" 2>/dev/null || true
  if [ -n "$(installed)" ] && [ "$(installed)" != not-installed ]; then as_root dpkg --purge "$pkg" > /dev/null || true; fi
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

# json <file> <field>: a top-level field of the JSON file <file>, or of its last line that is a JSON
# object (updater output); arrays are joined by ','.
json() {
  python3 -I - "$1" "$2" <<'PY'
import json, sys
text = open(sys.argv[1], encoding="utf-8").read()
try:
    value = json.loads(text).get(sys.argv[2])
except ValueError:
    lines = [l for l in text.splitlines() if l.strip().startswith("{")]
    try:
        value = json.loads(lines[-1]).get(sys.argv[2]) if lines else None
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

wait_for() { # <seconds> <command>...: polls until the command succeeds
  local deadline=$((SECONDS + $1))
  shift
  until "$@"; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.2
  done
}

field() { dpkg-deb --field "$1" "$2"; }

# inspect <deb> <version> <with updates: yes|no>: dpkg-deb's own view of the package.
inspect() {
  local deb=$1 version=$2 updates=$3 name contents
  name="$(basename "$deb")"
  dpkg-deb --info "$deb" | sed 's/^/      /'
  equal "$(field "$deb" Package)" "$pkg" "$name: Package"
  equal "$(field "$deb" Version)" "$version" "$name: Version"
  equal "$(field "$deb" Architecture)" "$debarch" "$name: Architecture"
  equal "$(field "$deb" Maintainer)" "zexoro e2e <e2e@zexoro.invalid>" "$name: Maintainer ([linux] maintainer)"
  equal "$(field "$deb" Homepage)" "https://github.com/zexoro/zexoro" "$name: Homepage (app.url)"
  check "$name: Installed-Size is a number of KiB" grep -Eqx '[0-9]+' <<< "$(field "$deb" Installed-Size)"
  check "$name: the description has a synopsis and an extended part" grep -q 'End-to-end' <<< "$(field "$deb" Description)"
  check "$name: no maintainer scripts" bash -c '! dpkg-deb --info "$1" | grep -Eq " (pre|post)(inst|rm) "' _ "$deb"
  if [ "$updates" = yes ]; then
    equal "$(field "$deb" Depends)" "libc6 (>= 2.28), libssl3t64 | libssl3 | libssl1.1, coreutils (>= 8.0)" "$name: Depends (the updater's, then [linux.deb] depends)"
    equal "$(field "$deb" Recommends)" ca-certificates "$name: Recommends"
  else
    equal "$(field "$deb" Depends)" "coreutils (>= 8.0)" "$name: Depends ([linux.deb] depends only)"
  fi
  contents="$(dpkg-deb --contents "$deb")"
  echo "$contents" | sed 's/^/      /'
  check "$name: every entry is owned by root/root" bash -c '! grep -v " root/root " <<< "$1" | grep -q .' _ "$contents"
  check "$name: /usr/bin/$pkg links to the main executable" grep -Eq "^lrwxrwxrwx .* \./usr/bin/$pkg -> $root/app/FixtureApp\$" <<< "$contents"
  check "$name: app/FixtureApp is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* \.$root/app/FixtureApp\$" <<< "$contents"
  check "$name: app/FixtureApp.dll is -rw-r--r--" grep -Eq "^-rw-r--r-- .* \.$root/app/FixtureApp.dll\$" <<< "$contents"
  check "$name: the .desktop file is in /usr/share/applications" grep -Eq "^-rw-r--r-- .* \./usr/share/applications/$app_id.desktop\$" <<< "$contents"
  check "$name: the icon is in the hicolor theme" grep -Eq "^-rw-r--r-- .* \./usr/share/icons/hicolor/[0-9]+x[0-9]+/apps/$app_id.png\$" <<< "$contents"
  if [ "$updates" = yes ]; then
    check "$name: zexoro-updater is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* \.$root/zexoro-updater\$" <<< "$contents"
  else
    check "$name: no zexoro-updater without [updates]" bash -c '! grep -q "zexoro-updater" <<< "$1"' _ "$contents"
  fi
}

# Lintian errors accepted for zexoro's packages (docs/design/0006-desktop-formats.md, "As implemented
# (step 6)"): third-party packages belong in /opt; zexoro knows neither the application's license
# nor its changes, so it writes no copyright file and no changelog; the Native AOT updater links
# the .NET runtime's own zlib statically.
accepted_lintian='^(dir-or-file-in-opt|no-copyright-file|no-changelog|debian-changelog-file-missing|embedded-library)$'

run_lintian() {
  local deb=$1 out errors
  if ! command -v lintian > /dev/null; then
    echo "      lintian is not installed; skipped"
    return
  fi
  out="$(lintian --tag-display-limit 0 "$deb" 2>&1 || true)"
  echo "$out" | sed 's/^/      lintian: /'
  errors="$(grep -E '^E: ' <<< "$out" | awk '{print $3}' | sort -u | grep -Ev "$accepted_lintian" || true)"
  equal "$errors" "" "lintian $(basename "$deb"): no errors besides the accepted ones"
}

# snapshot: everything the package installed, with modes, sizes, times and contents.
snapshot() {
  {
    find "$root" /usr/bin/$pkg /usr/share/applications/$app_id.desktop -printf '%p %m %u %s %T@ %l\n'
    find "$root" -type f -exec md5sum {} +
  } | sort
}

run_app() { # <context>: runs the installed application through /usr/bin and checks it ran from /opt
  rm -rf "$XDG_DATA_HOME/ZexoroE2E"
  local status=0
  "$pkg" || status=$?
  equal "$status" 0 "$1: $pkg (on PATH) exits with 0"
  equal "$(cat "$marker" 2>/dev/null)" "$root/app/" "$1: the application ran from $root/app/"
}

test_installed() { # <version>: the installed package and its files
  local version=$1 record=$root/zexoro-install.json
  equal "$(installed)" installed "dpkg reports $pkg as installed"
  equal "$(dpkg-query -W -f='${Version}' "$pkg")" "$version" "dpkg reports version $version"
  equal "$(command -v "$pkg")" "/usr/bin/$pkg" "$pkg is found on PATH in /usr/bin"
  equal "$(readlink /usr/bin/$pkg)" "$root/app/FixtureApp" "/usr/bin/$pkg links to $root/app/FixtureApp"
  equal "$(cd "$root" && ls -A | sort | tr '\n' ' ' | sed 's/ $//')" "app zexoro-install.json zexoro-updater" "$root holds app/, the record and the updater, nothing else"
  equal "$(stat -c '%U %a' "$root/zexoro-updater")" "root 755" "zexoro-updater is owned by root with mode 755"
  equal "$(stat -c '%U %a' "$record")" "root 644" "zexoro-install.json is owned by root with mode 644"
  equal "$(cat "$root/app/version.txt")" "$version" "app/version.txt is $version"
  for field in schema:1 app:$app_id version:$version os:linux arch:$arch format:deb scope:machine strategy:notify channel:stable main:FixtureApp; do
    equal "$(json "$record" "${field%%:*}")" "${field#*:}" "zexoro-install.json ${field%%:*}"
  done
  equal "$(json "$record" feed)" "http://localhost:8080/deb/stable/feed.json" "zexoro-install.json feed"
  check "/usr/share/applications/$app_id.desktop runs /usr/bin/$pkg" grep -qx "Exec=/usr/bin/$pkg" /usr/share/applications/$app_id.desktop
  check "the icon is installed" bash -c 'ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png > /dev/null'
  check "dpkg --verify finds no changed file" bash -c '[ -z "$(dpkg --verify "$1")" ]' _ "$pkg"
  run_app "$version"
}

test_gone() { # <context>: nothing of the package is left
  check "$1: $root is gone" test ! -e "$root"
  check "$1: /usr/bin/$pkg is gone" test ! -e /usr/bin/$pkg -a ! -L /usr/bin/$pkg
  check "$1: the .desktop file is gone" test ! -e /usr/share/applications/$app_id.desktop
  check "$1: the icon is gone" bash -c '! ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png 2> /dev/null'
}

publish_feed() { cp "$packages/feeds/deb/$1/feed.json" "$packages/feeds/deb/$1/feed.json.sig" "$served/"; }

# updater <command> <exit> <status> <message> [<error phrase>] [<more arguments>...]: runs the installed updater.
updater() {
  local command=$1 exit=$2 status=$3 message=$4 phrase=${5:-} code=0
  shift 5 2> /dev/null || shift $#
  (cd "$work" && "$root/zexoro-updater" "$command" --json "$@") > "$work/out.json" 2> "$work/err.txt" || code=$?
  echo "      zexoro-updater $command --json $* -> $code: $(cat "$work/out.json" "$work/err.txt" | tr '\n' ' ')"
  equal "$code" "$exit" "$command exits with $exit: $message"
  equal "$(json "$work/out.json" status)" "$status" "$command reports status '$status'"
  if [ -n "$phrase" ]; then
    check "$command error names the cause ('$phrase')" grep -qi -- "$phrase" <<< "$(json "$work/out.json" error)"
  fi
}

echo "== dpkg-deb and lintian"
check "no release fragment without [updates]" test ! -e "$plain.release.json"
equal "$(python3 -I -c 'import json,sys; a=json.load(open(sys.argv[1]))["release"]["artifacts"][0]; print("/".join([a["os"],a["arch"],a["format"],a["scope"]]))' "$v2.release.json")" \
  "linux/$arch/deb/machine" "the release fragment describes a linux/$arch/deb/machine artifact"
inspect "$v1" 1.0.0 yes
inspect "$plain" 1.0.0 no
run_lintian "$v1"

if [ -n "$(installed)" ] && [ "$(installed)" != not-installed ]; then
  echo "$pkg is installed already; purging it first"
  as_root dpkg --purge "$pkg"
fi

echo
echo "== install 1.0.0 with apt"
as_root apt-get install -y -q "$v1" 2>&1 | sed 's/^/      /'
test_installed 1.0.0

python3 -I -m http.server 8080 --directory "$www" > "$work/server.log" 2>&1 &
server=$!
wait_for 15 curl -fsS -o /dev/null "http://localhost:8080/" || { echo "the feed server did not start" >&2; cat "$work/server.log" >&2; exit 1; }
cp "$packages"/deb/${pkg}_*.deb "$served/"

echo
echo "== notify update scenario"
before="$(snapshot)"
echo "-- a valid feed without a newer version"
publish_feed seq1
updater check 1 up-to-date "the feed with sequence 1 has only 1.0.0"

echo "-- verification failures (exit 4)"
publish_feed seq2
flip_byte "$served/feed.json"
updater check 4 error "a feed byte was flipped" signature
publish_feed other-key
updater check 4 error "the feed is signed with an untrusted key" signature

echo "-- the feed with 2.0.0"
publish_feed seq2
updater check 0 available "the feed with sequence 2 has 2.0.0"
equal "$(json "$work/out.json" installed)" 1.0.0 "check reports installed 1.0.0"
equal "$(json "$work/out.json" version)" 2.0.0 "check reports version 2.0.0"
equal "$(json "$work/out.json" url)" "http://localhost:8080/deb/stable/$(basename "$v2")" "check reports the url of the 2.0.0 package"
equal "$(json "$work/out.json" size)" "$(wc -c < "$v2" | tr -d ' ')" "check reports the size of the 2.0.0 package"

echo "-- replay of the older feed (exit 4)"
publish_feed seq1
updater check 4 error "the validly signed feed with sequence 1 is served after sequence 2" sequence

echo "-- download and apply are left to the package manager (exit 7)"
publish_feed seq2
: > "$work/server.log"
updater download 7 error "the package manager installs updates" "apt or dpkg"
updater apply 7 error "the package manager installs updates" "apt or dpkg"
updater apply 7 error "also with --wait-pid and --restart" "apt or dpkg" --wait-pid $$ --restart
check "nothing was fetched" bash -c '! grep -q "GET " "$1"' _ "$work/server.log"
equal "$(snapshot)" "$before" "the installed files are unchanged (paths, modes, owners, sizes, times, contents)"
check "dpkg --verify still finds no changed file" bash -c '[ -z "$(dpkg --verify "$1")" ]' _ "$pkg"
check "nothing was downloaded or staged" test ! -e "$state/downloads"
check "no apply result was written" test ! -e "$state/last-apply.json"
equal "$(ls -A "$TMPDIR" | grep '^zexoro-updater' || true)" "" "no updater copy in \$TMPDIR"

echo
echo "== upgrade to 2.0.0 with dpkg"
as_root dpkg -i "$v2" 2>&1 | sed 's/^/      /'
test_installed 2.0.0
check "app/obsolete.txt is gone" test ! -e "$root/app/obsolete.txt"
updater check 1 up-to-date "2.0.0 is the newest release"
equal "$(json "$work/out.json" installed)" 2.0.0 "check reports installed 2.0.0"

echo
echo "== remove and purge with dpkg"
as_root dpkg --remove "$pkg" 2>&1 | sed 's/^/      /'
test_gone "removed"
as_root dpkg --purge "$pkg" 2>&1 | sed 's/^/      /'
test_gone "purged"
check "dpkg no longer knows $pkg" bash -c '[ -z "$(dpkg-query -W -f="\${db:Status-Status}" "$1" 2> /dev/null | grep -v not-installed)" ]' _ "$pkg"

echo
echo "== install 1.0.0 with dpkg, purge with apt"
as_root dpkg -i "$v1" 2>&1 | sed 's/^/      /'
equal "$(installed)" installed "dpkg -i installs 1.0.0"
check "1.0.0 has app/obsolete.txt" test -f "$root/app/obsolete.txt"
as_root apt-get purge -y -q "$pkg" 2>&1 | sed 's/^/      /'
test_gone "purged with apt"

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ]

#!/usr/bin/env bash
# Smoke test of a Linux or macOS zexoro-updater binary against a local feed server: the Native AOT
# build talks HTTP, verifies signed feeds and downloads, keeps its state where
# docs/design/0006-desktop-formats.md section 2 says, and applies a tar.gz made with the system's
# tar (modes and symbolic links kept, a private copy in $TMPDIR, the swap, the restart). The tar.gz
# packager and the full update scenario come with step 5, so the install here is hand-written.
#
#   tests/e2e/unix/check-updater.sh <zexoro executable> <zexoro-updater executable> [<runner>...]
#
# <runner> is an optional command prefix that runs the updater elsewhere, for example
#   docker run --rm --network host --user "$(id -u):$(id -g)" -e HOME -e XDG_STATE_HOME -e TMPDIR -v /tmp:/tmp alpine:3.21
# (the work directory is created below $TMPDIR, or /tmp, and must be visible at the same path; the
# variables must be passed on). With a runner, apply runs without --wait-pid and --restart, since
# processes on both sides cannot see each other.
# Needs bash, python3 (the feed server) and a free port 8081 on 127.0.0.1.
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <zexoro executable> <zexoro-updater executable> [<runner>...]" >&2
  exit 2
fi

absolute() { (cd "$(dirname "$1")" && printf '%s/%s\n' "$(pwd)" "$(basename "$1")"); }
cli="$(absolute "$1")"
updater_source="$(absolute "$2")"
shift 2
runner=("$@")

port=8081
app=dev.zexoro.smoke
work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-updater-check.XXXXXX")"
server=
cleanup() {
  [ -n "$server" ] && kill "$server" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

failures=0
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*" >&2; failures=$((failures + 1)); }

root="$work/install"
served="$work/served/stable"
home="$work/home"
temp="$work/tmp"
mkdir -p "$root/app" "$served" "$home" "$temp"
cp "$updater_source" "$root/zexoro-updater"
chmod 755 "$root/zexoro-updater"
printf 'removed by 2.0.0\n' > "$root/app/obsolete.txt"

"$cli" keys new --output "$work/keys" > /dev/null
public_key="$(tr -d '\r\n' < "$work/keys/zexoro-update.pub")"

case "$(uname -s)" in
  Darwin) os=macos; state_base="$home/Library/Application Support" ;;
  *) os=linux; state_base="$work/xdg-state" ;;
esac
state="$state_base/zexoro/updater/$app"

# record <version>: the install record of that version.
record() {
  cat <<JSON
{
  "schema": 1,
  "app": "$app",
  "version": "$1",
  "os": "$os", "arch": "x64", "format": "tar-gz", "scope": "user",
  "channel": "stable",
  "feed": "http://127.0.0.1:$port/stable/feed.json",
  "keys": ["$public_key"],
  "main": "bin/smoke",
  "strategy": "replace"
}
JSON
}
record 1.0.0 > "$root/zexoro-install.json"

# fragment <version>: a release fragment whose artifact, a portable tar.gz of that version, is
# served next to the feed.
fragment() {
  local version=$1 file="Smoke-$1-$os-x64.tar.gz" size sha stage="$work/stage-$1"
  mkdir -p "$stage/Smoke/app/bin" "$stage/Smoke/app/lib"
  printf '#!/bin/sh\necho "restarted %s" > "%s/restarted.txt"\n' "$version" "$work" > "$stage/Smoke/app/bin/smoke"
  chmod 755 "$stage/Smoke/app/bin/smoke"
  printf 'data %s\n' "$version" > "$stage/Smoke/app/lib/data.txt.1"
  chmod 640 "$stage/Smoke/app/lib/data.txt.1"
  ln -s data.txt.1 "$stage/Smoke/app/lib/data.txt"
  cp "$updater_source" "$stage/Smoke/zexoro-updater"
  chmod 755 "$stage/Smoke/zexoro-updater"
  record "$version" > "$stage/Smoke/zexoro-install.json"
  # COPYFILE_DISABLE: macOS tar would add AppleDouble ._ entries for extended attributes.
  COPYFILE_DISABLE=1 tar -czf "$served/$file" -C "$stage" Smoke
  size=$(wc -c < "$served/$file" | tr -d ' ')
  sha=$( (sha256sum "$served/$file" 2>/dev/null || shasum -a 256 "$served/$file") | cut -d' ' -f1)
  cat > "$work/$version.release.json" <<JSON
{
  "schema": 1,
  "app": "$app",
  "channel": "stable",
  "release": {
    "version": "$version",
    "minimumUpdater": "0.2.0-0",
    "artifacts": [{ "os": "$os", "arch": "x64", "format": "tar-gz", "scope": "user",
                    "url": "http://127.0.0.1:$port/stable/$file", "size": $size, "sha256": "$sha" }]
  }
}
JSON
  echo "$work/$version.release.json"
}

publish() {
  "$cli" feed add --feed "$work/feed/feed.json" --key "$work/keys/zexoro-update.key.pem" "$@" > /dev/null
  cp "$work/feed/feed.json" "$work/feed/feed.json.sig" "$served/"
}

# run_updater <expected exit> <description> <command>...: runs the installed updater from the root.
run_updater() {
  local expected=$1 description=$2 status
  shift 2
  set +e
  (cd "$root" && env HOME="$home" XDG_STATE_HOME="$state_base" TMPDIR="$temp" ${runner[@]+"${runner[@]}"} "$root/zexoro-updater" "$@" --json) > "$work/out.json" 2> "$work/err.txt"
  status=$?
  set -e
  if [ "$status" -eq "$expected" ]; then
    pass "$description (exit $status)"
  else
    fail "$description: exit $status, expected $expected"
    cat "$work/out.json" "$work/err.txt" >&2
  fi
}

expect_json() {
  if grep -q "$1" "$work/out.json"; then pass "output has $1"; else fail "output lacks $1: $(cat "$work/out.json")"; fi
}

python3 -m http.server "$port" --bind 127.0.0.1 --directory "$work/served" > "$work/server.log" 2>&1 &
server=$!
for _ in $(seq 1 50); do
  curl -fsS -o /dev/null "http://127.0.0.1:$port/" 2>/dev/null && break
  sleep 0.2
done

echo "== $(${runner[@]+"${runner[@]}"} "$root/zexoro-updater" --version) ($os)"

publish "$(fragment 1.0.0)"
run_updater 1 "check: the feed has only the installed 1.0.0" check
expect_json '"status":"up-to-date"'

publish "$(fragment 2.0.0)"
cp "$served/feed.json" "$work/feed-seq2.json"
python3 - "$served/feed.json" <<'PY'
import sys
path = sys.argv[1]
data = bytearray(open(path, "rb").read())
data[len(data) // 2] ^= 0x01
open(path, "wb").write(bytes(data))
PY
run_updater 4 "check: a feed byte was flipped" check
cp "$work/feed-seq2.json" "$served/feed.json"

run_updater 0 "check: 2.0.0 is available" check
expect_json '"version":"2.0.0"'
run_updater 0 "download: 2.0.0 is downloaded and verified" download
expect_json '"status":"downloaded"'

if [ -f "$state/downloads/Smoke-2.0.0-$os-x64.tar.gz" ] && [ -f "$state/downloads/staged.json" ] && [ -f "$state/state.json" ]; then
  pass "the state is in $state"
else
  fail "no state in $state"
  find "$work" -path "$work/served" -prune -o -print >&2
fi

# The application: a process that is not a child of the updater, which apply waits for.
if [ ${#runner[@]} -eq 0 ]; then
  app_pid=$(sh -c 'sleep 2 >/dev/null 2>&1 </dev/null & echo $!')
  started=$(date +%s)
  run_updater 0 "apply --wait-pid --restart: 2.0.0 is applied" apply --wait-pid "$app_pid" --restart
  if [ $(( $(date +%s) - started )) -ge 1 ]; then pass "apply waited for the application"; else fail "apply did not wait for process $app_pid"; fi
  for _ in $(seq 1 50); do [ -f "$work/restarted.txt" ] && break; sleep 0.1; done
  if grep -q "restarted 2.0.0" "$work/restarted.txt" 2>/dev/null; then pass "the application was restarted"; else fail "the application was not restarted"; fi
else
  run_updater 0 "apply: 2.0.0 is applied" apply
fi
expect_json '"status":"applied"'

mode() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }
grep -q '"version": "2.0.0"' "$root/zexoro-install.json" && pass "the record is 2.0.0" || fail "the record is not 2.0.0"
[ "$(mode "$root/app/bin/smoke")" = 755 ] && pass "app/bin/smoke kept mode 755" || fail "app/bin/smoke has mode $(mode "$root/app/bin/smoke")"
[ "$(mode "$root/app/lib/data.txt.1")" = 640 ] && pass "app/lib/data.txt.1 kept mode 640" || fail "app/lib/data.txt.1 has mode $(mode "$root/app/lib/data.txt.1")"
[ "$(mode "$root/zexoro-updater")" = 755 ] && pass "zexoro-updater kept mode 755" || fail "zexoro-updater has mode $(mode "$root/zexoro-updater")"
[ -L "$root/app/lib/data.txt" ] && [ "$(readlink "$root/app/lib/data.txt")" = data.txt.1 ] && grep -q "data 2.0.0" "$root/app/lib/data.txt" \
  && pass "the symbolic link app/lib/data.txt was kept" || fail "app/lib/data.txt is not the link to data.txt.1"
[ ! -e "$root/app/obsolete.txt" ] && pass "obsolete.txt is gone" || fail "obsolete.txt is still there"
left=$(cd "$root" && ls -d -- *.old *.new app.old app.new 2>/dev/null || true)
[ -z "$left" ] && pass "nothing staged or old is left in the install" || fail "left in the install: $left"
left=$(ls "$temp")
[ -z "$left" ] && pass "no updater copy is left in \$TMPDIR" || fail "left in \$TMPDIR: $left"
[ ! -e "$state/downloads" ] && pass "the downloads were cleared" || fail "$state/downloads is still there"
run_updater 1 "check: 2.0.0 is up to date" check

if [ "$failures" -ne 0 ]; then
  echo "$failures check(s) failed" >&2
  exit 1
fi
echo "all checks passed"

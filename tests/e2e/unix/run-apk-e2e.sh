#!/usr/bin/env bash
# End-to-end test of zexoro's apk packages (docs/design/0006-desktop-formats.md): independent
# checks of the three segments (split with Python's zlib, listed with GNU tar, the signature checked
# with openssl, the datahash with sha256sum), apk verify before and after the public key is trusted,
# install with apk, run through /usr/bin, the notify update scenario against a local feed server,
# apk audit, upgrade, remove.
#
#   tests/e2e/unix/run-apk-e2e.sh <directory written by tests/e2e/pack.sh>
#
# Consumes apk/zexoro-e2e-{1.0.0,2.0.0}-r0.<arch>.apk of the machine's architecture (signed, with
# [updates] pointing at http://localhost:8080/apk/stable/), apk/unsigned/zexoro-e2e-1.0.0-r0.<arch>.apk,
# zexoro-e2e.rsa.pub and the feeds pack.sh signed (feeds/apk/seq1, seq2 and other-key). Runs as root
# in an Alpine container; installs and finally removes the package zexoro-e2e and the trusted key
# /etc/apk/keys/zexoro-e2e.rsa.pub. Needs apk, python3 (the feed server, JSON, splitting), GNU tar,
# GNU find, openssl, the .NET runtime for musl (the fixture app is framework-dependent; DOTNET_ROOT)
# and a free port 8080.
#
# Notify scenario (0006 section 5): with 1.0.0 installed, check against seq1 (up to date); a
# tampered feed, a feed signed with an untrusted key and seq1 replayed after seq2 exit 4; check
# against seq2 reports 2.0.0 and the package's url; download and apply exit 7, fetch nothing and
# change nothing (files, modes, apk audit, the updater's state). The upgrade is then done with apk,
# as a user would.
#
# Each check prints PASS or FAIL; the script exits 1 when any check failed.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <directory written by tests/e2e/pack.sh>" >&2
  exit 2
fi

packages="$(cd "$1" && pwd)"
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 apkarch=x86_64 ;;
  aarch64 | arm64) arch=arm64 apkarch=aarch64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 2 ;;
esac
[ "$(id -u)" -eq 0 ] || { echo "run as root (in a container)" >&2; exit 2; }

pkg=zexoro-e2e
app_id=dev.zexoro.e2e.apk
root=/opt/$pkg
v1="$packages/apk/$pkg-1.0.0-r0.$apkarch.apk"
v2="$packages/apk/$pkg-2.0.0-r0.$apkarch.apk"
plain="$packages/apk/unsigned/$pkg-1.0.0-r0.$apkarch.apk"
key_name=zexoro-e2e.rsa.pub
public_key="$packages/$key_name"
trusted=/etc/apk/keys/$key_name
for f in "$v1" "$v2" "$v2.release.json" "$plain" "$public_key" "$packages"/feeds/apk/{seq1,seq2,other-key}/feed.json; do
  [ -f "$f" ] || { echo "$f not found; pack with the current tests/e2e/pack.sh" >&2; exit 2; }
done

work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-apk-e2e.XXXXXX")"
chmod 755 "$work"
www="$work/www"
served="$www/apk/stable"
export XDG_STATE_HOME="$work/state" XDG_DATA_HOME="$work/data" TMPDIR="$work/tmp"
state="$XDG_STATE_HOME/zexoro/updater/$app_id"
marker="$XDG_DATA_HOME/ZexoroE2E/ran.txt"
mkdir -p "$served" "$XDG_STATE_HOME" "$XDG_DATA_HOME" "$TMPDIR"
server=
installed() { apk info -e "$pkg" > /dev/null 2>&1; }
cleanup() {
  [ -n "$server" ] && kill "$server" 2> /dev/null || true
  if installed; then apk del -q "$pkg" > /dev/null 2>&1 || true; fi
  rm -f "$trusted"
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

http_ok() { python3 -I -c 'import sys, urllib.request; urllib.request.urlopen(sys.argv[1], timeout=2)' "$1" 2> /dev/null; }

# split <apk> <directory>: writes each gzip member of <apk> to <directory>/segment-<n>.gz, as
# abuild-gzsplit does, independently of zexoro; prints the number of members.
split() {
  python3 -I - "$1" "$2" <<'PY'
import sys, zlib
data = open(sys.argv[1], "rb").read()
n = 0
while data:
    d = zlib.decompressobj(31)
    d.decompress(data)
    assert d.eof, "a gzip member is cut"
    member = data[:len(data) - len(d.unused_data)]
    open("%s/segment-%d.gz" % (sys.argv[2], n), "wb").write(member)
    data = d.unused_data
    n += 1
print(n)
PY
}

# segments <apk> <signed: yes|no> <version>: the three (or two) segments, read with GNU tar, openssl and sha256sum.
segments() {
  local file=$1 signed=$2 version=$3 name dir count first listing info datahash
  name="$(basename "$file")"
  dir="$work/split-$name"
  mkdir -p "$dir"
  count="$(split "$file" "$dir")"
  if [ "$signed" = yes ]; then
    equal "$count" 3 "$name: three gzip streams (signature, control, data)"
    first=1
    listing="$(tar -tzvf "$dir/segment-0.gz" 2>&1 || true)"
    echo "$listing" | sed 's/^/      signature: /'
    check "$name: the signature segment holds .SIGN.RSA256.$key_name, owned by root" grep -Eq "^-rw-r--r-- root/root .* \.SIGN\.RSA256\.$key_name\$" <<< "$listing"
    tar -xzf "$dir/segment-0.gz" -C "$dir" 2> /dev/null || true
    check "$name: openssl verifies the RSA/SHA-256 signature over the control segment" \
      openssl dgst -sha256 -verify "$public_key" -signature "$dir/.SIGN.RSA256.$key_name" "$dir/segment-1.gz"
  else
    equal "$count" 2 "$name: two gzip streams (control, data), no signature"
    first=0
  fi
  listing="$(tar -tzvf "$dir/segment-$first.gz" 2>&1 || true)"
  echo "$listing" | sed 's/^/      control: /'
  check "$name: the control segment holds only .PKGINFO" bash -c '[ "$(grep -c . <<< "$1")" -eq 1 ] && grep -Eq "^-rw-r--r-- root/root .* \.PKGINFO$" <<< "$1"' _ "$listing"
  tar -xzf "$dir/segment-$first.gz" -C "$dir" 2> /dev/null || true
  info="$(cat "$dir/.PKGINFO")"
  echo "$info" | sed 's/^/      .PKGINFO: /'
  check "$name: pkgname" grep -qx "pkgname = $pkg" <<< "$info"
  check "$name: pkgver $version-r0" grep -qx "pkgver = $version-r0" <<< "$info"
  check "$name: arch $apkarch" grep -qx "arch = $apkarch" <<< "$info"
  check "$name: license ([linux.apk] license)" grep -qx "license = MIT" <<< "$info"
  check "$name: maintainer ([linux] maintainer)" grep -qx "maintainer = zexoro e2e <e2e@zexoro.invalid>" <<< "$info"
  check "$name: depends on busybox ([linux.apk] depends)" grep -qx "depend = busybox" <<< "$info"
  if [ "$signed" = yes ]; then
    check "$name: depends on musl for the updater" grep -qx "depend = so:libc.musl-$apkarch.so.1" <<< "$info"
    check "$name: depends on OpenSSL for the updater" grep -qx "depend = so:libssl.so.3" <<< "$info"
    check "$name: depends on the CA bundle for the updater" grep -qx "depend = ca-certificates-bundle" <<< "$info"
  else
    check "$name: no updater dependencies without [updates]" bash -c '! grep -q "so:lib\|ca-certificates" <<< "$1"' _ "$info"
  fi
  datahash="$(sha256sum "$dir/segment-$((first + 1)).gz" | cut -d' ' -f1)"
  check "$name: datahash is the SHA-256 of the data segment" grep -qx "datahash = $datahash" <<< "$info"
  listing="$(tar -tzvf "$dir/segment-$((first + 1)).gz" 2>&1)"
  echo "$listing" | sed 's/^/      data: /'
  check "$name: every data entry is owned by root/root" bash -c '! awk "{print \$2}" <<< "$1" | grep -v "^root/root$" | grep -q .' _ "$listing"
  check "$name: /usr/bin/$pkg links to the main executable" grep -Eq "^lrwxrwxrwx root/root .* usr/bin/$pkg -> $root/app/FixtureApp\$" <<< "$listing"
  check "$name: app/FixtureApp is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x root/root .* opt/$pkg/app/FixtureApp\$" <<< "$listing"
  check "$name: app/FixtureApp.dll is -rw-r--r--" grep -Eq "^-rw-r--r-- root/root .* opt/$pkg/app/FixtureApp.dll\$" <<< "$listing"
  check "$name: app/bin/helper.sh is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x root/root .* opt/$pkg/app/bin/helper.sh\$" <<< "$listing"
  check "$name: every parent directory has an entry" grep -Eq "^drwxr-xr-x root/root .* usr/share/applications/\$" <<< "$listing"
  check "$name: the .desktop file is in /usr/share/applications" grep -Eq "^-rw-r--r-- root/root .* usr/share/applications/$app_id.desktop\$" <<< "$listing"
  check "$name: the icon is in the hicolor theme" grep -Eq "^-rw-r--r-- root/root .* usr/share/icons/hicolor/[0-9]+x[0-9]+/apps/$app_id.png\$" <<< "$listing"
  if [ "$signed" = yes ]; then
    check "$name: zexoro-updater is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x root/root .* opt/$pkg/zexoro-updater\$" <<< "$listing"
  else
    check "$name: no zexoro-updater without [updates]" bash -c '! grep -q "zexoro-updater" <<< "$1"' _ "$listing"
  fi
  # The per-file checksums abuild-tar adds, as pax headers, checked against the contents.
  check "$name: every file and link has a correct APK-TOOLS.checksum.SHA1" python3 -I - "$dir/segment-$((first + 1)).gz" <<'PY'
import hashlib, sys, tarfile
bad = 0
count = 0
with tarfile.open(sys.argv[1], "r:gz") as tar:
    for m in tar:
        if m.isdir():
            continue
        expected = m.pax_headers.get("APK-TOOLS.checksum.SHA1")
        actual = hashlib.sha1(m.linkname.encode() if m.issym() else tar.extractfile(m).read()).hexdigest()
        count += 1
        if expected != actual:
            print("checksum of %s: %s, expected %s" % (m.name, expected, actual))
            bad += 1
sys.exit(1 if bad or count == 0 else 0)
PY
}

verify() { # <message> <expected: ok|fails> <apk>
  local out code=0
  out="$(apk verify "$3" 2>&1)" || code=$?
  echo "$out" | sed 's/^/      /'
  if [ "$2" = ok ]; then check "$1" test "$code" -eq 0; else check "$1" test "$code" -ne 0; fi
}

# version: the installed version of the package, from apk's database.
installed_version() { apk list -I "$pkg" 2> /dev/null | sed -n "s/^$pkg-\([^ ]*\) .*/\1/p" | head -1; }

# snapshot: everything the package installed, with modes, sizes, times and contents.
snapshot() {
  {
    find "$root" /usr/bin/$pkg /usr/share/applications/$app_id.desktop -printf '%p %m %u %s %T@ %l\n'
    find "$root" -type f -exec md5sum {} +
  } | sort
}

# audit: what apk audit reports about the package's files (empty when nothing changed).
audit() { apk audit --full 2> /dev/null | grep -E " (opt/$pkg|usr/bin/$pkg|usr/share/applications/$app_id)" || true; }

run_app() { # <context>: runs the installed application through /usr/bin and checks it ran from /opt
  rm -rf "$XDG_DATA_HOME/ZexoroE2E"
  local status=0
  "$pkg" || status=$?
  equal "$status" 0 "$1: $pkg (on PATH) exits with 0"
  equal "$(cat "$marker" 2> /dev/null)" "$root/app/" "$1: the application ran from $root/app/"
}

test_installed() { # <version>: the installed package and its files
  local version=$1 record=$root/zexoro-install.json
  check "apk reports $pkg as installed" installed
  equal "$(installed_version)" "$version-r0" "apk reports version $version-r0"
  equal "$(command -v "$pkg")" "/usr/bin/$pkg" "$pkg is found on PATH in /usr/bin"
  equal "$(readlink /usr/bin/$pkg)" "$root/app/FixtureApp" "/usr/bin/$pkg links to $root/app/FixtureApp"
  equal "$(cd "$root" && ls -A | sort | tr '\n' ' ' | sed 's/ $//')" "app zexoro-install.json zexoro-updater" "$root holds app/, the record and the updater, nothing else"
  equal "$(stat -c '%U %a' "$root/zexoro-updater")" "root 755" "zexoro-updater is owned by root with mode 755"
  equal "$(stat -c '%U %a' "$record")" "root 644" "zexoro-install.json is owned by root with mode 644"
  equal "$(stat -c '%a' "$root/app/bin/helper.sh")" "755" "app/bin/helper.sh has mode 755"
  equal "$(cat "$root/app/version.txt")" "$version" "app/version.txt is $version"
  for field in schema:1 app:$app_id version:$version os:linux arch:$arch format:apk scope:machine strategy:notify channel:stable main:FixtureApp; do
    equal "$(json "$record" "${field%%:*}")" "${field#*:}" "zexoro-install.json ${field%%:*}"
  done
  equal "$(json "$record" feed)" "http://localhost:8080/apk/stable/feed.json" "zexoro-install.json feed"
  check "/usr/share/applications/$app_id.desktop runs /usr/bin/$pkg" grep -qx "Exec=/usr/bin/$pkg" /usr/share/applications/$app_id.desktop
  check "the icon is installed" bash -c 'ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png > /dev/null'
  check "the updater is the musl build" bash -c 'grep -aq "ld-musl-" "$1"' _ "$root/zexoro-updater"
  equal "$(audit)" "" "apk audit --full finds no changed file of the package"
  run_app "$version"
}

test_gone() { # <context>: nothing of the package is left
  check "$1: apk no longer knows $pkg" bash -c '! apk info -e "$1" > /dev/null 2>&1' _ "$pkg"
  check "$1: $root is gone" test ! -e "$root"
  check "$1: /usr/bin/$pkg is gone" test ! -e /usr/bin/$pkg -a ! -L /usr/bin/$pkg
  check "$1: the .desktop file is gone" test ! -e /usr/share/applications/$app_id.desktop
  check "$1: the icon is gone" bash -c '! ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png 2> /dev/null'
}

publish_feed() { cp "$packages/feeds/apk/$1/feed.json" "$packages/feeds/apk/$1/feed.json.sig" "$served/"; }

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

echo "== $(apk --version), $(. /etc/os-release && echo "$PRETTY_NAME")"
if installed; then
  echo "$pkg is installed already; removing it first"
  apk del -q "$pkg"
fi
rm -f "$trusted"

echo
echo "== segments, read independently"
check "no release fragment for the unsigned package without [updates]" test ! -e "$plain.release.json"
equal "$(python3 -I -c 'import json,sys; a=json.load(open(sys.argv[1]))["release"]["artifacts"][0]; print("/".join([a["os"],a["arch"],a["format"],a["scope"]]))' "$v2.release.json")" \
  "linux/$arch/apk/machine" "the release fragment describes a linux/$arch/apk/machine artifact"
segments "$v1" yes 1.0.0
segments "$plain" no 1.0.0

echo
echo "== apk verify"
verify "apk verify refuses the signed package before its key is trusted" fails "$v1"
cp "$public_key" "$trusted"
for file in "$v1" "$v2"; do
  verify "apk verify accepts $(basename "$file") with the key in /etc/apk/keys" ok "$file"
done
verify "apk verify refuses the unsigned package" fails "$plain"
cp "$v1" "$work/tampered.apk"
flip_byte "$work/tampered.apk"
verify "apk verify refuses the package with one changed byte" fails "$work/tampered.apk"

echo
echo "== the unsigned package needs --allow-untrusted"
check "apk add refuses the unsigned package" bash -c '! apk add -q "$1" > "$2/refused.log" 2>&1' _ "$plain" "$work"
sed 's/^/      /' "$work/refused.log"
check "$pkg was not installed" bash -c '! apk info -e "$1" > /dev/null 2>&1' _ "$pkg"
apk add -q --allow-untrusted "$plain" 2>&1 | sed 's/^/      /'
check "apk add --allow-untrusted installs it" installed
check "it has no updater" test ! -e "$root/zexoro-updater"
apk del -q "$pkg" 2>&1 | sed 's/^/      /'
test_gone "unsigned package removed"

echo
echo "== install 1.0.0 with apk add"
apk add "$v1" 2>&1 | sed 's/^/      /'
test_installed 1.0.0
apk info -a "$pkg" 2>&1 | sed 's/^/      /' || true

python3 -I -m http.server 8080 --directory "$www" > "$work/server.log" 2>&1 &
server=$!
wait_for 15 http_ok "http://localhost:8080/" || { echo "the feed server did not start" >&2; cat "$work/server.log" >&2; exit 1; }
cp "$packages"/apk/$pkg-*.apk "$served/"

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
equal "$(json "$work/out.json" url)" "http://localhost:8080/apk/stable/$(basename "$v2")" "check reports the url of the 2.0.0 package"
equal "$(json "$work/out.json" size)" "$(wc -c < "$v2" | tr -d ' ')" "check reports the size of the 2.0.0 package"

echo "-- replay of the older feed (exit 4)"
publish_feed seq1
updater check 4 error "the validly signed feed with sequence 1 is served after sequence 2" sequence

echo "-- download and apply are left to the package manager (exit 7)"
publish_feed seq2
: > "$work/server.log"
updater download 7 error "the package manager installs updates" "apk"
updater apply 7 error "the package manager installs updates" "apk"
updater apply 7 error "also with --wait-pid and --restart" "apk" --wait-pid $$ --restart
check "nothing was fetched" bash -c '! grep -q "GET " "$1"' _ "$work/server.log"
equal "$(snapshot)" "$before" "the installed files are unchanged (paths, modes, owners, sizes, times, contents)"
equal "$(audit)" "" "apk audit still finds no changed file"
check "nothing was downloaded or staged" test ! -e "$state/downloads"
check "no apply result was written" test ! -e "$state/last-apply.json"
equal "$(ls -A "$TMPDIR" | grep '^zexoro-updater' || true)" "" "no updater copy in \$TMPDIR"

echo
echo "== upgrade to 2.0.0 with apk add"
apk add "$v2" 2>&1 | sed 's/^/      /'
test_installed 2.0.0
check "app/obsolete.txt is gone" test ! -e "$root/app/obsolete.txt"
updater check 1 up-to-date "2.0.0 is the newest release"
equal "$(json "$work/out.json" installed)" 2.0.0 "check reports installed 2.0.0"

echo
echo "== remove with apk del"
apk del "$pkg" 2>&1 | sed 's/^/      /'
test_gone "removed"

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ]

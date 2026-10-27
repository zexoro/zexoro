#!/usr/bin/env bash
# End-to-end test of zexoro's rpm packages (docs/design/0006-desktop-formats.md): independent
# checks with rpm itself (queries, rpm -K, rpmkeys --checksig), gpg --verify on the extracted header
# signature and rpmlint (each when installed), install with dnf or zypper, run through /usr/bin, the
# notify update scenario against a local feed server, upgrade, remove.
#
#   tests/e2e/unix/run-rpm-e2e.sh <directory written by tests/e2e/pack.sh> [enforce]
#
# With 'enforce', %_pkgverify_level is set to 'all' for every rpm operation (also inside dnf and
# zypper), as RPM 6 does by default: packages must carry a valid signature and digests, and the
# unsigned package must be refused.
#
# Consumes rpm/zexoro-e2e-{1.0.0,2.0.0}-1.<arch>.rpm of the machine's architecture (signed, with
# [updates] pointing at http://localhost:8080/rpm/stable/), rpm/unsigned/zexoro-e2e-1.0.0-1.<arch>.rpm,
# rpm-signing.pub.asc and the feeds pack.sh signed (feeds/rpm/seq1, seq2 and other-key). Runs as root
# in a container of an rpm distribution (Fedora, Rocky Linux, openSUSE); installs and finally removes
# the package zexoro-e2e and the imported key. Needs rpm, dnf or zypper, python3 (the feed server and
# JSON), GNU find and the .NET runtime (the fixture app is framework-dependent; DOTNET_ROOT) and a
# free port 8080.
#
# Notify scenario (0006 section 5): with 1.0.0 installed, check against seq1 (up to date); a
# tampered feed, a feed signed with an untrusted key and seq1 replayed after seq2 exit 4; check
# against seq2 reports 2.0.0 and the package's url; download and apply exit 7, fetch nothing and
# change nothing (files, modes, rpm -V, the updater's state). The upgrade is then done with the
# package manager, as a user would.
#
# Each check prints PASS or FAIL; the script exits 1 when any check failed.
set -euo pipefail

if [ $# -lt 1 ] || [ $# -gt 2 ] || { [ $# -eq 2 ] && [ "$2" != enforce ]; }; then
  echo "usage: $0 <directory written by tests/e2e/pack.sh> [enforce]" >&2
  exit 2
fi

packages="$(cd "$1" && pwd)"
enforce=${2:-}
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 rpmarch=x86_64 ;;
  aarch64 | arm64) arch=arm64 rpmarch=aarch64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 2 ;;
esac
[ "$(id -u)" -eq 0 ] || { echo "run as root (in a container)" >&2; exit 2; }

pkg=zexoro-e2e
app_id=dev.zexoro.e2e.rpm
root=/opt/$pkg
v1="$packages/rpm/$pkg-1.0.0-1.$rpmarch.rpm"
v2="$packages/rpm/$pkg-2.0.0-1.$rpmarch.rpm"
plain="$packages/rpm/unsigned/$pkg-1.0.0-1.$rpmarch.rpm"
public_key="$packages/rpm-signing.pub.asc"
for f in "$v1" "$v2" "$v2.release.json" "$plain" "$public_key" "$packages"/feeds/rpm/{seq1,seq2,other-key}/feed.json; do
  [ -f "$f" ] || { echo "$f not found; pack with the current tests/e2e/pack.sh" >&2; exit 2; }
done

if command -v dnf > /dev/null; then manager=dnf; elif command -v zypper > /dev/null; then manager=zypper; else manager=rpm; fi
key_id=e2ab728735d9a497
macros=/etc/rpm/macros.zexoro-e2e

work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-rpm-e2e.XXXXXX")"
chmod 755 "$work"
www="$work/www"
served="$www/rpm/stable"
export XDG_STATE_HOME="$work/state" XDG_DATA_HOME="$work/data" TMPDIR="$work/tmp"
state="$XDG_STATE_HOME/zexoro/updater/$app_id"
marker="$XDG_DATA_HOME/ZexoroE2E/ran.txt"
mkdir -p "$served" "$XDG_STATE_HOME" "$XDG_DATA_HOME" "$TMPDIR"
server=
installed() { rpm -q "$pkg" > /dev/null 2>&1; }
remove_key() { rpm -q gpg-pubkey --qf '%{NAME}-%{VERSION}-%{RELEASE}\n' 2> /dev/null | grep -i "${key_id: -8}-" | xargs -r rpm -e > /dev/null 2>&1 || true; }
cleanup() {
  [ -n "$server" ] && kill "$server" 2> /dev/null || true
  if installed; then rpm -e "$pkg" > /dev/null || true; fi
  remove_key
  rm -f "$macros"
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

# split_signature <rpm> <directory>: writes the main header (header.bin) and the OpenPGP signature
# of the signature header's RSAHEADER tag (header.sig) of <rpm>, read independently of zexoro.
split_signature() {
  python3 -I - "$1" "$2" <<'PY'
import struct, sys
data = open(sys.argv[1], "rb").read()
def header(at):
    assert data[at:at + 4] == b"\x8e\xad\xe8\x01", "bad header magic"
    count, size = struct.unpack(">ii", data[at + 8:at + 16])
    tags = {}
    for i in range(count):
        tag, kind, offset, n = struct.unpack(">iiii", data[at + 16 + i * 16:at + 32 + i * 16])
        tags[tag] = (kind, offset, n)
    store = at + 16 + count * 16
    return tags, store, store + size
tags, store, end = header(96)
kind, offset, n = tags[268]
open(sys.argv[2] + "/header.sig", "wb").write(data[store + offset:store + offset + n])
start = (end + 7) // 8 * 8
_, _, main_end = header(start)
open(sys.argv[2] + "/header.bin", "wb").write(data[start:main_end])
PY
}

q() { rpm -qp --qf "$1" "$2" 2> /dev/null; }

# inspect <rpm> <version> <signed with updates: yes|no>: rpm's own view of the package.
inspect() {
  local file=$1 version=$2 signed=$3 name contents requires
  name="$(basename "$file")"
  rpm -qip "$file" 2>&1 | sed 's/^/      /'
  equal "$(q '%{NAME}' "$file")" "$pkg" "$name: Name"
  equal "$(q '%{VERSION}-%{RELEASE}' "$file")" "$version-1" "$name: Version-Release"
  equal "$(q '%{ARCH}' "$file")" "$rpmarch" "$name: Arch"
  equal "$(q '%{PACKAGER}' "$file")" "zexoro e2e <e2e@zexoro.invalid>" "$name: Packager ([linux] maintainer)"
  equal "$(q '%{LICENSE}' "$file")" "MIT" "$name: License ([linux.rpm] license)"
  equal "$(q '%{URL}' "$file")" "https://github.com/zexoro/zexoro" "$name: URL (app.url)"
  equal "$(q '%{SUMMARY}' "$file")" "End-to-end test application" "$name: Summary"
  check "$name: no scriptlets" bash -c '[ -z "$(rpm -qp --scripts "$1" 2> /dev/null)" ]' _ "$file"
  requires="$(rpm -qp --requires "$file" 2> /dev/null | sort | tr '\n' ';')"
  echo "      requires: $requires"
  check "$name: requires coreutils ([linux.rpm] requires)" grep -qF ';coreutils;' <<< ";$requires"
  if [ "$signed" = yes ]; then
    check "$name: requires glibc 2.28 for the updater" grep -qF 'libc.so.6(GLIBC_2.28)(64bit);' <<< "$requires"
    check "$name: requires OpenSSL for the updater" grep -qF '(libssl.so.3()(64bit) or libssl.so.1.1()(64bit));' <<< "$requires"
    equal "$(rpm -qp --recommends "$file" 2> /dev/null)" ca-certificates "$name: recommends ca-certificates"
  else
    check "$name: no updater requirements without [updates]" bash -c '! grep -q "GLIBC\|libssl" <<< "$1"' _ "$requires"
  fi
  check "$name: provides $pkg = $version-1" grep -qx "$pkg = $version-1" <<< "$(rpm -qp --provides "$file" 2> /dev/null)"
  contents="$(rpm -qlvp "$file" 2> /dev/null)"
  echo "$contents" | sed 's/^/      /'
  check "$name: every file is owned by root/root" bash -c '! awk "{print \$3\"/\"\$4}" <<< "$1" | grep -v "^root/root$" | grep -q .' _ "$contents"
  check "$name: only /opt/$pkg and its own files are owned" bash -c '! awk "\$1 ~ /^d/ {print \$NF}" <<< "$1" | grep -v "^/opt/zexoro-e2e" | grep -q .' _ "$contents"
  check "$name: /usr/bin/$pkg links to the main executable" grep -Eq "^lrwxrwxrwx .* /usr/bin/$pkg -> $root/app/FixtureApp\$" <<< "$contents"
  check "$name: app/FixtureApp is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* $root/app/FixtureApp\$" <<< "$contents"
  check "$name: app/FixtureApp.dll is -rw-r--r--" grep -Eq "^-rw-r--r-- .* $root/app/FixtureApp.dll\$" <<< "$contents"
  check "$name: the .desktop file is in /usr/share/applications" grep -Eq "^-rw-r--r-- .* /usr/share/applications/$app_id.desktop\$" <<< "$contents"
  check "$name: the icon is in the hicolor theme" grep -Eq "^-rw-r--r-- .* /usr/share/icons/hicolor/[0-9]+x[0-9]+/apps/$app_id.png\$" <<< "$contents"
  if [ "$signed" = yes ]; then
    check "$name: zexoro-updater is -rwxr-xr-x" grep -Eq "^-rwxr-xr-x .* $root/zexoro-updater\$" <<< "$contents"
  else
    check "$name: no zexoro-updater without [updates]" bash -c '! grep -q "zexoro-updater" <<< "$1"' _ "$contents"
  fi
}

# Signatures before and after importing the key, read by rpm and, independently, by gpg.
signatures() {
  local out
  out="$(rpm -K -v "$v1" 2>&1 || true)"
  echo "$out" | sed 's/^/      /'
  check "rpm -K before the key is imported: the header SHA-256 digest is OK" grep -q 'Header SHA256 digest: OK' <<< "$out"
  check "rpm -K before the key is imported: the payload SHA-256 digest is OK" grep -q 'Payload SHA256 digest: OK' <<< "$out"
  check "rpm -K before the key is imported: an RSA/SHA-256 header signature, not yet trusted (NOKEY)" grep -Eqi "RSA/SHA256 signature.*NOKEY" <<< "$out"
  check "rpm -K names the signing key ${key_id: -8}" grep -qi "${key_id: -8}" <<< "$out"

  rpm --import "$public_key"
  check "rpm --import accepted the public key" bash -c 'rpm -q gpg-pubkey --qf "%{VERSION}\n" | grep -qi "${1: -8}$"' _ "$key_id"
  for file in "$v1" "$v2"; do
    out="$(rpmkeys --checksig -v "$file" 2>&1 || true)"
    echo "$out" | sed 's/^/      /'
    check "rpmkeys --checksig $(basename "$file"): the RSA/SHA-256 header signature is OK" grep -Eqi "RSA/SHA256 signature.*: OK" <<< "$out"
    check "rpmkeys --checksig $(basename "$file"): nothing failed" bash -c '! grep -Eq "BAD|NOT OK|NOKEY" <<< "$1"' _ "$out"
    check "rpm -K $(basename "$file") succeeds" rpm -K "$file"
  done
  out="$(rpm -K -v "$plain" 2>&1 || true)"
  echo "$out" | sed 's/^/      /'
  check "rpm -K of the unsigned package: digests OK, no signature" bash -c 'grep -q "Header SHA256 digest: OK" <<< "$1" && ! grep -qi "signature" <<< "$1"' _ "$out"

  cp "$v1" "$work/tampered.rpm"
  flip_byte "$work/tampered.rpm"
  check "rpm -K rejects the package with one changed byte" bash -c '! rpm -K "$1" > /dev/null 2>&1' _ "$work/tampered.rpm"

  if command -v gpg > /dev/null; then
    mkdir -p "$work/gpg" "$work/split"
    chmod 700 "$work/gpg"
    split_signature "$v1" "$work/split"
    GNUPGHOME="$work/gpg" gpg --batch --quiet --import "$public_key" 2> /dev/null
    out="$(GNUPGHOME="$work/gpg" gpg --batch --verify "$work/split/header.sig" "$work/split/header.bin" 2>&1 || true)"
    echo "$out" | sed 's/^/      gpg: /'
    check "gpg --verify accepts the extracted header signature" grep -q 'Good signature' <<< "$out"
  else
    echo "      gpg is not installed; gpg --verify skipped"
  fi
}

run_rpmlint() {
  local out
  if ! command -v rpmlint > /dev/null; then
    echo "      rpmlint is not installed; skipped"
    return
  fi
  # Findings are reported, not judged here: docs/design/0006-desktop-formats.md lists the ones accepted.
  out="$(rpmlint "$v1" 2>&1 || true)"
  echo "$out" | sed 's/^/      rpmlint: /'
  local errors
  errors="$(grep -E ': E: ' <<< "$out" | sed -E 's/.*: E: ([^ ]+).*/\1/' | sort -u | grep -Ev "$accepted_rpmlint" || true)"
  equal "$errors" "" "rpmlint $(basename "$v1"): no errors besides the accepted ones"
}

# rpmlint errors accepted for zexoro's packages (docs/design/0006-desktop-formats.md, "As implemented
# (step 7)"): third-party packages belong in /opt, which is the point of the layout; zexoro knows
# nothing about the application's history, so it writes no changelog; the e2e signing key is not
# one rpmlint's keyring knows (unknown-key; the signature itself is checked below).
accepted_rpmlint='^(dir-or-file-in-opt|no-changelogname-tag|unknown-key)$'

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
  equal "$(cat "$marker" 2> /dev/null)" "$root/app/" "$1: the application ran from $root/app/"
}

test_installed() { # <version>: the installed package and its files
  local version=$1 record=$root/zexoro-install.json
  check "rpm reports $pkg as installed" installed
  equal "$(rpm -q --qf '%{VERSION}' "$pkg")" "$version" "rpm reports version $version"
  # Fedora 42 and later merged /usr/sbin into /usr/bin (a link) and put it first on root's PATH.
  equal "$(cd "$(dirname "$(command -v "$pkg")")" && pwd -P)/$pkg" "/usr/bin/$pkg" "$pkg is found on PATH in /usr/bin"
  equal "$(readlink /usr/bin/$pkg)" "$root/app/FixtureApp" "/usr/bin/$pkg links to $root/app/FixtureApp"
  equal "$(cd "$root" && ls -A | sort | tr '\n' ' ' | sed 's/ $//')" "app zexoro-install.json zexoro-updater" "$root holds app/, the record and the updater, nothing else"
  equal "$(stat -c '%U %a' "$root/zexoro-updater")" "root 755" "zexoro-updater is owned by root with mode 755"
  equal "$(stat -c '%U %a' "$record")" "root 644" "zexoro-install.json is owned by root with mode 644"
  equal "$(cat "$root/app/version.txt")" "$version" "app/version.txt is $version"
  for field in schema:1 app:$app_id version:$version os:linux arch:$arch format:rpm scope:machine strategy:notify channel:stable main:FixtureApp; do
    equal "$(json "$record" "${field%%:*}")" "${field#*:}" "zexoro-install.json ${field%%:*}"
  done
  equal "$(json "$record" feed)" "http://localhost:8080/rpm/stable/feed.json" "zexoro-install.json feed"
  check "/usr/share/applications/$app_id.desktop runs /usr/bin/$pkg" grep -qx "Exec=/usr/bin/$pkg" /usr/share/applications/$app_id.desktop
  check "the icon is installed" bash -c 'ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png > /dev/null'
  check "rpm -V finds no changed file" bash -c 'out="$(rpm -V "$1" 2>&1)"; [ -z "$out" ] || { echo "$out"; false; }' _ "$pkg"
  run_app "$version"
}

test_gone() { # <context>: nothing of the package is left
  check "$1: rpm no longer knows $pkg" bash -c '! rpm -q "$1" > /dev/null 2>&1' _ "$pkg"
  check "$1: $root is gone" test ! -e "$root"
  check "$1: /usr/bin/$pkg is gone" test ! -e /usr/bin/$pkg -a ! -L /usr/bin/$pkg
  check "$1: the .desktop file is gone" test ! -e /usr/share/applications/$app_id.desktop
  check "$1: the icon is gone" bash -c '! ls /usr/share/icons/hicolor/*/apps/'"$app_id"'.png 2> /dev/null'
}

publish_feed() { cp "$packages/feeds/rpm/$1/feed.json" "$packages/feeds/rpm/$1/feed.json.sig" "$served/"; }

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

# pm_install <file>, pm_upgrade <file>, pm_remove: the distribution's package manager.
pm_install() {
  case $manager in
    dnf) dnf install -y -q "$1" ;;
    zypper) zypper --non-interactive --quiet install "$1" ;;
    *) rpm -i "$1" ;;
  esac
}
pm_upgrade() {
  case $manager in
    dnf) dnf upgrade -y -q "$1" ;;
    zypper) zypper --non-interactive --quiet install "$1" ;;
    *) rpm -U "$1" ;;
  esac
}
pm_remove() {
  case $manager in
    dnf) dnf remove -y -q "$pkg" ;;
    zypper) zypper --non-interactive --quiet remove "$pkg" ;;
    *) rpm -e "$pkg" ;;
  esac
}

echo "== $(rpm --version), $manager, $(. /etc/os-release && echo "$PRETTY_NAME")"
if installed; then
  echo "$pkg is installed already; removing it first"
  rpm -e "$pkg"
fi
remove_key
rm -f "$macros"

echo
echo "== rpm queries and rpmlint"
check "no release fragment for the unsigned package without [updates]" test ! -e "$plain.release.json"
equal "$(python3 -I -c 'import json,sys; a=json.load(open(sys.argv[1]))["release"]["artifacts"][0]; print("/".join([a["os"],a["arch"],a["format"],a["scope"]]))' "$v2.release.json")" \
  "linux/$arch/rpm/machine" "the release fragment describes a linux/$arch/rpm/machine artifact"
inspect "$v1" 1.0.0 yes
inspect "$plain" 1.0.0 no
run_rpmlint

echo
echo "== signatures"
signatures

if [ "$enforce" = enforce ]; then
  echo
  echo "== enforcement refuses the unsigned package"
  echo '%_pkgverify_level all' > "$macros"
  equal "$(rpm --eval '%_pkgverify_level')" all "%_pkgverify_level is 'all' (RPM 6 enforcement)"
  check "rpm -i refuses the unsigned package" bash -c '! rpm -i "$1" > "$2/refused.log" 2>&1' _ "$plain" "$work"
  sed 's/^/      /' "$work/refused.log"
  check "$pkg was not installed" bash -c '! rpm -q "$1" > /dev/null 2>&1' _ "$pkg"
fi

echo
echo "== install 1.0.0 with $manager"
pm_install "$v1" 2>&1 | sed 's/^/      /'
test_installed 1.0.0

python3 -I -m http.server 8080 --directory "$www" > "$work/server.log" 2>&1 &
server=$!
wait_for 15 http_ok "http://localhost:8080/" || { echo "the feed server did not start" >&2; cat "$work/server.log" >&2; exit 1; }
cp "$packages"/rpm/$pkg-*.rpm "$served/"

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
equal "$(json "$work/out.json" url)" "http://localhost:8080/rpm/stable/$(basename "$v2")" "check reports the url of the 2.0.0 package"
equal "$(json "$work/out.json" size)" "$(wc -c < "$v2" | tr -d ' ')" "check reports the size of the 2.0.0 package"

echo "-- replay of the older feed (exit 4)"
publish_feed seq1
updater check 4 error "the validly signed feed with sequence 1 is served after sequence 2" sequence

echo "-- download and apply are left to the package manager (exit 7)"
publish_feed seq2
: > "$work/server.log"
updater download 7 error "the package manager installs updates" "dnf, zypper or rpm"
updater apply 7 error "the package manager installs updates" "dnf, zypper or rpm"
updater apply 7 error "also with --wait-pid and --restart" "dnf, zypper or rpm" --wait-pid $$ --restart
check "nothing was fetched" bash -c '! grep -q "GET " "$1"' _ "$work/server.log"
equal "$(snapshot)" "$before" "the installed files are unchanged (paths, modes, owners, sizes, times, contents)"
check "rpm -V still finds no changed file" bash -c '[ -z "$(rpm -V "$1" 2>&1)" ]' _ "$pkg"
check "nothing was downloaded or staged" test ! -e "$state/downloads"
check "no apply result was written" test ! -e "$state/last-apply.json"
equal "$(ls -A "$TMPDIR" | grep '^zexoro-updater' || true)" "" "no updater copy in \$TMPDIR"

echo
echo "== upgrade to 2.0.0 with $manager"
pm_upgrade "$v2" 2>&1 | sed 's/^/      /'
test_installed 2.0.0
check "app/obsolete.txt is gone" test ! -e "$root/app/obsolete.txt"
updater check 1 up-to-date "2.0.0 is the newest release"
equal "$(json "$work/out.json" installed)" 2.0.0 "check reports installed 2.0.0"

echo
echo "== remove with $manager"
pm_remove 2>&1 | sed 's/^/      /'
test_gone "removed"

echo
echo "== install 1.0.0 and upgrade with rpm, remove with rpm -e"
rpm -i "$v1" 2>&1 | sed 's/^/      /'
check "rpm -i installs 1.0.0" installed
check "1.0.0 has app/obsolete.txt" test -f "$root/app/obsolete.txt"
rpm -U "$v2" 2>&1 | sed 's/^/      /'
equal "$(rpm -q --qf '%{VERSION}' "$pkg")" 2.0.0 "rpm -U upgrades to 2.0.0"
check "rpm refuses the downgrade to 1.0.0" bash -c '! rpm -U "$1" > /dev/null 2>&1' _ "$v1"
rpm -e "$pkg" 2>&1 | sed 's/^/      /'
test_gone "removed with rpm -e"

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ]

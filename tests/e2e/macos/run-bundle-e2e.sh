#!/usr/bin/env bash
# End-to-end test of the macOS zip (docs/design/0006-desktop-formats.md sections 2, 3 and 5) on a
# macOS runner, packed on that runner with the given zexoro:
#
#   tests/e2e/macos/run-bundle-e2e.sh <zexoro executable>
#
# - creates a temporary keychain with a self-signed code-signing certificate (openssl, security),
#   never touching the login keychain, and deletes it at the end (in CI it joins the keychain search
#   list for the run, which codesign on macOS 15 needs; the list is restored);
# - publishes the fixture app framework-dependent for osx-x64 and osx-arm64 and makes a universal
#   build output with lipo;
# - packs the runner's architecture and the universal build, signed with the certificate, hardened
#   runtime and the entitlements a framework-dependent .NET app needs, with [updates];
# - extracts each zip with ditto -x -k (as Finder does) and checks: codesign --verify --deep
#   --strict, the designated requirement names the certificate, Info.plist values, the record in
#   Contents/Resources, the updater's architectures (lipo), data files linked from Contents/MacOS;
#   runs the app directly and through Launch Services (open -W); runs the bundled updater against a
#   local feed (check finds the record and is up to date, apply exits 7 until step 10);
# - packs an ad-hoc signed bundle without [updates] (verifies too) and checks that [updates]
#   without [macos.signing] is refused.
#
# Needs bash, python3 (the feed server), the .NET SDK, openssl, lipo, and a free port 8082.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <zexoro executable>" >&2
  exit 2
fi

cli="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/zexoro-macos-e2e.XXXXXX")"
work="$(cd "$work" && pwd -P)"
keychain="$work/e2e.keychain-db"
port=8082
server=
search_list=
cleanup() {
  [ -n "$server" ] && kill "$server" 2>/dev/null || true
  if [ -n "$search_list" ]; then
    # shellcheck disable=SC2086 # the saved list is one quoted path per word
    eval security list-keychains -d user -s $search_list || true
  fi
  security delete-keychain "$keychain" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

ran="$(dscl . -read "/Users/$(id -un)" NFSHomeDirectory | sed 's/^NFSHomeDirectory: //')/Library/Application Support/ZexoroE2E/ran.txt"
failures=0
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*" >&2; failures=$((failures + 1)); }
check() { local what=$1; shift; if "$@"; then pass "$what"; else fail "$what"; fi; }

case "$(uname -m)" in
  arm64) host_arch=arm64; host_lipo=arm64 ;;
  x86_64) host_arch=x64; host_lipo=x86_64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 2 ;;
esac
echo "== macOS $(sw_vers -productVersion) on $host_arch"

echo "== Publishing the fixture app (framework-dependent, osx-x64 and osx-arm64)"
for rid in osx-x64 osx-arm64; do
  dotnet publish "$here/../fixture-app" -c Release -r "$rid" --self-contained false -o "$work/publish-$rid" -nologo -v quiet
  rm -f "$work/publish-$rid"/*.pdb
  printf '1.0.0\n' > "$work/publish-$rid/version.txt"
  mkdir -p "$work/publish-$rid/data"
  printf 'data\n' > "$work/publish-$rid/data/readme.txt"
done
# A RID-specific publish marks the assembly for that architecture; a universal app needs an AnyCPU one.
dotnet publish "$here/../fixture-app" -c Release -r osx-arm64 --self-contained false -p:PlatformTarget=AnyCPU -o "$work/publish-universal" -nologo -v quiet
rm -f "$work/publish-universal"/*.pdb
cp "$work/publish-osx-arm64/version.txt" "$work/publish-universal/"
cp -R "$work/publish-osx-arm64/data" "$work/publish-universal/"
mkdir -p "$work/input"
cp -R "$work/publish-osx-$host_arch" "$work/input/$host_arch"
cp -R "$work/publish-universal" "$work/input/universal"
lipo -create "$work/publish-osx-x64/FixtureApp" "$work/publish-osx-arm64/FixtureApp" -output "$work/input/universal/FixtureApp"
lipo -archs "$work/input/universal/FixtureApp"

echo "== Creating a temporary keychain with a self-signed code-signing certificate"
(
  cd "$work"
  /usr/bin/openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 2 -nodes -subj "/CN=zexoro e2e codesign" \
    -addext "keyUsage=critical,digitalSignature" -addext "extendedKeyUsage=critical,codeSigning" -addext "basicConstraints=critical,CA:false"
  /usr/bin/openssl pkcs12 -export -inkey key.pem -in cert.pem -out cert.p12 -passout pass:e2e -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1
  rm -f key.pem
)
security create-keychain -p e2e "$keychain"
security set-keychain-settings -lut 7200 "$keychain"
security unlock-keychain -p e2e "$keychain"
security import "$work/cert.p12" -k "$keychain" -P e2e -T /usr/bin/codesign > /dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k e2e "$keychain" > /dev/null
# codesign on macOS 15 finds the private key only in a keychain on the search list (--keychain
# alone is enough on macOS 26 and later), so in CI the keychain joins the user's search list for
# the run; the original list is restored at the end. Local runs leave the search list alone.
if [ -n "${CI:-}" ]; then
  search_list="$(security list-keychains -d user | tr -d ' ' | tr '\n' ' ')"
  # shellcheck disable=SC2086
  eval security list-keychains -d user -s "'$keychain'" $search_list
  security list-keychains -d user
fi
security find-identity -p codesigning "$keychain"
fingerprint="$(/usr/bin/openssl x509 -in "$work/cert.pem" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g' | tr '[:upper:]' '[:lower:]')"

cat > "$work/entitlements.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>com.apple.security.cs.allow-jit</key>
	<true/>
	<key>com.apple.security.cs.disable-library-validation</key>
	<true/>
</dict>
</plist>
PLIST
cp "$here/../fixture-app/app.ico" "$work/app.ico"
"$cli" keys new --output "$work/keys" > /dev/null

# config <signing: yes|no> <updates: yes|no>
config() {
  cat > "$work/zexoro.toml" <<TOML
[app]
id        = "dev.zexoro.e2e.mac"
name      = "Zexoro E2E Mac"
version   = "1.0.0"
publisher = "zexoro e2e"
icon      = "app.ico"

[input]
dir  = "input/$host_arch"
main = "FixtureApp"

[macos]
minimum_version = "12.0"
category        = "public.app-category.developer-tools"
TOML
  if [ "$1" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[macos.signing]
identity     = "zexoro e2e codesign"
keychain     = { env = "E2E_KEYCHAIN" }
entitlements = "entitlements.plist"
TOML
  fi
  if [ "$2" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[updates]
feed     = "http://127.0.0.1:$port/{channel}/feed.json"
download = "http://127.0.0.1:$port/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}
export E2E_KEYCHAIN="$keychain"

echo "== zexoro doctor"
config yes yes
(cd "$work" && "$cli" doctor) | tee "$work/doctor.log" || true
check "doctor finds codesign" grep -q "^ok  *codesign: /usr/bin/codesign" "$work/doctor.log"
check "doctor finds the signing identity" grep -q "^ok  *macos.signing: identity 'zexoro e2e codesign'" "$work/doctor.log"

echo "== Packing (signed, with [updates]): $host_arch and universal"
(cd "$work" && "$cli" pack --output dist)
(cd "$work" && "$cli" pack --output dist --input input/universal)
ls -l "$work/dist"

# inspect <arch> <lipo archs of the updater>
inspect() {
  local arch=$1 archs=$2
  local zip="$work/dist/ZexoroE2EMac-1.0.0-osx-$arch.zip"
  local out="$work/extracted-$arch"
  echo "== Checking $(basename "$zip")"
  check "$arch: zip exists" test -f "$zip"
  check "$arch: release fragment exists" test -f "$zip.release.json"
  check "$arch: release fragment names macos/$arch/app/user" python3 - "$zip.release.json" "$arch" <<'PY'
import json, sys
fragment = json.load(open(sys.argv[1]))
artifact = fragment["release"]["artifacts"][0]
assert (artifact["os"], artifact["arch"], artifact["format"], artifact["scope"]) == ("macos", sys.argv[2], "app", "user"), artifact
PY
  mkdir -p "$out"
  ditto -x -k "$zip" "$out"
  local app="$out/Zexoro E2E Mac.app"
  check "$arch: ditto restores the bundle" test -d "$app"
  check "$arch: codesign --verify --deep --strict" codesign --verify --deep --strict --verbose=2 "$app"
  codesign -d -r- "$app" 2>&1 | tee "$work/requirement-$arch.txt"
  check "$arch: designated requirement names the certificate" grep -q "certificate leaf = H\"$fingerprint\"" "$work/requirement-$arch.txt"
  codesign -d --entitlements - --xml "$app" > "$work/entitlements-$arch.xml" 2>/dev/null || true
  check "$arch: entitlements are applied" grep -q "com.apple.security.cs.allow-jit" "$work/entitlements-$arch.xml"
  codesign -dvv "$app" 2>&1 | tee "$work/codesign-$arch.txt"
  check "$arch: hardened runtime" grep -q "flags=0x10000(runtime)" "$work/codesign-$arch.txt"
  check "$arch: signed with a secure timestamp" grep -q "^Timestamp=" "$work/codesign-$arch.txt"
  check "$arch: updater is signed with the identity" sh -c "codesign -dvv '$app/Contents/MacOS/zexoro-updater' 2>&1 | grep -q 'Authority=zexoro e2e codesign'"
  local plist="$app/Contents/Info.plist"
  check "$arch: CFBundleIdentifier" test "$(plutil -extract CFBundleIdentifier raw "$plist")" = dev.zexoro.e2e.mac
  check "$arch: CFBundleExecutable" test "$(plutil -extract CFBundleExecutable raw "$plist")" = FixtureApp
  check "$arch: CFBundleShortVersionString" test "$(plutil -extract CFBundleShortVersionString raw "$plist")" = 1.0.0
  check "$arch: CFBundleVersion" test "$(plutil -extract CFBundleVersion raw "$plist")" = 1.0.0
  check "$arch: LSMinimumSystemVersion" test "$(plutil -extract LSMinimumSystemVersion raw "$plist")" = 12.0
  check "$arch: LSApplicationCategoryType" test "$(plutil -extract LSApplicationCategoryType raw "$plist")" = public.app-category.developer-tools
  check "$arch: icon" test -f "$app/Contents/Resources/AppIcon.icns"
  local record="$app/Contents/Resources/zexoro-install.json"
  check "$arch: record in Contents/Resources" test -f "$record"
  check "$arch: no record in Contents/MacOS" test ! -e "$app/Contents/MacOS/zexoro-install.json"
  check "$arch: record says macos/$arch/app/user/bundle" python3 - "$record" "$arch" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert (r["app"], r["version"], r["os"], r["arch"], r["format"], r["scope"], r["strategy"], r["main"]) == \
    ("dev.zexoro.e2e.mac", "1.0.0", "macos", sys.argv[2], "app", "user", "bundle", "Contents/MacOS/FixtureApp"), r
PY
  check "$arch: updater architectures are '$archs'" test "$(lipo -archs "$app/Contents/MacOS/zexoro-updater")" = "$archs"
  check "$arch: data file linked from Contents/MacOS" test "$(readlink "$app/Contents/MacOS/FixtureApp.dll")" = ../Resources/app/FixtureApp.dll
  check "$arch: nested data file linked" test "$(cat "$app/Contents/MacOS/data/readme.txt")" = data

  # Run the app directly and through Launch Services. It writes its base directory to ran.txt in
  # ~/Library/Application Support/ZexoroE2E (.NET takes the home directory from the user database, not $HOME).
  local home="$work/home-$arch"
  mkdir -p "$home"
  rm -f "$ran"
  check "$arch: app runs" "$app/Contents/MacOS/FixtureApp"
  check "$arch: app ran from the bundle" grep -qs "$out/Zexoro E2E Mac.app/Contents/Resources/app/" "$ran"
  if [ -n "${CI:-}" ]; then
    rm -f "$ran"
    check "$arch: open -W launches the app" open -W -n "$app"
    check "$arch: Launch Services ran the bundle's app" grep -qs "$out/Zexoro E2E Mac.app/Contents/" "$ran"
  fi

  # The bundled updater finds its record in Contents/Resources: up to date against the feed with 1.0.0.
  rm -rf "$work/served/stable" "$work/feed"
  mkdir -p "$work/served/stable"
  cp "$zip" "$work/served/stable/"
  "$cli" feed add --feed "$work/feed/feed.json" --key "$work/keys/zexoro-update.key.pem" "$zip.release.json" > /dev/null
  cp "$work/feed/feed.json" "$work/feed/feed.json.sig" "$work/served/stable/"
  set +e
  HOME="$home" "$app/Contents/MacOS/zexoro-updater" check --json > "$work/check-$arch.json"
  local status=$?
  HOME="$home" "$app/Contents/MacOS/zexoro-updater" apply > "$work/apply-$arch.log" 2>&1
  local apply_status=$?
  set -e
  cat "$work/check-$arch.json"
  check "$arch: updater check exits 1 (up to date)" test "$status" -eq 1
  check "$arch: updater apply exits 7 (bundle updates come with step 10)" test "$apply_status" -eq 7
}

mkdir -p "$work/served"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$work/served" > "$work/server.log" 2>&1 &
server=$!
for _ in $(seq 1 50); do
  curl -fsS -o /dev/null "http://127.0.0.1:$port/" 2>/dev/null && break
  sleep 0.2
done

inspect "$host_arch" "$host_lipo"
inspect universal "x86_64 arm64"

echo "== Ad-hoc signing without [updates]"
config no no
rm -rf "$work/dist-adhoc"
(cd "$work" && "$cli" pack --output dist-adhoc) | tee "$work/adhoc.log"
check "ad hoc: notice printed" grep -q "signed ad hoc" "$work/adhoc.log"
mkdir -p "$work/extracted-adhoc"
ditto -x -k "$work/dist-adhoc/ZexoroE2EMac-1.0.0-osx-$host_arch.zip" "$work/extracted-adhoc"
check "ad hoc: codesign --verify --deep --strict" codesign --verify --deep --strict "$work/extracted-adhoc/Zexoro E2E Mac.app"
check "ad hoc: no updater" test ! -e "$work/extracted-adhoc/Zexoro E2E Mac.app/Contents/MacOS/zexoro-updater"
check "ad hoc: app runs" "$work/extracted-adhoc/Zexoro E2E Mac.app/Contents/MacOS/FixtureApp"

echo "== [updates] without [macos.signing] is refused"
config no yes
set +e
(cd "$work" && "$cli" pack --output dist-refused) > "$work/refused.log" 2>&1
refused=$?
set -e
cat "$work/refused.log"
check "refused: pack fails" test "$refused" -ne 0
check "refused: says why" grep -q "\[updates\] needs \[macos.signing\]" "$work/refused.log"
check "refused: no zip written" test ! -e "$work/dist-refused/ZexoroE2EMac-1.0.0-osx-$host_arch.zip"

if [ "$failures" -ne 0 ]; then
  echo "$failures check(s) failed" >&2
  exit 1
fi
echo "All macOS bundle checks passed."

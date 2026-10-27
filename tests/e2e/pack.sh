#!/usr/bin/env bash
# Packs the end-to-end installers with a zexoro binary, on Linux, macOS or Windows (Git Bash).
#
#   tests/e2e/pack.sh <zexoro executable> <output directory>
#
# Output (the layout tests/e2e/windows/Run-E2E.ps1 expects):
#   user/ZexoroE2EUser-{1.0.0,2.0.0}-win-x64.exe        per-user, signed, with [updates]
#   machine/ZexoroE2EMachine-{1.0.0,2.0.0}-win-x64.exe  per-machine, signed, with [updates]
#   portable/ZexoroE2EPortable-{1.0.0,2.0.0}-win-x64.zip portable zip, signed, with [updates]
#   {user,machine}/*.exe.release.json, portable/*.zip.release.json   their release fragments
#   unsigned/ZexoroE2EUser-1.0.0-win-x64.{exe,zip}       per-user installer and zip, no [windows.signing], no [updates]
#   repro/ZexoroE2ERepro-1.0.0-win-x64.zip               a zip of fixed files; its bytes must be the same on every pack host
#   root.cer                                            the throwaway root that issued the signing certificate (DER), to trust while testing
#   zexoro-update.pub                                   the update public key the installers trust
#   zexoro-update.key.pem                               its (throwaway) private key, to publish more feeds while testing
#   feeds/{user,machine,portable}/seq1/feed.json{,.sig}       signed feed with 1.0.0 only (sequence 1)
#   feeds/{user,machine,portable}/seq2/feed.json{,.sig}       signed feed with 1.0.0 and 2.0.0 (sequence 2)
#   feeds/{user,machine,portable}/other-key/feed.json{,.sig}  like seq2, but signed with a key the installs do not trust
#
# Version 1.0.0 of the fixture contains obsolete.txt, which 2.0.0 drops. Both contain version.txt.
# The packages' [updates] point at http://localhost:8080/<kind>/stable/, where
# tests/e2e/windows/Run-UpdateE2E.ps1 (user, machine) and Run-ZipE2E.ps1 (portable) serve the
# feeds and packages.
set -euo pipefail

if [ $# -ne 2 ]; then
  echo "usage: $0 <zexoro executable> <output directory>" >&2
  exit 2
fi

cli="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
mkdir -p "$2"
out="$(cd "$2" && pwd)"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== Publishing the fixture app (framework-dependent, win-x64)"
dotnet publish "$here/fixture-app" -c Release -r win-x64 --self-contained false -o "$work/publish" -nologo -v quiet
for version in 1.0.0 2.0.0; do
  mkdir -p "$work/input/$version"
  cp -R "$work/publish/." "$work/input/$version/"
  rm -f "$work/input/$version"/*.pdb
  printf '%s\n' "$version" > "$work/input/$version/version.txt"
done
printf 'removed by version 2.0.0\n' > "$work/input/1.0.0/obsolete.txt"
cp "$here/fixture-app/app.ico" "$work/app.ico"

echo "== Creating a throwaway code-signing certificate"
# A root CA and a code-signing leaf issued by it: Windows does not accept a self-signed CA
# certificate as the signer itself. Relative paths and MSYS_NO_PATHCONV: Git Bash on Windows would
# otherwise rewrite the subjects "/CN=..." into paths, and a native openssl.exe cannot open MSYS
# paths such as /tmp/....
(
  cd "$work"
  export MSYS_NO_PATHCONV=1
  openssl req -x509 -newkey rsa:2048 -keyout root.key -out root.pem -days 7 -nodes -subj "/CN=zexoro e2e throwaway root"
  openssl req -newkey rsa:2048 -keyout key.pem -out signer.csr -nodes -subj "/CN=zexoro e2e throwaway signer"
  printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=codeSigning\n' > signer.ext
  openssl x509 -req -in signer.csr -CA root.pem -CAkey root.key -set_serial 2 -days 7 -extfile signer.ext -out signer.pem
  openssl pkcs12 -export -inkey key.pem -in signer.pem -certfile root.pem -out e2e.pfx -passout pass:e2e-password
  openssl x509 -in root.pem -outform der -out root.cer
  rm -f key.pem root.key
)
cp "$work/root.cer" "$out/root.cer"

echo "== Creating an update key pair"
"$cli" keys new --output "$work/keys"
cp "$work/keys/zexoro-update.pub" "$out/zexoro-update.pub"

# config <kind: user|machine|portable> <version> <signed: yes|no>: writes $work/zexoro.toml; signed
# packages also get [updates]. user and machine are NSIS installers of that scope, portable a zip.
config() {
  local scope=$1 version=$2 signed=$3 title windows
  title="$(printf '%s' "${scope:0:1}" | tr '[:lower:]' '[:upper:]')${scope:1}"
  if [ "$scope" = portable ]; then
    windows='formats = ["zip"]'
  else
    windows="install_scope = \"$scope\""
  fi
  cat > "$work/zexoro.toml" <<TOML
[app]
id        = "dev.zexoro.e2e.$scope"
name      = "Zexoro E2E $title"
version   = "$version"
publisher = "zexoro e2e"
icon      = "app.ico"

[input]
dir  = "input/$version"
main = "FixtureApp.exe"

[windows]
$windows
shortcuts     = ["start-menu"]

[windows.uninstall]
remove = ['%LOCALAPPDATA%\ZexoroE2E\cache']
TOML
  if [ "$signed" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[windows.signing]
certificate = { env = "E2E_CERT_PATH" }
password    = { env = "E2E_CERT_PASSWORD" }

[updates]
feed     = "http://localhost:8080/$scope/{channel}/feed.json"
download = "http://localhost:8080/$scope/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}

# The certificate path is relative to the configuration, so Windows and Git Bash agree on it.
export E2E_CERT_PATH=e2e.pfx
export E2E_CERT_PASSWORD=e2e-password

for scope in user machine portable; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing $scope $version (signed)"
    config "$scope" "$version" yes
    (cd "$work" && "$cli" pack --output "$out/$scope")
  done
done

echo "== Packing user 1.0.0 (unsigned, installer and zip)"
config user 1.0.0 no
(cd "$work" && "$cli" pack --format nsis,zip --output "$out/unsigned") | tee "$work/unsigned.log"
grep -q "notice: \[windows.signing\] is not configured; the installer" "$work/unsigned.log"
grep -q "notice: \[windows.signing\] is not configured; the application in the zip" "$work/unsigned.log"

echo "== Packing a zip of fixed files (reproducibility across pack hosts)"
mkdir -p "$work/repro/bin/empty"
printf '@echo off\r\necho repro\r\n' > "$work/repro/bin/run.cmd"
printf 'fixed content %s\n' $(seq 1 200) > "$work/repro/bin/data.txt"
cp "$here/fixture-app/app.ico" "$work/repro/bin/app.ico"
cat > "$work/repro/zexoro.toml" <<'TOML'
[app]
id        = "dev.zexoro.e2e.repro"
name      = "Zexoro E2E Repro"
version   = "1.0.0"
publisher = "zexoro e2e"
icon      = "bin/app.ico"

[input]
dir    = "bin"
main   = "run.cmd"
target = "win-x64"

[windows]
formats = ["zip"]
TOML
(cd "$work/repro" && "$cli" pack --output "$out/repro")

echo "== Installers"
(cd "$out" && find . -type f | sort)
for f in user/ZexoroE2EUser-1.0.0-win-x64.exe user/ZexoroE2EUser-2.0.0-win-x64.exe machine/ZexoroE2EMachine-1.0.0-win-x64.exe \
  machine/ZexoroE2EMachine-2.0.0-win-x64.exe portable/ZexoroE2EPortable-1.0.0-win-x64.zip portable/ZexoroE2EPortable-2.0.0-win-x64.zip; do
  test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
  test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
done
for f in unsigned/ZexoroE2EUser-1.0.0-win-x64.exe unsigned/ZexoroE2EUser-1.0.0-win-x64.zip repro/ZexoroE2ERepro-1.0.0-win-x64.zip; do
  test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
done
if ls "$out"/portable/*.exe >/dev/null 2>&1; then
  echo "formats = [\"zip\"] also built an installer" >&2
  exit 1
fi
if ls "$out"/unsigned/*.release.json "$out"/repro/*.release.json >/dev/null 2>&1; then
  echo "a package without [updates] got a release fragment" >&2
  exit 1
fi

echo "== Publishing signed feeds (one per application)"
"$cli" keys new --output "$work/keys" --name other
cp "$work/keys/zexoro-update.key.pem" "$out/zexoro-update.key.pem"
for scope in user machine portable; do
  title="$(printf '%s' "${scope:0:1}" | tr '[:lower:]' '[:upper:]')${scope:1}"
  extension=$([ "$scope" = portable ] && echo zip || echo exe)
  v1="$out/$scope/ZexoroE2E$title-1.0.0-win-x64.$extension.release.json"
  v2="$out/$scope/ZexoroE2E$title-2.0.0-win-x64.$extension.release.json"
  feed="$work/feed/$scope/feed.json"
  # The same feed file twice: 1.0.0 alone, then 2.0.0 added, which raises the sequence.
  "$cli" feed add --feed "$feed" --key "$work/keys/zexoro-update.key.pem" "$v1"
  mkdir -p "$out/feeds/$scope/seq1"
  cp "$feed" "$feed.sig" "$out/feeds/$scope/seq1/"
  "$cli" feed add --feed "$feed" --key "$work/keys/zexoro-update.key.pem" "$v2"
  mkdir -p "$out/feeds/$scope/seq2"
  cp "$feed" "$feed.sig" "$out/feeds/$scope/seq2/"
  "$cli" feed add --feed "$work/other/$scope/feed.json" --key "$work/keys/other.key.pem" "$v1" "$v2"
  mkdir -p "$out/feeds/$scope/other-key"
  cp "$work/other/$scope/feed.json" "$work/other/$scope/feed.json.sig" "$out/feeds/$scope/other-key/"
  for variant in seq1 seq2; do
    "$cli" feed verify --public-key "$work/keys/zexoro-update.pub" "$out/feeds/$scope/$variant/feed.json"
  done
  grep -q '"sequence": 1,' "$out/feeds/$scope/seq1/feed.json"
  grep -q '"sequence": 2,' "$out/feeds/$scope/seq2/feed.json"
  if "$cli" feed verify --public-key "$work/keys/zexoro-update.pub" "$out/feeds/$scope/other-key/feed.json" >/dev/null 2>&1; then
    echo "a feed signed with another key verified" >&2
    exit 1
  fi
done

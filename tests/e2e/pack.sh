#!/usr/bin/env bash
# Packs the end-to-end installers with a zexoro binary, on Linux, macOS or Windows (Git Bash).
#
#   tests/e2e/pack.sh <zexoro executable> <output directory>
#
# Output (the layout tests/e2e/windows/Run-E2E.ps1 expects):
#   user/ZexoroE2EUser-{1.0.0,2.0.0}-win-x64.exe        per-user, signed
#   machine/ZexoroE2EMachine-{1.0.0,2.0.0}-win-x64.exe  per-machine, signed
#   unsigned/ZexoroE2EUser-1.0.0-win-x64.exe            per-user, no [windows.signing]
#   root.cer                                            the throwaway root that issued the signing certificate (DER), to trust while testing
#
# Version 1.0.0 of the fixture contains obsolete.txt, which 2.0.0 drops. Both contain version.txt.
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

# config <scope> <version> <signed: yes|no>: writes $work/zexoro.toml
config() {
  local scope=$1 version=$2 signed=$3 title
  title="$(printf '%s' "${scope:0:1}" | tr '[:lower:]' '[:upper:]')${scope:1}"
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
install_scope = "$scope"
shortcuts     = ["start-menu"]

[windows.uninstall]
remove = ['%LOCALAPPDATA%\ZexoroE2E\cache']
TOML
  if [ "$signed" = yes ]; then
    cat >> "$work/zexoro.toml" <<'TOML'

[windows.signing]
certificate = { env = "E2E_CERT_PATH" }
password    = { env = "E2E_CERT_PASSWORD" }
TOML
  fi
}

# The certificate path is relative to the configuration, so Windows and Git Bash agree on it.
export E2E_CERT_PATH=e2e.pfx
export E2E_CERT_PASSWORD=e2e-password

for scope in user machine; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing $scope $version (signed)"
    config "$scope" "$version" yes
    (cd "$work" && "$cli" pack --output "$out/$scope")
  done
done

echo "== Packing user 1.0.0 (unsigned)"
config user 1.0.0 no
(cd "$work" && "$cli" pack --output "$out/unsigned") | tee "$work/unsigned.log"
grep -q "notice: \[windows.signing\] is not configured" "$work/unsigned.log"

echo "== Installers"
(cd "$out" && find . -type f | sort)
for f in user/ZexoroE2EUser-1.0.0 user/ZexoroE2EUser-2.0.0 machine/ZexoroE2EMachine-1.0.0 machine/ZexoroE2EMachine-2.0.0 unsigned/ZexoroE2EUser-1.0.0; do
  test -f "$out/$f-win-x64.exe" || { echo "missing $f-win-x64.exe" >&2; exit 1; }
done

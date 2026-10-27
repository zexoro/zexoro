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
#   repro/ZexoroE2ERepro-1.0.0-linux-x64.tar.gz          a tar.gz of the same files; likewise
#   linux/ZexoroE2ETar-{1.0.0,2.0.0}-linux-{x64,arm64}.tar.gz  portable tar.gz with [updates] (and *.tar.gz.release.json)
#   linux/no-updates/ZexoroE2ETar-1.0.0-linux-{x64,arm64}.tar.gz  portable tar.gz without [updates]
#   deb/zexoro-e2e_{1.0.0,2.0.0}_{amd64,arm64}.deb       deb packages with [updates] (and *.deb.release.json)
#   deb/no-updates/zexoro-e2e_1.0.0_{amd64,arm64}.deb    deb packages without [updates]
#   repro/repro_1.0.0_amd64.deb                         a deb of the same files; likewise
#   rpm/zexoro-e2e-{1.0.0,2.0.0}-1.{x86_64,aarch64}.rpm  rpm packages signed with rpm-signing.key.asc, with [updates] (and *.rpm.release.json)
#   rpm/unsigned/zexoro-e2e-1.0.0-1.{x86_64,aarch64}.rpm unsigned rpm packages without [updates]
#   repro/repro-1.0.0-1.x86_64.rpm                      a signed rpm of the same files (SOURCE_DATE_EPOCH fixes the signature time); likewise
#   rpm-signing.pub.asc                                 the OpenPGP public key the rpm packages are signed with
#   apk/zexoro-e2e-{1.0.0,2.0.0}-r0.{x86_64,aarch64}.apk apk packages (musl fixture app) signed with apk-signing/zexoro-e2e.rsa, with [updates] (and *.apk.release.json)
#   apk/unsigned/zexoro-e2e-1.0.0-r0.{x86_64,aarch64}.apk unsigned apk packages without [updates]
#   repro/repro-1.0.0-r0.x86_64.apk                     a signed apk of the same files; likewise
#   zexoro-e2e.rsa.pub                                  the RSA public key the apk packages are signed with (for /etc/apk/keys)
#   linux/links                                         present when the tar.gz inputs hold a symbolic link
#                                                       (app/bin/version.txt -> ../version.txt; not on Windows hosts)
#   root.cer                                            the throwaway root that issued the signing certificate (DER), to trust while testing
#   zexoro-update.pub                                   the update public key the installers trust
#   zexoro-update.key.pem                               its (throwaway) private key, to publish more feeds while testing
#   feeds/{user,machine,portable,tar,deb,rpm,apk}/seq1/feed.json{,.sig}       signed feed with 1.0.0 only (sequence 1)
#   feeds/{user,machine,portable,tar,deb,rpm,apk}/seq2/feed.json{,.sig}       signed feed with 1.0.0 and 2.0.0 (sequence 2)
#   feeds/{user,machine,portable,tar,deb,rpm,apk}/other-key/feed.json{,.sig}  like seq2, but signed with a key the installs do not trust
#   (feeds/tar/*, feeds/deb/*, feeds/rpm/* and feeds/apk/* list the x64 and arm64 packages of each version)
#
# Version 1.0.0 of the fixture contains obsolete.txt, which 2.0.0 drops. Both contain version.txt.
# The packages' [updates] point at http://localhost:8080/<kind>/stable/, where
# tests/e2e/windows/Run-UpdateE2E.ps1 (user, machine), Run-ZipE2E.ps1 (portable) and
# tests/e2e/unix/run-tar-e2e.sh (tar), run-deb-e2e.sh (deb), run-rpm-e2e.sh (rpm) and run-apk-e2e.sh
# (apk) serve the feeds and packages. The rpm and apk signing keys are committed throwaway keys
# (tests/e2e/rpm-signing/, password e2e-password; tests/e2e/apk-signing/), so every pack host signs
# with the same keys.
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

# Git Bash on Windows cannot be relied on to create symbolic links, so only other hosts add one.
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) links=no ;;
  *) links=yes ;;
esac

echo "== Publishing the fixture app (framework-dependent, linux-x64, linux-arm64, linux-musl-x64 and linux-musl-arm64)"
for rid in linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64; do
  dotnet publish "$here/fixture-app" -c Release -r "$rid" --self-contained false -o "$work/publish-$rid" -nologo -v quiet
  for version in 1.0.0 2.0.0; do
    dir="$work/input-$rid/$version"
    mkdir -p "$dir/bin"
    cp -R "$work/publish-$rid/." "$dir/"
    rm -f "$dir"/*.pdb
    printf '%s\n' "$version" > "$dir/version.txt"
    # A script without an execute bit on disk: the archive must make it executable (#!).
    printf '#!/bin/sh\necho "helper %s"\n' "$version" > "$dir/bin/helper.sh"
    chmod 644 "$dir/bin/helper.sh"
    if [ "$links" = yes ]; then ln -s ../version.txt "$dir/bin/version.txt"; fi
  done
  printf 'removed by version 2.0.0\n' > "$work/input-$rid/1.0.0/obsolete.txt"
done

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

# tar_config <version> <arch> <with updates: yes|no>: writes $work/zexoro.toml for the portable tar.gz.
tar_config() {
  local version=$1 arch=$2 updates=$3
  cat > "$work/zexoro.toml" <<TOML
[app]
id        = "dev.zexoro.e2e.tar"
name      = "Zexoro E2E Tar"
version   = "$version"
publisher = "zexoro e2e"
icon      = "app.ico"

[input]
dir  = "input-linux-$arch/$version"
main = "FixtureApp"

[linux]
formats    = ["tar.gz"]
categories = ["Utility"]
TOML
  if [ "$updates" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[updates]
feed     = "http://localhost:8080/tar/{channel}/feed.json"
download = "http://localhost:8080/tar/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}

for arch in x64 arm64; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing tar.gz $version linux-$arch"
    tar_config "$version" "$arch" yes
    (cd "$work" && "$cli" pack --output "$out/linux")
  done
  echo "== Packing tar.gz 1.0.0 linux-$arch without [updates]"
  tar_config 1.0.0 "$arch" no
  (cd "$work" && "$cli" pack --output "$out/linux/no-updates")
done
if [ "$links" = yes ]; then touch "$out/linux/links"; fi

# deb_config <version> <arch> <with updates: yes|no>: writes $work/zexoro.toml for the deb package.
deb_config() {
  local version=$1 arch=$2 updates=$3
  cat > "$work/zexoro.toml" <<TOML
[app]
id          = "dev.zexoro.e2e.deb"
name        = "Zexoro E2E Deb"
version     = "$version"
publisher   = "zexoro e2e"
icon        = "app.ico"
description = "End-to-end test application\nPackaged by tests/e2e/pack.sh to test zexoro's deb packages."
url         = "https://github.com/zexoro/zexoro"

[input]
dir  = "input-linux-$arch/$version"
main = "FixtureApp"

[linux]
formats    = ["deb"]
package    = "zexoro-e2e"
maintainer = "zexoro e2e <e2e@zexoro.invalid>"
categories = ["Utility"]

[linux.deb]
depends = ["coreutils (>= 8.0)"]
TOML
  if [ "$updates" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[updates]
feed     = "http://localhost:8080/deb/{channel}/feed.json"
download = "http://localhost:8080/deb/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}

for arch in x64 arm64; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing deb $version linux-$arch"
    deb_config "$version" "$arch" yes
    (cd "$work" && "$cli" pack --output "$out/deb")
  done
  echo "== Packing deb 1.0.0 linux-$arch without [updates]"
  deb_config 1.0.0 "$arch" no
  (cd "$work" && "$cli" pack --output "$out/deb/no-updates")
done

# rpm_config <version> <arch> <signed with [updates]: yes|no>: writes $work/zexoro.toml for the rpm package.
rpm_config() {
  local version=$1 arch=$2 signed=$3
  cat > "$work/zexoro.toml" <<TOML
[app]
id          = "dev.zexoro.e2e.rpm"
name        = "Zexoro E2E Rpm"
version     = "$version"
publisher   = "zexoro e2e"
icon        = "app.ico"
description = "End-to-end test application\nPackaged by tests/e2e/pack.sh to test zexoro's rpm packages."
url         = "https://github.com/zexoro/zexoro"

[input]
dir  = "input-linux-$arch/$version"
main = "FixtureApp"

[linux]
formats    = ["rpm"]
package    = "zexoro-e2e"
maintainer = "zexoro e2e <e2e@zexoro.invalid>"
categories = ["Utility"]

[linux.rpm]
requires = ["coreutils"]
license  = "MIT"
TOML
  if [ "$signed" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[linux.rpm.signing]
key      = { env = "E2E_RPM_KEY" }
password = { env = "E2E_RPM_PASSWORD" }

[updates]
feed     = "http://localhost:8080/rpm/{channel}/feed.json"
download = "http://localhost:8080/rpm/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}

E2E_RPM_KEY="$(cat "$here/rpm-signing/rpm-signing.key.asc")"
export E2E_RPM_KEY E2E_RPM_PASSWORD=e2e-password
cp "$here/rpm-signing/rpm-signing.pub.asc" "$out/rpm-signing.pub.asc"
for arch in x64 arm64; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing rpm $version linux-$arch (signed)"
    rpm_config "$version" "$arch" yes
    (cd "$work" && "$cli" pack --output "$out/rpm")
  done
  echo "== Packing rpm 1.0.0 linux-$arch (unsigned, without [updates])"
  rpm_config 1.0.0 "$arch" no
  (cd "$work" && "$cli" pack --output "$out/rpm/unsigned") | tee "$work/rpm-unsigned.log"
  grep -q "notice: \[linux.rpm.signing\] is not configured; the rpm is unsigned" "$work/rpm-unsigned.log"
done

# apk_config <version> <arch> <signed with [updates]: yes|no>: writes $work/zexoro.toml for the apk package.
apk_config() {
  local version=$1 arch=$2 signed=$3
  cat > "$work/zexoro.toml" <<TOML
[app]
id          = "dev.zexoro.e2e.apk"
name        = "Zexoro E2E Apk"
version     = "$version"
publisher   = "zexoro e2e"
icon        = "app.ico"
description = "End-to-end test application\nPackaged by tests/e2e/pack.sh to test zexoro's apk packages."
url         = "https://github.com/zexoro/zexoro"

[input]
dir  = "input-linux-musl-$arch/$version"
main = "FixtureApp"

[linux]
formats    = ["apk"]
package    = "zexoro-e2e"
maintainer = "zexoro e2e <e2e@zexoro.invalid>"
categories = ["Utility"]

[linux.apk]
depends = ["busybox"]
license = "MIT"
TOML
  if [ "$signed" = yes ]; then
    cat >> "$work/zexoro.toml" <<TOML

[linux.apk.signing]
key      = { env = "E2E_APK_KEY" }
key_name = "zexoro-e2e.rsa.pub"

[updates]
feed     = "http://localhost:8080/apk/{channel}/feed.json"
download = "http://localhost:8080/apk/{channel}/{file}"
keys     = [{ file = "keys/zexoro-update.pub" }]
TOML
  fi
}

E2E_APK_KEY="$(cat "$here/apk-signing/zexoro-e2e.rsa")"
export E2E_APK_KEY
cp "$here/apk-signing/zexoro-e2e.rsa.pub" "$out/zexoro-e2e.rsa.pub"
for arch in x64 arm64; do
  for version in 1.0.0 2.0.0; do
    echo "== Packing apk $version linux-musl-$arch (signed)"
    apk_config "$version" "$arch" yes
    (cd "$work" && "$cli" pack --output "$out/apk")
  done
  echo "== Packing apk 1.0.0 linux-musl-$arch (unsigned, without [updates])"
  apk_config 1.0.0 "$arch" no
  (cd "$work" && "$cli" pack --output "$out/apk/unsigned") | tee "$work/apk-unsigned.log"
  grep -q "notice: \[linux.apk.signing\] is not configured; the apk is unsigned" "$work/apk-unsigned.log"
done

echo "== Packing a zip and a tar.gz of fixed files (reproducibility across pack hosts)"
mkdir -p "$work/repro/bin/empty"
printf '@echo off\r\necho repro\r\n' > "$work/repro/bin/run.cmd"
printf '#!/bin/sh\necho repro\n' > "$work/repro/bin/run.sh"
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
sed -e 's/main   = "run.cmd"/main   = "run.sh"/' -e 's/target = "win-x64"/target = "linux-x64"/' -e 's/^\[windows\]$/[linux]/' -e 's/formats = \["zip"\]/formats = ["tar.gz"]/' \
  "$work/repro/zexoro.toml" > "$work/repro/linux.toml"
grep -q 'target = "linux-x64"' "$work/repro/linux.toml"
(cd "$work/repro" && "$cli" pack --config linux.toml --output "$out/repro")
(cd "$work/repro" && "$cli" pack --config linux.toml --format deb --output "$out/repro")
# A signed rpm: the committed key and a fixed signature time make its bytes host-independent too.
cp "$here/rpm-signing/rpm-signing.key.asc" "$work/repro/rpm-signing.key.asc"
printf '\n[linux.rpm.signing]\nkey      = "rpm-signing.key.asc"\npassword = "e2e-password"\n' >> "$work/repro/linux.toml"
(cd "$work/repro" && SOURCE_DATE_EPOCH=1780000000 "$cli" pack --config linux.toml --format rpm --output "$out/repro")
# A signed apk: RSA PKCS #1 v1.5 signatures are deterministic, so no time needs fixing.
cp "$here/apk-signing/zexoro-e2e.rsa" "$work/repro/zexoro-e2e.rsa"
printf '\n[linux.apk.signing]\nkey      = "zexoro-e2e.rsa"\nkey_name = "zexoro-e2e.rsa.pub"\n' >> "$work/repro/linux.toml"
(cd "$work/repro" && "$cli" pack --config linux.toml --format apk --output "$out/repro")

echo "== Installers"
(cd "$out" && find . -type f | sort)
for f in user/ZexoroE2EUser-1.0.0-win-x64.exe user/ZexoroE2EUser-2.0.0-win-x64.exe machine/ZexoroE2EMachine-1.0.0-win-x64.exe \
  machine/ZexoroE2EMachine-2.0.0-win-x64.exe portable/ZexoroE2EPortable-1.0.0-win-x64.zip portable/ZexoroE2EPortable-2.0.0-win-x64.zip; do
  test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
  test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
done
for arch in x64 arm64; do
  for version in 1.0.0 2.0.0; do
    f="linux/ZexoroE2ETar-$version-linux-$arch.tar.gz"
    test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
    test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
  done
done
for debarch in amd64 arm64; do
  for version in 1.0.0 2.0.0; do
    f="deb/zexoro-e2e_${version}_$debarch.deb"
    test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
    test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
  done
  test -f "$out/deb/no-updates/zexoro-e2e_1.0.0_$debarch.deb" || { echo "missing deb/no-updates/zexoro-e2e_1.0.0_$debarch.deb" >&2; exit 1; }
done
for rpmarch in x86_64 aarch64; do
  for version in 1.0.0 2.0.0; do
    f="rpm/zexoro-e2e-$version-1.$rpmarch.rpm"
    test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
    test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
  done
  test -f "$out/rpm/unsigned/zexoro-e2e-1.0.0-1.$rpmarch.rpm" || { echo "missing rpm/unsigned/zexoro-e2e-1.0.0-1.$rpmarch.rpm" >&2; exit 1; }
done
for apkarch in x86_64 aarch64; do
  for version in 1.0.0 2.0.0; do
    f="apk/zexoro-e2e-$version-r0.$apkarch.apk"
    test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
    test -f "$out/$f.release.json" || { echo "missing $f.release.json" >&2; exit 1; }
  done
  test -f "$out/apk/unsigned/zexoro-e2e-1.0.0-r0.$apkarch.apk" || { echo "missing apk/unsigned/zexoro-e2e-1.0.0-r0.$apkarch.apk" >&2; exit 1; }
done
for f in repro/repro-1.0.0-r0.x86_64.apk repro/repro-1.0.0-1.x86_64.rpm repro/repro_1.0.0_amd64.deb unsigned/ZexoroE2EUser-1.0.0-win-x64.exe unsigned/ZexoroE2EUser-1.0.0-win-x64.zip repro/ZexoroE2ERepro-1.0.0-win-x64.zip \
  repro/ZexoroE2ERepro-1.0.0-linux-x64.tar.gz linux/no-updates/ZexoroE2ETar-1.0.0-linux-x64.tar.gz linux/no-updates/ZexoroE2ETar-1.0.0-linux-arm64.tar.gz; do
  test -f "$out/$f" || { echo "missing $f" >&2; exit 1; }
done
if ls "$out"/portable/*.exe >/dev/null 2>&1; then
  echo "formats = [\"zip\"] also built an installer" >&2
  exit 1
fi
if ls "$out"/unsigned/*.release.json "$out"/repro/*.release.json "$out"/linux/no-updates/*.release.json "$out"/deb/no-updates/*.release.json "$out"/rpm/unsigned/*.release.json "$out"/apk/unsigned/*.release.json >/dev/null 2>&1; then
  echo "a package without [updates] got a release fragment" >&2
  exit 1
fi

echo "== Publishing signed feeds (one per application)"
"$cli" keys new --output "$work/keys" --name other
cp "$work/keys/zexoro-update.key.pem" "$out/zexoro-update.key.pem"
for scope in user machine portable tar deb rpm apk; do
  title="$(printf '%s' "${scope:0:1}" | tr '[:lower:]' '[:upper:]')${scope:1}"
  if [ "$scope" = tar ]; then
    # Both architectures of each version: one release with two artifacts.
    v1=("$out"/linux/ZexoroE2ETar-1.0.0-linux-*.tar.gz.release.json)
    v2=("$out"/linux/ZexoroE2ETar-2.0.0-linux-*.tar.gz.release.json)
  elif [ "$scope" = deb ]; then
    v1=("$out"/deb/zexoro-e2e_1.0.0_*.deb.release.json)
    v2=("$out"/deb/zexoro-e2e_2.0.0_*.deb.release.json)
  elif [ "$scope" = rpm ]; then
    v1=("$out"/rpm/zexoro-e2e-1.0.0-1.*.rpm.release.json)
    v2=("$out"/rpm/zexoro-e2e-2.0.0-1.*.rpm.release.json)
  elif [ "$scope" = apk ]; then
    v1=("$out"/apk/zexoro-e2e-1.0.0-r0.*.apk.release.json)
    v2=("$out"/apk/zexoro-e2e-2.0.0-r0.*.apk.release.json)
  else
    extension=$([ "$scope" = portable ] && echo zip || echo exe)
    v1=("$out/$scope/ZexoroE2E$title-1.0.0-win-x64.$extension.release.json")
    v2=("$out/$scope/ZexoroE2E$title-2.0.0-win-x64.$extension.release.json")
  fi
  feed="$work/feed/$scope/feed.json"
  # The same feed file twice: 1.0.0 alone, then 2.0.0 added, which raises the sequence.
  "$cli" feed add --feed "$feed" --key "$work/keys/zexoro-update.key.pem" "${v1[@]}"
  mkdir -p "$out/feeds/$scope/seq1"
  cp "$feed" "$feed.sig" "$out/feeds/$scope/seq1/"
  "$cli" feed add --feed "$feed" --key "$work/keys/zexoro-update.key.pem" "${v2[@]}"
  mkdir -p "$out/feeds/$scope/seq2"
  cp "$feed" "$feed.sig" "$out/feeds/$scope/seq2/"
  "$cli" feed add --feed "$work/other/$scope/feed.json" --key "$work/keys/other.key.pem" "${v1[@]}" "${v2[@]}"
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

#!/usr/bin/env bash
# Publishes zexoro-updater for a Linux runtime identifier inside a container of an old distribution,
# so the binary runs on every distribution .NET 10 supports (docs/design/0006-desktop-formats.md
# section 7, "glibc floor"). Native AOT links against the C library of the build system: a binary
# built on a new glibc refuses to start on an older one.
#
#   eng/updater/publish-in-container.sh <rid> <output directory>
#
# - glibc (linux-x64, linux-arm64): AlmaLinux 8 (glibc 2.28, the RHEL 8 family), the oldest glibc
#   distribution .NET 10 supports; the build fails if the binary needs anything newer than 2.28.
# - musl (linux-musl-x64, linux-musl-arm64): Alpine 3.21, the oldest Alpine .NET 10 supports.
#
# The container's architecture is the host's: run it on an x64 runner for -x64, on an arm64 runner
# for -arm64. Needs docker. VERSION is passed on to eng/updater/publish.sh.
set -euo pipefail

if [ $# -ne 2 ]; then
  echo "usage: $0 <rid> <output directory>" >&2
  exit 2
fi
rid=$1
out=$2

case "$rid" in
  linux-musl-x64 | linux-musl-arm64)
    image=alpine:3.21
    setup='apk add --no-cache bash curl icu-libs libstdc++ libgcc clang lld build-base zlib-dev binutils'
    glibc_max=
    sdk=dotnet-musl
    ;;
  linux-x64 | linux-arm64)
    image=almalinux:8
    setup='dnf install -y -q --setopt=install_weak_deps=False clang lld zlib-devel libicu binutils findutils tar gzip curl'
    glibc_max=2.28
    sdk=dotnet-glibc
    ;;
  *)
    echo "$0: $rid is not a Linux runtime identifier" >&2
    exit 2
    ;;
esac

# The NuGet cache and the SDKs (one per C library) live in the workspace (not in the container) so the second build on
# the same runner reuses them; restore outputs under obj/ refer to these paths.
cache=artifacts/container-home
mkdir -p "$cache"
docker run --rm \
  -v "$PWD:/src" -w /src \
  -e HOME=/src/$cache -e VERSION -e ZEXORO_GLIBC_MAX="$glibc_max" -e GITHUB_STEP_SUMMARY=/src/$cache/summary.md \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  "$image" sh -c "
    set -e
    $setup
    if [ ! -x /src/$cache/$sdk/dotnet ]; then
      curl -fsSL --retry 5 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
      bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir /src/$cache/$sdk
    fi
    export PATH=/src/$cache/$sdk:\$PATH
    bash eng/updater/publish.sh $rid $out
  "
if [ -n "${GITHUB_STEP_SUMMARY:-}" ] && [ -f "$cache/summary.md" ]; then
  cat "$cache/summary.md" >> "$GITHUB_STEP_SUMMARY"
  rm -f "$cache/summary.md"
fi

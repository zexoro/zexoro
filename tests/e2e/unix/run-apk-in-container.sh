#!/usr/bin/env bash
# Runs tests/e2e/unix/run-apk-e2e.sh as root in an Alpine container, with a .NET runtime for musl
# mounted for the framework-dependent fixture app.
#
#   tests/e2e/unix/run-apk-in-container.sh <image> <directory written by tests/e2e/pack.sh> <.NET runtime for musl>
#
# The runtime directory holds a linux-musl .NET runtime of the machine's architecture (for example
# from dotnet-install.sh --runtime dotnet --os linux-musl). Installs what the test needs from
# Alpine (bash, python3, GNU tar and find, openssl; libstdc++ and libgcc for .NET). Needs docker
# and the repository as the working directory.
set -euo pipefail

if [ $# -ne 3 ]; then
  echo "usage: $0 <image> <directory written by tests/e2e/pack.sh> <.NET runtime for musl>" >&2
  exit 2
fi

image=$1
packages=$2
runtime="$(cd "$3" && pwd)"
docker run --rm -v "$PWD:/w" -w /w -v "$runtime:/opt/dotnet:ro" -e DOTNET_ROOT=/opt/dotnet \
  -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "$image" sh -c '
  set -eu
  for attempt in 1 2 3; do
    if apk add -q bash python3 tar findutils openssl libstdc++ libgcc > /dev/null; then break; fi
    [ "$attempt" -lt 3 ] || exit 1
    echo "package installation failed (attempt $attempt); retrying" >&2
    sleep 10
  done
  tests/e2e/unix/run-apk-e2e.sh "$1"' _ "$packages"

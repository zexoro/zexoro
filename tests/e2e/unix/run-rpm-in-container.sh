#!/usr/bin/env bash
# Runs tests/e2e/unix/run-rpm-e2e.sh as root in a container of an rpm distribution, with the
# runner's .NET mounted for the framework-dependent fixture app.
#
#   tests/e2e/unix/run-rpm-in-container.sh <image> <directory written by tests/e2e/pack.sh> [enforce]
#
# Installs what the test needs from the distribution (python3, gpg, findutils; rpmlint where the
# distribution has it in its main repositories, Fedora). Needs docker and the repository as the
# working directory.
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <image> <directory written by tests/e2e/pack.sh> [enforce]" >&2
  exit 2
fi

image=$1
packages=$2
shift 2
dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
docker run --rm -v "$PWD:/w" -w /w -v "$dotnet_root:/opt/dotnet:ro" -e DOTNET_ROOT=/opt/dotnet \
  -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "$image" bash -c '
  set -euo pipefail
  install() {
    for attempt in 1 2 3; do
      if command -v dnf > /dev/null; then
        dnf install -y -q "$@" > /dev/null && return
      else
        zypper --non-interactive --quiet install --no-recommends "$@" > /dev/null && return
      fi
      echo "package installation failed (attempt $attempt); retrying" >&2
      sleep 10
    done
    return 1
  }
  if command -v dnf > /dev/null; then
    install python3 gnupg2 findutils
    if grep -q "^ID=fedora" /etc/os-release; then install rpmlint; fi
  else
    install python3 gpg2 findutils
  fi
  tests/e2e/unix/run-rpm-e2e.sh "$@"' _ "$packages" "$@"

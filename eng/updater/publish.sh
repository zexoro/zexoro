#!/usr/bin/env bash
# Publishes zexoro-updater for one runtime identifier with Native AOT and smoke-tests it where it was
# built (docs/design/0006-desktop-formats.md section 2). Run from the repository root, on a host (or
# in a container, see publish-in-container.sh) of the runtime identifier's operating system.
#
#   eng/updater/publish.sh <rid> <output directory> [norun]
#
# Writes <output directory>/zexoro-updater-<rid>[.exe]. Unless 'norun' is given (a binary for
# another architecture than the host's), it runs the binary: --version, and 'check' outside an
# install, which must ask for a manual update (exit 7).
#
# Environment:
#   VERSION           publish with this version and require --version to print exactly it
#   ZEXORO_GLIBC_MAX  linux glibc builds: fail when the binary needs a newer glibc (e.g. 2.28)
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <rid> <output directory> [norun]" >&2
  exit 2
fi
rid=$1
out=$2
run=$([ "${3:-}" = norun ] && echo no || echo yes)

args=()
if [ -n "${VERSION:-}" ]; then
  args+=(-p:Version="$VERSION" -p:IncludeSourceRevisionInInformationalVersion=false)
fi
dotnet publish src/Zexoro.Updater -c Release -r "$rid" -o "artifacts/updater-publish/$rid" -nologo ${args[@]+"${args[@]}"}

case "$rid" in
  win-*) name=zexoro-updater.exe ;;
  *) name=zexoro-updater ;;
esac
mkdir -p "$out"
file="$out/${name%.exe}-$rid"
[ "$name" = zexoro-updater.exe ] && file="$file.exe"
cp "artifacts/updater-publish/$rid/$name" "$file"
echo "$rid: $(wc -c < "$file" | tr -d ' ') bytes"

summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$*" >> "$GITHUB_STEP_SUMMARY" || true; }

case "$rid" in
  linux-*) echo "$rid: needs $(readelf -d "$file" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' | tr '\n' ' ')" ;;
esac

case "$rid" in
  linux-musl-*)
    interpreter=$(readelf -l "$file" | sed -n 's/.*Requesting program interpreter: \(.*\)]/\1/p')
    echo "$rid: interpreter $interpreter"
    case "$interpreter" in *musl*) ;; *) echo "::error::$rid is not linked against musl ($interpreter)"; exit 1 ;; esac
    summary "- \`$rid\`: $(wc -c < "$file" | tr -d ' ') bytes, interpreter \`$interpreter\`"
    ;;
  linux-*)
    glibc=$(objdump -T "$file" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)
    echo "$rid: needs $glibc"
    if [ -n "${ZEXORO_GLIBC_MAX:-}" ] && [ "$(printf '%s\n%s\n' "GLIBC_$ZEXORO_GLIBC_MAX" "$glibc" | sort -V | tail -1)" != "GLIBC_$ZEXORO_GLIBC_MAX" ]; then
      echo "::error::$rid needs $glibc, newer than the floor glibc $ZEXORO_GLIBC_MAX"
      exit 1
    fi
    summary "- \`$rid\`: $(wc -c < "$file" | tr -d ' ') bytes, needs \`$glibc\`"
    ;;
  *)
    summary "- \`$rid\`: $(wc -c < "$file" | tr -d ' ') bytes"
    ;;
esac

if [ "$run" = yes ]; then
  actual=$("$file" --version | tr -d '\r')
  echo "$rid --version: $actual"
  if [ -n "${VERSION:-}" ] && [ "$actual" != "$VERSION" ]; then
    echo "::error::zexoro-updater-$rid --version printed '$actual', expected '$VERSION'"
    exit 1
  fi
  # Outside an install the updater has no record and asks for a manual update.
  set +e
  "$file" check --json
  status=$?
  set -e
  if [ "$status" -ne 7 ]; then
    echo "::error::zexoro-updater-$rid check outside an install exited $status, expected 7"
    exit 1
  fi
fi

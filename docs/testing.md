# Testing

## Unit tests

```bash
dotnet test
```

They run on Linux, macOS and Windows in CI (`.github/workflows/ci.yml`, job `build`).

Target detection (`ExecutableInspectorTests`) runs on small synthetic headers: PE images from `tests/Zexoro.Signing.Fixtures/Shared/TestPe.cs`, and hand-built ELF, thin Mach-O and fat Mach-O headers, including unsupported machines, wrong ELF class or byte order, and a Java class file (which shares the fat magic). `TargetConfigTests` and `PackCommandTests` cover `formats`, `[input] target`, `pack --format/--input/--main` and the "not implemented yet" refusal for every format except `nsis`, Windows `zip`, Linux `tar.gz` and `deb`.

The Windows zip (docs/design/0006-desktop-formats.md) is covered by `Zexoro.Formats.Zip.Tests` (layout, install record with and without `[updates]`, signing of the main executable and the updater, byte-identical output regardless of file times and Unix modes, MS-DOS "made by" in the central directory) and by `ZipApplyTests` in `Zexoro.Updater.Tests`, which apply archives written by the real zip writer: extraction into `app.new`, a changed `main`, leftovers of an interrupted run, a rename failure injected at every swap step (rolled back, exit 5) and a failed rollback (exit 8), a record for another app, version, format or scope (exit 5), malicious archives (absolute paths, drive letters, backslashes, `..`, `.` and empty segments, a second top-level folder, files outside it, unexpected directories, duplicates differing only in case, a Unix symlink entry, not a zip at all: exit 4, nothing written, download discarded), and a root the user cannot write to (mode 0555 on Unix, a deny ACE on Windows: exit 7, the download kept for a later apply).

The Linux tar.gz and the Linux common pieces (docs/design/0006-desktop-formats.md, step 5) are covered by `Zexoro.Formats.Linux.Tests`: `ReproducibleTarTests` (sorted entries, fixed time, owner 0/0 without names, the gzip header's OS byte, the same bytes for files with other times, long and non-ASCII names, invalid names), `LinuxPayloadTests` (modes for the main executable, ELF files, `#!` scripts, execute bits on Unix hosts and other files; relative links kept, links to directories not followed; absolute links, links leaving the input and links through another link refused), `LinuxTarPackagerTests` (layout, record with and without `[updates]`, updater mode 0755 and architecture, `[linux] icon`, a wrong updater hash or architecture, x86 refused, the musl warning, the output directory inside the input), `PngIconTests` (a PNG as it is, the largest image of an ICO, 32-bit and palette bitmaps with their AND mask converted to PNG, the e2e fixture icon) and `DesktopEntryTests` (keys, `Exec` quoting and escaping). `DebPackagerTests` cover the deb (step 6): the ar members in order with fixed headers, `control.tar.gz` (`./`, `control`, `md5sums` owned by root, checksums of every file), the data layout under `/opt/<pkg>` and `/usr` with parent directories, the `/usr/bin` link, the `.desktop` file and the icon path (hicolor or pixmaps), the record (`deb`/`machine`/`notify`), control fields with and without `[updates]` (the updater's `Depends` and `Recommends`, `[linux.deb] depends`, `Maintainer` and its notice, `Homepage`, a multi-line description), the version mapping (`~` for pre-releases, build metadata dropped, `-` in a pre-release refused, and the order of usual pre-releases under dpkg's comparison), the same bytes for files with other times, x86 refused. `LinuxConfigTests` in `Zexoro.Core.Tests` cover the `[linux]` keys (including `maintainer` and `[linux.deb] depends`) and package names, `NotifyTests` in `Zexoro.Updater.Tests` the `notify` strategy (`check --json` with `url`, a tampered feed, `download` and `apply` exit 7 without network access or any change to the install or the state, also with a staged download), and `TarGzApplyTests` in `Zexoro.Updater.Tests` applies an archive written by the packager's own writer.

The Unix platform layer of the updater (docs/design/0006-desktop-formats.md section 2, step 4) is covered by `UnixTests` (state directories per system, including a relative `XDG_STATE_HOME` that must be ignored; the temporary copy's directory and file with mode 0700; only copies older than an hour removed from the shared temporary directory; waiting for a process that is not a child, which exits, keeps running past the timeout, or does not exist) and `TarGzApplyTests` (archives written with `TarWriter`: modes of files and directories and symbolic links kept through the swap, a read-only directory that keeps the owner's write access, `download` then `apply`, and rejected archives: links that are absolute, leave `app/` or pass through another link, links outside `app/`, entries below a link, hard links, FIFOs and devices, unsafe paths, not gzip or not tar). The tests that need Unix modes, links or processes are skipped on Windows. `UpdaterTests` and `CommandTests` in `Zexoro.Cli.Tests` cover every runtime identifier in `ZEXORO_UPDATER_DIR` (fake PE, ELF and Mach-O headers; a file for another system is refused) and, in CI builds, the gzip-compressed embedded updaters (decompressed, SHA-256 and header checked).

### Updater builds

The `updater` jobs of `ci.yml` and `release.yml` publish zexoro-updater for all nine runtime identifiers with `eng/updater/publish.sh`, each on a runner of its system and architecture (Native AOT cannot cross-compile between operating systems): Windows (`win-x86`, `win-x64`, `win-arm64`), `macos-15` (`osx-arm64`), `macos-15-intel` (`osx-x64`), and `ubuntu-24.04` / `ubuntu-24.04-arm` for Linux, where `eng/updater/publish-in-container.sh` builds inside `almalinux:8` (glibc 2.28, the oldest glibc of a distribution .NET 10 supports; the build fails if the binary needs a newer glibc) and `alpine:3.21` (musl). Each binary that can run on its runner prints `--version` (the release version in `release.yml`) and `check` outside an install must exit 7; the job summary lists sizes, the glibc version needed and the musl interpreter.

`tests/e2e/unix/check-updater.sh <zexoro> <zexoro-updater> [<runner>...]` then runs each Linux and macOS updater on a matching runner (the `build` jobs; in `release.yml` all four Linux and macOS runners, the musl builds inside `alpine:3.21`) against a feed served by `python3 -m http.server` on `127.0.0.1:8081`, with an install record written by hand: `check` up to date (exit 1), a flipped feed byte (exit 4), `check` and `download` of 2.0.0 (exit 0, state in `$XDG_STATE_HOME/zexoro/updater/<app.id>` or `~/Library/Application Support/zexoro/updater/<app.id>`), then `apply --wait-pid <pid> --restart` of a tar.gz made with the system's `tar`: apply waits for a process that is not its child, the record is 2.0.0, file modes (755, 640) and a symbolic link are kept, an obsolete file is gone, nothing `.new`/`.old` and no updater copy in `$TMPDIR` is left, the downloads are cleared, the application was restarted, and `check` exits 1. It runs on a developer Mac or Linux machine as well:

```bash
dotnet publish src/Zexoro.Cli -c Release -r osx-arm64 -o artifacts/cli
dotnet publish src/Zexoro.Updater -c Release -r osx-arm64 -o artifacts/updater
tests/e2e/unix/check-updater.sh artifacts/cli/zexoro artifacts/updater/zexoro-updater
```

## End-to-end tests

The end-to-end tests (docs/design/0003-mvp.md section 5) install real installers on Windows. They have two halves:

1. **Pack** (`tests/e2e/pack.sh`, any OS with bash, `openssl` and the .NET SDK). Publishes the fixture app (`tests/e2e/fixture-app`, framework-dependent `win-x64`, `linux-x64` and `linux-arm64`) and packs, with a throwaway code-signing certificate and a throwaway update key:
   - `user/ZexoroE2EUser-{1.0.0,2.0.0}-win-x64.exe` and `machine/ZexoroE2EMachine-{1.0.0,2.0.0}-win-x64.exe` (signed, with `[updates]` pointing at `http://localhost:8080/<scope>/stable/`), with their release fragments `*.release.json`,
   - `portable/ZexoroE2EPortable-{1.0.0,2.0.0}-win-x64.zip` (`formats = ["zip"]`, signed, with `[updates]` pointing at `http://localhost:8080/portable/stable/`) and their release fragments,
   - `unsigned/ZexoroE2EUser-1.0.0-win-x64.exe` and `.zip` (one `pack --format nsis,zip` run),
   - `linux/ZexoroE2ETar-{1.0.0,2.0.0}-linux-{x64,arm64}.tar.gz` (with `[updates]` pointing at `http://localhost:8080/tar/stable/`) and their release fragments, and `linux/no-updates/ZexoroE2ETar-1.0.0-linux-{x64,arm64}.tar.gz`; the inputs hold `bin/helper.sh` (a `#!` script with mode 0644 on disk) and, except on Windows hosts, a symbolic link `bin/version.txt -> ../version.txt` (`linux/links` marks that),
   - `deb/zexoro-e2e_{1.0.0,2.0.0}_{amd64,arm64}.deb` (`[linux] package`, `maintainer`, `[linux.deb] depends = ["coreutils (>= 8.0)"]`, a two-line description, `[updates]` pointing at `http://localhost:8080/deb/stable/`) and their release fragments, and `deb/no-updates/zexoro-e2e_1.0.0_{amd64,arm64}.deb`, from the same inputs as the tar.gz,
   - `repro/ZexoroE2ERepro-1.0.0-win-x64.zip`, `repro/ZexoroE2ERepro-1.0.0-linux-x64.tar.gz` and `repro/repro_1.0.0_amd64.deb`, a zip, a tar.gz and a deb of the same fixed files (`[input] target`, a `.cmd` and a `.sh` entry point),
   - `root.cer`, the certificate to trust while testing,
   - `zexoro-update.pub` and `zexoro-update.key.pem`, the update key pair,
   - `feeds/<kind>/seq1` (1.0.0 only, sequence 1), `feeds/<kind>/seq2` (1.0.0 and 2.0.0, sequence 2) and `feeds/<kind>/other-key` (signed with a key the packages do not trust), each `feed.json` plus `feed.json.sig`, for `user`, `machine`, `portable`, `tar` and `deb` (whose releases list the x64 and arm64 packages).

   Version 1.0.0 contains `app\obsolete.txt`, which 2.0.0 drops.
2. **Install** (`tests/e2e/windows/Run-E2E.ps1`, Windows PowerShell 5.1 or PowerShell 7). For each scope: silent install of 1.0.0 (files, `zexoro-install.json`, uninstall key, Start menu shortcut, Authenticode signatures of the installer, uninstaller, main executable and `zexoro-updater.exe`, the app runs), stage mode (`/S /ZEXORO_STAGE`), upgrade to 2.0.0 (stale file gone, one uninstall entry, new version), 1.0.0 again (exit code 3, nothing changed), 2.0.0 while the app runs (exit code 4), silent uninstall through `QuietUninstallString` (root, key, shortcut and updater state gone, user data kept, the `[windows.uninstall] remove` path deleted).
3. **Update** (`tests/e2e/windows/Run-UpdateE2E.ps1`). Serves the feeds and installers with an `HttpListener` on `http://localhost:8080/` (the port must be free) and drives the installed `zexoro-updater.exe`, for the per-user scope (strategy `replace`) and the per-machine scope (strategy `reinstall`, whose installer the updater starts through `ShellExecute`):
   - a reference install of 2.0.0 records what the installer itself writes into the uninstall key; then 1.0.0 is installed;
   - `check` against `seq1`: exit 1 (up to date);
   - exit 4 and nothing changed for: `seq2` with one byte flipped, `other-key`, `seq1` again after `seq2` was accepted (replay), and a `download` whose served installer has one byte changed (nothing is kept); `apply` with nothing downloaded exits 1;
   - `check` against `seq2`: exit 0, `available` 2.0.0; `download`: exit 0, the installer is staged;
   - `apply --wait-pid <pid> --restart` while the fixture app runs: apply waits for it, the app is then stopped, and the update finishes: `last-apply.json` reports `applied`, the app was restarted from the install, the files, `zexoro-install.json`, `DisplayVersion`, `ZexoroVersionOrder` and `EstimatedSize` (equal to the reference install) are 2.0.0, `obsolete.txt` is gone, nothing staged or `.old` is left (except `zexoro-updater.exe.old` after `replace`, which the next apply or the uninstaller removes), the downloads are cleared;
   - `check` exits 1 (up to date at 2.0.0); the uninstaller removes the install and the updater state.

4. **Portable zip** (`tests/e2e/windows/Run-ZipE2E.ps1`, no administrator rights needed; with them the throwaway root is trusted for the run and signatures are checked too). Extracts the unsigned zip (one top-level folder, `app\` and the record but no updater, record `zip`/`user`/`replace` without a feed, the app runs, nothing registered), then the portable zip into a folder with a space in its path and runs the shared update scenario of 0006 section 5 against the same local feed server: `seq1` exits 1; a flipped feed byte, `other-key`, `seq1` replayed after `seq2` and a downloaded archive with one byte changed exit 4 with nothing changed; `download` and `apply --wait-pid <pid> --restart` move to 2.0.0 (record, files, `obsolete.txt` gone, nothing staged, only `zexoro-updater.exe.old` left, downloads cleared, still nothing registered, the app restarted from the extracted folder); `check` exits 1. Finally a fresh 1.0.0 copy whose folder denies the user creating files: `apply` exits 7 and changes nothing; once the folder is writable again, the kept download applies.

5. **Portable tar.gz** (`tests/e2e/unix/run-tar-e2e.sh <dir>`, Linux with GNU tar, python3, the .NET runtime and a free port 8080; the archives of the machine's architecture). Lists each archive with the system's `tar -tzvf` (owner 0/0, 1980-02-01 00:00, modes), extracts the archive without `[updates]` (layout without updater, record without a feed, the app runs), then the 1.0.0 archive into a directory with a space in its path (layout, modes 755 for the apphost, the updater and the `#!` script, 644 otherwise, the symbolic link, record `tar-gz`/`user`/`replace` with feed, keys and main, `icon.png`, the app runs) and runs the shared update scenario against `python3 -m http.server`: `seq1` exits 1; a flipped feed byte, `other-key`, `seq1` replayed after `seq2` and a downloaded archive with one byte changed exit 4 with nothing changed; `download` and `apply --wait-pid <pid> --restart` while the app runs move to 2.0.0 (apply waits, `last-apply.json`, the app restarted from the extracted directory, `obsolete.txt` gone, modes and the link kept, nothing `.new`/`.old`, downloads cleared, no updater copy in `$TMPDIR`); `check` exits 1. Finally a fresh 1.0.0 copy in a directory without write permission: `apply` exits 7 and changes nothing; after `chmod u+w` the kept download applies. State, data and temporary files live in a scratch directory (`XDG_STATE_HOME`, `XDG_DATA_HOME`, `TMPDIR`).

6. **deb** (`tests/e2e/unix/run-deb-e2e.sh <dir>`, Linux with dpkg and apt as root or with password-less sudo, python3, curl, the .NET runtime and a free port 8080; the packages of the machine's architecture; installs and finally purges the package `zexoro-e2e`). Checks both packages with `dpkg-deb --info/--field/--contents` (fields, `Depends` with and without `[updates]`, no maintainer scripts, root/root, modes, the `/usr/bin` link, the `.desktop` file, the hicolor icon, no updater without `[updates]`) and, when installed, `lintian` (findings printed; errors other than the accepted `dir-or-file-in-opt`, `no-copyright-file`, `no-changelog` and `embedded-library` (zlib in the updater) fail). Installs 1.0.0 with `apt-get install ./…deb` (dpkg status and version, `/usr/bin/zexoro-e2e` on `PATH`, `/opt/zexoro-e2e` with exactly `app/`, the record and the updater, root-owned, record `deb`/`machine`/`notify`, `dpkg --verify` clean, the app runs through `/usr/bin` from `/opt/zexoro-e2e/app/`), then the notify scenario as the calling user against `python3 -m http.server`: `seq1` exits 1; a flipped feed byte, `other-key` and `seq1` replayed after `seq2` exit 4; `seq2` exits 0 with version 2.0.0, the package's `url` and size; `download`, `apply` and `apply --wait-pid --restart` exit 7 naming apt or dpkg, fetch nothing, and leave the installed files (paths, modes, owners, sizes, times, MD5), `dpkg --verify` and the updater's state (no downloads, no `last-apply.json`, no copy in `$TMPDIR`) unchanged. Then `dpkg -i` 2.0.0 (version, record, `obsolete.txt` gone, the app runs, `check` exits 1), `dpkg --remove` and `--purge` (nothing left in `/opt/zexoro-e2e`, `/usr/bin`, the `.desktop` file or the icon; dpkg forgets the package), and `dpkg -i` 1.0.0 followed by `apt-get purge`.

CI packs on Ubuntu, macOS and Windows and runs the Windows scripts on a Windows runner for each host's packages (job `e2e-windows`) and `run-tar-e2e.sh` and `run-deb-e2e.sh` on `ubuntu-24.04` and `ubuntu-24.04-arm` for each host's packages (job `e2e-linux`); the same job runs `run-deb-e2e.sh` again in a `debian:stable` container (as root, with lintian, apt resolving the updater's OpenSSL dependency; the runner's .NET mounted for the fixture app). Job `reproducible` compares the three hosts' `repro/*.zip`, `repro/*.tar.gz` and `repro/*.deb`, which must be byte-identical per format.

### Running the tar.gz and deb tests on Linux

Pack anywhere (or take the `e2e-installers-<os>` artifact of a CI run), then on an x64 or arm64 Linux machine with the .NET 10 runtime:

```bash
tests/e2e/pack.sh artifacts/cli/zexoro artifacts/e2e
tests/e2e/unix/run-tar-e2e.sh artifacts/e2e
tests/e2e/unix/run-deb-e2e.sh artifacts/e2e   # installs and purges the package zexoro-e2e
```

The zexoro binary must embed the Linux updaters (a CI or release build), or `ZEXORO_UPDATER_DIR` must point at real `zexoro-updater-linux-<arch>` builds.

### Running locally on Windows

Requirements: an **elevated** PowerShell (per-machine installs, and the test trusts `root.cer` in `LocalMachine\Root` and removes it at the end), and the .NET 10 runtime (the fixture app is framework-dependent). A session over OpenSSH as an administrator is elevated. Use a test machine or VM: the script installs and uninstalls `Zexoro E2E User` and `Zexoro E2E Machine` and deletes `%LOCALAPPDATA%\ZexoroE2E`.

1. Pack, on any OS (or take the `e2e-installers-<os>` artifact of a CI run):

   ```bash
   dotnet publish src/Zexoro.Cli -c Release -r osx-arm64 -o artifacts/cli   # the host's RID
   tests/e2e/pack.sh artifacts/cli/zexoro artifacts/e2e
   ```

2. Copy `artifacts/e2e` and `tests/e2e/windows` to the Windows machine, for example `scp -r artifacts/e2e tests/e2e/windows me@winbox:C:/temp/`.

3. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\temp\windows\Run-E2E.ps1 -InstallerDir C:\temp\e2e
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\temp\windows\Run-UpdateE2E.ps1 -InstallerDir C:\temp\e2e
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\temp\windows\Run-ZipE2E.ps1 -InstallerDir C:\temp\e2e
   ```

   `-Scope user` or `-Scope machine` limits a run to one scope. Every check prints `PASS` or `FAIL`; the exit code is 1 when anything failed. The scripts remove leftovers of an interrupted earlier run before they start.

### Running over SSH

The scripts run unattended in an OpenSSH session of an administrator, which is elevated, so the per-machine installer that the updater starts through `ShellExecute` raises no UAC prompt. Use key authentication (`-o BatchMode=yes` fails instead of asking for a password):

```bash
host=me@winbox                      # an administrator on the test machine
dir=C:/temp/zexoro-e2e              # a scratch directory on it
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -Command \"New-Item -ItemType Directory -Force '$dir' | Out-Null\""
scp -r artifacts/e2e tests/e2e/windows "$host:$dir/"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -ExecutionPolicy Bypass -File $dir/windows/Run-E2E.ps1 -InstallerDir $dir/e2e"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -ExecutionPolicy Bypass -File $dir/windows/Run-UpdateE2E.ps1 -InstallerDir $dir/e2e"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -ExecutionPolicy Bypass -File $dir/windows/Run-ZipE2E.ps1 -InstallerDir $dir/e2e"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -Command \"Remove-Item -Recurse -Force '$dir'\""
```

The machine needs the .NET 10 runtime and a free port 8080. The applications are restarted in the SSH session, without a desktop; they only write their marker file.

## Release builds

`.github/workflows/release.yml` publishes zexoro for linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64 from `v*` tags, each with Native AOT on a runner of that operating system and architecture. Every binary is smoke-tested where it is built: `--version` must print the release version, `doctor` must report the bundled NSIS and all nine bundled updaters as ok, `pack` must build and sign an installer, and on Linux and macOS `tests/e2e/unix/check-updater.sh` runs the runner's updaters (above). Pull requests that change the workflow or `eng/`, and manual runs with `dry-run`, do all of this with version `<VersionPrefix>-dryrun.<run number>` and publish nothing.

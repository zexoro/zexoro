# Testing

## Unit tests

```bash
dotnet test
```

They run on Linux, macOS and Windows in CI (`.github/workflows/ci.yml`, job `build`).

Target detection (`ExecutableInspectorTests`) runs on small synthetic headers: PE images from `tests/Zexoro.Signing.Fixtures/Shared/TestPe.cs`, and hand-built ELF, thin Mach-O and fat Mach-O headers, including unsupported machines, wrong ELF class or byte order, and a Java class file (which shares the fat magic). `TargetConfigTests` and `PackCommandTests` cover `formats`, `[input] target`, `pack --format/--input/--main` and the "not implemented yet" refusal for every format except `nsis`.

## End-to-end tests

The end-to-end tests (docs/design/0003-mvp.md section 5) install real installers on Windows. They have two halves:

1. **Pack** (`tests/e2e/pack.sh`, any OS with bash, `openssl` and the .NET SDK). Publishes the fixture app (`tests/e2e/fixture-app`, framework-dependent `win-x64`) and packs, with a throwaway code-signing certificate and a throwaway update key:
   - `user/ZexoroE2EUser-{1.0.0,2.0.0}-win-x64.exe` and `machine/ZexoroE2EMachine-{1.0.0,2.0.0}-win-x64.exe` (signed, with `[updates]` pointing at `http://localhost:8080/<scope>/stable/`), with their release fragments `*.release.json`,
   - `unsigned/ZexoroE2EUser-1.0.0-win-x64.exe`,
   - `root.cer`, the certificate to trust while testing,
   - `zexoro-update.pub` and `zexoro-update.key.pem`, the update key pair,
   - `feeds/<scope>/seq1` (1.0.0 only, sequence 1), `feeds/<scope>/seq2` (1.0.0 and 2.0.0, sequence 2) and `feeds/<scope>/other-key` (signed with a key the installers do not trust), each `feed.json` plus `feed.json.sig`.

   Version 1.0.0 contains `app\obsolete.txt`, which 2.0.0 drops.
2. **Install** (`tests/e2e/windows/Run-E2E.ps1`, Windows PowerShell 5.1 or PowerShell 7). For each scope: silent install of 1.0.0 (files, `zexoro-install.json`, uninstall key, Start menu shortcut, Authenticode signatures of the installer, uninstaller, main executable and `zexoro-updater.exe`, the app runs), stage mode (`/S /ZEXORO_STAGE`), upgrade to 2.0.0 (stale file gone, one uninstall entry, new version), 1.0.0 again (exit code 3, nothing changed), 2.0.0 while the app runs (exit code 4), silent uninstall through `QuietUninstallString` (root, key, shortcut and updater state gone, user data kept, the `[windows.uninstall] remove` path deleted).
3. **Update** (`tests/e2e/windows/Run-UpdateE2E.ps1`). Serves the feeds and installers with an `HttpListener` on `http://localhost:8080/` (the port must be free) and drives the installed `zexoro-updater.exe`, for the per-user scope (strategy `replace`) and the per-machine scope (strategy `reinstall`, whose installer the updater starts through `ShellExecute`):
   - a reference install of 2.0.0 records what the installer itself writes into the uninstall key; then 1.0.0 is installed;
   - `check` against `seq1`: exit 1 (up to date);
   - exit 4 and nothing changed for: `seq2` with one byte flipped, `other-key`, `seq1` again after `seq2` was accepted (replay), and a `download` whose served installer has one byte changed (nothing is kept); `apply` with nothing downloaded exits 1;
   - `check` against `seq2`: exit 0, `available` 2.0.0; `download`: exit 0, the installer is staged;
   - `apply --wait-pid <pid> --restart` while the fixture app runs: apply waits for it, the app is then stopped, and the update finishes: `last-apply.json` reports `applied`, the app was restarted from the install, the files, `zexoro-install.json`, `DisplayVersion`, `ZexoroVersionOrder` and `EstimatedSize` (equal to the reference install) are 2.0.0, `obsolete.txt` is gone, nothing staged or `.old` is left (except `zexoro-updater.exe.old` after `replace`, which the next apply or the uninstaller removes), the downloads are cleared;
   - `check` exits 1 (up to date at 2.0.0); the uninstaller removes the install and the updater state.

CI packs on Ubuntu, macOS and Windows and runs both scripts on a Windows runner for each host's installers (job `e2e-windows`).

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
   ```

   `-Scope user` or `-Scope machine` limits a run to one scope. Every check prints `PASS` or `FAIL`; the exit code is 1 when anything failed. The scripts remove leftovers of an interrupted earlier run before they start.

### Running over SSH

Both scripts run unattended in an OpenSSH session of an administrator, which is elevated, so the per-machine installer that the updater starts through `ShellExecute` raises no UAC prompt. Use key authentication (`-o BatchMode=yes` fails instead of asking for a password):

```bash
host=me@winbox                      # an administrator on the test machine
dir=C:/temp/zexoro-e2e              # a scratch directory on it
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -Command \"New-Item -ItemType Directory -Force '$dir' | Out-Null\""
scp -r artifacts/e2e tests/e2e/windows "$host:$dir/"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -ExecutionPolicy Bypass -File $dir/windows/Run-E2E.ps1 -InstallerDir $dir/e2e"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -ExecutionPolicy Bypass -File $dir/windows/Run-UpdateE2E.ps1 -InstallerDir $dir/e2e"
ssh -o BatchMode=yes "$host" "pwsh -NoProfile -Command \"Remove-Item -Recurse -Force '$dir'\""
```

The machine needs the .NET 10 runtime and a free port 8080. The applications are restarted in the SSH session, without a desktop; they only write their marker file.

## Release builds

`.github/workflows/release.yml` publishes zexoro for linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64 from `v*` tags, each with Native AOT on a runner of that operating system and architecture. Every binary is smoke-tested where it is built: `--version` must print the release version, `doctor` must report the bundled NSIS as ok, and `pack` must build and sign an installer. Pull requests that change the workflow or `eng/`, and manual runs with `dry-run`, do all of this with version `<VersionPrefix>-dryrun.<run number>` and publish nothing.

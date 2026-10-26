# Testing

## Unit tests

```bash
dotnet test
```

They run on Linux, macOS and Windows in CI (`.github/workflows/ci.yml`, job `build`).

## End-to-end tests

The end-to-end tests (docs/design/0003-mvp.md section 5) install real installers on Windows. They have two halves:

1. **Pack** (`tests/e2e/pack.sh`, any OS with bash, `openssl` and the .NET SDK). Publishes the fixture app (`tests/e2e/fixture-app`, framework-dependent `win-x64`) and packs, with a throwaway self-signed code-signing certificate:
   - `user/ZexoroE2EUser-{1.0.0,2.0.0}-win-x64.exe` and `machine/ZexoroE2EMachine-{1.0.0,2.0.0}-win-x64.exe` (signed),
   - `unsigned/ZexoroE2EUser-1.0.0-win-x64.exe`,
   - `root.cer`, the certificate to trust while testing.

   Version 1.0.0 contains `app\obsolete.txt`, which 2.0.0 drops.
2. **Test** (`tests/e2e/windows/Run-E2E.ps1`, Windows PowerShell 5.1 or PowerShell 7). For each scope: silent install of 1.0.0 (files, `zexoro-install.json`, uninstall key, Start menu shortcut, Authenticode signatures of the installer, uninstaller and main executable, the app runs), upgrade to 2.0.0 (stale file gone, one uninstall entry, new version), 1.0.0 again (exit code 3, nothing changed), 2.0.0 while the app runs (exit code 4), silent uninstall through `QuietUninstallString` (root, key and shortcut gone, user data kept, the `[windows.uninstall] remove` path deleted).

CI packs on Ubuntu, macOS and Windows and runs the test on a Windows runner for each host's installers (job `e2e-windows`).

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
   ```

   `-Scope user` or `-Scope machine` limits the run to one scope. Every check prints `PASS` or `FAIL`; the exit code is 1 when anything failed. The script removes leftovers of an interrupted earlier run before it starts.

## Release builds

`.github/workflows/release.yml` publishes zexoro for linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64 from `v*` tags, each with Native AOT on a runner of that operating system and architecture. Every binary is smoke-tested where it is built: `--version` must print the release version, `doctor` must report the bundled NSIS as ok, and `pack` must build and sign an installer. Pull requests that change the workflow or `eng/`, and manual runs with `dry-run`, do all of this with version `<VersionPrefix>-dryrun.<run number>` and publish nothing.

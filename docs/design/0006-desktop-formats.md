# 0006: Remaining desktop formats

- Status: accepted (stage 5)
- Date: 2026-10-27
- Goal: add the remaining mainstream desktop formats for Windows, Linux and macOS, one at a time. Each format is done only when it installs, upgrades and uninstalls correctly **and** its update strategy (`0002-update-contract.md` §7) works on real installs, tested end to end.

Facts below were checked on 2026-10-27 unless marked as a recommendation or an assumption to verify.

## 1. Formats and order

| # | Format | OS | Strategy | Built by | Hosts that can pack |
| --- | --- | --- | --- | --- | --- |
| 1 | `zip` (portable) | Windows | replace | .NET (`System.IO.Compression`) | any |
| 2 | `tar.gz` (portable) | Linux | replace | .NET (`System.Formats.Tar`, `GZipStream`) | any |
| 3 | `deb` | Linux | notify | .NET (ar + tar + gzip) | any |
| 4 | `rpm` | Linux | notify | .NET (RPM v4 headers + cpio + gzip) | any |
| 5 | `apk` | Linux (Alpine) | notify | .NET (APK v2: concatenated gzip streams) | any |
| 6 | `zip` (`.app` in zip) | macOS | bundle | .NET + OS `codesign`, `notarytool` | macOS |
| 7 | `dmg` | macOS | bundle | OS `hdiutil` | macOS |
| 8 | `appimage` | Linux | replace | .NET squashfs writer + type 2 runtime from `zexoro/appimage-toolset` | any |
| 9 | `msi` | Windows | reinstall | WiX 3.14.1 from `zexoro/wix-toolset` | Windows |
| 10 | `pkg` | macOS | reinstall | OS `pkgbuild`, `productbuild` | macOS |

Why this order:

- **1–2 reuse almost everything.** The Windows updater, the feed pipeline and the `replace` swap (`FileSwap`) already exist; the only new updater work is extracting an archive instead of running an installer in stage mode. Windows zip needs no new updater binary at all, so it also proves the format-selection plumbing (step 2) on the platform that already has a full e2e harness. tar.gz brings the first non-Windows updater.
- **3–5 are BCL-only and fully testable in CI** (Ubuntu runners and distro containers), and they share one new strategy (`notify`), which is the smallest piece of updater work. deb first (largest user base, easiest to test on the runner itself), apk last (needs a musl updater and strict format conformance, below).
- **6–7 need macOS hosts and OS tools** but no toolset. The `bundle` strategy is the largest new updater piece; it comes after the Linux formats so the Unix updater base is settled. dmg is cheap once the bundle exists.
- **8–10 depend on toolsets or are the riskiest:** AppImage needs a runtime toolset repository and a squashfs writer; MSI needs a WiX build from archived sources, a new signing path for MSI files, and only packs on Windows; pkg updates cannot be silent without the updater requesting authorization (section 2). Late placement keeps the risk from blocking the cheap formats; each later format can be dropped or deferred without affecting earlier ones.

## 2. Install layout, identity and update strategy per format

Common rules (unchanged from 0002/0005): `app.id` is the identity; the install record `zexoro-install.json` carries `app`, `version`, `os`, `arch`, `format`, `scope`, `strategy`, `channel`, `feed`, `keys`, `main`; the updater accepts only an artifact whose (os, arch, format, scope) matches the record; tamper and replay checks are shared code in `Zexoro.Updates`. Artifact names are `<name>-<version>-<rid>.<ext>` as for NSIS, except where a package manager has its own convention.

| Format | Root and layout | Identity | Scope / elevation |
| --- | --- | --- | --- |
| Windows `zip` | Top-level folder `<name>\` containing `app\`, `zexoro-updater.exe`, `zexoro-install.json`. The root is wherever the user extracts it. | Record only; no registry entry, no shortcuts. | `user`; never elevates. A root that is not writable makes `apply` exit 7. |
| Linux `tar.gz` | Same, with `zexoro-updater` (mode 0755). Unix modes and symlinks preserved. | Record only. | `user`. |
| `deb`, `rpm`, `apk` | `/opt/<pkg>/app/`, `/opt/<pkg>/zexoro-updater`, `/opt/<pkg>/zexoro-install.json`; `/usr/bin/<pkg>` symlink to the main executable; `/usr/share/applications/<app.id>.desktop`; icon in `/usr/share/icons/hicolor/<n>x<n>/apps/<app.id>.png`. No maintainer scripts (desktop and icon caches are refreshed by distro triggers). | Package name = last segment of `app.id`, lower-cased (0002 §2), `[linux] package` overrides; validated against each format's name rules. | `machine`; the package manager elevates. |
| macOS `zip`, `dmg` | `<name>.app`; updater at `Contents/MacOS/zexoro-updater` (nested code, signed with the app), record at `Contents/Resources/zexoro-install.json` (code signing forbids data in `Contents/MacOS`). The updater looks for the record there when it runs inside a bundle. | `CFBundleIdentifier` = `app.id` unless `[macos] bundle_id`. Record `format` is `app` for both containers. | `user` (wherever the bundle lives). |
| `appimage` | One file. Inside the read-only squashfs: `AppRun`, `.desktop`, icon, `app/`, `zexoro-updater`, `zexoro-install.json`. | Record only. | `user`. |
| `msi` | `%ProgramFiles%\<name>\` with the NSIS layout; uninstall entry written by Windows Installer. | `UpgradeCode` = UUIDv5(zexoro namespace, `app.id`) (0002 §2); `ProductCode` new per build. | `machine` only. Per-user MSI is out of scope: Windows Installer's per-user mode is fragile and per-user Windows installs are covered by NSIS and zip. |
| `pkg` | Installs `/Applications/<name>.app` (same bundle as above, record `format` = `pkg`). Component plist sets `BundleIsRelocatable = false`, otherwise Installer silently "upgrades" another copy with the same bundle ID elsewhere on disk. | Package identifier = bundle identifier. | `machine`; Installer asks for authorization. |

### replace — Windows zip, tar.gz

The release artifact is the archive itself. `apply` (after the existing self-copy to the temp directory) extracts it with the BCL into `<root>\app.new\` and `<file>.new` root files, rejecting absolute paths, `..` and links that point outside the root (same rules as `ToolsetCache`), checks the new record's `app`, `format` and `scope`, then uses the existing rename swap and rollback. On Windows no registry entry exists, so nothing else is written. Exit codes are unchanged. A changed `main` is fine here: there are no shortcuts to break.

**As implemented for Windows zip (step 3).** The archive is written by `ReproducibleZip` (`src/Zexoro.Formats.Zip`): entries sorted ordinally, a directory entry for every directory, every timestamp 1980-02-01 00:00, MS-DOS attributes only and the central directory's "made by" host patched to MS-DOS, because `ZipArchive` otherwise records the host and, on Unix, Unix modes. Without `[updates]` the zip mirrors NSIS: a record without `channel`/`feed`/`keys`/`main`, no updater, no release fragment. Fragments set `minimumUpdater` to `0.2.0-0`, the first updater with zip `apply` (`VersionPrefix` moved to 0.2.0 after the v0.1.0 release). In `apply`, after the self-copy: a write probe in the root (`UnauthorizedAccessException` → exit 7, download kept), leftovers removed, then `ArchiveStaging` checks every entry before writing anything: one top-level folder, only `app/…` and plain files directly below it, no absolute paths, drive letters, backslashes, empty, `.` or `..` segments, no Unix symlink entries, no encrypted entries, no duplicates ignoring case. A rejected archive exits 4 and discards the download (it passed its sha256, so the signed feed itself names a bad artifact); a staged record whose app, target (os, arch, format, scope), strategy or version differs exits 5, as for NSIS stage mode. Downloads keep the artifact's file name when it ends in `.zip` (`<app.id>-<version>.zip` otherwise). Renames during the swap retry sharing violations for about two seconds: on the Windows test machine one run failed to rename the running `zexoro-updater.exe` of a freshly extracted folder (exit 5, rolled back) and the next run passed, consistent with a scanner holding the file briefly.

**As implemented for Linux tar.gz (step 5).** `LinuxTarPackager` (`src/Zexoro.Formats.Linux`) writes `<name>-<version>-linux-<arch>.tar.gz` (x64, arm64) with one top-level `<name>/` holding `app/`, `zexoro-install.json` (`os` linux, `format` `tar-gz`, `scope` user, `strategy` replace) and, with `[updates]`, `zexoro-updater` (0755). Refinements and decisions:

- **`icon.png` in the root.** The archive also carries the PNG icon (from `[linux] icon` or `[app] icon`), so users can point a launcher of their own at it; the updater swaps it like any root file. **No `.desktop` file** is shipped: `Exec` must be an absolute path or a name on `PATH`, and neither is known for an archive extracted anywhere (portable archives of other projects ship none either). The `.desktop` writer (`DesktopEntry`), the PNG icon (`PngIcon`: a PNG as it is, from an ICO the largest image, bitmaps of 1, 4, 8, 24 or 32 bits re-encoded as PNG with the AND mask as transparency) and the package name (`LinuxPackageName`: last segment of `app.id` lower-cased or `[linux] package`, validated against Debian's rule, the strictest of deb/rpm/apk) exist now for deb, rpm and apk; `[linux] package`, `icon` and `categories` are read and validated, the other `[linux]` keys of section 4 come with their formats.
- **Writer.** `ReproducibleTar`: GNU tar format, because `TarWriter`'s pax format names its extended headers `PaxHeaders.<process id>` and ustar limits names; entries sorted ordinally, mtime 1980-02-01 00:00 (as the zip), uid/gid 0 without user or group names; the gzip header's OS byte is overwritten with 3 (Unix), since zlib records the host there. CI compares a repro tar.gz packed on Ubuntu, macOS and Windows.
- **Modes.** Directories and executables 0755, other files 0644, independent of the umask. Executable means: `input.main`, an ELF header, `#!`, or (on Linux and macOS hosts) any execute bit. Windows hosts have no execute bits and rely on the first three; output is host-independent whenever the input's execute bits agree with them, as in usual build outputs. Symbolic links in the input are kept only when the updater would accept them (relative, inside `app/`, not through another link) and are an error otherwise, so an archive that installs also updates; directory links are never followed.
- **musl.** The archive always carries the glibc updater of its architecture. Target detection cannot tell glibc from musl, and the feed's target (os, arch, format, scope) could not tell a glibc and a musl tar.gz apart, so a musl tar.gz would need a new target dimension. Decision: tar.gz is a glibc format; `pack` warns when the main executable's ELF interpreter is musl's (`ld-musl-*`); musl systems are served by apk (step 8) with the musl updater. No `linux-musl-*` value for `[input] target` was added.
- **`minimumUpdater`** of tar.gz fragments is `0.2.0-0`: 0.2.0 is the first updater built for Linux, and it applies `tar-gz` (step 4).
- **e2e.** `tests/e2e/unix/run-tar-e2e.sh` runs the shared update scenario on `ubuntu-24.04` and `ubuntu-24.04-arm` for the archives packed on each of the three hosts, with the fixture app published framework-dependent for `linux-x64` and `linux-arm64`, plus a non-writable root (exit 7). `doctor` reports the Linux icon when Linux is a target.

### replace — AppImage (refines 0002 §7)

An AppImage is a single read-only file, so the 0002 directory layout cannot apply. Decision: the AppImage file **is** the root. The runtime sets `$APPIMAGE` (absolute path of the file) for the application; the updater inherits it and refuses `apply` without it. `apply` downloads into `<dir>/.<file>.zexoro-new`, verifies it, sets mode 0755 and renames it over `$APPIMAGE` (atomic on the same filesystem; the running process keeps the old inode). The file name is kept even if it contains the old version. A non-writable directory exits 7. `check` works from the read-only mount because state lives outside the install (below).

### notify — deb, rpm, apk

The updater never touches files owned by the package manager. `check` works as today and its JSON gains `url` (the artifact URL from the signed feed), so the application can link to it. `download` and `apply` exit 7 (manual update needed) with a message naming the package manager. The feed artifact is the package itself (`format` = `deb`/`rpm`/`apk`, `scope` = `machine`). Repository metadata (apt, dnf, apk indexes) is out of scope; a developer who runs a repository just points `notes` or the application UI at it.

**As implemented for deb (step 6).** `DebPackager` (`src/Zexoro.Formats.Linux`) writes the package without external tools: an ar archive written by hand (`!<arch>`, members `debian-binary` = `2.0`, `control.tar.gz`, `data.tar.gz`, names without GNU's trailing `/` as `dpkg-deb` writes them, mtime 1980-02-01 00:00, owner 0, mode 100644) whose tar members come from `ReproducibleTar` in the form `dpkg-deb` produces (a `./` entry first, `./`-prefixed names, owner and group named `root`, an entry for every parent directory). The package is reproducible like the tar.gz, and CI compares a repro deb packed on Ubuntu, macOS and Windows. Decisions and refinements:

- **Strategy `notify` in the updater.** `check` is unchanged except that its JSON has a `url` field for every strategy (the selected artifact's URL; `null` when up to date), which is additive. For a record with strategy `notify`, `download` and `apply` exit 7 before any network access and without touching the install or the updater's state (no downloads, no `last-apply.json`), naming the package manager by format (`apt or dpkg`, `dnf, zypper or rpm`, `apk`). Before this step `apply` already exited 7 for an unknown strategy, but `download` would have fetched the package.
- **`minimumUpdater` stays `0.2.0-0`** for deb fragments, as for tar.gz: 0.2.0 is not released yet and is the first Linux updater, so every Linux updater that will be released knows `notify`, and no `VersionPrefix` bump is needed. (A 0.2.0 development build from before this step would `check` correctly anyway; only its `download` would fetch the package needlessly.)
- **Version.** `DebVersion`: a pre-release becomes `~` plus its identifiers (`1.2.3-beta.1` → `1.2.3~beta.1`); build metadata is dropped, because SemVer and the updater ignore it for precedence, so dpkg must see `1.2.3+ci.5` and `1.2.3` as the same version too; no Debian revision is written. Pre-release identifiers containing `-` are refused (`pack` fails): Debian allows `-` in an upstream version only before a revision, and adding one would change the order. Accepted difference: SemVer orders a numeric identifier before an alphanumeric one at the same position (`alpha.1` < `alpha.beta`), dpkg the other way round; consistent schemes (`alpha.N`, `beta.N`, `rc.N`) compare identically (unit-tested against dpkg's algorithm). The install record keeps the full SemVer version.
- **File name.** Debian's convention, `<pkg>_<debversion>_<arch>.deb` (`amd64`, `arm64`), as section 2 allows for package managers with their own convention; `dpkg-name` and users expect it. Pre-release names contain `~`, an unreserved URL character; hosts that rename unusual characters in uploads (to be verified for GitHub release assets) would break the `{file}` URL of such fragments.
- **Layout.** As in section 2. The `.desktop` file's `Exec` is `/usr/bin/<pkg>`, `Icon` the theme name `<app.id>`; the icon goes to `/usr/share/icons/hicolor/<n>x<n>/apps/` when it is square and the theme has a directory for its size (16 … 512), else to `/usr/share/pixmaps/`. `/usr/bin/<pkg>` is an absolute link (Debian policy: links between top-level directories are absolute). No maintainer scripts, no conffiles.
- **Control fields.** `Package`, `Version`, `Architecture`, `Maintainer` (`[linux] maintainer`, else `app.publisher` with a notice when it has no `<email>`; control characters are refused), `Installed-Size` (each file rounded up to KiB, one KiB per directory and link, as dpkg-gencontrol estimates), `Depends`, `Recommends`, `Section: misc`, `Priority: optional`, `Homepage` (`app.url`), `Description` (first line of `app.description` or `app.name`; further lines as the extended description). `md5sums` lists every regular file. **With `[updates]` zexoro prepends the updater's own needs to `Depends`**: `libc6 (>= 2.28)` (the glibc it is built against, step 4) and `libssl3t64 | libssl3 | libssl1.1` (.NET loads OpenSSL for HTTPS and for verifying the feed's ECDSA signature; a minimal `debian:stable` may lack it), plus `Recommends: ca-certificates`. `[linux.deb] depends` follows verbatim (one line each). `[linux] maintainer` and `[linux.deb] depends` are the only new keys.
- **Not signed** (section 3): dpkg verifies no package signatures.
- **lintian** (run in the `debian:stable` container of the e2e): errors accepted and documented — `dir-or-file-in-opt` (third-party packages belong in `/opt`, which is the point of the layout), `no-copyright-file` and a missing changelog (zexoro knows neither the application's license nor its history; a copyright file with invented content would be worse than none). Other findings are printed in the job log; warnings and informational tags do not fail the test.
- **e2e.** `tests/e2e/unix/run-deb-e2e.sh` on `ubuntu-24.04` and `ubuntu-24.04-arm` (install with `apt-get install ./…deb`, upgrade with `dpkg -i`, `dpkg --remove`/`--purge`, then `dpkg -i` and `apt-get purge`) and the same script as root in a `debian:stable` container with lintian, for the packages of each pack host; on the runners the notify scenario runs against the installed `/opt/<pkg>/zexoro-updater` as an unprivileged user.

### bundle — macOS `.app` (zip and dmg)

- **Update payload is always the zip.** With `[updates]`, macOS packing always produces `<name>-<version>-osx-<arch>.zip` and its release fragment (artifact `format` = `app`), also when only `dmg` is selected. The dmg is a first-install container only. This keeps `hdiutil` out of the updater.
- `apply`: refuse (exit 7, with a message to move the app to Applications) when the bundle path contains an `AppTranslocation` component or lies on a read-only volume (a mounted dmg). Extract the zip (symlinks, modes) into `<parent>/.<name>.app.zexoro-new` on the same volume. Verify with Security.framework through P/Invoke (OS API, like the registry on Windows; no extra dependency): `SecStaticCodeCheckValidity` (strict, all architectures) on the new bundle, its `CFBundleIdentifier`, and that it satisfies the **installed** bundle's designated requirement (`SecCodeCopyDesignatedRequirement`). Failure exits 4. Swap with `renamex_np(RENAME_SWAP)` (atomic exchange), falling back to two renames with rollback; delete the old bundle.
- Parent not writable (a standard user and `/Applications`): 0002 §7 requires asking for authorization. The updater runs its temporary copy through `osascript … with administrator privileges` for the swap only. This is the one place where zexoro's updater, not an installer, triggers an elevation prompt; 0002 allows it for `bundle` explicitly.
- Files written by the updater's own HTTP client get no quarantine attribute, so the updated app does not trigger Gatekeeper's first-launch prompt again.
- Consequence: an ad-hoc signed bundle's designated requirement is its own hash, which no new version can satisfy. Therefore `pack` refuses `[updates]` for macOS without `[macos.signing]`. Any stable certificate works technically (CI uses a self-signed one); distribution needs a Developer ID.

### reinstall — MSI, pkg

- **MSI:** the updater starts `msiexec.exe /i <file> /passive /norestart` through `ShellExecute`; Windows Installer raises UAC itself, as the NSIS per-machine installer does. `MajorUpgrade` (scheduled `afterInstallInitialize`) replaces the old version in place. Exit codes: 0 and 3010 → 0, 1602 (cancelled, including declined UAC) → 6, anything else → 5. Verified constraint: MSI `ProductVersion` fields are limited to 255.255.65535 and only the first three are compared, so `pack --format msi` rejects versions beyond that; prereleases map to the same `ProductVersion` as their release, so `AllowSameVersionUpgrades` is set. `MajorUpgrade` refuses lower `ProductVersion`s; between builds with the same `ProductVersion` (`1.2.3-beta.1` and `1.2.3`) only the updater enforces SemVer precedence, and a manual install of the older one is not refused. Accepted: the alternative is a custom action, which this design avoids.
- **pkg:** macOS has no way to run an installer silently with elevation without the caller requesting authorization. Decision: the updater opens the package with `open -W <file>` so Installer.app asks for authorization itself (honouring "elevation is requested by the installer"), then reads the record again to decide success (`open` reports no installer result). This is **not silent**, which deviates from 0002 §7; accepted because the alternative (`osascript` running `/usr/sbin/installer` as root) makes the updater the elevating party. Re-evaluate when pkg is implemented.

### New updater work

| Piece | Needed for |
| --- | --- |
| Archive extraction into `app.new` | zip, tar.gz |
| Updater builds: `linux-x64`, `linux-arm64` (glibc), `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` | all Linux and macOS formats |
| Unix platform layer: state in `$XDG_STATE_HOME/zexoro/updater/<app.id>/` (Linux) and `~/Library/Application Support/zexoro/updater/<app.id>/` (macOS); process wait without handles (`kill(pid, 0)` polling); self-copy to `$TMPDIR` with mode 0700; restart by exec | Linux and macOS |
| `notify`: `url` in `check --json`, exit 7 for `download`/`apply` | deb, rpm, apk |
| Single-file swap via `$APPIMAGE` | AppImage |
| Security.framework checks, translocation, `RENAME_SWAP`, authorization fallback, universal (fat) updater written by `pack` from the two Mach-O slices | macOS bundle |
| `msiexec` exit-code mapping; `open -W` + record re-read | MSI, pkg |

**As implemented (step 4).** The updater keeps its state in `UpdaterEnvironment.StateBase` + `zexoro/updater/<app.id>/`: `%LOCALAPPDATA%` on Windows, `$XDG_STATE_HOME` when it is absolute (the XDG specification ignores relative values), else `~/.local/state`, on Linux, `~/Library/Application Support` on macOS. Waiting for the application uses the BCL: `Process.WaitForExit` on a process that is not a child polls `kill(pid, 0)` on Linux and macOS, so no P/Invoke is needed; a tested fact on both systems (`UnixTests`). On Unix the temporary copy lives in a fresh `$TMPDIR/zexoro-updater-<guid>/` with mode 0700 (directory and file), keeps the updater's own file name, and only copies older than an hour are removed (Unix deletes running programs, so a younger copy may belong to a concurrent apply). The rename swap is unchanged: on POSIX `rename(2)` of `app` → `app.old` and `app.new` → `app` works while files in `app` are open or running, and the old updater can be deleted at once. Restart starts the main executable as a new process (not `exec`: the updater still has to report its result). Archive staging reads both formats into one plan and checks everything before writing; tar.gz extraction is implemented already (`System.Formats.Tar`): permission bits restored (no set-id or sticky bits; directories keep the owner's `rwx` so a later update can remove them), symbolic links allowed only inside `app/` with a relative target that stays in `app/` without passing through another link, no entry below a link, links created last, hard links, devices and FIFOs refused. **The record and feed format of tar.gz is `tar-gz`**, not `tar.gz`: feed tokens allow no `.`, and updaters up to 0.2.0 reject a whole feed with such a token, which would break the updates of every other platform sharing the feed; the configuration name stays `tar.gz`. Every CLI embeds the nine updaters gzip-compressed (`eng/Updater.targets`; about 45 % of their size) and decompresses one when packing. Linux glibc builds run in `almalinux:8` (glibc 2.28, RHEL 8, the oldest glibc distribution .NET 10 supports; .NET itself states 2.27 as its baseline, but has no supported distribution with it), musl builds in `alpine:3.21` (the oldest Alpine .NET 10 supports); `eng/updater/publish.sh` fails a glibc build that needs anything newer than 2.28. Measured on the first CI run: both glibc builds reference no symbol version newer than `GLIBC_2.17` (and need `libdl`, `librt`, `libm`, `libpthread`, `libc`), so 2.28 is the tested floor, not a technical one. Measured size cost: the CLI binary grew from about 25.2 MB (v0.1.0, three raw Windows updaters) to 38.8–39.3 MB, its release archive from about 13.4 MB to 34.2–35.1 MB (gzip inside gzip compresses no further). Embedding uncompressed would make the binary roughly 61 MB for an archive of roughly 32 MB; the compressed embedding was chosen for the installed size.

Native AOT cannot cross-compile between operating systems, so the `updater` job gains Linux (x64 and arm64 runners, glibc build inside an older-glibc container so the binary runs on every distribution .NET 10 supports; musl build inside an Alpine container) and macOS legs. All binaries are embedded into every CLI as today (`eng/Updater.targets`), so packing for Linux on Windows needs no Linux build. Release fragments of each new format set `minimumUpdater` to the first updater version that implements its strategy.

## 3. Dependencies

- **.NET, BCL only, no external tools:** zip, tar.gz, deb (ar written by hand; `control.tar.gz`, `data.tar.gz`; gzip is accepted by every dpkg), rpm (lead, signature header, header, gzip-compressed cpio; SHA-256 header and payload digests), apk (v2 format per `apk-v2(5)`: signature, control and data segments as concatenated gzip streams), squashfs for AppImage (zlib compression via `ZLibStream`), `.icns` and PNG icons (PNG-in-ICNS; a BMP entry in the `.ico` is re-encoded as PNG), Info.plist, fat Mach-O, `.desktop` files. All output is reproducible (fixed timestamps from the version's pack time, sorted entries).
- **Package signing in .NET:**
  - **rpm:** upstream RPM 6.0 (released 2025-09-22) enforces signature checking by default; Fedora 43 kept the old default and plans enforcement for a later release. zexoro signs rpm headers with an OpenPGP v4 RSA/SHA-256 signature built in .NET from an armored RSA secret key (`gpg --export-secret-keys --armor`, optionally passphrase-protected with standard S2K/AES). Ed25519 OpenPGP keys are not supported: Ed25519 is not in the BCL. Unsigned rpms are still allowed, with a notice.
  - **apk:** `apk` refuses unsigned packages unless `--allow-untrusted`. zexoro signs with an RSA PEM key (what `abuild-keygen` produces); the signature entry is named after `[linux.apk.signing] key_name`.
  - **deb:** dpkg does not verify package signatures (repositories sign their `Release` files), so deb is not signed.
- **Toolsets built from source by zexoro and embedded, never downloaded at run time:**
  - `zexoro/appimage-toolset`: builds `AppImage/type2-runtime` (MIT; statically linked against musl, with zlib and zstd for squashfs) from a pinned commit for x86_64 and aarch64. Unlike makensis these are **target** payloads, so every CLI embeds both. The runtime still needs `fusermount`/`fusermount3` on the user's machine, or `--appimage-extract-and-run`.
  - `zexoro/wix-toolset`: builds the needed subset of WiX 3.14.1 (tag `wix3141rtm` of `wixtoolset/wix3`; MS-RL; the repository was archived on 2025-02-14 and FireGiant announced no further public v3 releases) — `candle`, `light`, `WixUtilExtension`, `WixUIExtension` — on a Windows runner. WiX 3 runs on the .NET Framework 4.x that ships with Windows and needs Windows (`msi.dll`), so MSI packs only on Windows hosts and only the `win-x64` CLI embeds it. MS-RL obliges us to publish modified WiX sources, which the public toolset repository does.
- **Host-OS tools used, not shipped (macOS):** `codesign`, `hdiutil`, `pkgbuild`, `productbuild`, `productsign` (part of macOS) and `xcrun notarytool`/`stapler` (Xcode command line tools). They are platform components that cannot be redistributed, so the "built and shipped by zexoro" rule cannot apply; `doctor` reports their presence. Consequently **all macOS formats pack only on macOS** in stage 5; other hosts fail with a clear message. Producing an unsigned bundle elsewhere is not useful: Apple silicon runs only signed arm64 code (ad-hoc at least), and editing a bundle invalidates its signature.
- **macOS signing and notarization — what is feasible:**
  - Signing: `codesign --sign <identity> --options runtime --timestamp`, inside out (nested code, the updater, then the bundle), with optional entitlements; without `[macos.signing]` the bundle is ad-hoc signed (`--sign -`) with a notice. dmg and pkg are signed with `codesign` and `productsign` respectively.
  - Notarization: `xcrun notarytool submit --wait` with an App Store Connect API key (`.p8`, key ID, issuer from the environment), then `stapler staple` on the `.app` (re-zipped afterwards: a zip cannot be stapled), the dmg and the pkg. Requires a paid Apple Developer account; skipped with a notice when not configured.
  - Recommendation for a later stage, not stage 5: implementing Mach-O signing (CodeDirectory, requirements, CMS) and the notary REST API in .NET, as was done for Authenticode, is feasible (independent implementations exist) and would make macOS packing cross-platform. It is a large, separate piece of work with its own design document; stapling and Gatekeeper behaviour would need a real Mac for verification either way.
- **MSI signing:** Authenticode for MSI is not a PE signature; it is a `DigitalSignature` stream in the OLE compound file over the sorted streams. Since MSI packs only on Windows, zexoro reads and writes the compound file through `ole32` structured-storage APIs (P/Invoke, OS API) and reuses `Zexoro.Signing`'s CMS builder. `signtool` is not used.

## 4. Configuration

Fully backward compatible: a configuration valid today produces exactly the same NSIS installer.

```toml
[windows]
formats       = ["nsis", "zip"]       # nsis | zip | msi; default ["nsis"]
install_scope = "user"                # nsis only; zip is always user, msi always machine

[linux]
formats    = ["tar.gz", "deb"]        # tar.gz | appimage | deb | rpm | apk; default ["tar.gz"]
package    = "notes"                  # default: last segment of app.id
icon       = "assets/app.png"         # default: largest image of [app] icon
categories = ["Office"]               # .desktop Categories
maintainer = "Example Ltd. <dev@example.com>"   # deb/rpm/apk; default: app.publisher

[linux.deb]
depends = ["libicu74 | libicu72"]     # verbatim; likewise [linux.rpm] requires, [linux.apk] depends

[linux.rpm.signing]
key      = { env = "RPM_SIGNING_KEY" }       # armored OpenPGP RSA secret key
password = { env = "RPM_SIGNING_PASSWORD" }  # optional

[linux.apk.signing]
key      = { env = "APK_SIGNING_KEY" }       # RSA PEM
key_name = "dev@example.com-6123abcd.rsa.pub"

[macos]
formats         = ["dmg"]             # zip | dmg | pkg; default ["zip"]
bundle_id       = "com.example.notes" # default app.id
icon            = "assets/app.icns"   # default: converted from [app] icon
minimum_version = "11.0"
category        = "public.app-category.productivity"

[macos.signing]
identity     = "Developer ID Application: Example Ltd. (TEAMID1234)"
keychain     = { env = "MACOS_KEYCHAIN" }      # optional
entitlements = "macos/entitlements.plist"      # optional
installer_identity = "Developer ID Installer: Example Ltd. (TEAMID1234)"   # pkg only

[macos.notarization]
key    = { env = "NOTARY_KEY_PATH" }   # App Store Connect API key (.p8)
key_id = { env = "NOTARY_KEY_ID" }
issuer = { env = "NOTARY_ISSUER" }
```

- **One `pack` run packs one build output for one target.** The target (OS and architecture) is read from the main executable: PE (existing), ELF `e_machine`, Mach-O `cputype` (fat = `universal`). `[input] dir` may be an existing `.app` bundle on macOS; its `Info.plist` (XML or binary) must match `bundle_id` and the version, otherwise a flat directory is wrapped into a new bundle. Inputs whose main file is not a native executable (a script) need `[input] target = "linux-x64"`.
- **Selecting formats:** the target OS's table decides the formats; `pack --format <f>[,<f>…]` overrides it (each must be valid for the target). New overrides `pack --input <dir>` and `--main <path>` let one configuration serve a CI matrix of build outputs. Absent platform tables mean defaults, as `[windows]` does today. Unknown keys and formats remain errors with suggestions.
- Platform tables that do not match the target are validated but ignored, so one file describes all platforms.

## 5. Completion criteria and tests

A format is done when: it packs on every host listed in section 1 with identical output (byte-identical where reproducible); unit tests cover its writer; CI installs, upgrades and uninstalls it on a real system; and its update strategy passes the shared update scenario on a real install. The **shared update scenario** (as in 0005 §4, driven by a portable script per OS family, `tests/e2e/windows/` for Windows and a new bash `tests/e2e/unix/`): install 1.0.0; `check` against `seq1` exits 1; exit 4 with nothing changed for a flipped feed byte, a feed signed with another key, a replayed `seq1` after `seq2`, and a downloaded artifact with one byte changed; `check`/`download`/`apply --wait-pid --restart` against `seq2` moves to 2.0.0 with the stale file gone and nothing staged left; `check` exits 1. For `notify`, the scenario ends at `check` (exit 0, `url` correct), `download`/`apply` exit 7 and nothing is touched; the upgrade itself is done with the package manager. Feeds are served by a local test-only HTTP server.

| Format | Unit tests | CI e2e (GitHub runners) | Independent checks (test-only tools) | Real machines |
| --- | --- | --- | --- | --- |
| Windows zip | layout, record, extraction safety, swap with injected failures | `windows-latest`, packed on all three hosts | — | Windows test machine over SSH (same scripts) |
| tar.gz | modes, symlinks, extraction safety | `ubuntu-24.04`, `ubuntu-24.04-arm` | `tar -tvf` | — |
| deb | ar/tar structure, control fields, version mapping (`1.2.3-beta.1` → `1.2.3~beta.1`) | `ubuntu-24.04` (dpkg/apt install, upgrade, remove, purge) and `debian:stable` container | `dpkg-deb --info/--contents`, `lintian` | — |
| rpm | header tags, digests, OpenPGP signature packet, version mapping (`~`) | containers `fedora:latest`, `rockylinux:9`, `opensuse/leap` (rpm/dnf/zypper); one run with `%_pkgverify_level all` to emulate RPM 6 enforcement | `rpm -K`, `rpmlint`, `gpg --verify` on the extracted signature | — |
| apk | segment layout, signature, version mapping (only `alpha.N`, `beta.N`, `rc.N` prereleases map; others are rejected) | containers `alpine:3.22` (apk-tools 2) and `alpine:latest` (apk-tools 3) | `apk verify`, `tar -tzvf` per segment | — |
| macOS zip | Info.plist, icns, fat Mach-O, translocation and DR logic with fakes | `macos-15` (arm64) and `macos-15-intel`: temporary keychain with a self-signed code-signing certificate; extra negative cases: a bundle signed with another certificate and a bundle with a modified resource (exit 4), the app run from a read-only dmg mount (exit 7) | `codesign --verify --deep --strict` | developer Mac: Developer ID signing, notarization, Gatekeeper first launch, standard-user authorization prompt (needs an Apple Developer account) |
| dmg | — (thin wrapper) | `macos-15`: attach, copy to `~/Applications`, then the macOS zip update scenario | `hdiutil verify` | developer Mac with notarization |
| AppImage | squashfs writer against the format spec | `ubuntu-24.04` with `fuse3` installed, and once with `APPIMAGE_EXTRACT_AND_RUN=1` | `unsquashfs -l`, `--appimage-extract` | — |
| MSI | WiX source generation (snapshot), version limits, MSI signature structure | `windows-latest` elevated (UAC is off there): `msiexec /i /qn`, major upgrade, downgrade refused, uninstall, update via `ShellExecute` | `Get-AuthenticodeSignature`, `osslsigncode verify` on Linux | Windows test machine: UAC prompt and declining it (exit 6), reached through the remote desktop session for viewing |
| pkg | component plist, distribution XML | `macos-15`: `sudo installer -pkg` install and upgrade, `pkgutil --forget` uninstall | `pkgutil --check-signature` | developer Mac: the `open -W` update path (needs a GUI session) |

The Windows test machine (SSH, elevated) repeats every Windows e2e script; the remote desktop session is only for observing prompts. Anything not covered by CI is listed as a manual check in the format's pull request.

## 6. Steps

One pull request each into `dev`, merged when CI passes. Each step updates `README.md` and `docs/testing.md` for what it adds.

1. `phase-5/design` — this document.
2. `phase-5/targets` — target detection (PE/ELF/Mach-O), `formats` per platform table, `pack --format/--input/--main`, `[input] target`, a packager abstraction with NSIS moved behind it; no new format.
3. `phase-5/windows-zip` — zip packager, archive-extraction `replace` in the updater, Windows e2e for zip.
4. `phase-5/updater-unix` — Unix platform layer, updater builds for the six new RIDs in `ci.yml` and `release.yml`, embedding and `doctor`.
5. `phase-5/linux-tar` — tar.gz packager, Linux common pieces (package name, `.desktop`, PNG icon), `tests/e2e/unix/` harness, e2e on both Ubuntu runners.
6. `phase-5/deb` — deb writer, `notify` strategy in the updater, deb e2e.
7. `phase-5/rpm` — rpm writer, OpenPGP RSA signing, container e2e.
8. `phase-5/apk` — apk writer and RSA signing, Alpine e2e with the musl updater.
9. `phase-5/macos-bundle` — bundle creation, Info.plist, icns, fat updater, `codesign`/`notarytool`/`stapler` integration, macOS zip packer, `doctor` checks.
10. `phase-5/macos-updates` — `bundle` strategy, macOS e2e.
11. `phase-5/dmg` — dmg packer and e2e.
12. `phase-5/appimage-toolset` — new repository `zexoro/appimage-toolset`, first release, `eng/toolsets.json` entry.
13. `phase-5/appimage` — squashfs writer, AppImage packer, single-file `replace`, e2e.
14. `phase-5/wix-toolset` — new repository `zexoro/wix-toolset` building the WiX 3.14.1 subset, first release, embedding in the `win-x64` CLI.
15. `phase-5/msi` — MSI packer, `reinstall` via `msiexec`, e2e.
16. `phase-5/msi-signing` — Authenticode for MSI through structured storage, signature e2e.
17. `phase-5/pkg` — pkg packer, signing, `open -W` update path, e2e.
18. `phase-5/release` — release workflow smoke tests per format, example workflows for all three platforms; then the `dev → main` pull request.

Steps 12 and 14 can run in parallel with steps 3–11 since they live in other repositories.

## 7. Risks and out of scope

Risks:

- **WiX 3 from source.** The archived build expects older Visual Studio and .NET Framework targeting packs; the subset build on current runners may need patches (published under MS-RL). Fallback if it cannot be made reliable: defer MSI and record the reason; zexoro still does not ship binaries it did not build.
- **apk-tools 3.** Alpine 3.23 uses apk-tools 3 while still shipping v2 packages, and apk-tools 3.0 dropped support for malformed v2 packages from foreign tools. zexoro's writer must follow `apk-v2(5)` exactly and is tested against both apk-tools versions; a future switch of Alpine to v3 packages would need a v3 (ADB) writer.
- **RPM signing enforcement** arrives distribution by distribution; the OpenPGP subset (RSA only) may need Ed25519 later, which the BCL lacks.
- **glibc floor.** The Linux updater must run on older distributions than the CLI's build host. Fixed in step 4: built on glibc 2.28 (`almalinux:8`, enforced by the build); the binaries reference only `GLIBC_2.17` symbols, so older systems may work but are untested and unsupported by .NET 10.
- **macOS tests without an Apple Developer account** cover signing mechanics and update logic but not notarization or Gatekeeper; those stay manual.
- **pkg updates are not silent** (section 2); if that proves unacceptable, pkg may move to `notify`.
- **AppImage runtime needs FUSE** on user machines; distributions without `fusermount` require extract-and-run.

Out of scope for stage 5:

- Self-implemented Mach-O signing and notarization (later stage, own design), and therefore macOS packing on Linux or Windows.
- Repository metadata and hosting (apt, dnf, apk indexes), Flatpak, Snap, Microsoft Store, Mac App Store, MSIX.
- Per-user MSI, WiX 4 or later.
- Delta updates, staged rollouts, background update checks (0002 §8).
- File associations, URL protocols, services, autostart, installer UI customization (product non-goals).

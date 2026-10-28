# zexoro

Package applications built with any technology into native installers, and keep them up to date.

> **Status:** early (0.x). zexoro builds signed, automatically updatable Windows installers (NSIS), portable Windows zip archives, portable Linux tar.gz archives, Debian packages (deb), signed rpm packages and signed Alpine packages (apk) from an already-built application directory, on Linux, macOS or Windows. Other formats are not available yet, and the configuration may still change between 0.x releases. See the [product definition](docs/product.md) for where it is going.

## Install

zexoro is a single self-contained executable; no .NET installation is needed and the NSIS toolset is built in. Download the archive for your platform from the [releases](https://github.com/zexoro/zexoro/releases):

| Platform | Archive |
| --- | --- |
| Linux x64 (glibc 2.34+, e.g. Ubuntu 22.04) | `zexoro-<version>-linux-x64.tar.gz` |
| Linux arm64 (glibc 2.34+) | `zexoro-<version>-linux-arm64.tar.gz` |
| macOS Intel | `zexoro-<version>-osx-x64.tar.gz` |
| macOS Apple silicon | `zexoro-<version>-osx-arm64.tar.gz` |
| Windows x64 | `zexoro-<version>-win-x64.zip` |

Verify the download against `SHA256SUMS` from the same release, then put the executable on your `PATH`:

```bash
version=0.1.0 rid=linux-x64
base=https://github.com/zexoro/zexoro/releases/download/v$version
curl -fsSLO "$base/zexoro-$version-$rid.tar.gz" -O "$base/SHA256SUMS"
sha256sum --check --ignore-missing SHA256SUMS      # macOS: shasum -a 256 --check --ignore-missing SHA256SUMS
mkdir -p ~/.local/bin && tar -xzf "zexoro-$version-$rid.tar.gz" -C ~/.local/bin zexoro
zexoro --version
```

On Windows, check the zip with `Get-FileHash zexoro-<version>-win-x64.zip -Algorithm SHA256` against its line in `SHA256SUMS`, and extract `zexoro.exe` into a folder on your `PATH`. The macOS binaries are not notarized: a file downloaded with a browser needs `xattr -d com.apple.quarantine zexoro` (downloads with `curl` are not quarantined).

## Quick start

```bash
zexoro init      # writes a commented zexoro.toml into the current directory
# edit zexoro.toml: [app] identity, [input] your build output, [windows] options
zexoro doctor    # checks the configuration, the files it names, the certificate and the bundled NSIS
zexoro pack      # builds dist/<name>-<version>-win-<arch>.exe (and .zip with formats = ["nsis", "zip"]),
                 # or dist/<name>-<version>-linux-<arch>.tar.gz for a Linux build output
                 # (and dist/<pkg>_<version>_<arch>.deb, dist/<pkg>-<version>-1.<arch>.rpm and
                 #  dist/<pkg>-<version>-r0.<arch>.apk with formats = ["tar.gz", "deb", "rpm", "apk"]),
                 # or dist/<name>-<version>-osx-<arch>.zip (a signed .app) for a macOS build output, on macOS
```

`zexoro pack --version 1.2.3` overrides `app.version` (useful in CI), `--output <dir>` writes somewhere other than `dist/` next to the configuration, and `--config <path>` reads another configuration file than `./zexoro.toml`. `--input <dir>` and `--main <path>` replace `input.dir` and `input.main` (relative to the current directory and to the input directory), so one configuration can serve a CI matrix of build outputs, and `--format <f>[,<f>...]` replaces the formats of the target platform's table. With [`[updates]`](#updates-optional), `--channel <channel>` overrides `updates.channel` and `--notes <url>` sets the release notes URL of the release fragment.

zexoro packages build output; it never builds your application. One `pack` run packages one build output for one target platform, which zexoro reads from the main executable's header:

| Main executable | Target |
| --- | --- |
| PE (`.exe`) for x86, x64 or arm64 | `win-x86`, `win-x64`, `win-arm64` |
| ELF, 64-bit little-endian, for x86-64 or AArch64 | `linux-x64`, `linux-arm64` |
| Mach-O for x86_64 or arm64; a universal binary with both slices | `osx-x64`, `osx-arm64`; `osx-universal` |

An entry point that is not a native executable (a script) needs [`[input] target`](#input-required). The target platform's table (`[windows]`, `[linux]` or `[macos]`) decides which formats are built; the tables of the other platforms are still checked, but ignored, so one file can describe every platform. Windows `nsis` and `zip`, Linux `tar.gz`, `deb`, `rpm` and `apk`, and macOS `zip` are implemented so far: the other formats are recognized, and `pack` refuses them with "not implemented yet" before building anything.

## Configuration

`zexoro.toml` is [TOML](https://toml.io/). Paths are relative to the configuration file. Unknown keys are errors, and every error names the line and column.

### `[app]` (required)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | string | required | Permanent reverse-DNS identity, for example `com.example.notes`: two or more dot-separated segments of letters, digits and `-`, each starting with a letter. Upgrades are recognized by it, so never change it. |
| `name` | string | required | Display name, also the install directory name, so it must be a valid file name. |
| `version` | string | required | [Semantic Versioning 2.0.0](https://semver.org/). Major, minor and patch must each be at most 65535. `pack --version` overrides it. |
| `publisher` | string | required | Shown in Installed apps. |
| `icon` | path | required | `.ico` file for the installer, uninstaller and Installed apps. |
| `description` | string | none | Shown as the comment in Installed apps. |
| `url` | string | none | Absolute `http`/`https` URL of the application's web site; shown in Installed apps and in the signatures. |

### `[input]` (required)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `dir` | path | required | The build output directory to install. |
| `main` | string | required | The main executable, relative to `dir` (no absolute paths, `.` or `..`). Shortcuts point to it. |
| `target` | string | from `main` | The target platform when `main` is not a native executable: `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` or `osx-universal`. When `main` is a native executable, `target` must match it. |

### `[windows]` (optional)

Used when the target is Windows; an absent table means the defaults.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `formats` | array of `"nsis"`, `"zip"`, `"msi"` | `["nsis"]` | The formats to build: `nsis` an installer, `zip` a [portable archive](#the-portable-zip). `msi` is not implemented yet. |
| `install_scope` | `"user"` \| `"machine"` | `"user"` | Per-user install without elevation, or per-machine install with elevation (`nsis` only; a zip is always per-user). |
| `shortcuts` | array of `"start-menu"`, `"desktop"` | `["start-menu"]` | Shortcuts to create; `[]` for none (`nsis` only). |
| `license` | path | none | Text file shown on a license page; no page when absent (`nsis` only). |
| `sign` | `"main"` \| `"all"` | `"main"` | With signing configured: sign only the main executable, or every PE file (`.exe`, `.dll`) in `dir` that is not signed already. |

### `[windows.signing]` (optional)

Omit the table to build unsigned installers (`pack` prints a notice). Every value can be a literal string or `{ env = "NAME" }`, read from the environment when packing.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `certificate` | string or `{ env }` | required | Path of the PFX (PKCS #12) file with the code-signing certificate and its RSA or ECDSA key. |
| `password` | string or `{ env }` | none | The PFX password. Prefer `{ env = "..." }` over a literal. |
| `timestamp` | string or `{ env }` | none | RFC 3161 timestamp authority URL, for example `http://timestamp.digicert.com`. |

### `[windows.uninstall]` (optional)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `remove` | array of strings | `[]` | Extra paths the uninstaller deletes, such as user data. Each starts with `%LOCALAPPDATA%`, `%APPDATA%` or `%PROGRAMDATA%` followed by a sub-path, for example `'%LOCALAPPDATA%\Example Notes'`. |

### `[linux]` and `[macos]` (optional)

Used when the target is Linux or macOS. Linux `tar.gz`, `deb`, `rpm` and `apk` and macOS `zip` are implemented; `appimage`, `dmg` and `pkg` are not yet, but the tables accept their names already.

| Table | Key | Type | Default | Description |
| --- | --- | --- | --- | --- |
| `[linux]` | `formats` | array of `"tar.gz"`, `"appimage"`, `"deb"`, `"rpm"`, `"apk"` | `["tar.gz"]` | `tar.gz` is a [portable archive](#the-portable-targz), `deb` a [Debian package](#the-deb-package), `rpm` an [rpm package](#the-rpm-package), `apk` an [Alpine package](#the-apk-package). |
| `[linux]` | `icon` | path | the largest image of `app.icon` | A PNG icon for Linux formats. Without it, `app.icon` is used: a PNG as it is, from an ICO the largest image (bitmaps are converted to PNG). |
| `[linux]` | `package` | string | last segment of `app.id`, lower-cased | The package name of the deb, rpm and apk formats: lower-case letters, digits, `+`, `-` and `.`, at least two characters, starting with a letter or digit. |
| `[linux]` | `categories` | array of strings | `[]` | The `Categories` of the `.desktop` launcher that deb, rpm and apk packages install, for example `["Office"]`. |
| `[linux]` | `maintainer` | string | `app.publisher` | The package maintainer of deb, rpm and apk packages, conventionally `"Name <email>"`; one line. |
| `[linux.deb]` | `depends` | array of strings | `[]` | Relations for the deb's `Depends`, written verbatim, for example `["libicu74 \| libicu72"]`. With `[updates]`, zexoro puts the updater's own dependencies first (see [the deb package](#the-deb-package)). |
| `[linux.rpm]` | `requires` | array of strings | `[]` | Dependencies for the rpm's `Requires`, one per entry as in a spec file: `"name"`, `"name >= version"` (spaces around the operator) or a rich dependency such as `"(libicu or libicu74)"`. With `[updates]`, zexoro adds the updater's own (see [the rpm package](#the-rpm-package)). |
| `[linux.rpm]` | `license` | string | none | The rpm's `License`, conventionally an SPDX expression such as `"MIT"`. Without it the tag is left out (with a notice): zexoro does not guess your license. |
| `[linux.rpm.signing]` | `key` | string or `{ env = "NAME" }` | none | The OpenPGP secret key rpm packages are signed with: the ASCII-armored key itself (`gpg --export-secret-keys --armor <key ID>`, usually from an environment variable) or the path of a file holding it. RSA only (2048 bits or more); without the table packages are unsigned, with a notice. |
| `[linux.rpm.signing]` | `password` | string or `{ env = "NAME" }` | none | The key's passphrase, when it has one. |
| `[linux.apk]` | `depends` | array of strings | `[]` | Dependencies for the apk, one apk dependency per entry without spaces: `"icu-libs"`, `"so:libicuuc.so.74"`, `"icu-libs>=74"`. With `[updates]`, zexoro adds the updater's own (see [the apk package](#the-apk-package)). |
| `[linux.apk]` | `license` | string | none | The apk's `license`, conventionally an SPDX expression. Without it the field is left out (with a notice). |
| `[linux.apk.signing]` | `key` | string or `{ env = "NAME" }` | none | The RSA private key apk packages are signed with, PEM (what `abuild-keygen` or `openssl genrsa` writes, unencrypted, 2048 bits or more): the key itself (usually from an environment variable) or the path of a file holding it. Without the table packages are unsigned, with a notice. |
| `[linux.apk.signing]` | `key_name` | string | required with `key` | The file name of the public key in `/etc/apk/keys`, for example `"dev@example.com-6123abcd.rsa.pub"`; apk finds the key by it. |
| `[macos]` | `formats` | array of `"zip"`, `"dmg"`, `"pkg"` | `["zip"]` | `zip` is a [signed `.app` bundle in a zip](#the-macos-zip). |
| `[macos]` | `bundle_id` | string | `app.id` | The bundle's `CFBundleIdentifier`: ASCII letters, digits, `-` and `.`. |
| `[macos]` | `icon` | path | converted from `app.icon` | An `.icns` file (used as it is) or a square PNG of 16, 32, 64, 128, 256, 512 or 1024 pixels. Without it, the largest image of `app.icon` becomes a one-image icns, so it must have one of those sizes. |
| `[macos]` | `minimum_version` | string | none | `LSMinimumSystemVersion`, for example `"12.0"`. |
| `[macos]` | `category` | string | none | `LSApplicationCategoryType`, for example `"public.app-category.productivity"`. |
| `[macos.signing]` | `identity` | string or `{ env = "NAME" }` | required | The code-signing identity `codesign --sign` takes: the certificate's name, such as `"Developer ID Application: Example Ltd. (TEAMID1234)"`, or its SHA-1 hash. Without the table bundles are signed ad hoc, with a notice. |
| `[macos.signing]` | `keychain` | string or `{ env = "NAME" }` | the keychain search list | The keychain file holding the identity, for example a temporary CI keychain. On macOS 15 and earlier, codesign finds the key only when that keychain is also on the search list (`security list-keychains -d user -s <keychain> <the current list>`). |
| `[macos.signing]` | `entitlements` | path | none | An entitlements property list for the application (applied to the bundle, not to nested code). A framework-dependent .NET application under the hardened runtime needs `com.apple.security.cs.allow-jit` and `com.apple.security.cs.disable-library-validation`. |
| `[macos.signing]` | `installer_identity` | string or `{ env = "NAME" }` | none | The installer-signing identity for `pkg` (not implemented yet). |
| `[macos.notarization]` | `key` | string or `{ env = "NAME" }` | required | Path of the App Store Connect API key (`AuthKey_<id>.p8`). Needs `[macos.signing]`. |
| `[macos.notarization]` | `key_id` | string or `{ env = "NAME" }` | required | The API key's ID. |
| `[macos.notarization]` | `issuer` | string or `{ env = "NAME" }` | required | The issuer ID of your App Store Connect team. |

### `[updates]` (optional)

Makes installers and portable archives updatable: they then include `zexoro-updater.exe` (`zexoro-updater` in a tar.gz, deb, rpm or apk), record where to look for updates, and `pack` writes a release fragment `<package>.release.json` next to each package for `zexoro feed add` (see [Automatic updates](#automatic-updates)). Without the table, packages have no updater.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `feed` | string | required | URL template of the feed. It must contain `{channel}`, so every channel has its own feed: `"https://example.com/updates/{channel}/feed.json"`. |
| `download` | string | required | URL template of a package: `{file}` (the installer's or archive's file name), `{version}` and `{channel}`, for example `"https://example.com/dl/{version}/{file}"`. |
| `channel` | string | `"stable"` | The channel the installers follow: ASCII letters, digits, `.`, `-` and `_`. `pack --channel` overrides it. |
| `keys` | array | required | The trusted public keys, at least one: the base64 text that `zexoro keys new` prints, or `{ file = "zexoro-update.pub" }`. Rotate keys by listing the new one next to the old one (a release signed with the old key ships the new list). |

Both URLs must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `[::1]` (testing). The private key never belongs in `zexoro.toml`: `pack` does not need it, only `zexoro feed add` does.

```toml
[updates]
feed     = "https://example.com/updates/{channel}/feed.json"
download = "https://example.com/dl/{version}/{file}"
keys     = [{ file = "zexoro-update.pub" }]
```

`pack` needs a `zexoro-updater` for the installer's architecture: release builds of zexoro embed them for every platform the updater is built for (Windows x86, x64 and arm64; Linux x64 and arm64, glibc and musl; macOS x64 and arm64), so any host can pack for any target; `ZEXORO_UPDATER_DIR` overrides them (`zexoro doctor` reports which are available). The Linux glibc updaters are built on glibc 2.28 and run on RHEL 8, Debian 10, Ubuntu 18.10 and later (they reference no symbol newer than `GLIBC_2.17`, but nothing older than 2.28 is tested); the musl ones are built on Alpine 3.21.

## Signing

zexoro signs with Authenticode itself; `signtool` is not needed, so signing works on Linux and macOS too. With `[windows.signing]` configured, `pack` signs, in order, the application files chosen by `sign` (in a staging copy, never in your input directory), `zexoro-updater.exe`, the uninstaller, and the installer. A zip archive itself has no signature; the files in it are signed the same way.

```toml
[windows.signing]
certificate = { env = "SIGN_CERT_PATH" }
password    = { env = "SIGN_CERT_PASSWORD" }
timestamp   = "http://timestamp.digicert.com"
```

The password reaches zexoro only through the environment; it is never written to disk or passed on a command line. Use a timestamp: without one, signatures stop being valid when the certificate expires. `zexoro doctor` checks that the certificate loads (the timestamp authority is not contacted).

## What the installer does

- **Location:** per-user installs go to `%LOCALAPPDATA%\Programs\<name>`, per-machine installs to `%ProgramFiles%\<name>` (`%ProgramFiles(x86)%` for x86 applications). Files live under `<root>\app\`, next to `uninstall.exe`, `zexoro-install.json` and, with `[updates]`, `zexoro-updater.exe`.
- **Installed apps:** an entry under `HKCU` or `HKLM` `...\Uninstall\<app.id>` with name, version, publisher, icon and size.
- **Upgrade:** installing over an existing version upgrades in place, where it is installed: the old files are removed, so nothing stale remains, and there is still one entry. The same version reinstalls; an older version is refused.
- **Running application:** the installer and uninstaller refuse to replace files while the application runs, and offer Retry/Cancel.
- **Uninstall:** removes the files, shortcuts and entry, what an interrupted update left behind and the updater's state (`%LOCALAPPDATA%\zexoro\updater\<app.id>`), but keeps user data unless it is listed in `[windows.uninstall] remove`.
- **Silent mode:** `/S` installs without UI; `/D=<dir>` chooses the location of a fresh install (an existing install is always upgraded where it is). The silent uninstall command is in the entry's `QuietUninstallString`.
- **Stage mode (internal):** with `[updates]`, the updater runs a per-user installer as `/S /ZEXORO_STAGE`; it then writes the new version next to the install (`app.new\` and `.new` files) without touching the registry or shortcuts, and the updater swaps it in.
- **Exit codes:** `0` success, `1` cancelled, `2` aborted (unsupported Windows, a per-machine install without administrator rights, or a refused stage mode), `3` a newer version is already installed, `4` the application is running or its files are in use.

## The portable zip

With `formats = ["zip"]` (or `pack --format zip`), `pack` writes `<name>-<version>-win-<arch>.zip`, which users extract wherever they like and run from there. Nothing is installed or registered: no Installed apps entry, no shortcuts, no elevation.

```
<name>\
  app\                  your build output
  zexoro-install.json   identity and update settings (format zip, scope user, strategy replace)
  zexoro-updater.exe    with [updates] only
```

The archive is reproducible: entries are sorted, every timestamp is 1980-02-01 00:00 and no host-specific attributes are recorded, so the same files give the same bytes on Linux, macOS and Windows. With `[updates]`, the updater replaces `app\` and the root files with those of the next archive (below); the folder must be writable by the user, otherwise `apply` exits 7 and the user updates by extracting the new version. Without `[updates]` the archive has no updater and no release fragment, as for installers.

## The portable tar.gz

For a Linux target (an ELF main executable, or `[input] target = "linux-x64"`/`"linux-arm64"`), `pack` writes `<name>-<version>-linux-<arch>.tar.gz` by default. Users extract it wherever they like (`tar -xzf`) and run it from there; nothing is installed system-wide.

```
<name>/
  app/                  your build output
  icon.png              the application icon (see [linux] icon), for launchers users create themselves
  zexoro-install.json   identity and update settings (format tar-gz, scope user, strategy replace)
  zexoro-updater        with [updates] only (mode 0755)
```

Modes are normalized so the archive does not depend on your umask: directories and executables 0755, other files 0644. A file is executable when it is `input.main`, starts with an ELF header or `#!`, or has an execute bit on a Linux or macOS packing host; packing on Windows, which has no execute bits, relies on the first three. Symbolic links are kept when their target is relative and stays inside the input directory; any other link is an error. Like the zip, the archive is reproducible (sorted entries, every timestamp 1980-02-01 00:00, owner and group 0 without names, GNU tar format), so the same files give the same bytes on Linux, macOS and Windows.

The archive carries the **glibc** updater of its architecture. zexoro cannot tell glibc from musl builds by their headers, and the feed could not tell two such archives apart, so tar.gz is for glibc distributions; `pack` warns when the main executable needs musl's loader. Alpine and other musl systems are served by the [apk package](#the-apk-package).

## The deb package

With `formats = ["deb"]` (or `pack --format deb`), `pack` writes a Debian binary package `<pkg>_<version>_<arch>.deb`, following Debian's naming: `<pkg>` is [`[linux] package`](#linux-and-macos-optional), the architecture `amd64` or `arm64`, and the version is `app.version` in Debian form: a pre-release gets `~` (`1.2.3-beta.1` becomes `1.2.3~beta.1`, which dpkg orders before `1.2.3`), build metadata is dropped (SemVer ignores it for ordering, so dpkg must too), and pre-release identifiers containing `-` are refused. Users install it with `sudo apt install ./<file>.deb` (or `dpkg -i`); it installs:

```
/opt/<pkg>/app/                         your build output
/opt/<pkg>/zexoro-install.json          identity and update settings (format deb, scope machine, strategy notify)
/opt/<pkg>/zexoro-updater               with [updates] only
/usr/bin/<pkg>                          a link to the main executable
/usr/share/applications/<app.id>.desktop
/usr/share/icons/hicolor/<n>x<n>/apps/<app.id>.png   (/usr/share/pixmaps/ for sizes the theme has no directory for)
```

The control file has `Package`, `Version`, `Architecture`, `Maintainer` ([`[linux] maintainer`](#linux-and-macos-optional), else `app.publisher`), `Installed-Size`, `Depends`, `Section: misc`, `Priority: optional`, `Homepage` (`app.url`) and `Description` (the first line of `app.description`, or `app.name`; further lines become the extended description); `md5sums` lists every file. There are no maintainer scripts. With `[updates]`, `Depends` starts with what the updater needs, `libc6 (>= 2.28)` and OpenSSL (`libssl3t64 | libssl3 | libssl1.1`, which .NET uses for HTTPS and the feed signature), followed by your [`[linux.deb] depends`](#linux-and-macos-optional), and `Recommends: ca-certificates`. The package is written by zexoro itself (no `dpkg-deb` needed) and is reproducible like the tar.gz, so any host packs the same bytes. It is not signed: dpkg does not check package signatures (apt repositories sign their indexes, and zexoro does not build repositories).

The package manager owns the installed files, so the updater never changes them (strategy `notify`): `check` reports a newer release and its package `url` from the signed feed, and `download` and `apply` exit 7. Your application tells the user, for example by linking to the `url` or to your apt repository; the user upgrades with `sudo apt install ./<new>.deb`.

Pre-release file names use `.` where the version has `~` (`notes_1.2.3.beta.1_amd64.deb` holds version `1.2.3~beta.1`): GitHub renames `~` to `.` in release assets, which would break the download URL of the release fragment.

## The rpm package

With `formats = ["rpm"]` (or `pack --format rpm`), `pack` writes an rpm package `<pkg>-<version>-1.<arch>.rpm`, following rpm's naming: the architecture is `x86_64` or `aarch64`, the release is always `1`, and the version is `app.version` mapped as for the deb (`1.2.3-beta.1` becomes `1.2.3~beta.1`, which rpm orders before `1.2.3`; build metadata dropped; `-` in pre-release identifiers refused; `.` instead of `~` in the file name). Users install it with `sudo dnf install ./<file>.rpm` or `sudo zypper install ./<file>.rpm`; it installs the same files as the deb, under `/opt/<pkg>/` (record format `rpm`), `/usr/bin/<pkg>`, the `.desktop` file and the icon. The package owns `/opt/<pkg>` and its own files, not system directories such as `/opt` or `/usr/bin` (distributions give those different modes). There are no scriptlets.

The header has `Name`, `Version`, `Release`, `Summary` (the first line of `app.description`, or `app.name`), `Description`, `License` ([`[linux.rpm] license`](#linux-and-macos-optional)), `Packager` (`[linux] maintainer`, else `app.publisher`), `Vendor` (`app.publisher`), `URL` (`app.url`), `Group: Unspecified`, the file list with SHA-256 digests, `Provides: <pkg> = <version>-1` (and the architecture-qualified name rpmbuild adds), and `Requires`. With `[updates]`, `Requires` has what the updater needs, `libc.so.6(GLIBC_2.28)(64bit)` and `(libssl.so.3()(64bit) or libssl.so.1.1()(64bit))` (OpenSSL, for HTTPS and the feed signature), plus your [`[linux.rpm] requires`](#linux-and-macos-optional), and `Recommends: ca-certificates`. The payload is a gzip-compressed cpio archive. zexoro writes the whole package itself (no `rpmbuild` needed); it is reproducible, so any host packs the same bytes, also when signed if `SOURCE_DATE_EPOCH` fixes the signature time.

**Signing.** With [`[linux.rpm.signing]`](#linux-and-macos-optional), the header is signed with your OpenPGP key (a version 4 RSA/SHA-256 signature, what `rpmsign` writes), which covers the payload through its SHA-256 digest in the header. RPM 6 refuses unsigned packages by default, and repositories normally require signatures, so sign releases. Create and export a key with gpg:

```bash
gpg --quick-gen-key "Example Ltd. <dev@example.com>" rsa4096 sign,cert 3y
gpg --export-secret-keys --armor dev@example.com > rpm-signing.key.asc   # keep secret: put it in a CI secret
gpg --export --armor dev@example.com > rpm-signing.pub.asc               # publish for your users
```

Users import the public key once with `sudo rpm --import rpm-signing.pub.asc`; `rpm -K <file>.rpm` then reports the signature as OK. Ed25519 keys (gpg's default) are not supported, because .NET has no Ed25519; zexoro says so and suggests an RSA key. The signature time is the current time, or `SOURCE_DATE_EPOCH` when set (it must not lie before the key was created). `zexoro doctor` checks that the key loads with its password.

Updates work as for the deb (strategy `notify`): `check` reports the new package's `url`, `download` and `apply` exit 7, and the user upgrades with `sudo dnf upgrade ./<new>.rpm` or `sudo zypper install ./<new>.rpm`.

## The apk package

With `formats = ["apk"]` (or `pack --format apk`), `pack` writes an Alpine package `<pkg>-<version>-r0.<arch>.apk` (`x86_64` or `aarch64`; abuild names its files without the architecture, which zexoro adds so both architectures fit into one GitHub release). Alpine uses musl, so build your application for `linux-musl-x64` or `linux-musl-arm64`; `pack` warns when the main executable needs glibc. The version is `app.version` with the release `r0`; apk versions allow only certain pre-release suffixes, so only the pre-releases `alpha.N`, `beta.N` and `rc.N` are supported (`1.2.3-beta.1` becomes `1.2.3_beta1`, which apk orders before `1.2.3`; build metadata is dropped) and `pack` refuses others. Users install it with `sudo apk add ./<file>.apk`; it installs the same files as the deb, under `/opt/<pkg>/` (record format `apk`), `/usr/bin/<pkg>`, the `.desktop` file and the icon, and a directory entry for each parent directory, as apk requires. There are no install scripts.

The package is apk's v2 format, which current Alpine releases install with apk-tools 2 and 3: three gzip streams (signature, control, data) that together form one tar archive, `.PKGINFO` with `pkgname`, `pkgver`, `pkgdesc` (the first line of `app.description`, or `app.name`), `url`, `builddate` (fixed), `packager` and `maintainer` (`[linux] maintainer`, else `app.publisher`), `size`, `arch`, `origin`, `license`, `depend` lines and the SHA-256 `datahash` of the data, and per-file SHA-1 checksums as abuild writes them. With `[updates]`, the package carries the musl build of `zexoro-updater` and depends on what it needs: `so:libc.musl-<arch>.so.1`, `so:libcrypto.so.3` and `so:libssl.so.3` (OpenSSL) and `ca-certificates-bundle`, all part of Alpine's base image, followed by your [`[linux.apk] depends`](#linux-and-macos-optional). zexoro writes the whole package itself (no `abuild` needed), and it is reproducible, signed or not.

**Signing.** apk refuses packages without a trusted signature unless given `--allow-untrusted`. With [`[linux.apk.signing]`](#linux-and-macos-optional), the control segment is signed with your RSA key (RSA with SHA-256, the `.SIGN.RSA256.<key_name>` entry that `abuild-sign -t RSA256` writes). Create a key with `abuild-keygen` or openssl:

```bash
openssl genrsa -out dev@example.com-6123abcd.rsa 4096                               # keep secret: put it in a CI secret
openssl rsa -in dev@example.com-6123abcd.rsa -pubout -out dev@example.com-6123abcd.rsa.pub   # publish for your users
```

Set `key_name = "dev@example.com-6123abcd.rsa.pub"`; users copy the public key to `/etc/apk/keys/` under exactly that name, after which `apk verify <file>.apk` reports it as OK and `apk add` installs it. `zexoro doctor` checks that the key loads.

Updates work as for the deb (strategy `notify`): `check` reports the new package's `url`, `download` and `apply` exit 7, and the user upgrades with `sudo apk add ./<new>.apk`.

## The macOS zip

With `formats = ["zip"]` in `[macos]` (the default), `pack` writes `<name>-<version>-osx-<arch>.zip` (`x64`, `arm64` or `universal`) holding a signed `<app.name>.app`. macOS formats pack **only on macOS**: signing uses the system's `codesign`, and notarization `xcrun notarytool` and `stapler` from the Xcode command line tools; other hosts fail with a clear message. Users unzip it (Finder or `ditto -x -k`) and move the app to Applications.

**The bundle.** A flat build output is wrapped into a bundle. `input.main` must be a Mach-O executable in the top directory of `input.dir`. codesign accepts only code in `Contents/MacOS`, but applications expect their files next to the executable, so `Contents/MacOS/` mirrors your build output: Mach-O files (executables, `.dylib`s) are stored there, every other file in `Contents/Resources/app/` at the same relative path, and each tree has relative symbolic links to the other's files, so every file is found from either directory. Symbolic links in the output are kept when they are relative and stay inside it. Nested bundles (`.app`, `.framework`, …) in a flat output are refused; build the `.app` yourself in that case and set `input.dir` to it: an existing bundle is copied as it is, after checking that its `Info.plist` (XML or binary) has `CFBundleIdentifier` = `[macos] bundle_id` (or `app.id`), `CFBundleShortVersionString` = the version's `major.minor.patch`, and that `input.main` is `Contents/MacOS/<CFBundleExecutable>`.

A generated `Info.plist` has `CFBundleIdentifier`, `CFBundleName` and `CFBundleDisplayName` (`app.name`), `CFBundleExecutable`, `CFBundlePackageType` `APPL`, `CFBundleShortVersionString` and `CFBundleVersion` (both `major.minor.patch`: Apple allows only numbers there, so `1.2.3-beta.1` becomes `1.2.3`; the full version is in the install record), `CFBundleIconFile` (`Contents/Resources/AppIcon.icns`), `LSMinimumSystemVersion`, `LSApplicationCategoryType`, `NSHighResolutionCapable` and `NSHumanReadableCopyright` (`app.publisher`). Every bundle gets the install record `Contents/Resources/zexoro-install.json` (format `app`, scope `user`, strategy `bundle`), and with `[updates]` `Contents/MacOS/zexoro-updater`: the osx-x64 or osx-arm64 build for a thin application, and for a universal one a universal binary that zexoro writes from both.

**Signing.** With [`[macos.signing]`](#linux-and-macos-optional), every nested code item (Mach-O files and nested bundles, deepest first, including the updater) and then the bundle are signed with `codesign --sign <identity> --options runtime --timestamp` (hardened runtime and a secure timestamp, which notarization requires; failures of Apple's timestamp service are retried), the entitlements on the bundle; `codesign --verify --deep --strict` must accept the result. Without the table the bundle is signed ad hoc (`--sign -`, without hardened runtime), which runs on the Mac that packed it but is blocked by Gatekeeper elsewhere. **`[updates]` requires `[macos.signing]`**: an update must satisfy the installed bundle's designated requirement, and an ad-hoc signature's requirement is its own hash. Any stable certificate works for that (the tests use a self-signed one); distribution needs a Developer ID Application certificate from a paid Apple Developer account.

**Notarization.** With [`[macos.notarization]`](#linux-and-macos-optional), the signed bundle is submitted with `xcrun notarytool submit --wait` (the API key, key ID and issuer), Apple's log is printed when it is not accepted, the ticket is stapled to the bundle with `xcrun stapler staple`, and the bundle is zipped again (a zip itself cannot be stapled). Without the table a notice says the bundle is not notarized.

The zip keeps Unix modes and symbolic links in the layout `ditto -c -k --keepParent` writes (no extended attributes), with sorted entries and fixed timestamps; `ditto -x -k` and Finder restore it with a valid signature. `zexoro doctor` reports `codesign`, `notarytool` and `stapler`, whether the signing identity is in the keychain, the icon conversion, and the notarization key file.

With `[updates]`, the zip is also the update payload (artifact format `app`, scope `user`) for every macOS container. In this version the bundled updater finds its record inside the bundle and `check` and `download` work, but `apply` exits 7 without touching the app: replacing a signed bundle comes with the next step.

## Automatic updates

Installers and portable archives packed with [`[updates]`](#updates-optional) include `zexoro-updater.exe` (signed with your certificate when `[windows.signing]` is configured), or `zexoro-updater` in a tar.gz, deb, rpm, apk or macOS bundle (`Contents/MacOS/zexoro-updater`), and come with release fragments. Publishing a release takes a key pair and a signed feed ([design 0005](docs/design/0005-nsis-updates.md)):

```bash
zexoro keys new                     # once: zexoro-update.key.pem (private) and zexoro-update.pub
zexoro pack                         # installer or zip + <package>.release.json
zexoro feed add --feed feed.json --key zexoro-update.key.pem dist/*.release.json
zexoro feed verify --public-key zexoro-update.pub feed.json
```

Upload the packages to the `download` URL first, then `feed.json` and `feed.json.sig` to the `feed` URL. `feed add` merges release fragments (packed on any number of hosts) into `feed.json`, increases its `sequence` and writes the detached signature `feed.json.sig`. The private key can also come from `ZEXORO_UPDATE_KEY` (the PEM text, for CI secrets); `ZEXORO_UPDATE_KEY_PASSWORD` encrypts a new key and decrypts an existing one.

> **Keep the private key out of the repository and back it up.** Installed copies trust only the keys listed in `[updates] keys`; if every trusted private key is lost, they can no longer be updated automatically and users must reinstall by hand.

Your application drives the installed updater, `<install root>\zexoro-updater.exe`:

```
zexoro-updater check    [--json]                       # 0 update available, 1 up to date; --json includes the package url
zexoro-updater download [--json]                       # downloads and verifies the update
zexoro-updater apply --wait-pid <pid> [--restart] [--json]
```

`apply` waits up to 60 seconds for the process `<pid>` (your application, which exits after starting it), installs the update and restarts the application with `--restart`. Per-user installs are updated in place by swapping the new files in (the new installer stages them; a zip or tar.gz install extracts the new archive next to itself, refusing absolute paths and `..`; a tar.gz keeps modes and symbolic links that stay inside `app/`, a zip may hold no links); per-machine installs run the new installer, which asks for elevation. Installs owned by a package manager (deb, rpm, apk) are only checked: `download` and `apply` exit 7 without touching anything, and the user installs the new package (its `url` is in `check --json`). One feed can carry the installer, the zip, the tar.gz archives and the deb, rpm and apk packages of a release: each install takes the artifact of its own operating system, architecture and format. The updater rejects a feed whose signature does not verify, an older feed than one already seen, and downloads whose size or SHA-256 differ from the feed (exit code 4). Other exit codes: `3` network, `5` apply failed and rolled back, `6` cancelled (elevation declined or the application did not exit), `7` manual update needed (including a zip or tar.gz in a folder the user cannot write to), `8` apply and rollback failed. The result of the last `apply` is in `%LOCALAPPDATA%\zexoro\updater\<app.id>\last-apply.json` (on Linux `$XDG_STATE_HOME/zexoro/updater/<app.id>/`, by default `~/.local/state/...`; on macOS `~/Library/Application Support/zexoro/updater/<app.id>/`).

## zexoro exit codes

`0` success, `1` failure (including `doctor` finding errors), `2` usage error.

## Environment variables

| Variable | Meaning |
| --- | --- |
| `ZEXORO_CACHE_DIR` | Where the bundled toolset is extracted. Default: `%LOCALAPPDATA%\zexoro`, `~/Library/Caches/zexoro` or `$XDG_CACHE_HOME/zexoro`. |
| `ZEXORO_NSIS_DIR` | Use this NSIS directory instead of the bundled one (development). |
| `ZEXORO_UPDATER_DIR` | A directory with `zexoro-updater-<rid>` files (`zexoro-updater-win-<arch>.exe` on Windows, e.g. `zexoro-updater-linux-musl-x64`) to put into packages instead of the embedded ones (development). |

## GitHub Actions

[`docs/examples/github-actions-windows-installer.yml`](docs/examples/github-actions-windows-installer.yml) is a complete workflow: it builds an application, downloads and verifies zexoro, packs a signed installer with the certificate from repository secrets (`SIGN_CERT_PFX_BASE64`, `SIGN_CERT_PASSWORD`), and uploads it. Copy it to `.github/workflows/` and adapt the build step. zexoro runs on Linux runners as well as Windows ones.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet publish src/Zexoro.Cli -c Release -r <rid>   # Native AOT for the host OS, e.g. osx-arm64, linux-x64, win-x64
```

Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml) from `v*` tags. More documentation: [`docs/`](docs/) (product definition, design notes, [testing](docs/testing.md)).

## License

[MIT](LICENSE)

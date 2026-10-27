# zexoro

Package applications built with any technology into native installers, and keep them up to date.

> **Status:** early (0.x). zexoro builds signed Windows installers (NSIS) from an already-built application directory, on Linux, macOS or Windows. Automatic updates and other formats are not available yet, and the configuration may still change between 0.x releases. See the [product definition](docs/product.md) for where it is going.

## Install

zexoro is a single self-contained executable; no .NET installation is needed and the NSIS toolset is built in. Download the archive for your platform from the [releases](https://github.com/zexoro/zexoro/releases):

| Platform | Archive |
| --- | --- |
| Linux x64 (glibc 2.38+, e.g. Ubuntu 24.04) | `zexoro-<version>-linux-x64.tar.gz` |
| Linux arm64 (glibc 2.38+) | `zexoro-<version>-linux-arm64.tar.gz` |
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
zexoro pack      # builds dist/<name>-<version>-win-<arch>.exe
```

`zexoro pack --version 1.2.3` overrides `app.version` (useful in CI), `--output <dir>` writes somewhere other than `dist/` next to the configuration, and `--config <path>` reads another configuration file than `./zexoro.toml`. With [`[updates]`](#updates-optional), `--channel <channel>` overrides `updates.channel` and `--notes <url>` sets the release notes URL of the release fragment.

zexoro packages build output; it never builds your application. The input directory must contain the main executable, an `.exe` for x86, x64 or arm64 Windows; the installer's architecture comes from its PE header.

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

### `[windows]` (optional; required by `pack`)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `install_scope` | `"user"` \| `"machine"` | `"user"` | Per-user install without elevation, or per-machine install with elevation. |
| `shortcuts` | array of `"start-menu"`, `"desktop"` | `["start-menu"]` | Shortcuts to create; `[]` for none. |
| `license` | path | none | Text file shown on a license page; no page when absent. |
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

### `[updates]` (optional)

Makes installers updatable: they then include `zexoro-updater.exe`, record where to look for updates, and `pack` writes a release fragment `<installer>.release.json` next to each installer for `zexoro feed add` (see [Update keys and feeds](#update-keys-and-feeds-preview)). Without the table, installers have no updater.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `feed` | string | required | URL template of the feed. It must contain `{channel}`, so every channel has its own feed: `"https://example.com/updates/{channel}/feed.json"`. |
| `download` | string | required | URL template of an installer: `{file}` (the installer's file name), `{version}` and `{channel}`, for example `"https://example.com/dl/{version}/{file}"`. |
| `channel` | string | `"stable"` | The channel the installers follow: ASCII letters, digits, `.`, `-` and `_`. `pack --channel` overrides it. |
| `keys` | array | required | The trusted public keys, at least one: the base64 text that `zexoro keys new` prints, or `{ file = "zexoro-update.pub" }`. Rotate keys by listing the new one next to the old one (a release signed with the old key ships the new list). |

Both URLs must be `https`; plain `http` is accepted only for `localhost`, `127.0.0.1` and `[::1]` (testing). The private key never belongs in `zexoro.toml`: `pack` does not need it, only `zexoro feed add` does.

```toml
[updates]
feed     = "https://example.com/updates/{channel}/feed.json"
download = "https://example.com/dl/{version}/{file}"
keys     = [{ file = "zexoro-update.pub" }]
```

`pack` needs a `zexoro-updater` for the installer's architecture: release builds of zexoro embed them for x86, x64 and arm64; `ZEXORO_UPDATER_DIR` overrides them (`zexoro doctor` reports which are available).

## Signing

zexoro signs with Authenticode itself; `signtool` is not needed, so signing works on Linux and macOS too. With `[windows.signing]` configured, `pack` signs, in order, the application files chosen by `sign` (in a staging copy, never in your input directory), the uninstaller, and the installer.

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

## Update keys and feeds (preview)

Automatic updates are being built (see [design 0005](docs/design/0005-nsis-updates.md)). Installers packed with [`[updates]`](#updates-optional) include the updater and come with release fragments; the signing side works:

```bash
zexoro keys new                     # zexoro-update.key.pem (private) and zexoro-update.pub
zexoro feed add --feed feed.json --key zexoro-update.key.pem <fragment.release.json>...
zexoro feed verify --public-key zexoro-update.pub feed.json
```

`feed add` merges release fragments into `feed.json`, increases its `sequence` and writes the detached signature `feed.json.sig`. The private key can also come from `ZEXORO_UPDATE_KEY` (the PEM text); `ZEXORO_UPDATE_KEY_PASSWORD` encrypts a new key and decrypts an existing one. Keep the private key out of the repository and back it up: if every trusted private key is lost, installed copies can no longer be updated automatically.

## zexoro exit codes

`0` success, `1` failure (including `doctor` finding errors), `2` usage error.

## Environment variables

| Variable | Meaning |
| --- | --- |
| `ZEXORO_CACHE_DIR` | Where the bundled toolset is extracted. Default: `%LOCALAPPDATA%\zexoro`, `~/Library/Caches/zexoro` or `$XDG_CACHE_HOME/zexoro`. |
| `ZEXORO_NSIS_DIR` | Use this NSIS directory instead of the bundled one (development). |
| `ZEXORO_UPDATER_DIR` | A directory with `zexoro-updater-win-<arch>.exe` files to put into installers instead of the embedded ones (development). |

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

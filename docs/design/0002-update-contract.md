# 0002: Update contract

- Status: accepted (stage 2; design only, no code)
- Date: 2026-10-26

Every format must be updatable, and the way a format is installed decides how it can be updated. This document fixes the contract before any format is built, so each format is designed to satisfy it from the start.

## 1. Pieces

| Piece | Owner | Purpose |
| --- | --- | --- |
| **Feed** | the developer hosts it (any static HTTPS host) | `feed.json` + `feed.json.sig`, one per channel, listing releases |
| **Signing key** | the developer | ECDSA P-256 key pair; the private key signs feeds, the public key ships inside every install |
| **Install record** | written by the installer | `zexoro-install.json` in the install root: who this install is and how to update it |
| **Updater** | shipped by zexoro inside every install | standalone executable `zexoro-updater`; checks, downloads, verifies and applies updates |

zexoro never hosts feeds or packages. `zexoro pack` produces the packages and the feed entries, `zexoro feed add` merges them into the feed and signs it (refined in `0005-nsis-updates.md`); the developer uploads them.

## 2. Identity

- `app.id` (reverse-DNS, e.g. `com.example.notes`) is the application's permanent identity. Changing it creates a different application.
- Every platform identifier is derived from it deterministically, so the same configuration always yields the same identity:
  - Windows: uninstall registry key name, and MSI `UpgradeCode` = UUIDv5(zexoro namespace, `app.id`).
  - macOS: `CFBundleIdentifier` = `app.id` unless overridden.
  - Linux: package name = last segment of `app.id`, lower-cased, unless overridden.
- An install records `app.id`, format and scope. An update is accepted only if all three match.

## 3. Versions

- Application versions must be Semantic Versioning 2.0.0. Precedence follows the specification; build metadata is ignored for comparison.
- An update is offered only when the feed version is strictly greater than the installed version. Downgrades are never automatic; to roll back, publish a higher version.
- Channels (`stable`, `beta`, …) are separate feeds, not inferred from prerelease tags. An install follows one channel, recorded in the install record.

## 4. Feed format

`feed.json`, UTF-8 JSON:

```json
{
  "schema": 1,
  "app": "com.example.notes",
  "channel": "stable",
  "sequence": 42,
  "published": "2026-10-26T12:00:00Z",
  "releases": [
    {
      "version": "1.2.3",
      "notes": "https://example.com/notes/1.2.3",
      "minimumUpdater": "0.1.0",
      "artifacts": [
        {
          "os": "windows", "arch": "x64", "format": "nsis", "scope": "user",
          "url": "https://example.com/dl/ExampleNotes-1.2.3-win-x64.exe",
          "size": 48213112,
          "sha256": "9f2c…"
        }
      ]
    }
  ]
}
```

- `sequence` increases with every publish. The updater remembers the highest sequence it has accepted and rejects any lower one, which blocks replaying an old (validly signed) feed to hold users on a vulnerable version.
- Unknown fields are ignored, so later schema additions (delta packages, staged rollout percentages) stay compatible. A higher `schema` than the updater understands is rejected with a message to update manually.
- `minimumUpdater` lets a release require a newer updater; older updaters only report it.

## 5. Signing and keys

- Algorithm: ECDSA P-256 with SHA-256, available in the .NET base class library on every platform.
- `feed.json.sig` is a detached signature over the exact bytes of `feed.json`. The updater verifies the signature **before** parsing; it never interprets unverified bytes.
- Each artifact is trusted through its `sha256` in the signed feed. Platform signatures (Authenticode, macOS code signing) are checked in addition where the format requires them (see section 7), but the feed key is the root of trust.
- Feeds must be served over HTTPS. Plain HTTP is accepted only for `localhost`, for tests.
- `zexoro keys new` creates a key pair. The private key never enters the repository; `zexoro feed add` reads it from a file path or environment variable (refined in `0005-nsis-updates.md`).
- Key rotation: the install record holds a list of trusted public keys. A release can ship a new list; because that release is verified with a currently trusted key, the change is authenticated. Losing every trusted private key means installed copies can no longer be updated automatically — the documentation must say this plainly.

## 6. Install record and updater

`zexoro-install.json`, written by the installer next to the updater:

```json
{
  "schema": 1,
  "app": "com.example.notes",
  "version": "1.2.3",
  "os": "windows", "arch": "x64", "format": "nsis", "scope": "user",
  "channel": "stable",
  "feed": "https://example.com/updates/stable/feed.json",
  "keys": ["MFkwEwYHKoZIzj0CAQ…"],
  "strategy": "replace"
}
```

The updater is a standalone Native AOT executable built for each target platform and bundled with zexoro, so packaging for Windows on macOS needs no Windows build. Applications use it as a process, from any language:

```
zexoro-updater check    [--json]       # exit 0: update available, 1: up to date, ≥2: error
zexoro-updater download [--json]       # verify and stage the newest release
zexoro-updater apply    --wait-pid <pid> [--restart]
```

`--json` prints machine-readable results on stdout; human-readable messages go to stderr. Language SDKs, if any, only wrap these commands.

## 7. Update strategies and what each format must satisfy

Each format declares exactly one strategy. A format is not done until its strategy is implemented and tested on real installs.

| Strategy | Formats | The format must guarantee |
| --- | --- | --- |
| **replace** — the updater swaps application files itself | NSIS per-user, Windows zip, Linux tar.gz, AppImage | Install root writable by the user without elevation. Layout: `<root>/app/` (application), `<root>/zexoro-updater[.exe]`, `<root>/zexoro-install.json`. Shortcuts and registry entries point into `<root>/app/` only, so they stay valid across versions. The updater stages into `<root>/app.new/`, swaps by rename (`app` → `app.old`, `app.new` → `app`), rolls back on failure, then deletes `app.old`. On Windows the registry uninstall entry's version is updated by the updater. |
| **reinstall** — the updater runs the new installer silently | NSIS per-machine, MSI, macOS pkg | Stable identity (section 2). The installer upgrades in place when an older version is present, supports a silent mode, and does not require uninstalling first. Elevation is requested by the installer (UAC, macOS authorization), never by the updater itself. |
| **bundle** — the updater swaps a signed `.app` bundle | macOS `.app` (distributed in dmg or zip) | The new bundle must satisfy the installed bundle's designated requirement (same Team ID). Updates are refused while the app runs translocated (launched from a dmg or quarantine location); the user is asked to move it to Applications first. If the bundle's parent directory is not user-writable, the update falls back to asking for authorization. |
| **notify** — the updater only reports | deb, rpm, apk, and any store-installed copy | The package manager owns the files; the updater never touches them. `check` reports the new version and the download or repository URL; the application decides how to tell the user. |

Updating the updater: the updater is part of every release's payload and is replaced together with the application. To replace itself, `apply` copies the updater to a temporary location and runs from there.

## 8. Not in contract version 1

- Delta updates (the feed leaves room for them).
- Staged rollouts and forced or mandatory updates.
- Background services or scheduled update checks; the application decides when to call the updater.

## 9. Consequences for the MVP

The MVP (NSIS) does not ship the updater, but it must already:

- use the `<root>/app/` layout and put the install record in place;
- derive its uninstall key from `app.id`;
- keep per-user installs free of elevation and per-machine installs upgradeable in place, so `replace` and `reinstall` work without changing the installer later.

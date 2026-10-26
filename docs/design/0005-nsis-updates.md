# 0005: Updates for NSIS installers

- Status: accepted (stage 4)
- Date: 2026-10-26
- Goal: implement the update contract (`0002-update-contract.md`) for the first format: `replace` for per-user NSIS installs, `reinstall` for per-machine ones. A format is not done until its updates work; this stage finishes NSIS.

## 1. Configuration

```toml
[updates]                                          # optional; absent = no updater, no feed entries
feed     = "https://example.com/updates/{channel}/feed.json"
download = "https://example.com/dl/{file}"         # artifact URL; {file}, {version}, {channel}
channel  = "stable"                                # default "stable"; pack --channel overrides
keys     = ["MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…", { file = "keys/update.pub" }]
```

- `feed` is one URL template for every channel and must contain `{channel}`, so two channels can never share a feed by accident. Both URLs must be `https` (plain `http` only for `localhost`, `127.0.0.1`, `[::1]`).
- `keys` lists the trusted public keys (base64 SubjectPublicKeyInfo, P-256) literally or from a `.pub` file written by `zexoro keys new`. At least one is required. Rotation (0002 §5) is editing this list: the release that ships the new list is still signed with an old key.
- **The private key is never in `zexoro.toml` and `pack` never sees it.** Signing happens in `zexoro feed add` (below), which reads the key from `--key <file>` or from the `ZEXORO_UPDATE_KEY` environment variable (the PEM text, for CI secrets); an encrypted key's password comes only from `ZEXORO_UPDATE_KEY_PASSWORD`. This refines 0002 §1/§5, where `pack` was to sign: one feed collects artifacts packed on several hosts (x64 and arm64, both scopes), so signing per `pack` run would make the hosts race on one file.

## 2. Developer commands

- `zexoro keys new [--output <dir>] [--name <name>]` writes `<name>.key.pem` (PKCS #8; encrypted with AES-256-CBC / PBKDF2-SHA256 when `ZEXORO_UPDATE_KEY_PASSWORD` is set; mode 0600 on Unix) and `<name>.pub` (base64 SPKI). Default name `zexoro-update`. It refuses to overwrite and prints the public key with a warning that losing every trusted private key ends automatic updates for installed copies.
- `zexoro pack` with `[updates]` writes, next to each installer, a **release fragment** `<installer>.release.json`: `{ schema, app, channel, release: { version, notes?, minimumUpdater, artifacts: [one artifact] } }` with size and sha256 of the final (signed) installer and the URL from `download`. It also writes `channel`, `feed`, `keys` and `main` into the install record, and puts `zexoro-updater.exe` in the installer. `[updates] notes` is not a key; `pack --notes <url>` sets it.
- `zexoro feed add --feed <feed.json> [--key <file>] <fragment>...` merges fragments into a feed (created with sequence 1 if missing), sets `sequence` to the previous value + 1 (once per run, however many fragments) and `published` to now, and writes `feed.json` and `feed.json.sig` (base64 of the DER ECDSA-SHA256 signature over the exact bytes, verifiable with `openssl dgst -sha256 -verify`). Fragments must match the feed's `app` and `channel`. A fragment for an existing version adds its artifacts; the same artifact (os, arch, format, scope) with the same sha256 is a no-op, with a different sha256 an error (publish a new version instead). If nothing changed, the feed is not rewritten. Releases are kept, sorted by descending precedence.
- `zexoro feed verify --public-key <key or .pub file> <feed.json>` checks signature and structure and prints the releases.

## 3. The updater executable

- Project `src/Zexoro.Updater` (assembly `zexoro-updater`), Native AOT, BCL only, sharing `Zexoro.Updates` (feed, signature, selection, replay state, downloads). RIDs: `win-x86`, `win-x64`, `win-arm64` — the install's architecture picks it, so an x86 application on 32-bit Windows still updates.
- **Build once, embed everywhere.** Native AOT cannot cross-compile between operating systems, but it can between architectures of the same one. Both workflows get a job `updater` on `windows-latest` that publishes the three RIDs and uploads them as a workflow artifact (`zexoro-updater-<rid>.exe` + `SHA256SUMS`). Every CLI build job (`ci.yml` and `release.yml`, all five host RIDs) needs it, downloads it and builds with `-p:ZexoroUpdaterDir=<dir>`; `eng/Updater.targets` embeds each file as `Zexoro.Updater.<rid>.exe` with its SHA-256 as assembly metadata. The updater is therefore always built from the same commit as the CLI that ships it. The release workflow sets `ZexoroUpdaterRequired=true` (missing files are a build error) and its smoke test checks `zexoro doctor` reports the embedded updaters.
- **Local builds and tests.** Without `ZexoroUpdaterDir` the CLI builds without updaters; unit tests never need one. On Windows, `dotnet publish src/Zexoro.Updater -r win-x64` produces one. At run time `ZEXORO_UPDATER_DIR` (a directory with `zexoro-updater-<rid>.exe`) overrides the embedded copies, like `ZEXORO_NSIS_DIR`. `pack` with `[updates]` fails with a clear message when no updater for the installer's architecture is available; `doctor` reports it.
- **Install record** (0002 §6) gains `channel`, `feed` (the expanded URL), `keys`, `main` (relative path of the main executable inside `app\`) and keeps `strategy`. The updater reads `zexoro-install.json` next to its own executable.
- **State** in `%LOCALAPPDATA%\zexoro\updater\<app.id>\`: `state.json` (highest accepted `sequence` per channel) and `downloads\` (verified artifacts). Per user for both scopes: a per-machine install's root is not user-writable.

### Command line (0002 §6)

```
zexoro-updater check    [--json]
zexoro-updater download [--json]
zexoro-updater apply    --wait-pid <pid> [--restart] [--json]
zexoro-updater --version
```

`check` fetches feed and signature, verifies the signature against the record's keys **before** parsing, rejects a higher `schema`, another `app` or `channel`, and a `sequence` below the stored one, stores the new sequence, then selects the highest release whose version is strictly greater than the installed one (SemVer precedence; prereleases are ordinary versions, channels are separate feeds), that has an artifact for the record's (os, arch, format, scope), and whose `minimumUpdater` this updater meets; an older release that it meets is a stepping stone that brings a newer updater. `download` does the same, then streams the artifact into `downloads\` while hashing, aborting beyond the declared size, and keeps it only if size and sha256 match. `apply` re-hashes the staged file and keeps it open with read-only sharing until the installer has exited, so it cannot be swapped after the check.

`--json` writes one object to stdout: `{"status": "available" | "up-to-date" | "downloaded" | "applied" | "error", "installed", "version", "notes", "size", "error"}`. Exit codes: **0** update available / downloaded / applied, **1** up to date or nothing staged, **2** usage error, **3** network or HTTP failure, **4** verification failed (signature, sha256, size, replay, wrong app or channel, insecure URL), **5** apply failed and was rolled back, **6** cancelled (UAC declined, the application did not exit within 60 s), **7** manual update needed (`schema` too new, or every newer release requires a newer updater via `minimumUpdater`), **8** apply failed and rollback failed (the install needs a reinstall).

### `replace` (per-user)

1. Wait for `--wait-pid` to exit. Copy the updater to `%TEMP%\zexoro-updater-<guid>\` and re-run it from there (`apply --from-root <root> …`), so the installed `zexoro-updater.exe` can be replaced (0002 §7).
2. Delete leftover `app.new`, `app.old` and `*.new`/`*.old` files from an interrupted run. Run the verified installer with `/S /ZEXORO_STAGE`: in stage mode it touches neither registry nor shortcuts and writes `<root>\app.new\` plus `zexoro-updater.exe.new`, `zexoro-install.json.new`, `uninstall.exe.new`. It refuses (exit 2) if the existing install's location differs from the updater's root.
3. If the new record's `main` differs from the installed one, shortcuts would break: fall back to `reinstall` (the per-user installer upgrades in place without elevation).
4. Swap by rename: `app` → `app.old`, `app.new` → `app`, then each root file `F` → `F.old`, `F.new` → `F`. Any failure renames everything back in reverse order (exit 5; exit 8 if that fails too).
5. Update `HKCU\…\Uninstall\<app.id>`: `DisplayVersion`, `ZexoroVersionOrder` (`NsisVersionOrder`, shared from `Zexoro.Formats.Nsis`) and `EstimatedSize`. Delete the `.old` items (a locked leftover is deleted on the next run), start `app\<main>` if `--restart`.

### `reinstall` (per-machine)

Wait for the process, copy the updater to `%TEMP%` (the installer replaces `<root>\zexoro-updater.exe`), then start the installer with `/S` through `ShellExecute`, so its `requireAdministrator` manifest raises UAC; the updater never elevates. The installer's in-place upgrade (0003 §3) writes the new record, key, `DisplayVersion` and `ZexoroVersionOrder`. Exit codes map: 0 → 0, `ERROR_CANCELLED` (UAC declined) → 6, installer 3 (newer installed) → 1, anything else → 5. `--restart` starts the application from the unelevated updater, so it does not inherit elevation.

## 4. Tests

- **Unit (all CI hosts):** signatures (round trip, tampered byte, wrong key, truncated/garbage signature, key rotation via a key list), key PEM round trip (plain and encrypted, wrong password, non-P-256 keys rejected), feed parsing (unknown fields ignored, `schema` too new, duplicates, malformed values), replay (lower rejected, equal accepted, state persisted atomically), selection (strictly greater, build metadata ignored, prereleases on both sides, target mismatch, `minimumUpdater`), downloads (size and hash mismatch, oversize, https rule incl. redirects), `feed add` merge rules, and the updater's swap and rollback logic against a temporary directory with an injected failure at every step.
- **Windows e2e** (`tests/e2e/windows/Run-UpdateE2E.ps1`, both scopes, installers packed on all three hosts): generate a key; pack v1 and v2 with `[updates]` pointing at a local HTTP server (`http://localhost`); `feed add` v2. Install v1 → `check` exits 0 → `download` → `apply` → files, record, `DisplayVersion`, `ZexoroVersionOrder` and updater are v2, no `*.old`/`*.new` left, the app runs, `check` exits 1. Negative cases each exit 4 and leave v1 intact: a feed byte flipped, a signature from another key, an artifact whose bytes differ from its sha256, and a validly signed older feed (lower `sequence`) served after a newer one was accepted. Per-machine runs elevated on the runner (UAC is off there), which still exercises the `ShellExecute` path.

## 5. Steps

Each step is a branch merged into `dev` after CI passes.

1. `phase-4/update-core`: this document; `Zexoro.Updates` (feed model, signatures, keys, selection, replay state, downloads, URL rule); `zexoro keys new`, `zexoro feed add`, `zexoro feed verify`.
2. `phase-4/updater`: `src/Zexoro.Updater` with `check`/`download`/`apply`, both strategies, self-copy and rollback, unit tests of the swap logic; the `updater` job in `ci.yml`.
3. `phase-4/updater-embedding`: `eng/Updater.targets`, `ZEXORO_UPDATER_DIR`, `doctor` check, the `updater` job and `ZexoroUpdaterRequired` in `release.yml`.
4. `phase-4/nsis-updates`: `[updates]` configuration, install record fields, updater in the installer, stage mode `/ZEXORO_STAGE`, release fragments, `pack --channel/--notes`, uninstaller removes `*.new`/`*.old`.
5. `phase-4/update-e2e`: the Windows e2e above in CI; README sections for updates, keys and feeds (including the lost-key warning) and an updated example workflow.

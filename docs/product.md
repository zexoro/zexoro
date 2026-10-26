# Product definition

## In one sentence

zexoro turns an application built with any technology into native installers for each platform, and keeps it up to date afterwards — one configuration file, one command, the same result locally and in CI.

## 1. Who it is for

**Primary users:** independent developers and small teams (1–10 people) who build desktop applications and ship them to end users, without a dedicated release or build engineer.

- Any technology stack: .NET (Avalonia, WPF, MAUI), Go, Rust, Qt/C++, Flutter, packaged Python, and so on — anything that produces a runnable application directory.
- The application already runs on the developer's machine; what is missing is turning it into a proper installer and shipping new versions afterwards.

**Not a focus, but not excluded:** Electron and Tauri users, whose ecosystems already have good tools. **Not a target:** large organizations with established release pipelines.

## 2. The problem

Producing a proper installer and keeping it updatable costs effort that has nothing to do with the application itself:

- **A different toolchain per platform.** Windows has NSIS and WiX; macOS needs the `.app` layout, dmg/pkg, code signing and notarization; Linux has deb, rpm, AppImage and more. Each comes with its own scripting language and pitfalls.
- **Most tools are tied to one ecosystem or build tool.** Changing language or build system means changing the packaging setup.
- **Many details, easy to get wrong.** Shortcuts, uninstall entries, in-place upgrades, signature timestamps, per-user versus per-machine installs. Getting them wrong makes the application look untrustworthy on first contact.
- **Updates are a second problem.** Every install method needs a different update method — replace files in place, elevate, defer to the package manager, keep the signature identity stable — and the mismatch usually shows up only when the first new version ships.
- **Local and CI builds diverge.** Packaging that works on the developer's machine has to be redone for CI.

zexoro's promise: **you describe what your application is; zexoro gets the platform details and updates right.**

## 3. Scope and MVP

**Full scope:** the mainstream native formats on all three desktop platforms (Windows, macOS, Linux), with **automatic updates for every format**. Mobile (Android, iOS) may follow later, so target platforms are modeled as operating system plus architecture and the core pipeline must not assume a desktop. The list of formats is settled one format at a time, each in its own design document.

**MVP: given an already-built Windows application directory, produce a signable NSIS installer (`.exe`) that installs, runs, upgrades and uninstalls correctly.**

Acceptance criteria:

1. `zexoro init` creates a minimal configuration; filling in the name, version, entry point and icon is enough.
2. `zexoro pack` produces a single installer `.exe`. It runs on macOS, Linux or Windows, with functionally identical results.
3. After installation there is a Start menu shortcut (desktop shortcut optional), and the application appears in Installed apps with the correct name, version, publisher and icon.
4. Installing a newer version over an older one upgrades in place: no stale files and no duplicate entry in Installed apps.
5. Uninstalling leaves nothing behind (the user data directory is kept by default; configurable).
6. When a certificate is configured, the installer and the main executable are Authenticode-signed by zexoro itself, without `signtool`. Without a certificate, signing is skipped with a clear notice.
7. Install location, identity and permissions satisfy the update contract (defined before the MVP). The MVP does not ship updates, but nothing in it may need rework to support them.
8. Real install, upgrade and uninstall tests run automatically on GitHub Actions Windows runners, and an example workflow is provided for users.

Why Windows first: the NSIS toolset is the heaviest dependency, so building it early surfaces the risk early; per-user installs are the simplest case for proving updates; and everything can be verified without an Apple developer account.

## 4. Non-goals

**Not in the MVP (planned for later stages)**

- Automatic updates (the first format's updates come right after the MVP)
- Any format other than NSIS, including everything for macOS and Linux
- Advanced install behavior: file associations, URL protocols, services, start at login
- Installer UI customization (skins, multi-language wizards, custom pages beyond a license page)
- Integrations with specific build tools (MSBuild, dotnet tool, and so on), which live in separate repositories

**Never**

- **Building your application.** The input is build output, not source code. Build with your own tools.
- **Requiring your application to reference a zexoro library.** Updates are performed by a standalone updater shipped with the installer, which any language can invoke as a process. Language-specific SDKs are optional conveniences.
- **Downloading tools at run time.** External tools needed for packaging are built and maintained by zexoro and shipped with it.
- **Writing our own installer engine.** Complex formats use existing engines (NSIS, WiX); simple formats (zip, tar.gz, deb, rpm, and so on) are produced directly in .NET.
- **Hosting.** zexoro does not store or serve your installers or updates.
- **A graphical configuration tool.** The command line plus a configuration file is the only interface.

## 5. How it is used

**The command line is the primary interface**, and the command is `zexoro`.

```bash
zexoro init                    # create zexoro.toml in the current directory
zexoro pack                    # build installers into ./dist as configured
zexoro pack --version 1.2.3    # override a configuration value (common in CI)
zexoro doctor                  # check configuration, certificates and bundled tools
```

**Configuration** is TOML (`zexoro.toml`): readable, supports comments, not indentation-sensitive, and comfortable to write by hand. zexoro ships its own parser rather than a third-party library.

```toml
[app]
id        = "com.example.notes"      # stable identity, used to recognize upgrades
name      = "Example Notes"
version   = "1.2.3"
publisher = "Example Ltd."
icon      = "assets/app.ico"

[input]
dir  = "build/win-x64"               # your build output
main = "ExampleNotes.exe"            # entry point

[windows]
install_scope = "user"               # user | machine
shortcuts     = ["start-menu"]       # add "desktop" if wanted

[windows.signing]                    # optional; omit to skip signing
certificate = { env = "SIGN_CERT_PATH" }
password    = { env = "SIGN_CERT_PASSWORD" }
timestamp   = "http://timestamp.digicert.com"
```

**Distribution:** a self-contained single-file executable (Native AOT), one per host operating system and architecture; users do not need .NET installed. The packaging tools are bundled. Other channels such as `dotnet tool` belong to the later build-tool integrations.

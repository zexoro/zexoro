# 0001: Foundations

- Status: accepted
- Date: 2026-10-26

## Repositories

| Repository | Contents | When |
| --- | --- | --- |
| `zexoro/zexoro` (this repository) | Core, CLI, TOML parser, signing, format back ends, documentation | Now |
| `zexoro/nsis-toolset` | Builds `makensis` and plugins from source for macOS, Linux and Windows; publishes versioned toolsets | Before the MVP, with its own design document |
| WiX toolset | Separate repository or part of this repository's build; decided in the MSI design | With the MSI format |

Back ends start in this repository with clear project boundaries so they can be split out later. Anything with its own build and release cadence — engine toolsets and build-tool integrations — gets its own repository.

Layout:

```
src/
  Zexoro.Cli/           command-line entry point, Native AOT
  Zexoro.Core/          platform model, configuration model, packaging pipeline
  Zexoro.Toml/          TOML parser (MVP)
  Zexoro.Signing/       Authenticode signing (MVP)
  Zexoro.Formats.Nsis/  NSIS back end (MVP)
tests/                  one test project per source project
docs/
  product.md
  decisions/
```

## Packaging engines

Complex formats use existing engines, built and maintained by us and shipped with zexoro; we do not reimplement them.

- **NSIS:** built from source in `zexoro/nsis-toolset`.
- **MSI:** the last WiX 3.x release. WiX 4 and later ship binaries under a maintenance-fee EULA, are much larger, and need a .NET runtime that Windows does not include, which conflicts with bundling the toolset. WiX 3 runs on the .NET Framework that ships with Windows.
  Possible later upgrade, to be verified in the MSI design: the newer source remains under MS-RL and the EULA does not restrict self-compiled binaries; a self-built, self-contained, trimmed WiX would also remove the runtime dependency. Open points are its trimmed size, whether it still requires Windows (likely, through `msi.dll`, as 3.x does), and that WiX 3 no longer receives upstream fixes.

## Names

- Command: `zexoro`.
- Package and namespace prefix: `Zexoro.` No `Zexoro*` packages exist on nuget.org; reserve the prefix before the first package is published.

## .NET and build

- .NET 10 (LTS), pinned in `global.json` with `rollForward: latestFeature`.
- The CLI is published with Native AOT as a self-contained single file. Libraries set `IsAotCompatible` so incompatibilities fail at build time.
- Nullable reference types on, warnings as errors, central package management, invariant globalization.
- Native AOT cannot cross-compile between operating systems, so each platform's CLI binary is built on a runner of that platform.
- Runtime code uses the .NET base class library only; any exception is evaluated individually. Test-only dependencies are not restricted by this rule.
- Tests use xUnit v3 on Microsoft.Testing.Platform.

## License

MIT.

## Language

Everything in the repository — code, comments, documentation, commit messages, pull requests — is in English.

## CI

GitHub Actions on Ubuntu, macOS and Windows: build, test, publish the CLI with Native AOT, and run it as a smoke test. Only GitHub-owned actions are used.

## Versions and branches

- Semantic Versioning; `0.x` until the MVP, which is tagged `v0.1.0`.
- `main` is protected by a ruleset: changes arrive through pull requests, force pushes are blocked, and CI must pass. No approval count is required; repository owners may bypass.
- Each stage follows: one-page design → agreement → implementation and tests → pull request → review and merge.

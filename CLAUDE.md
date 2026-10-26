# Working on zexoro

zexoro packages applications built with any technology into native installers and keeps them updatable. Read `docs/product.md` for scope and `docs/decisions/` for settled decisions before proposing changes.

## Process

- `dev` is the integration branch. Work happens on short-lived branches (`<stage>/<topic>`) merged into `dev` through pull requests once CI passes; these merges need no user review.
- Before implementing a stage, write a one-page design in `docs/design/` and proceed without waiting for approval.
- `main` only receives `dev`, at milestones (the first is the MVP), through a pull request the user reviews and merges. Never push to or merge into `main` yourself.
- Keep repository content (code, comments, docs, commit messages, pull requests) in English.
- Separate verified facts from recommendations and opinions. Disagree openly, with reasons.

## Design rules

- Runtime code uses the .NET base class library only. Any other dependency needs an explicit, individual evaluation. Test-only dependencies are exempt.
- External packaging tools (NSIS, WiX) are built and maintained by zexoro and shipped with it; never download tools at run time.
- Simple formats (zip, tar.gz, deb, rpm, apk) are implemented in .NET, with no external tools.
- Target platforms are operating system plus architecture. Nothing in the core may assume a desktop.
- Every format must satisfy the update contract (install location, identity, permissions). A format is not done until its updates work.
- Libraries must stay Native AOT compatible.

## Commands

```bash
dotnet build
dotnet test
dotnet publish src/Zexoro.Cli -c Release -r osx-arm64   # AOT; use the host's RID
```

## Code style

- Follow `.editorconfig`; warnings are errors.
- File-scoped namespaces; root namespace `Zexoro`.
- Test names: `Subject_ExpectedBehavior`.

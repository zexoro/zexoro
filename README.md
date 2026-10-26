# zexoro

Package applications built with any technology into native installers, and keep them up to date.

> **Status:** early development. Nothing is usable yet. The first milestone is a signable Windows NSIS installer; see the [product definition](docs/product.md).

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet publish src/Zexoro.Cli -c Release -r <rid>   # e.g. osx-arm64, linux-x64, win-x64
```

## License

[MIT](LICENSE)

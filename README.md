# Bundler

A desktop packaging toolkit for published .NET applications. The project currently targets:

- Windows: NSIS and MSI
- macOS: app bundles and DMG
- Linux: DEB, RPM and AppImage

The bundler consumes existing `dotnet publish` output. Compilation and RID-specific publishing remain separate from installer generation.

## Current status

The initial foundation implements configuration loading, validation, target/format compatibility checks, dependency-aware planning, and backend abstractions. Package-producing backends are not implemented yet.

## Build and test

```shell
dotnet build Bundler.sln
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
```

The test project intentionally uses a dependency-free executable harness so the repository can build without downloading test packages.

## CLI

```shell
dotnet run --project src/Bundler.Cli -- validate examples/bundler.example.json
dotnet run --project src/Bundler.Cli -- plan examples/bundler.example.json
```

Paths in the JSON file are resolved relative to the configuration file. `validate` checks that each publish directory and main executable exists; `plan` only checks configuration structure so it can run before publishing.

See `examples/bundler.example.json` for the current configuration shape.

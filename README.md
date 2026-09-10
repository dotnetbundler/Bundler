# DotNet.Bundler

[简体中文](README.zh-CN.md)

`DotNet.Bundler` is a NuGet build package that creates desktop installers from `dotnet publish` output. A consuming project references the package, sets a few MSBuild properties, and receives an installer automatically after publishing.

## Status

The first supported path is Windows + NSIS. MSI, macOS and Linux formats are planned but are not implemented yet.

The package carries a pinned NSIS 3.12 portable archive. Consumers do not install NSIS themselves. The archive is verified with SHA-256 and extracted into the project's intermediate directory when first used.

## Consumer configuration

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>

    <BundlerEnabled>true</BundlerEnabled>
    <BundlerIdentifier>com.example.myapp</BundlerIdentifier>
    <BundlerProductName>My App</BundlerProductName>
    <BundlerPublisher>Example Company</BundlerPublisher>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.2" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Then publish normally:

```powershell
dotnet publish -c Release
```

The installer is written to `artifacts/<rid>/nsis/` by default. Installer generation runs after `Publish`, not after an ordinary `Build`.

## MSBuild properties

| Property | Required | Default |
| --- | --- | --- |
| `BundlerEnabled` | Yes | `false` |
| `BundlerIdentifier` | Yes | — |
| `RuntimeIdentifier` | Yes | — |
| `BundlerFormat` | No | `nsis` |
| `BundlerProductName` | No | `$(AssemblyName)` |
| `BundlerVersion` | No | `$(Version)` |
| `BundlerMainExecutable` | No | `$(TargetName).exe` |
| `BundlerPublisher` | No | `$(Company)` |
| `BundlerOutputPath` | No | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | No | `$(BaseIntermediateOutputPath)bundler\tools` |

## Repository commands

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
dotnet pack src/Bundler.Cli/Bundler.Cli.csproj -c Release -o artifacts/packages
```

The repository also retains the `validate` and `plan` CLI commands for development and diagnostics. See `examples/bundler.example.json` for the configuration-file shape.

## Security and licensing

The NSIS archive checksum is pinned in source. Third-party notices and the upstream NSIS license are included in the NuGet package. The license for this repository itself has not yet been selected.

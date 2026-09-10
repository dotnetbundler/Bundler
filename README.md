# DotNet.Bundler

[简体中文](README.zh-CN.md)

`DotNet.Bundler` is a NuGet build package that creates desktop installers from `dotnet publish` output. A consuming project references the package, sets a few MSBuild properties, and receives an installer automatically after publishing.

## Status

The first supported path is Windows + NSIS. MSI, macOS and Linux formats are planned but are not implemented yet.

The package carries a pinned NSIS 3.12 portable archive. Consumers do not install NSIS themselves. The archive is verified with SHA-256 and extracted into the project's intermediate directory when first used. This per-project cache is intentional for the first release and may become a shared content-addressed cache later.

The in-process MSBuild task targets `netstandard2.0` for compatibility with MSBuild hosts. It starts the bundled `net8.0` packaging driver out of process, so the .NET 8 runtime/SDK is currently required when bundling.

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.4" PrivateAssets="all" />
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
| `BundlerNsisTemplate` | No | Template included in the package |

## Packaging pipeline and NSIS customization

All formats run through one pipeline: validate configuration, build a format-aware plan, create an isolated work directory, invoke a backend, verify its artifact, and clean the work directory. Adding MSI, macOS, or Linux support should therefore add a backend instead of duplicating orchestration.

The NSIS script is stored at `templates/nsis/installer.nsi`, not embedded in C#. The default template includes current-user installation, DPI awareness, compression, English/Simplified Chinese UI, Start Menu and desktop shortcuts, Add/Remove Programs metadata, silent uninstall, and finish-page launch behavior. Set `BundlerNsisTemplate` to an absolute path to use a customized copy. Supported placeholders are `product_name`, `version`, `numeric_version`, `publisher`, `identifier`, `main_executable`, `install_folder`, `input_glob`, `output_file`, and `estimated_size`, each written as `{{name}}`.

This is a practical Tauri-inspired baseline, not feature parity. Upgrade/downgrade policy, install scope selection, file associations, deep links, signing, and lifecycle hooks still need structured configuration before they should be exposed.

## Repository commands

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
dotnet pack src/Bundler.Cli/Bundler.Cli.csproj -c Release -o artifacts/packages
```

The repository also retains the `validate` and `plan` CLI commands for development and diagnostics. See `examples/bundler.example.json` for the configuration-file shape.

## Security and licensing

The NSIS archive checksum is pinned in source. Third-party notices and the upstream NSIS license are included in the NuGet package. The license for this repository itself has not yet been selected.

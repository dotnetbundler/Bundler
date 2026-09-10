# DotNet.Bundler

[简体中文](README.zh-CN.md)

`DotNet.Bundler` is a NuGet build package that creates desktop installers from `dotnet publish` output. A consuming project references the package, sets a few MSBuild properties, and receives an installer automatically after publishing.

## Status

The first supported path is Windows + NSIS. MSI, macOS and Linux formats are planned but are not implemented yet.

The package carries a pinned NSIS 3.12 portable archive. Consumers do not install NSIS themselves. The archive is verified with SHA-256 and extracted into the project's intermediate directory when first used. This per-project cache is intentional for the first release and may become a shared content-addressed cache later.

The MSBuild task and the Core assembly it loads both target `netstandard2.0`. Packaging decisions and backend orchestration run directly inside MSBuild; the package does not launch a separate .NET CLI driver. NSIS compilation still starts the bundled `makensis.exe`, because that executable is the actual installer compiler.

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.9" PrivateAssets="all" />
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
| `BundlerFormats` | No | `nsis` |
| `BundlerProductName` | No | `$(AssemblyName)` |
| `BundlerVersion` | No | `$(Version)` |
| `BundlerMainExecutable` | No | `$(TargetName).exe` |
| `BundlerPublisher` | No | `$(Company)` |
| `BundlerDescription` | No | `$(Description)` |
| `BundlerOutputPath` | No | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | No | `$(BaseIntermediateOutputPath)bundler\tools` |
| `BundlerNsisTemplate` | No | Template included in the package |
| `BundlerNsisLanguages` | No | `English` |
| `BundlerNsisDisplayLanguageSelector` | No | `false` |

Multiple formats use a semicolon-separated value, for example `<BundlerFormats>nsis;msi</BundlerFormats>`. The task parses the complete request and the Core planner determines the required package steps. At present only the NSIS backend is implemented, so requesting MSI intentionally fails instead of silently skipping it.

Icons and extra resources are passed as MSBuild items:

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*" />
</ItemGroup>
```

## Packaging pipeline and NSIS customization

All formats run through one pipeline: validate configuration, build a format-aware plan, create an isolated work directory, invoke a backend, verify its artifact, and clean the work directory. Adding MSI, macOS, or Linux support should therefore add a backend instead of duplicating orchestration.

The NSIS script is stored at `templates/nsis/installer.nsi`, not embedded in C#. Installer languages are configured like Tauri: `BundlerNsisLanguages` is a semicolon-separated list, the first language is the fallback, and the selector is shown only when `BundlerNsisDisplayLanguageSelector` is true and multiple languages are enabled. English and Simplified Chinese message files are bundled.

```xml
<PropertyGroup>
  <BundlerNsisLanguages>English;SimpChinese</BundlerNsisLanguages>
  <BundlerNsisDisplayLanguageSelector>true</BundlerNsisDisplayLanguageSelector>
</PropertyGroup>
```

Additional NSIS languages require a custom message file containing every `LangString` used by the template:

```xml
<ItemGroup>
  <BundlerNsisLanguageFile Include="installer-languages\German.nsh" Language="German" />
</ItemGroup>
```

The default template includes current-user installation, a user-selectable directory that remembers the previous location, a warning for unrelated non-empty directories, DPI awareness, compression, optional Start Menu and desktop shortcuts, running-app detection and closure, Add/Remove Programs metadata, optional application-data cleanup, silent uninstall, and finish-page launch behavior. When application-data deletion is not selected, uninstall removes packaged payload paths from the program directory: files created later at new paths remain, while files created or replaced at a packaged path are removed. When application-data deletion is selected, uninstall recursively removes the complete program directory plus `%APPDATA%\<identifier>` and `%LOCALAPPDATA%\<identifier>`.

Set `BundlerNsisTemplate` to an absolute path to use a customized copy. Supported placeholders are `product_name`, `version`, `numeric_version`, `publisher`, `identifier`, `main_executable`, `process_name`, `install_folder`, `input_glob`, `output_file`, `estimated_size`, `uninstall_payload`, `language_macros`, `language_files`, and `display_language_selector`, each written as `{{name}}`.

This is a practical Tauri-inspired baseline, not feature parity. Upgrade/downgrade policy, install scope selection, file associations, deep links, signing, and lifecycle hooks still need structured configuration before they should be exposed.

## Repository commands

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
dotnet pack src/Bundler.MSBuild/Bundler.MSBuild.csproj -c Release -o artifacts/packages
```

The Core project contains configuration, validation, planning, tool resolution, and backends without depending on MSBuild. The existing CLI is retained as a development client of Core, but it is no longer shipped in the NuGet build package.

## Security and licensing

The NSIS archive checksum is pinned in source. Third-party notices and the upstream NSIS license are included in the NuGet package. The license for this repository itself has not yet been selected.

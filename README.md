# DotNet.Bundler

[简体中文](README.zh-CN.md)

`DotNet.Bundler` is a NuGet build package that creates desktop installers from `dotnet publish` output. A consuming project references the package, sets a few MSBuild properties, and receives an installer automatically after publishing.

## Status

The first supported path is Windows + NSIS. MSI, macOS and Linux formats are planned but are not implemented yet.

The implementation is split into reusable NuGet packages. `DotNet.Bundler.Core` owns format-neutral planning and orchestration, `DotNet.Bundler.Nsis` exposes the standalone NSIS API and owns all NSIS implementation assets, and `DotNet.Bundler` is the thin MSBuild integration package.

`DotNet.Bundler.Nsis` carries a pinned NSIS 3.12 portable archive as an embedded resource. Consumers do not install NSIS or download tools themselves. The archive is verified with SHA-256 and extracted once into the shared `%LOCALAPPDATA%\DotNetBundler\tools` cache. Projects on the same machine reuse the content-addressed tool directory.

The MSBuild task and the Core/NSIS assemblies it loads all provide `netstandard2.0` assets. Packaging decisions and backend orchestration run directly inside MSBuild; the package does not launch a separate .NET CLI driver. NSIS compilation still starts the bundled `makensis.exe`, because that executable is the actual installer compiler.

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.12" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Then publish normally:

```powershell
dotnet publish -c Release
```

The installer is written to `artifacts/<rid>/nsis/` by default. Installer generation runs after `Publish`, not after an ordinary `Build`.

## Standalone NSIS API

Applications and build tools that do not use MSBuild integration can reference `DotNet.Bundler.Nsis` directly:

```xml
<PackageReference Include="DotNet.Bundler.Nsis" Version="0.1.0-alpha.12" />
```

```csharp
using Bundler.Core.Configuration;
using Bundler.Core.Models;
using DotNet.Bundler.Nsis;

var request = new BundleConfiguration
{
    ProductName = "My App",
    Identifier = "com.example.myapp",
    Version = "1.0.0",
    OutputDirectory = "artifacts",
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = "publish/win-x64",
            MainExecutable = "MyApp.exe",
            Formats = [PackageFormat.Nsis]
        }
    ]
};

var artifacts = await new NsisBundler().BuildAsync(request);
```

`NsisBundleConfiguration` controls NSIS-specific behavior. `NsisBundlerOptions` can override the shared cache, compiler, archive, template, or language directory for advanced and test scenarios; normal callers need none of those paths.

The current implementation needs no custom NSIS plugin. If a future feature cannot be implemented reliably in NSIS script alone, its native plugin must be built with [`dotnetbundler/NsisPlugin`](https://github.com/dotnetbundler/NsisPlugin) as a separate `win-x86` Native AOT project, and the compiled DLL—not a build-time SDK requirement—will be embedded in `DotNet.Bundler.Nsis`.

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
| `BundlerHomepage` | No | `$(PackageProjectUrl)` |
| `BundlerCopyright` | No | `$(Copyright)` |
| `BundlerLicenseFile` | No | None; `.txt` or `.rtf` |
| `BundlerOutputPath` | No | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | No | `%LOCALAPPDATA%\DotNetBundler\tools` |
| `BundlerNsisTemplate` | No | Template included in the package |
| `BundlerNsisInstallMode` | No | `currentUser`; also supports `perMachine` and `both` |
| `BundlerNsisInstallerIcon` | No | First `BundlerIcon` `.ico` |
| `BundlerNsisUninstallerIcon` | No | Installer icon |
| `BundlerNsisHeaderImage` | No | Default NSIS artwork; `.bmp` |
| `BundlerNsisSidebarImage` | No | Default NSIS artwork; `.bmp` |
| `BundlerNsisUninstallerHeaderImage` | No | Installer header image; `.bmp` |
| `BundlerNsisInstallerHooks` | No | Optional `.nsh` lifecycle macro file |
| `BundlerNsisLanguages` | No | `English` |
| `BundlerNsisDisplayLanguageSelector` | No | `false` |

Multiple formats use a semicolon-separated value, for example `<BundlerFormats>nsis;msi</BundlerFormats>`. The task parses the complete request and the Core planner determines the required package steps. At present only the NSIS backend is implemented, so requesting MSI intentionally fails instead of silently skipping it.

Icons and extra resources are passed as MSBuild items:

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*">
    <TargetPath>licenses\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
  </BundlerResource>
</ItemGroup>
```

For NSIS, the first configured `.ico` is the fallback for both the installer and uninstaller; the NSIS-specific icon properties can override either one. Header images should be 150×57 BMP files and sidebar images should be 164×314 BMP files. `BundlerLicenseFile` adds a license page. Description, homepage, copyright, product version, and file version are written to the applicable executable or Add/Remove Programs metadata. Each resource is installed at its relative `TargetPath`; when omitted, the source file name is used. Target collisions and paths escaping the installation directory are rejected before packaging.

## Packaging pipeline and NSIS customization

All formats run through one pipeline: validate configuration, build a format-aware plan, create an isolated work directory, invoke a backend, verify its artifact, and clean the work directory. Adding MSI, macOS, or Linux support should therefore add a backend instead of duplicating orchestration.

The editable NSIS source template is stored at `templates/nsis/installer.nsi`. During package production it, the language files, and the NSIS archive are embedded into `DotNet.Bundler.Nsis`, so standalone API and MSBuild consumers get identical assets without project-output copies. Installer languages are configured like Tauri: `BundlerNsisLanguages` is a semicolon-separated list, the first language is the fallback, and the selector is shown only when `BundlerNsisDisplayLanguageSelector` is true and multiple languages are enabled. English and Simplified Chinese message files are bundled.

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

`BundlerNsisInstallMode` controls Windows installation scope. `currentUser` installs without elevation and writes uninstall metadata and shortcuts in the current-user context. `perMachine` requests administrator access, installs under Program Files, and uses the all-users shell and HKLM registry context. `both` uses the standard NSIS MultiUser page to let the user choose; because NSIS must be able to select the machine scope, launching this mode requests the highest available execution level. x64 and arm64 packages use the 64-bit registry view.

An optional `BundlerNsisInstallerHooks` file can define any of `NSIS_HOOK_PREINSTALL`, `NSIS_HOOK_POSTINSTALL`, `NSIS_HOOK_PREUNINSTALL`, and `NSIS_HOOK_POSTUNINSTALL` as NSIS macros. The installer calls each defined macro at the corresponding lifecycle boundary. Hook code runs with the installer's privileges and is responsible for handling failures explicitly.

Set `BundlerNsisTemplate` to an absolute path to use a customized copy. Supported placeholders are `product_name`, `version`, `numeric_version`, `publisher`, `identifier`, `main_executable`, `process_name`, `install_folder`, `input_glob`, `output_file`, `estimated_size`, `uninstall_payload`, `language_macros`, `language_files`, and `display_language_selector`, each written as `{{name}}`.

This is a practical Windows baseline, not Tauri feature parity. Upgrade/downgrade policy, file associations, deep links, signing, and updater command-line behavior remain planned work.

## Repository commands

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj
dotnet pack Bundler.slnx -c Release -o artifacts/packages
powershell -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.12
```

The Windows integration test installs a dedicated fixture into a Chinese path containing spaces, validates payload/resources/metadata/registry/shortcuts/process shutdown, exercises both data-preserving and full-data removal uninstalls, and cleans its test state in `finally`.

Core contains only format-neutral configuration, validation, planning, and orchestration. The NSIS project contains the public API, NSIS-specific configuration, templates, tool resolution, process execution, and backend. MSBuild and the development CLI are adapters over those packages; neither implements NSIS packaging.

## Security and licensing

The NSIS archive checksum is pinned in source. Third-party notices and the upstream NSIS license are included in the NuGet package. The license for this repository itself has not yet been selected.

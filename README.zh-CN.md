# DotNet.Bundler

[English](README.md)

`DotNet.Bundler` 是一个通过 NuGet 引用的构建包，用于把 `dotnet publish` 产物转换为桌面安装程序。使用者只需引用包并配置少量 MSBuild 属性，发布完成后便会自动生成安装包。

## 当前状态

第一条已支持链路是 Windows + NSIS。MSI、macOS 和 Linux 格式属于后续路线，目前尚未实现。

实现已经拆分为可复用的 NuGet 包。`DotNet.Bundler` 只是便利元包，实际打包代码位于以下各层。

## 包结构

| 包 | 职责 |
| --- | --- |
| `DotNet.Bundler.Abstractions` | 公共请求、目标、结果、日志契约和后端接口 |
| `DotNet.Bundler.Core` | 验证、规划、编排、工作目录和共享的内容寻址 ZIP 工具缓存 |
| `DotNet.Bundler.Nsis` | 独立 NSIS API、NSIS 配置、脚本生成、语言和内置 `makensis` 工具链 |
| `DotNet.Bundler.MSBuild` | MSBuild 参数转换与后端 API 调用；不包含 NSIS 实现 |
| `DotNet.Bundler` | 空的便利元包，引入 `DotNet.Bundler.MSBuild` 且不屏蔽其传递性构建资产 |

后续实现 WiX/MSI 时再增加 `DotNet.Bundler.Wix`，当前不会发布没有实现的空占位包。

`DotNet.Bundler.Nsis` 完整嵌入固定版本的 [`NsisToolset` 3.12-r1](https://github.com/dotnetbundler/NsisToolset/releases/tag/v3.12-r1)，其上游 NSIS 版本为 3.12。工具集包含一份公共 NSIS 数据目录，以及 Windows、Linux x64/arm64、macOS x64/arm64 的宿主编译器。使用者无需安装 NSIS，也不需要在线下载工具。首次使用会校验 SHA-256，并解压到共享的用户工具缓存；同一台机器上的项目会复用按内容寻址的工具目录。

MSBuild Task 及其直接加载的 Abstractions/Core/NSIS 程序集都提供 `netstandard2.0` 资产。打包决策和后端调度直接在 MSBuild 进程内完成，包不会再启动额外的 .NET CLI 驱动。NSIS 编译仍会启动包内与当前宿主匹配的原生 `makensis`，因为它本身就是安装程序编译器。因此，Windows NSIS 目标包可以在任一受支持的宿主上构建。

## 使用配置

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>

    <BundlerEnabled>true</BundlerEnabled>
    <BundlerIdentifier>com.example.myapp</BundlerIdentifier>
    <BundlerProductName>我的应用</BundlerProductName>
    <BundlerPublisher>示例公司</BundlerPublisher>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.13" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

然后正常执行发布：

```powershell
dotnet publish -c Release
```

安装程序默认输出到 `artifacts/<rid>/nsis/`。自动打包发生在 `Publish` 之后，普通的 `Build` 不会生成安装包。

## 独立 NSIS API

不使用 MSBuild 集成的应用和构建工具可以直接引用 `DotNet.Bundler.Nsis`：

```xml
<PackageReference Include="DotNet.Bundler.Nsis" Version="0.1.0-alpha.13" />
```

```csharp
using DotNet.Bundler;
using DotNet.Bundler.Nsis;

var request = new BundleConfiguration
{
    ProductName = "我的应用",
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

`NsisBundleConfiguration` 控制 NSIS 专属行为。高级调用和测试场景可以通过 `NsisBundlerOptions` 覆盖共享缓存、编译器、工具集压缩包、NSIS 数据目录、模板或语言目录；普通调用者不需要提供这些路径。提供自定义编译器时，如果编译器需要显式的 `NSISDIR`，还应设置 `DataDirectory`。

当前实现不需要自定义 NSIS 插件。若后续某项能力无法仅靠 NSIS 脚本可靠实现，必须使用 [`dotnetbundler/NsisPlugin`](https://github.com/dotnetbundler/NsisPlugin) 建立独立的 `win-x86` Native AOT 项目，并将编译后的 DLL 嵌入 `DotNet.Bundler.Nsis`；不能把插件 SDK 或现场编译要求转嫁给使用者。

## MSBuild 属性

| 属性 | 是否必填 | 默认值 |
| --- | --- | --- |
| `BundlerEnabled` | 是 | `false` |
| `BundlerIdentifier` | 是 | — |
| `RuntimeIdentifier` | 是 | — |
| `BundlerFormats` | 否 | `nsis` |
| `BundlerProductName` | 否 | `$(AssemblyName)` |
| `BundlerVersion` | 否 | `$(Version)` |
| `BundlerMainExecutable` | 否 | `$(TargetName).exe` |
| `BundlerPublisher` | 否 | `$(Company)` |
| `BundlerDescription` | 否 | `$(Description)` |
| `BundlerHomepage` | 否 | `$(PackageProjectUrl)` |
| `BundlerCopyright` | 否 | `$(Copyright)` |
| `BundlerLicenseFile` | 否 | 无；支持 `.txt` 或 `.rtf` |
| `BundlerOutputPath` | 否 | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | 否 | `%LOCALAPPDATA%\DotNetBundler\tools` |
| `BundlerNsisTemplate` | 否 | 包内自带模板 |
| `BundlerNsisInstallMode` | 否 | `currentUser`；也支持 `perMachine` 和 `both` |
| `BundlerNsisInstallerIcon` | 否 | 第一个 `BundlerIcon` `.ico` |
| `BundlerNsisUninstallerIcon` | 否 | 安装器图标 |
| `BundlerNsisHeaderImage` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisSidebarImage` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisUninstallerHeaderImage` | 否 | 安装器 Header 图片；`.bmp` |
| `BundlerNsisInstallerHooks` | 否 | 可选的 `.nsh` 生命周期宏文件 |
| `BundlerNsisLanguages` | 否 | `English` |
| `BundlerNsisDisplayLanguageSelector` | 否 | `false` |

多个格式使用分号分隔，例如 `<BundlerFormats>nsis;msi</BundlerFormats>`。Task 会解析完整请求，再由 Core 规划需要执行的打包步骤。目前只有 NSIS 后端已经实现，因此请求 MSI 会明确失败，不会被静默忽略。

图标和额外资源通过 MSBuild Item 传入：

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*">
    <TargetPath>licenses\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
  </BundlerResource>
</ItemGroup>
```

对 NSIS 而言，第一个 `.ico` 文件是安装器和卸载器的回退图标，也可以分别用 NSIS 专属属性覆盖。Header 图片建议为 150×57 BMP，Sidebar 图片建议为 164×314 BMP。`BundlerLicenseFile` 会增加许可证页面。描述、主页、版权、产品版本和文件版本会写入相应的可执行文件或“应用和功能”元数据。每个资源安装到相对的 `TargetPath`，省略时使用源文件名。目标路径冲突或逃逸安装目录会在打包前报错。

## 通用打包流程与 NSIS 定制

所有格式共用同一条管线：校验配置、生成包含格式依赖关系的计划、创建隔离工作目录、调用后端、确认产物存在、清理工作目录。后续增加 MSI、macOS 或 Linux 支持时，应增加后端，而不是复制整套调度代码。

可编辑的 NSIS 源模板存放在 `templates/nsis/installer.nsi`。发布包时，该模板、语言文件和完整的多宿主 NsisToolset 压缩包会嵌入 `DotNet.Bundler.Nsis`，因此独立 API 和 MSBuild 使用者得到完全相同的资源，也不会把工具复制到项目输出目录。安装器语言按 Tauri 的方式配置：`BundlerNsisLanguages` 是分号分隔的语言列表，第一项是回退语言；只有启用多个语言并把 `BundlerNsisDisplayLanguageSelector` 设为 `true` 时才显示语言选择器。包内目前提供 English 和 SimpChinese 文案文件。

```xml
<PropertyGroup>
  <BundlerNsisLanguages>English;SimpChinese</BundlerNsisLanguages>
  <BundlerNsisDisplayLanguageSelector>true</BundlerNsisDisplayLanguageSelector>
</PropertyGroup>
```

其他 NSIS 语言需要提供包含模板全部 `LangString` 的自定义文案文件：

```xml
<ItemGroup>
  <BundlerNsisLanguageFile Include="installer-languages\German.nsh" Language="German" />
</ItemGroup>
```

默认模板提供当前用户安装、可选择并记住上次位置的安装目录、非本应用的非空目录警告、DPI 感知、压缩、可选的开始菜单与桌面快捷方式、运行程序检测和关闭、“应用和功能”卸载信息、可选删除应用数据、静默卸载以及完成页启动程序。未选择删除应用数据时，卸载只删除程序目录中属于构建载荷的路径：程序后来在新路径创建的文件会保留；如果创建或覆盖的是构建载荷中的同名路径，卸载时仍会删除。选择删除应用数据时，会递归删除整个程序安装目录，以及 `%APPDATA%\<identifier>` 和 `%LOCALAPPDATA%\<identifier>`。

`BundlerNsisInstallMode` 控制 Windows 安装范围。`currentUser` 不提权，卸载信息和快捷方式写入当前用户上下文；`perMachine` 请求管理员权限，安装到 Program Files，并使用所有用户 Shell 上下文和 HKLM 注册表；`both` 使用 NSIS 自带的 MultiUser 页面让用户选择。由于安装器必须具备切换到计算机范围的能力，`both` 启动时会请求最高可用权限。x64 和 arm64 包使用 64 位注册表视图。

可选的 `BundlerNsisInstallerHooks` 文件可以把 `NSIS_HOOK_PREINSTALL`、`NSIS_HOOK_POSTINSTALL`、`NSIS_HOOK_PREUNINSTALL`、`NSIS_HOOK_POSTUNINSTALL` 中任意几项定义为 NSIS 宏，安装器会在相应生命周期边界调用。Hook 使用安装器当前权限执行，失败处理需要在宏中明确编写。

若要定制，可把模板复制出来，并将 `BundlerNsisTemplate` 设为其绝对路径。模板支持 `product_name`、`version`、`numeric_version`、`publisher`、`identifier`、`main_executable`、`process_name`、`install_folder`、`input_glob`、`output_file`、`estimated_size`、`uninstall_payload`、`language_macros`、`language_files`、`display_language_selector`，写法为 `{{name}}`。

这是面向 Windows 的可用基线，并不等于 Tauri 功能对等。升级/降级策略、文件关联、深链接、签名和更新器命令行行为仍属于后续工作。

## 仓库命令

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj
dotnet pack Bundler.slnx -c Release -o artifacts/packages
powershell -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.13
```

Windows 集成测试会把专用测试程序安装到包含中文和空格的目录，验证载荷、外部资源、元数据、注册表、快捷方式和进程关闭，分别执行保留数据与彻底删除数据的卸载，并在 `finally` 中清理测试状态。

Abstractions 保存跨包稳定契约。Core 实现与格式无关的校验、规划、编排、工作目录生命周期和通用 ZIP 工具缓存。NSIS 项目包含公共后端 API、NSIS 专属配置、模板、进程执行和资源。MSBuild 与开发用 CLI 都只是这些包的适配层，不实现 NSIS 打包逻辑。

## 安全与许可证

NsisToolset 发布包的校验值已经固定在源码中。NuGet 包会同时携带第三方声明和 NSIS 上游许可证。本仓库自身采用何种开源许可证尚未确定。

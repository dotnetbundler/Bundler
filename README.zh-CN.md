# DotNet.Bundler

[English](README.md)

`DotNet.Bundler` 是一个通过 NuGet 引用的构建包，用于把 `dotnet publish` 产物转换为桌面安装程序。使用者只需引用包并配置少量 MSBuild 属性，发布完成后便会自动生成安装包。

## 当前状态

第一条已支持链路是 Windows + NSIS。MSI、macOS 和 Linux 格式属于后续路线，目前尚未实现。

NuGet 包内携带固定版本的 NSIS 3.12 便携压缩包，使用者无需自行安装 NSIS。首次使用时会先校验 SHA-256，再解压到项目的中间输出目录。首个版本有意保留项目级缓存，后续可迭代为按内容寻址的共享缓存。

MSBuild Task 及其直接加载的 Core 程序集都以 `netstandard2.0` 为目标框架。打包决策和后端调度直接在 MSBuild 进程内完成，包不会再启动额外的 .NET CLI 驱动。NSIS 编译仍会启动包内的 `makensis.exe`，因为它本身就是安装程序编译器。

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.7" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

然后正常执行发布：

```powershell
dotnet publish -c Release
```

安装程序默认输出到 `artifacts/<rid>/nsis/`。自动打包发生在 `Publish` 之后，普通的 `Build` 不会生成安装包。

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
| `BundlerOutputPath` | 否 | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | 否 | `$(BaseIntermediateOutputPath)bundler\tools` |
| `BundlerNsisTemplate` | 否 | 包内自带模板 |

多个格式使用分号分隔，例如 `<BundlerFormats>nsis;msi</BundlerFormats>`。Task 会解析完整请求，再由 Core 规划需要执行的打包步骤。目前只有 NSIS 后端已经实现，因此请求 MSI 会明确失败，不会被静默忽略。

图标和额外资源通过 MSBuild Item 传入：

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*" />
</ItemGroup>
```

## 通用打包流程与 NSIS 定制

所有格式共用同一条管线：校验配置、生成包含格式依赖关系的计划、创建隔离工作目录、调用后端、确认产物存在、清理工作目录。后续增加 MSI、macOS 或 Linux 支持时，应增加后端，而不是复制整套调度代码。

NSIS 脚本存放在 `templates/nsis/installer.nsi` 文件中，不再嵌入 C#。默认模板提供当前用户安装、可选择并记住上次位置的安装目录、非本应用的非空目录警告、DPI 感知、压缩、中英文界面、可选的开始菜单与桌面快捷方式、运行程序检测和关闭、“应用和功能”卸载信息、可选删除应用数据、静默卸载以及完成页启动程序。卸载时只删除构建载荷中记录的文件，不会递归删除用户任意选择的目录。若要定制，可把模板复制出来，并将 `BundlerNsisTemplate` 设为其绝对路径。模板支持 `product_name`、`version`、`numeric_version`、`publisher`、`identifier`、`main_executable`、`process_name`、`install_folder`、`input_glob`、`output_file`、`estimated_size`、`uninstall_payload`，写法为 `{{name}}`。

这只是参考 Tauri 后形成的可用基线，并不等于已经达到 Tauri 的功能完整度。升级/降级策略、安装范围选择、文件关联、深链接、签名和生命周期钩子仍需先设计成结构化配置，再适合对外开放。

## 仓库命令

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
dotnet pack src/Bundler.MSBuild/Bundler.MSBuild.csproj -c Release -o artifacts/packages
```

Core 项目包含配置、校验、规划、工具解析和打包后端，并不依赖 MSBuild。现有 CLI 仍作为 Core 的开发调用端保留，但不再发布到 NuGet 构建包中。

## 安全与许可证

NSIS 压缩包的校验值已经固定在源码中。NuGet 包会同时携带第三方声明和 NSIS 上游许可证。本仓库自身采用何种开源许可证尚未确定。

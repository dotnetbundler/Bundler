# DotNet.Bundler

[English](README.md)

`DotNet.Bundler` 是一个通过 NuGet 引用的构建包，用于把 `dotnet publish` 产物转换为桌面安装程序。使用者只需引用包并配置少量 MSBuild 属性，发布完成后便会自动生成安装包。

## 当前状态

第一条已支持链路是 Windows + NSIS。MSI、macOS 和 Linux 格式属于后续路线，目前尚未实现。

NuGet 包内携带固定版本的 NSIS 3.12 便携压缩包，使用者无需自行安装 NSIS。首次使用时会先校验 SHA-256，再解压到项目的中间输出目录。

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.2" PrivateAssets="all" />
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
| `BundlerFormat` | 否 | `nsis` |
| `BundlerProductName` | 否 | `$(AssemblyName)` |
| `BundlerVersion` | 否 | `$(Version)` |
| `BundlerMainExecutable` | 否 | `$(TargetName).exe` |
| `BundlerPublisher` | 否 | `$(Company)` |
| `BundlerOutputPath` | 否 | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | 否 | `$(BaseIntermediateOutputPath)bundler\tools` |

## 仓库命令

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Core.Tests/Bundler.Core.Tests.csproj
dotnet pack src/Bundler.Cli/Bundler.Cli.csproj -c Release -o artifacts/packages
```

仓库仍保留 `validate` 和 `plan` CLI 命令，供开发和诊断使用。配置文件结构可参考 `examples/bundler.example.json`。

## 安全与许可证

NSIS 压缩包的校验值已经固定在源码中。NuGet 包会同时携带第三方声明和 NSIS 上游许可证。本仓库自身采用何种开源许可证尚未确定。

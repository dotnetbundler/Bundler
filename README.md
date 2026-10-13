# DotNet.Bundler

`DotNet.Bundler` 是一个通用桌面应用打包工具。
当前首先发布 NuGet/MSBuild 集成：使用者引用包并配置少量 MSBuild 属性后，可以把 `dotnet publish` 产物转换为桌面安装程序。
Core 和格式后端本身不依赖 `.csproj` 或 .NET 应用模型，`bundler` CLI 与直接公共 API 复用同一套实现，为其他语言和构建系统提供入口。

## 当前状态

全部打包格式、CLI 与 UPDATE 自更新模块均已冻结（冻结=功能面不再扩展，缺陷修复照走）。

| 平台 | 格式 | 文档 |
| --- | --- | --- |
| Windows | `nsis`、`msi` | [`docs/formats/win-nsis.md`](docs/formats/win-nsis.md)、[`win-msi.md`](docs/formats/win-msi.md) |
| macOS | `.app`、`.dmg`、`.pkg` | [`mac-app.md`](docs/formats/mac-app.md)、[`mac-dmg.md`](docs/formats/mac-dmg.md)、[`mac-pkg.md`](docs/formats/mac-pkg.md) |
| Linux | `.deb`、`.rpm`、`.AppImage`、Alpine `.apk` | [`linux-deb.md`](docs/formats/linux-deb.md)、[`linux-rpm.md`](docs/formats/linux-rpm.md)、[`linux-appimage.md`](docs/formats/linux-appimage.md)、[`linux-alpine-apk.md`](docs/formats/linux-alpine-apk.md) |
| 跨平台 | `.zip`/`.tar.gz` | [`archive.md`](docs/formats/archive.md) |
| 其他 | `bundler` CLI、UPDATE、签名 | [`cli.md`](docs/cli.md)、[`update.md`](docs/update.md)、[`signing.md`](docs/signing.md) |

各格式打包/消费的平台支持范围汇总见 [`docs/support-matrix.md`](docs/support-matrix.md)。
全后端统一示例见 [`samples/HelloBundlerApp`](samples/HelloBundlerApp/hello-bundler-app-sample.md)：单个工程覆盖全部格式的全部公开旋钮——公共旋钮在主工程，各后端专属旋钮按 `formats/<Format>.props` 导入（用户项目可直接复制该组织方式）。

实现已经拆分为可复用的 NuGet 包。
`DotNet.Bundler` 只是便利元包，实际打包代码位于以下各层。

## 包结构

| 包 | 职责 |
| --- | --- |
| `DotNet.Bundler.Abstractions` | 公共请求、目标、结果、日志契约和后端接口 |
| `DotNet.Bundler.Core` | 验证、规划、编排、工作目录和共享的内容寻址 ZIP 工具缓存 |
| `DotNet.Bundler.Nsis` | 独立 NSIS API、NSIS 配置、脚本生成、语言和内置 `makensis` 工具链 |
| `DotNet.Bundler.Wix` | 独立 MSI API、WiX 3.14.1 工具子集和声明式 Windows Installer 数据库生成 |
| `DotNet.Bundler.Signing.Windows` | 可复用的 Windows Authenticode 签名实现 |
| `DotNet.Bundler.MacApp` | 独立 macOS `.app` API、Info.plist/`.icns` 生成与 Contents 载荷映射（不内嵌 Apple 工具） |
| `DotNet.Bundler.MacDmg` | 独立 macOS `.dmg` API、`hdiutil` 全链与拖放卷生成（压缩可配 `Udzo`/`Ulmo`/`Udbz`） |
| `DotNet.Bundler.MacPkg` | 独立 macOS `.pkg` API、`pkgbuild` 组件包 + `productbuild` 分发包生成（identifier/version/install-location/domain/页面/脚本可配、任意载荷映射），`pkgbuild --sign`/`productsign` 签名与 notarytool 公证管线 |
| `DotNet.Bundler.Deb` | 独立 Debian `.deb` API、纯托管 ar/tar/gzip 写入器（核心 control 字段、md5sums、`usr/lib`+`usr/bin` 链接布局、SemVer→deb 版本映射），无原生工具依赖 |
| `DotNet.Bundler.Rpm` | 独立 RPM `.rpm` API、纯托管 lead/header/cpio/gzip 写入器（核心 tag 集、六族关系字段、文件清单与 %doc/%license 标记、`usr/lib`+`usr/bin` 链接布局、freedesktop 桌面集成、SemVer→rpm 版本映射、`RpmPackageReader` 回读器），无原生工具依赖 |
| `DotNet.Bundler.AlpineApk` | 独立 Alpine `.apk` API、纯托管三段 gzip 写入器（`.PKGINFO`、六段安装脚本、`depend`/`provides`/`triggers`、`usr/lib`+`usr/bin` 链接、逐文件 pax 校验和、可选 RSA 签名段、`ApkPackageReader` 回读器），无原生工具依赖 |
| `DotNet.Bundler.AppImage` | 独立 Linux `.AppImage` API、内嵌固定版本 appimagetool+type2 runtime（SHA-256 provenance）与 AppDir 组装，仅 Linux 宿主构建 |
| `DotNet.Bundler.Archive` | 独立 `.zip`/`.tar.gz` API、纯托管写入器（unix mode/符号链接保留、`.sha256` 侧车），无原生工具依赖 |
| `DotNet.Bundler.Update` | UPDATE 打包侧：静态 JSON 清单发射 + ECDSA P-256 `.sig`/`.blockmap` 旁车生成 |
| `DotNet.Bundler.Updater` | UPDATE 应用内库：`UpdateClient` 查/下/验/换四动词 + block-map 差分 |
| `DotNet.Bundler.Updater.Bootstrap` | UPDATE 外置引导件：per-target Native AOT `bundler-updater` + posix sh 降级件 |
| `DotNet.Bundler.MSBuild` | MSBuild 参数转换与后端 API 调用；不包含 NSIS 实现 |
| `DotNet.Bundler` | 空的便利元包，引入 `DotNet.Bundler.MSBuild` 且不屏蔽其传递性构建资产 |

`bundler` CLI（`src/Bundler.Cli`）提供 `validate`/`plan`/`bundle`/`init` 命令，覆盖全部冻结格式，双分发：dotnet tool（`DotNet.Bundler.Cli` nupkg，`ToolCommandName=bundler`）与原生 AOT 单二进制（`dotnet publish -r <rid>`，宿主裁剪自研后端）。

```powershell
dotnet tool install --global DotNet.Bundler.Cli
# 最小路径：只指发布目录，其余全部推断（target=宿主、formats=目标 OS 常用集、
# productName=目录名、version=0.1.0、identifier=dev.bundler.<名>、
# mainExecutable=目录内唯一可执行文件）——推断值逐条打到 stderr
bundler bundle --input-dir <publish目录>
# 显式覆盖任意字段（恒胜推断）
bundler bundle --input-dir <publish目录> --target linux-x86_64 --formats zip,deb \
  --product-name MyApp --identifier com.example.myapp --package-version 1.0.0 \
  --output-dir dist
# --strict 关闭推断：缺什么报什么，适合发布脚本
# 多目录 universal 合并：--input-dir 可重复，≥2 个目录先合并再打包
bundler bundle --input-dir bin/osx-x64 --input-dir bin/osx-arm64 --target macos-universal --formats zip
# 生成最小 bundler.json 模板（身份字段占位，targets 注释示例）；cwd 里的 bundler.json 自动生效
bundler init && bundler bundle --input-dir <publish目录>
```

也可用 `bundler.json` 承载配置（自动发现于当前目录；CLI 参数覆盖文件值，`--<fmt>.<knob>=` 透传格式旋钮，`--no-config` 跳过自动发现），schema 与 MSBuild `Bundler*` 属性一一对应，映射表与退出码/`--json` 契约见 [`docs/cli.md`](docs/cli.md)。
后续路线和产品边界见 [`docs/roadmap.md`](docs/roadmap.md)。
MSI 语义契约（current-user/per-machine、版本映射与升降级、旧 MSI 迁移、WiX 扩展与专家模式、38 语言产物）与验证边界见 [`docs/formats/win-msi.md`](docs/formats/win-msi.md)；外部待验收项登记在 [`docs/special-acceptance.md`](docs/special-acceptance.md)。

`DotNet.Bundler.Wix` 是可独立引用的 MSI 后端包和直接 API；MSBuild 调用同一后端。
跨格式的开发与包消费规则见 [`docs/development-rules.md`](docs/development-rules.md)。

`DotNet.Bundler.Nsis` 完整嵌入固定版本的 [`NsisToolset` 3.12-r1](https://github.com/dotnetbundler/NsisToolset/releases/tag/v3.12-r1)，其上游 NSIS 版本为 3.12。
工具集包含一份公共 NSIS 数据目录，以及 Windows、Linux x64/arm64、macOS x64/arm64 的宿主编译器。
使用者无需安装 NSIS，也不需要在线下载工具。
每次解析都会先校验压缩包 SHA-256，再以压缩包内逐文件哈希清单验证共享缓存；缺失、篡改、额外文件、manifest 损坏或重解析点都会在跨进程锁内触发安全重建。
缓存支持 Unicode 和长路径，同一台机器上的项目复用按内容寻址的工具目录。

MSBuild Task 及其直接加载的 Abstractions/Core/NSIS/WiX 程序集都提供 `netstandard2.0` 资产，使任务能同时被 .NET Framework `MSBuild.exe` 和 `dotnet msbuild` 双宿主加载。
打包决策和后端调度直接在 MSBuild 进程内完成，包不会再启动额外的 .NET CLI 驱动。
NSIS 编译仍会启动包内与当前宿主匹配的原生 `makensis`，因为它本身就是安装程序编译器；MSI 编译使用包内 WiX 工具，当前仅开放 Windows 构建宿主。

## 使用配置

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>

    <BundlerEnabled>true</BundlerEnabled>
    <BundlerTarget>windows-x86_64</BundlerTarget>
    <BundlerIdentifier>com.example.myapp</BundlerIdentifier>
    <BundlerProductName>我的应用</BundlerProductName>
    <BundlerPublisher>示例公司</BundlerPublisher>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.102" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

然后正常执行发布：

```powershell
dotnet publish -c Release -r win-x64
```

`dotnet publish` 后自动打包；`BundlerTarget` 可按 `$(RuntimeIdentifier)` 从 canonical RID 表推导省略——上例 `win-x64` 即推导为 `windows-x86_64`，显式 `-p:BundlerTarget=` 恒覆盖。
`BundlerTarget` 与 `RuntimeIdentifier` 都未设时回落 SDK 宿主 RID 推导——`-t:BundlerBundle -p:BundlerInputDir=` 免目标即开即用，且推导在 eval 期完成，csproj 内按 `$(BundlerTarget)` 条件化的配置照常命中。
不经 Publish 钩子也可以显式调用 `BundlerBundle` target，三种模式：

```powershell
# 自动发布+打包：按 BundlerTarget 查 canonical publish RID 内层发布到 obj/BundlerPublish/ 再打包
dotnet msbuild -t:BundlerBundle -p:BundlerTarget=windows-x86_64 -p:Configuration=Release
# 只打包不发布：拿已有发布目录直接打
dotnet msbuild -t:BundlerBundle -p:BundlerInputDir=bin\Release\net10.0\win-x64\publish -p:BundlerTarget=windows-x86_64
# canonical 映射外 RID：显式指定内层发布 RID
dotnet msbuild -t:BundlerBundle -p:BundlerTarget=linux-x86_64 -p:BundlerPublishRid=ubuntu.24.04-x64
```

安装程序默认输出到 `artifacts/`（扁平布局；`BundlerOutputLayout=byFormat` 时按 `artifacts/<format>/` 分格）。
自动打包发生在 `Publish` 之后，普通的 `Build` 不会生成安装包。
`BundlerEnabled` 只控 Publish 钩子路；显式 `-t:BundlerBundle` 不需要它（无任何开关依赖）。
`BundlerIdentifier` 缺省时合成 `dev.bundler.<AssemblyName>` 并打警告——正式发布仍应显式指定。
两入口同样支持 `BundlerUniversalTargets`（macOS universal 内层双 RID 发布+合并）。
canonical RID 映射表：`windows-x86_64→win-x64`、`windows-i686→win-x86`、`windows-aarch64→win-arm64`、`macos-x86_64→osx-x64`、`macos-aarch64→osx-arm64`、`linux-x86_64→linux-x64`、`linux-i686→linux-x86`、`linux-aarch64→linux-arm64`、`linux-musl-x86_64→linux-musl-x64`、`linux-musl-aarch64→linux-musl-arm64`；`macos-universal` 无单一 RID 映射，走 `BundlerUniversalTargets`。

## 独立 NSIS API

不使用 MSBuild 集成的应用和构建工具可以直接引用 `DotNet.Bundler.Nsis`：

```xml
<PackageReference Include="DotNet.Bundler.Nsis" Version="0.1.0-alpha.102" />
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
            Target = "windows-x86_64",
            InputDirectory = "publish/win-x64",
            MainExecutable = "MyApp.exe",
            Formats = [PackageFormat.Nsis]
        }
    ]
};

var artifacts = await new NsisBundler().BuildAsync(request);
```

`NsisBundleConfiguration` 控制 NSIS 专属行为，包括安装范围、压缩、Artwork、语言、Hook、降级和快捷方式。
`Compression` 支持 `Lzma`（默认）、`Zlib`、`Bzip2` 和 `None`。
`windows-i686`、`windows-x86_64`、`windows-aarch64` 目标均可产出 NSIS 安装器；x86 载荷需在装有 32 位 .NET 运行时的宿主上运行（仅 x64 运行时的宿主启动报 `0x800700C1`），属目标环境前置而非打包限制。
高级调用和测试场景可以通过 `NsisBundlerOptions` 覆盖共享缓存、编译器、工具集压缩包、NSIS 数据目录、模板或语言目录；普通调用者不需要提供这些路径。
提供自定义编译器时，如果编译器需要显式的 `NSISDIR`，还应设置 `DataDirectory`。

独立 API 通过 `NsisBundleConfiguration.Shortcuts` 设置 `NsisShortcutConfiguration`。
其中的 `Desktop`、`StartMenu`、`Arguments`、`WorkingDirectory`、`Icon`、`AppUserModelId` 和 `StartMenuFolder` 与下方 MSBuild 属性对应；
`LegacyProductNames` 和 `LegacyMainExecutables` 用于在改名后显式启用安全迁移。

包内嵌了使用 [`dotnetbundler/NsisPlugin`](https://github.com/dotnetbundler/NsisPlugin) 构建的 `win-x86` Native AOT 插件。
它提供 SemVer 比较、MSI 查询、降权启动和基于 Shell COM 的快捷方式操作，不要求使用者安装额外 SDK 或打包工具。

## 独立 MSI API

普通 .NET 项目可以只引用 MSI 后端包，直接打包已准备好的目录，无需引用 MSBuild 便利元包或本仓库源码：

```xml
<PackageReference Include="DotNet.Bundler.Wix" Version="0.1.0-alpha.102" />
```

```csharp
using DotNet.Bundler;
using DotNet.Bundler.Wix;

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
            Target = "windows-x86_64",
            InputDirectory = "publish/win-x64",
            MainExecutable = "MyApp.exe",
            Formats = [PackageFormat.Msi]
        }
    ]
};

var artifacts = await new WixBundler(new WixBundleConfiguration
{
    Languages = ["zh-CN"]
}).BuildAsync(request);
```

`WixBundleConfiguration` 配置 MSI 安装范围、`Languages`（culture 列表，每语言一个独立 MSI）、`LocaleFiles`（culture→`.wxl` 覆盖文件）、`FipsCompliant`、代码页、快捷方式、`MsiVersion`、`AllowDowngrades`、`InstallDirectorySelection`、`BannerBitmap`、`DialogBitmap`、`AddToPath`、`UninstallShortcut` 和 `LaunchAfterInstall`。
范围内安装目录限定 current-user 在 `%LOCALAPPDATA%` 子目录、per-machine 在 Program Files 子目录，静默 `INSTALLFOLDER=` 走同一校验；
启动勾选只在交互安装勾选时以用户会话执行，静默/被动/修复/升级不触发。
`windows-i686`、`windows-x86_64`、`windows-aarch64` 是独立产品线；x86 可在当前 x64 Windows 构建并安装。
默认用三段稳定应用版本作 MSI 版本；预发布应用须显式提供有效的三段 `MsiVersion`，第四段与越界版本被拒绝。
不同应用版本不得复用同一 MSI 版本，已安装的同版异包会被拒绝。
降级默认禁止，只有调用方显式设置 `AllowDowngrades=true` 才允许。
代码页 `0`（默认）按语言表自动选择（如英文 1252、简体中文 936）；显式值覆盖所有语言，但必须能编码该语言的合并本地化字符串。
各语言是独立的单语言 MSI 产品线，非英语包名带 `-<culture>` 后缀（如 `-zh-cn`、`-ja-jp`），安装目录和 UpgradeCode 也隔离。
应用可以提供 `.rtf` `LicenseFile` 以启用该语言的 WiX 交互许可页；
启用目录选择或品牌位图等新 UI 能力时使用自定义 dialog 序列（有许可含许可页，无许可不含），未启用任何 UI 能力时继续使用 Windows Installer 原生的基础界面，不显示替代性许可条款。
已有英文产品身份保持稳定。
应用的其他显示文本由调用方提供，Bundler 不自动翻译。

`WixBundlerOptions.Signer` 可复用 `WindowsAuthenticodeSigner` 或自定义 `IBundleSigner`：先签隔离副本中的主程序和显式 `SigningFiles`，再签最终 MSI。
MSBuild 使用既有的 `BundlerWindowsSigning*` 属性和 `BundlerWindowsSigningFile` 项。
签名失败会移除新 MSI；已有同版本签名包不会被静默复用，重新签名需新输出目录或新应用版本。
生产证书和时间戳由发行方提供，私钥不进入仓库。

WiX 3.14.1 工具随包提供，当前 MSI 构建要求 Windows 宿主。
当前开发版本尚未公开到默认 NuGet 源时，先从本仓库 pack 并指定本地包源。
上述包引用与实际编译由复制到仓库外目录的 `tests/Bundler.LocalPackagesTests/Fixtures/Msi/Standalone` fixture 自动验证；MSI 产品身份和安装行为以 MSI 专用文档为准。

## MSBuild 属性

| 属性 | 是否必填 | 默认值 |
| --- | --- | --- |
| `BundlerEnabled` | Publish 钩子路必填 | `false`；显式 `-t:BundlerBundle` 不检查 |
| `BundlerIdentifier` | 否 | 缺省合成 `dev.bundler.<AssemblyName>`（打警告；正式发布应显式指定） |
| `BundlerTarget` | 条件必填 | —；缺省时按 `$(RuntimeIdentifier)`、再回落 SDK 宿主 RID 查 canonical RID 表推导，显式指定恒覆盖；`BundlerUniversalTargets` 设置时也可省略 |
| `RuntimeIdentifier` | 否 | `dotnet publish` 侧标准属性（.NET RID）；`BundlerTarget` 缺省时兼作推导来源 |
| `BundlerInputDir` | 否 | 无；`-t:BundlerBundle` 下非空即“只打包不发布”——拿已有发布目录直接打，跳过内层 publish |
| `BundlerPublishRid` | 否 | 按 `BundlerTarget`→canonical publish RID 表默认；表外 RID 显式覆盖内层发布 |
| `BundlerFormats` | 否 | 按目标 OS 推导：`windows→nsis`、`macos*/universal→dmg`、`linux→deb`、`linux-musl→alpineapk`；分号分隔多值扇出，可用 `nsis`/`msi`/`app`/`dmg`/`pkg`/`deb`/`rpm`/`appimage`/`zip`/`targz`/`alpineapk`（CLI 另收 `all` 全量别名） |
| `BundlerUniversalTargets` | 否 | 无；macOS universal 用——复数 .NET RID（如 `osx-x64;osx-arm64`，命令行 `-p:` 须写 `%3B`）对每个 RID 内层 `dotnet publish` 后托管合并成 universal 目录再按 `macos-universal` 打包 |
| `BundlerProductName` | 否 | `$(AssemblyName)` |
| `BundlerVersion` | 否 | `$(Version)` |
| `BundlerMainExecutable` | 否 | `$(TargetName).exe` |
| `BundlerPublisher` | 否 | `$(Company)` |
| `BundlerDescription` | 否 | `$(Description)` |
| `BundlerHomepage` | 否 | `$(PackageProjectUrl)` |
| `BundlerCopyright` | 否 | `$(Copyright)` |
| `BundlerLicenseFile` | 否 | 无；NSIS 支持 `.txt`/`.rtf`，MSI 交互许可界面仅支持 `.rtf`，DMG 将其注入 SLA 挂载许可面板 |
| `BundlerOutputPath` | 否 | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerToolCachePath` | 否 | `%LOCALAPPDATA%\DotNetBundler\tools` |
| `BundlerNsisTemplate` | 否 | 包内自带模板 |
| `BundlerNsisInstallScope` | 否 | `currentUser`；也支持 `perMachine` 和 `both` |
| `BundlerNsisCompression` | 否 | `lzma`；也支持 `zlib`、`bzip2` 和 `none` |
| `BundlerNsisInstallerIconFile` | 否 | 第一个 `BundlerIcon` `.ico` |
| `BundlerNsisUninstallerIconFile` | 否 | 安装器图标 |
| `BundlerNsisHeaderFile` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisSidebarFile` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisUninstallerHeaderFile` | 否 | 安装器 Header 图片；`.bmp` |
| `BundlerNsisInstallerHooksFile` | 否 | 可选的 `.nsh` 生命周期宏文件 |
| `BundlerNsisLanguages` | 否 | `English` |
| `BundlerNsisDisplayLanguageSelector` | 否 | `false` |
| `BundlerNsisAllowDowngrades` | 否 | `false` |
| `BundlerNsisShortcutDesktop` | 否 | `true` |
| `BundlerNsisShortcutStartMenu` | 否 | `true` |
| `BundlerNsisShortcutArguments` | 否 | 无 |
| `BundlerNsisShortcutWorkingDirectory` | 否 | 安装目录；设置时使用安装目录内相对路径 |
| `BundlerNsisShortcutIcon` | 否 | 主程序；设置时使用安装目录内相对路径 |
| `BundlerNsisShortcutAppUserModelId` | 否 | `BundlerIdentifier` |
| `BundlerNsisShortcutStartMenuFolder` | 否 | `BundlerProductName`；用 `.` 表示 Programs 根目录 |
| `BundlerNsisShortcutLegacyProductNames` | 否 | 分号分隔的旧产品名 |
| `BundlerNsisShortcutLegacyMainExecutables` | 否 | 分号分隔的旧主程序安装相对路径 |
| `BundlerNsisLegacyMsiProductCodes` | 否 | 分号分隔的 MSI ProductCode GUID |
| `BundlerNsisLegacyMsiUpgradeCodes` | 否 | 分号分隔的 MSI UpgradeCode GUID |
| `BundlerNsisLegacyMsiAutoDetect` | 否 | `false`；启用后按 DisplayName+Publisher+msiexec 自动匹配旧 MSI |
| `BundlerWixInstallScope` | 否 | `currentUser`；也支持 `perMachine`，两者是独立产品线 |
| `BundlerWixUpgradeCode` | 否 | 自动稳定生成；仅在有依据的旧产品迁移中显式指定 |
| `BundlerWixVersion` | 否 | 空；默认映射稳定三段 `BundlerVersion`，显式值须为有效三段 MSI 版本 |
| `BundlerWixAllowDowngrades` | 否 | `false`；仅显式选择时允许旧 MSI 版本替换新版 |
| `BundlerWixLanguages` | 否 | `en-US`；分号分隔的多语言列表（命令行用 `%3B` 转义分号），每语言一个独立 MSI，支持 38 个 WiX 内嵌 culture |
| `BundlerWixLanguage` | 否 | 旧单语言回退；`BundlerWixLanguages` 为空时生效 |
| `BundlerWixLanguageFile`（项） | 否 | 调用方 `.wxl` 覆盖文件，要求 `Language` 元数据；键须属 Bundler 自有键集，编码/culture 经校验 |
| `BundlerWixFipsCompliant` | 否 | `false`；仅向 `candle.exe` 透传 `-fips`，不构成 FIPS 认证声明 |
| `BundlerWixCodepage` | 否 | `0` 按语言表自动（如英文 1252/简体中文 936）；显式值覆盖所有语言且须能编码包内文字 |
| `BundlerWixStartMenuShortcut` | 否 | `false` |
| `BundlerWixDesktopShortcut` | 否 | `false` |
| `BundlerWixInstallDirectorySelection` | 否 | `false`；开启后交互可选范围内子目录，静默 `INSTALLFOLDER` 走同一校验 |
| `BundlerWixBannerFile` | 否 | WiX 默认横幅；`.bmp` 且必须 493×58 |
| `BundlerWixDialogFile` | 否 | WiX 默认对话框图；`.bmp` 且必须 503×314 |
| `BundlerWixAddToPath` | 否 | `false`；用户/系统 PATH 仅追加安装目录，卸载移除 |
| `BundlerWixUninstallShortcut` | 否 | `false`；开始菜单生成 `msiexec /x` 卸载入口 |
| `BundlerWixLaunchAfterInstall` | 否 | `false`；仅交互完成页勾选时启动，静默/被动/修复/升级不触发 |
| `BundlerWixExtensionFragment`（项） | 否 | 常规模式 `.wxs` fragment；白名单声明式元素，所有 `@Id` 须以 `BundlerWixExtensionIdPrefix` 开头 |
| `BundlerWixExtensionIdPrefix` | 否 | 常规模式必填自定义 id 前缀（如 `Ext.`）；保留 Bundler/WiX 前缀不可用 |
| `BundlerWixExtensionComponentRef`/`…ComponentGroupRef`/`…FeatureRef`（项） | 否 | 常规模式显式引用 id，注入 Product Feature |
| `BundlerWixExpertTemplateFile` | 否 | 专家模式：整份 `.wxs` 替换生成的产品文档；身份经 `candle -d` 变量与构建后回读强制 |
| `BundlerWixExpertMergeModule`（项） | 否 | 专家模式 `.msm` 直传 light；专家产物为调用方自备逻辑，不受管、不担保可回滚 |
| `BundlerMacAppPackageName` | 否 | `BundlerProductName`；`CFBundleName` |
| `BundlerMacAppDisplayName` | 否 | `BundlerProductName`；`CFBundleDisplayName` |
| `BundlerMacAppShortVersion` | 否 | `BundlerVersion`；`CFBundleShortVersionString` |
| `BundlerMacAppBuildVersion` | 否 | `BundlerVersion`；`CFBundleVersion` |
| `BundlerMacAppMinimumSystemVersion` | 否 | 无；未配置不写入 `LSMinimumSystemVersion`，安装宿主超限拒绝靠安装时校验 |
| `BundlerMacAppCategory` | 否 | 无；`LSApplicationCategoryType` |
| `BundlerMacAppIconName` | 否 | `AppIcon`；`CFBundleIconName`（`.car` 直通/`actool` 管线时匹配 Assets 目录件） |
| `@(BundlerMacDocumentType)`/`@(BundlerMacUrlType)` | 否 | 文件关联 `CFBundleDocumentTypes`+UTI 导出 / `CFBundleURLTypes` 项组 |
| `@(BundlerMacAppFile)`/`@(BundlerMacAppFrameworkDirectory)` | 否 | Contents 下任意相对路径 / `Frameworks` 嵌套项（`Destination` 元数据） |
| `BundlerMacAppExceptionDomain` | 否 | 无；ATS `NSExceptionDomains` 单一域（允许不安全 HTTP 载荷含子域） |
| `BundlerMacAppInfoPlistFile`/`…InfoPlistXml` | 否 | 无；调用方 plist 整文件 / 内联 XML 合并（二选一；身份键回读拒绝） |
| `BundlerMacAppSignIdentity` | 否 | 无；`-`=ad-hoc / 证书 CN，开启 inside-out `codesign` 管线（仅 macOS 宿主） |
| `BundlerMacAppTemporaryCertificateFile`/`…Password` | 否 | 无；p12 证书导入临时钥匙串签名（与 identity 互斥；密码属秘密勿入库） |
| `BundlerMacAppHardenedRuntime` | 否 | `false`；开启后每个可执行签名加 `--options runtime`（公证必需） |
| `BundlerMacAppEntitlementsFile` | 否 | 无；entitlements plist 文件 |
| `BundlerMacAppNotarize` | 否 | `false`；显式公证管线（ditto→`notarytool`→`stapler`，需凭证） |
| `BundlerMacAppNotaryWait`/`…SkipStapling` | 否 | `true`/`false`；notarytool 不等待 / 跳过 stapler |
| `BundlerMacAppNotaryProfile` | 否 | 无；keychain profile 名（公证凭证三模式之一） |
| `BundlerMacAppAppleId`/`…ApplePassword`/`…AppleTeamId` | 否 | 无；Apple ID 凭证三元组（密码属秘密勿入库） |
| `BundlerMacAppNotaryApiKeyFile`/`…KeyId`/`…KeyIssuer` | 否 | 无；App Store Connect API key 凭证三元组 |
| —— 公证凭证环境变量回退 | —— | 全部凭证属性缺省时依次回退 `APPLE_PROFILE`（profile）/`APPLE_API_KEY_PATH`+`APPLE_API_KEY`+`APPLE_API_ISSUER`（API key 三元组）/`APPLE_ID`+`APPLE_PASSWORD`+`APPLE_TEAM_ID`（Apple ID 三元组），三者按该顺序取第一组完整组 |
| `BundlerMacDmgCompression` | 否 | `Ulmo`；也支持 `Udzo`/`Udbz` |
| `BundlerMacDmgVolumeName` | 否 | `BundlerProductName`；挂载显示卷名 |
| `BundlerMacDmgLayoutSkip` | 否 | `false`；显式跳过 `.DS_Store` 布局（无 GUI 宿主自动降级+警告仍产可挂载 DMG） |
| `BundlerMacDmgLayoutWindowX`/`Y`/`Width`/`Height` | 否 | `200`/`120`/`660`/`400`；Finder 窗口位/尺寸 |
| `BundlerMacDmgLayoutAppIconX`/`Y` | 否 | `180`/`170`；`.app` 图标位 |
| `BundlerMacDmgLayoutApplicationsIconX`/`Y` | 否 | `480`/`170`；`/Applications` 图标位 |
| `BundlerMacDmgLayoutIconSize` | 否 | `128`；图标尺寸 |
| `BundlerMacDmgBackgroundFile` | 否 | 无；拷入卷内 `.background/` 并被 `.DS_Store` 引用 |
| `BundlerMacDmgVolumeIconFile` | 否 | 无；落盘 `.VolumeIcon.icns` + `SetFile -a C` |
| `BundlerMacDmgSignIdentity`/`BundlerMacDmgTemporaryCertificateFile`/`BundlerMacDmgTemporaryCertificatePassword` | 否 | 无；DMG 本体 `codesign`（同 app 三模式，仅 macOS 宿主） |
| `BundlerMacPkgPackageName`/`BundlerMacPkgVersion` | 否 | `BundlerIdentifier`/`BundlerVersion` |
| `BundlerMacPkgInstallRoot` | 否 | `/Applications` |
| `BundlerMacPkgTitle` | 否 | `BundlerProductName`；分发包 `<title>` |
| `BundlerMacPkgWelcomeFile`/`…ConclusionFile` | 否 | 无；分发包欢迎/结语页文件 |
| `BundlerMacPkgInstallScope` | 否 | `System`；也支持 `CurrentUserHome`（免提权装 `~/Applications`，枚举名不区分大小写） |
| `BundlerMacPkgScriptsDirectory` | 否 | 无；`pkgbuild --scripts` 专家脚本目录 |
| `@(BundlerMacPkgFile)`（`Destination`） | 否 | 无；任意文件树 → install-location 相对路径 |
| `BundlerMacPkgSignIdentity`/`…SignCertificatePath`/`…SignCertificatePassword` | 否 | 无；`pkgbuild --sign`/`productsign --sign`（Installer 证书，仅 macOS 宿主） |
| `BundlerMacPkgNotarize`/`…NotaryWait`/`…SkipStapling`/`…NotaryProfile`/`…AppleId`/`…ApplePassword`/`…AppleTeamId`/`…NotaryApiKeyFile`/`…KeyId`/`…KeyIssuer` | 否 | 同 `.app` 公证组（`.pkg` 直接过 notarytool，无 ad-hoc 签名等价物） |
| `BundlerDebPackageName` | 否 | `BundlerProductName` 的 kebab-case 化；须匹配 Debian 包名规则 `[a-z0-9][a-z0-9+.-]+` |
| `BundlerDebVersion` | 否 | 空时由 `BundlerVersion` 按 SemVer→deb 映射（预发布 `-`→`~`、`+build` 保留）；显式值须为完整 Debian 版本（可含 `epoch:`与`-revision`） |
| `BundlerDebRelease` | 否 | `1`；空值（MSBuild 传 `none` 以外的字面空不可达，直接 API 用 `""`）省略 `-revision` |
| `BundlerDebEpoch` | 否 | 无；纯数字前缀 `N:`，不进文件名 |
| `BundlerDebArchitecture` | 否 | 按目标架构映射（x86_64→`amd64`、aarch64→`arm64`）；显式值须为 Debian 架构名 |
| `BundlerDebVendor` | 否 | `BundlerPublisher` → `BundlerIdentifier` 回退 |
| `BundlerDebInstallRoot` | 否 | `/usr/lib/<包名>`；须为绝对路径且无 `..`；非 `/usr` 根下时 `usr/bin` 链接目标转绝对路径 |
| `BundlerDebBinLink` | 否 | 包名；`none`（不分大小写）关闭 `usr/bin` 链接 |
| `BundlerDebDepends`/`Recommends`/`Provides`/`Conflicts`/`Replaces` | 否 | 空；分号分隔子句、原样透传 control（`-p:` 含分号需 `%3B` 转义） |
| `BundlerDebSection`/`BundlerDebPriority` | 否 | Section 省略 / `optional`；Priority 限 `required`/`important`/`standard`/`optional`/`extra` |
| `BundlerDebCategories` | 否 | 省略；生成 `.desktop` 的 `Categories`（freedesktop 分号列表） |
| `BundlerDebDesktopFile` | 否 | 无；`.desktop` 整文件覆盖，跳过自动生成 |
| `BundlerDebMetainfoFile` | 否 | 无；AppStream metainfo → `usr/share/metainfo/<包名>.metainfo.xml` |
| `BundlerDebChangelogFile` | 否 | 无；→ `usr/share/doc/<包名>/changelog.gz`（自动 gzip；`BundlerLicenseFile` → `copyright`） |
| `@(BundlerDebFile)`（`Destination`） | 否 | 无；任意文件 → 包内绝对路径（含文件名，拒相对/`..`/空段；`/etc` 下自动登记 conffile） |
| `BundlerDebPreinstFile`/`PostinstFile`/`PrermFile`/`PostrmFile` | 否 | 无；维护者脚本整文件注入 control 归档（0755，须 shebang+LF） |
| `BundlerDebSystemdServiceFile` | 否 | 无；unit → `usr/lib/systemd/system/<包名>.service`，postinst 自动合成 `daemon-reload`（装而不启） |
| `BundlerDebConfigLocations` | 否 | 无；显式 conffile 绝对路径列表（须存在于载荷） |
| `BundlerRpmPackageName` | 否 | `BundlerProductName` 的 kebab-case 化；须匹配 rpm 包名规则 `[A-Za-z0-9][A-Za-z0-9+._-]+` 且≥2 字符 |
| `BundlerRpmVersion` | 否 | 空时由 `BundlerVersion` 按 SemVer→rpm 映射（预发布段→Release `0.<n>.<label>`、`+build`→Release 后缀）；显式值禁含 `-`、须匹配 `[0-9A-Za-z.+_]+` |
| `BundlerRpmRelease` | 否 | `1`（或 SemVer 预发布映射值）；rpm Release 字符串 |
| `BundlerRpmEpoch` | 否 | 无；非负整数写入 EPOCH tag，不进文件名 |
| `BundlerRpmArchitecture` | 否 | 按目标架构映射（x86_64→`x86_64`、aarch64→`aarch64`、i686→`i686`）；显式值须为 rpm 架构名（如 `noarch`） |
| `BundlerRpmVendor` | 否 | `BundlerPublisher` → `BundlerIdentifier` 回退 |
| `BundlerRpmInstallRoot` | 否 | `/usr/lib/<包名>`；须为绝对路径且无 `..`/空格；非 `/usr` 根下时 `usr/bin` 链接目标转绝对路径 |
| `BundlerRpmBinLink` | 否 | 包名；`none`（不分大小写）关闭 `usr/bin` 链接 |
| `BundlerRpmSigningKeyFile` | 否 | OpenPGP 私钥文件（ASCII-armored 或二进制）；供则签 `RPMSIGTAG_RSA`+`RPMSIGTAG_PGP` 双标签，不供即未签名产物 |
| `BundlerRpmSigningKeyPassphrase` | 否 | 私钥口令；只给口令不给密钥文件为配置错误，口令属秘密勿入库 |
| `BundlerAppImagePackageName` | 否 | `BundlerProductName` 的 kebab-case 化；进入文件名与根 `.desktop`/图标名（仅 Linux 宿主可构建） |
| `BundlerAppImageVersion` | 否 | `BundlerVersion` 原样；AppImage 无 EVR 规则 |
| `BundlerAppImageArchitecture` | 否 | 按目标架构映射（x86_64→`x86_64`、aarch64→`aarch64`）；仅 `x86_64`/`aarch64`/`i686`，交叉经内嵌 runtime |
| `BundlerAppImageInstallRoot` | 否 | `usr/lib/<包名>`；AppDir 相对路径，拒绝对路径/`..`/空段 |
| `BundlerAppImageBinLink` | 否 | 包名；`none` 关闭 `usr/bin` 链接（`AppRun` 直连主程序） |
| `BundlerAppImageIconFile` | 否 | PNG 拷为根 `<包名>.png`+`.DirIcon`；缺省取 hicolor 最大方图，全无则内置默认 PNG |
| `BundlerAppImageDesktopFile` | 否 | 整文件覆盖生成的 `.desktop`（须含 `Categories=`，appimagetool 硬要求） |
| `BundlerAppImageCategories` | 否 | `Utility`；分号分隔 freedesktop 分类 |
| `BundlerAppImageMetainfoFile` | 否 | AppStream metainfo → `usr/share/metainfo/<包名>.metainfo.xml` |
| `@(BundlerAppImageFile)`（`Destination`） | 否 | 无；任意文件 → AppDir 相对路径（含文件名；拒绝对路径/`..`/空段/反斜杠/生成件碰撞） |
| `BundlerAppImageSigningKeyFile` | 否 | OpenPGP 私钥文件（ASCII-armored）；供则走 `appimagetool --sign`（需宿主 `gpg`），不供即未签名产物 |
| `BundlerAppImageSigningKeyPassphrase` | 否 | 私钥口令（经 `APPIMAGETOOL_SIGN_PASSPHRASE` 注入）；只给口令不给密钥文件为配置错误，口令属秘密勿入库 |
| `BundlerArchivePackageName` | 否 | `BundlerProductName` 的 kebab-case 化；进入归档名与顶层目录 |
| `BundlerArchiveVersion` | 否 | `BundlerVersion` 原样 |
| `BundlerArchiveName` | 否 | `<包名>-<版本>-<target>`；整名覆盖，须为单一路径段 |
| `@(BundlerArchiveFile)`（`Destination`） | 否 | 无；任意文件 → 顶层目录内相对 POSIX 路径（拒绝对路径/`..`/`.`/空段/反斜杠/载荷碰撞） |
| `BundlerAlpineApkPackageName` | 否 | `BundlerProductName` 的 kebab-case 化；进 `pkgname` 与文件名 |
| `BundlerAlpineApkVersion` | 否 | 空时由 `BundlerVersion` 按 SemVer→apk 映射；显式值须为 apk 版本串 |
| `BundlerAlpineApkRelease` | 否 | `0`（SemVer 预发布映射值优先）；非负整数，`pkgver=<version>-r<release>` |
| `BundlerAlpineApkArchitecture` | 否 | 按目标映射（x86_64→`x86_64`、aarch64→`aarch64`）；仅 `linux-musl-*` 目标适用 |
| `BundlerAlpineApkLicense` | 否 | 无；SPDX 表达式写入 `license` 键 |
| `BundlerAlpineApkBuildDate` | 否 | `0`；Unix 秒，确定性构建不建议覆盖 |
| `BundlerAlpineApkDepends`/`Provides` | 否 | 无；分号列表逐行写入 `.PKGINFO` |
| `BundlerAlpineApkTriggers` | 否 | 无；绝对路径分号列表，空格串联写入 `triggers` 键 |
| `BundlerAlpineApkBinLink` | 否 | 包名；`none`（不分大小写）关闭 `usr/bin` 链接 |
| `BundlerAlpineApkPreInstallFile`/`PostInstallFile`/`PreUninstallFile`/`PostUninstallFile`/`PreUpgradeFile`/`PostUpgradeFile` | 否 | 无；脚本整文件注入控制段（0755，须非空+LF） |
| `@(BundlerAlpineApkFile)`（`Destination`） | 否 | 无；任意文件 → 绝对路径（含文件名；拒相对/`..`/`.`/尾斜杠/重复目标） |
| `BundlerAlpineApkSigningKeyFile` | 否 | PEM RSA 私钥（未加密或密码保护）；供则前置 `.SIGN.RSA.<文件名>.rsa.pub` 签名段，不供即未签名产物 |
| `BundlerAlpineApkSigningKeyPassphrase` | 否 | 私钥口令；只给口令不给密钥文件为配置错误，口令属秘密勿入库 |
| `BundlerWindowsSigningPfxFile` | 否 | PFX/P12 代码签名证书路径 |
| `BundlerWindowsSigningPfxPasswordEnvironmentVariable` | 否 | 保存 PFX 密码的环境变量名 |
| `BundlerWindowsSigningCertificateThumbprint` | 否 | Windows `My` 证书存储区中的证书指纹 |
| `BundlerWindowsSigningCertificateStoreLocation` | 否 | `CurrentUser`；也支持 `LocalMachine` |
| `BundlerWindowsSigningTimestampUrl` | 否 | RFC 3161 时间戳服务 URL |
| `BundlerWindowsSigningCommand` | 否 | 外部签名 provider 的可执行文件；与 PFX/指纹二选一 |
| `@(BundlerWindowsSigningFile)` | 否 | 输入目录内需额外签名的 DLL、sidecar 或辅助程序相对路径 |
| `@(BundlerWindowsSigningCommandArgument)` | 外部命令时必需 | 独立参数；至少一项含 `{path}` 或 `%1` |

多个格式使用分号分隔，例如 `<BundlerFormats>nsis;msi</BundlerFormats>`。
Task 会解析完整请求，再由 Core 规划需要执行的打包步骤。
MSI 单独请求由 WiX 后端处理，`.deb` 单独请求由 Deb 后端处理，`.rpm` 单独请求由 Rpm 后端处理；`deb;rpm;appimage` 组合请求已由管线扇出产出全部格式，`zip`/`targz` 与它们的任意混排同样支持，其他组合请求按已实现的编排契约验证，不能假定它会隐式复用单格式入口。

图标和额外资源通过 MSBuild Item 传入：

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*">
    <TargetPath>licenses\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
  </BundlerResource>
</ItemGroup>
```

文件关联和自定义 URL 协议属于与打包格式无关的应用元数据：

```xml
<ItemGroup>
  <BundlerFileAssociation Include="mydoc"
                          Name="我的应用文档"
                          Description="我的应用文档文件"
                          MimeType="application/x-myapp-document" />
  <BundlerUrlProtocol Include="myapp" Name="我的应用链接" />
</ItemGroup>
```

扩展名可以带开头的点，每个 Item 表示一项关联，匹配时不区分大小写。
NSIS 会注册应用专属 ProgID、“打开方式”候选项和 Windows 默认应用 Capabilities，但不会覆盖用户当前选择的默认程序。
`MimeType` 是供需要 MIME 类型的后端使用的通用元数据；Windows NSIS 不会把它写入共享的扩展名注册表项。
自定义 URL 协议安装后可直接通过 `myapp:...` 启动；卸载时只有协议命令仍指向本次安装目录才会删除，因此不会破坏后来接管该协议的程序。

对 NSIS 而言，第一个 `.ico` 文件是安装器和卸载器的回退图标，也可以分别用 NSIS 专属属性覆盖。
Header 图片建议为 150×57 BMP，Sidebar 图片建议为 164×314 BMP。
`BundlerLicenseFile` 会增加许可证页面。
描述、主页、版权、产品版本和文件版本会写入相应的可执行文件或“应用和功能”元数据。
每个资源安装到相对的 `TargetPath`，省略时使用源文件名。
目标路径冲突或逃逸安装目录会在打包前报错。

## 通用打包流程与 NSIS 定制

所有格式共用同一条管线：校验配置、生成包含格式依赖关系的计划、创建隔离工作目录、调用后端、确认产物存在、清理工作目录。
全部格式后端（NSIS/Wix/MacApp/MacDmg/MacPkg/Deb/Rpm/AppImage/Archive/AlpineApk）复用同一管线；后续新增格式按同样方式扩展后端和入口映射，不复制整套调度代码。

产物确定性边界：`.deb`/`.rpm`/`.zip`/`.tar.gz`/`.apk` 为纯托管写入器，相同输入与条件下产物逐字节确定（联合测试实测：同宿主跨轮与 linux↔qemu 同型跨宿主一致；`.apk` 含可选 RSA 签名段亦逐字节确定——RSA PKCS#1 v1.5 为确定性签名）；`.nsis` 与 `.AppImage` 不承诺逐字节一致——makensis 将源文件时间戳编入输出、appimagetool 的 squashfs 元数据与内嵌 runtime 摘要随构建环境变化，属上游工具性质而非写入器缺陷。

可编辑的 NSIS 源模板存放在 `src/Bundler.Nsis/templates/installer.nsi`。
发布包时，该模板、语言文件和完整的多宿主 NsisToolset 压缩包会嵌入 `DotNet.Bundler.Nsis`，因此独立 API 和 MSBuild 使用者得到完全相同的资源，也不会把工具复制到项目输出目录。
安装器语言按 Tauri 的能力范围配置：`BundlerNsisLanguages` 是分号分隔的语言列表，第一项是系统语言不匹配时的回退语言；
只有启用多个语言并把 `BundlerNsisDisplayLanguageSelector` 设为 `true` 时才显示语言选择器。
包内提供 Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian 和 Vietnamese。
对外使用 `Persian`，内部会映射到 NSIS 的 `Farsi` 标识。

```xml
<PropertyGroup>
  <BundlerNsisCompression>zlib</BundlerNsisCompression>
  <BundlerNsisLanguages>English;SimpChinese;Japanese</BundlerNsisLanguages>
  <BundlerNsisDisplayLanguageSelector>true</BundlerNsisDisplayLanguageSelector>
</PropertyGroup>
```

`BundlerNsisLanguageFile` 可以完整替换某个已选择语言的内置文案。
同一语言只能有一个自定义文件；文件必须恰好包含模板要求的全部 `LangString`，缺失、重复、未知键或错误的 `LANG_*` 常量都会在打包时失败：

```xml
<ItemGroup>
  <BundlerNsisLanguageFile Include="installer-languages\German.nsh" Language="German" />
</ItemGroup>
```

默认模板提供当前用户安装、可选择并记住上次位置的安装目录、运行程序检测和关闭（Restart Manager，不误关同名进程）、可选快捷方式与删除应用数据、静默卸载与完成页启动。
安装事务 journal 与失败恢复、卸载前向恢复、版本/升级/降级策略、锁定载荷安全失败、安装器命令行协议与退出码的全部语义契约见 [`docs/formats/win-nsis.md`](docs/formats/win-nsis.md)（§4.1–4.13）；
真实重启等外部验收项见 [`docs/special-acceptance.md`](docs/special-acceptance.md)。

如果产品以前使用 MSI 发布，应配置历史安装包的准确标识，不按产品名猜测：

```xml
<PropertyGroup>
  <BundlerNsisLegacyMsiProductCodes>{PRODUCT-CODE-GUID}</BundlerNsisLegacyMsiProductCodes>
  <BundlerNsisLegacyMsiUpgradeCodes>{UPGRADE-CODE-GUID}</BundlerNsisLegacyMsiUpgradeCodes>
</PropertyGroup>
```

两个属性都支持填写多个以分号分隔的 GUID。
ProductCode 精确表示一个 MSI 产品；UpgradeCode 会在安装器运行时查找所有已安装的关联产品。
包内原生插件通过 Windows Installer API 精确查询。
MSI 迁移到 NSIS 时必须先卸载所有匹配的 MSI，再安装 NSIS 载荷；Windows Installer 的“成功但需要重启”返回码会被正确处理，降级仍遵循统一策略。
这里不会采用产品名和发布者匹配，因为可能误删无关软件。
真实迁移标识必须从历史 MSI 中取得，仓库内测试夹具不能代替生产标识验证。

`BundlerNsisInstallScope` 控制 Windows 安装范围。
`currentUser` 不提权，卸载信息和快捷方式写入当前用户上下文；
`perMachine` 请求管理员权限，安装到 Program Files，并使用所有用户 Shell 上下文和 HKLM 注册表；
`both` 使用 NSIS 自带的 MultiUser 页面让用户选择。
由于安装器必须具备切换到计算机范围的能力，`both` 启动时会请求最高可用权限。
x64 和 arm64 包使用 64 位注册表视图。

可选的 `BundlerNsisInstallerHooksFile` 文件可以把 `NSIS_HOOK_PREINSTALL`、`NSIS_HOOK_POSTINSTALL`、`NSIS_HOOK_PREUNINSTALL`、`NSIS_HOOK_POSTUNINSTALL` 中任意几项定义为 NSIS 宏，安装器会在相应生命周期边界调用。
Hook 使用安装器当前权限执行。
安装 Hook 可以用 `SetErrors` 或 `Abort` 报告失败，安装器会回滚已激活的事务；直接使用 `Quit` 或终止进程无法继续执行即时回滚，但 active journal 会由下一次安装启动恢复。
卸载 Hook 用 `SetErrors` 报告失败时会保留前向恢复 journal；进程被终止时同样由下一次安装启动继续。
由于恢复可能再次执行尚未完成的卸载阶段，卸载 Hook 必须按幂等方式编写。

Windows Authenticode 签名由独立的 `DotNet.Bundler.Signing.Windows` 包实现。
启用签名后，Bundler 把输入复制到临时工作区，依次签主 EXE、显式列入 `BundlerWindowsSigningFile` 的 DLL/sidecar、Bundler 自带 NSIS 插件、导出的卸载器和最终安装器；不会修改原始发布目录，也不会擅自重签其余第三方文件。
任一步失败都会删除最终安装器。
内置 PFX 和证书存储区指纹只能二选一；PFX 密码只通过环境变量读取，不应写进项目文件或命令行。
生产发布强烈建议设置可信的 RFC 3161 时间戳服务，否则证书过期后签名无法继续证明签署时证书有效。

```xml
<PropertyGroup>
  <BundlerWindowsSigningPfxFile>$(SigningCertificatePath)</BundlerWindowsSigningPfxFile>
  <BundlerWindowsSigningPfxPasswordEnvironmentVariable>BUNDLER_SIGNING_PASSWORD</BundlerWindowsSigningPfxPasswordEnvironmentVariable>
  <BundlerWindowsSigningTimestampUrl>https://你的时间戳服务</BundlerWindowsSigningTimestampUrl>
</PropertyGroup>

<ItemGroup>
  <BundlerWindowsSigningFile Include="tools\Updater.exe" />
</ItemGroup>
```

云 HSM、USB Token 或远程签名可改用外部 provider。
每个参数使用独立 Item，支持 `{path}`/`%1`、`{artifactKind}`、`{target}`、`{productName}` 占位符；
不要把令牌或密码写入参数，provider 应从环境变量、系统身份或自己的安全存储读取。
provider 输出和参数不会被写入普通错误消息。

```xml
<PropertyGroup>
  <BundlerWindowsSigningCommand>trusted-signing-client.exe</BundlerWindowsSigningCommand>
</PropertyGroup>
<ItemGroup>
  <BundlerWindowsSigningCommandArgument Include="sign" />
  <BundlerWindowsSigningCommandArgument Include="--file" />
  <BundlerWindowsSigningCommandArgument Include="{path}" />
  <BundlerWindowsSigningCommandArgument Include="--kind" />
  <BundlerWindowsSigningCommandArgument Include="{artifactKind}" />
</ItemGroup>
```

### 本地自签名测试

下面的命令会在当前用户的 `My` 证书存储区创建一个有效期一天的一次性代码签名证书，用它构建 `HelloBundlerApp` 项目（安装后的应用叫 Hello Bundler App），并检查主程序、安装器和安装后的卸载器。
它只用于验证签名流程；自签名证书没有受信任 CA 的证书链，因此 `Get-AuthenticodeSignature` 通常会报告 `UnknownError` 或“不受信任的根证书”，也不会让真实用户看到可信发布者。

```powershell
# 在仓库根目录创建一次性测试证书。
$certificate = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=Hello Bundler App Test Publisher" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter ([DateTime]::Now.AddDays(1))
$thumbprint = $certificate.Thumbprint

dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r win-x64 `
  -p:BundlerFormats=nsis `
  -p:HelloBundlerSigningCertificateThumbprint=$thumbprint

$installer = Resolve-Path `
  "samples/HelloBundlerApp/artifacts/Hello Bundler App-1.0.0-x64-setup.exe"
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
$installerSignature | Select-Object Status, StatusMessage
$installerSignature.SignerCertificate | Select-Object Subject, Thumbprint

if ($installerSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "安装器没有使用预期证书签名。"
}
```

运行安装器并完成安装后，继续验证卸载器；如果安装时修改了目录，请替换下面的路径：

```powershell
$mainExecutable = "$env:LOCALAPPDATA\Programs\Hello Bundler App\HelloBundlerApp.exe"
$mainSignature = Get-AuthenticodeSignature -LiteralPath $mainExecutable
if ($mainSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "主程序没有使用预期证书签名。"
}

$uninstaller = "$env:LOCALAPPDATA\Programs\Hello Bundler App\Uninstall.exe"
$uninstallerSignature = Get-AuthenticodeSignature -LiteralPath $uninstaller
if ($uninstallerSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "卸载器没有使用预期证书签名。"
}

# 验收完成后删除一次性测试证书。
Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -Force
```

若要观察 UAC 发布者页面，可在构建时同时传入 `-p:HelloBundlerNsisInstallScope=perMachine`。
自签名证书仍会显示为未知或不受信任的发布者；正式发布必须换成受信任 CA 签发的代码签名证书，并配置 RFC 3161 时间戳。

内置签名器直接调用 Windows 的 Authenticode API，因此启用它时构建宿主必须是 Windows；外部命令 provider 和自定义 `IBundleSigner` 可在 provider 支持的其他宿主运行。
不启用签名时，Linux 和 macOS 上的 NSIS 构建不受影响。
独立 API 使用者可设置每个 `BundleTargetConfiguration.SigningFiles`，并为 `NsisBundlerOptions.Signer` 使用内置或自定义实现。

若要定制，可把模板复制出来，并将 `BundlerNsisTemplate` 设为其绝对路径。
自定义模板必须保留签名两阶段编译所需的 `uninstaller_finalize_command`、`uninstaller_import_define` 和 `signed_uninstaller` 占位符。
使用配置的压缩方式时应保留 `compression_directive`。
其他变量包括 `product_name`、`version`、`numeric_version`、`publisher`、`identifier`、`main_executable`、`process_name`、`install_folder`、`install_mode`、`target_architecture`、`allow_downgrades`、`legacy_msi_product_codes`、`legacy_msi_upgrade_codes`、`input_glob`、`output_file`、`estimated_size`、`plugin_directory`、`uninstall_payload`、`language_macros`、`language_files`、`display_language_selector`，写法为 `{{name}}`。

这是面向 Windows 的可用基线，并不等于 Tauri 功能对等。

## 应用自更新（UPDATE）

打包产物可选接入自更新模块（决策、契约与能力清单见 `docs/update.md`）。
启用后打包额外产出：`bundler-update-feed.{channel}.json` 静态清单、每制品的 `.sig`（ECDSA P-256）与 `.blockmap` 差分块表，并在安装载荷内烙 `bundler-update.json` 身份旁车。

| MSBuild 属性 | 用途 |
| --- | --- |
| `BundlerUpdateEnabled` | 开总闸（默认关） |
| `BundlerUpdateFeedUrl` | 清单发布位置——http(s)/file:// 本地路径/UNC 均可；制品按清单同目录解析 |
| `BundlerUpdateChannel` | 通道名（默认 `latest`），进清单文件名 |
| `BundlerUpdateSigningKeyFile` | PKCS#8 私钥路径（签名强制；私钥不入库） |
| `BundlerUpdatePublicKey` | 烙进身份的验签公钥（base64） |
| `BundlerUpdateNotes` | 版本说明写入清单 `notes` |

签名密钥用 CLI 生成（BCL ECDSA，无外部依赖）：

```powershell
bundler update-keygen --key-file <path>   # 产私钥文件并打印公钥
```

应用侧经 `DotNet.Bundler.Updater` 库接入（netstandard2.0+net10.0、零依赖）：

```csharp
var client = UpdateClient.FromInstallDirectory(appDir, currentVersion);
var info = await client.CheckForUpdateAsync();
if (info is not null)
{
    var path = await client.DownloadAsync(info, downloadDir);   // block-map 差分优先，回退全量
    client.Verify(info, path);
    var process = client.Apply(info, path);                     // 引导件换包+重启
}
```

自更新格式：`nsis`/`msi` 重跑安装器，`.app`/`zip`/`targz`/`appimage` 走三段式换包；`deb`/`rpm`/`apk`/`dmg`/`pkg` 按权威实践不做自更新。

## 仓库命令

```powershell
dotnet build Bundler.slnx
dotnet test tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet test tests/Bundler.ApiTests/Bundler.ApiTests.csproj -c Release
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r win-x64
# 只产某一格式：-p:BundlerFormats=nsis 或 =msi（msi 需 Windows 宿主）
# 集成测试按格式类选择（`--` 后接 `--filter-class` 单名过滤，各宿主只跑宿主可用腿）
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class "*NsisIntegrationTests*"
dotnet test tests/Bundler.LocalPackagesTests/Bundler.LocalPackagesTests.csproj -c Release -- --filter-class "*MsiLocalPackagesTests*"
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class "*CliIntegrationTests*"
```

Linux/macOS 宿主对应的真实集成按同规则选类（`DebIntegrationTests`/`RpmIntegrationTests`/`AppImageIntegrationTests`/`ArchiveIntegrationTests`/`AlpineApkIntegrationTests`/`MacAppIntegrationTests`/`MacDmgIntegrationTests`/`MacPkgIntegrationTests`）；
真装腿另需同意闸与资源 trait 过滤，完整选择方式见 [`tests/README.md`](tests/README.md)。

只能人工/外部付费完成的验收统一登记在 [`docs/special-acceptance.md`](docs/special-acceptance.md)。
仓库可自动化的测试仍由上述命令执行，不转为人工清单。

## 安全与许可证

NsisToolset 发布包的校验值已经固定在源码中。
NuGet 包会同时携带第三方声明和 NSIS 上游许可证。
本仓库采用 MIT 许可证（见根目录 `LICENSE`），NuGet 包同步携带 `MIT` license expression 与许可证文件。

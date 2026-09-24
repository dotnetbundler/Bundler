# DotNet.Bundler

[English](README.md)

`DotNet.Bundler` 是一个通用桌面应用打包工具。当前首先发布 NuGet/MSBuild 集成：使用者引用包并配置少量 MSBuild 属性后，可以把 `dotnet publish` 产物转换为桌面安装程序。Core 和格式后端本身不依赖 `.csproj` 或 .NET 应用模型，后续正式 CLI 将复用同一套 API，为其他语言和构建系统提供入口。

## 当前状态

第一条已冻结的链路是 Windows + NSIS。MSI 已实现 WIN-MSI-1/2 的本机范围，尚未完成格式冻结；正式 CLI、macOS 和 Linux 格式仍属后续路线。

实现已经拆分为可复用的 NuGet 包。`DotNet.Bundler` 只是便利元包，实际打包代码位于以下各层。

## 包结构

| 包 | 职责 |
| --- | --- |
| `DotNet.Bundler.Abstractions` | 公共请求、目标、结果、日志契约和后端接口 |
| `DotNet.Bundler.Core` | 验证、规划、编排、工作目录和共享的内容寻址 ZIP 工具缓存 |
| `DotNet.Bundler.Nsis` | 独立 NSIS API、NSIS 配置、脚本生成、语言和内置 `makensis` 工具链 |
| `DotNet.Bundler.Wix` | 独立 MSI API、WiX 3.14.1 工具子集和声明式 Windows Installer 数据库生成 |
| `DotNet.Bundler.Signing.Windows` | 可复用的 Windows Authenticode 签名实现 |
| `DotNet.Bundler.MSBuild` | MSBuild 参数转换与后端 API 调用；不包含 NSIS 实现 |
| `DotNet.Bundler` | 空的便利元包，引入 `DotNet.Bundler.MSBuild` 且不屏蔽其传递性构建资产 |

仓库内已有 `DotNet.Bundler.Cli` 原型，但它的参数覆盖和发布契约尚未完成，因此当前不作为受支持入口。后续路线和产品边界见 [`docs/roadmap.md`](docs/roadmap.md)。WiX 3.14.1 MSI 后端已完成 `WIN-MSI-1` 和 `WIN-MSI-2` 的本机范围验证：最小 current-user 安装/卸载、两版本升级与降级/同版本异包拒绝、快捷方式及关联/协议候选注册。per-machine 包仅生成并检查数据库，提权安装及干净 Windows/ARM64 宿主尚未验收；签名等后续阶段未开始，**MSI 格式尚未冻结**。最小示例见 [`samples/HelloMsiApp/README.md`](samples/HelloMsiApp/README.md)，实施状态见 [`docs/msi-roadmap.md`](docs/msi-roadmap.md)。

`DotNet.Bundler.Wix` 是可独立引用的 MSI 后端包和直接 API；MSBuild 调用同一后端。跨格式的开发与包消费规则见 [`docs/development-rules.md`](docs/development-rules.md)。

`DotNet.Bundler.Nsis` 完整嵌入固定版本的 [`NsisToolset` 3.12-r1](https://github.com/dotnetbundler/NsisToolset/releases/tag/v3.12-r1)，其上游 NSIS 版本为 3.12。工具集包含一份公共 NSIS 数据目录，以及 Windows、Linux x64/arm64、macOS x64/arm64 的宿主编译器。使用者无需安装 NSIS，也不需要在线下载工具。每次解析都会先校验压缩包 SHA-256，再以压缩包内逐文件哈希清单验证共享缓存；缺失、篡改、额外文件、manifest 损坏或重解析点都会在跨进程锁内触发安全重建。缓存支持 Unicode 和长路径，同一台机器上的项目复用按内容寻址的工具目录。

MSBuild Task 及其直接加载的 Abstractions/Core/NSIS/WiX 程序集都提供 `netstandard2.0` 资产。打包决策和后端调度直接在 MSBuild 进程内完成，包不会再启动额外的 .NET CLI 驱动。NSIS 编译仍会启动包内与当前宿主匹配的原生 `makensis`，因为它本身就是安装程序编译器；MSI 编译使用包内 WiX 工具，当前仅开放 Windows 构建宿主。

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
    <PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.34" PrivateAssets="all" />
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
<PackageReference Include="DotNet.Bundler.Nsis" Version="0.1.0-alpha.34" />
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

`NsisBundleConfiguration` 控制 NSIS 专属行为，包括安装范围、压缩、Artwork、语言、Hook、降级和快捷方式。`Compression` 支持 `Lzma`（默认）、`Zlib`、`Bzip2` 和 `None`。高级调用和测试场景可以通过 `NsisBundlerOptions` 覆盖共享缓存、编译器、工具集压缩包、NSIS 数据目录、模板或语言目录；普通调用者不需要提供这些路径。提供自定义编译器时，如果编译器需要显式的 `NSISDIR`，还应设置 `DataDirectory`。

独立 API 通过 `NsisBundleConfiguration.Shortcuts` 设置 `NsisShortcutConfiguration`。其中的 `Desktop`、`StartMenu`、`Arguments`、`WorkingDirectory`、`Icon`、`AppUserModelId` 和 `StartMenuFolder` 与下方 MSBuild 属性对应；`LegacyProductNames` 和 `LegacyMainExecutables` 用于在改名后显式启用安全迁移。

包内嵌了使用 [`dotnetbundler/NsisPlugin`](https://github.com/dotnetbundler/NsisPlugin) 构建的 `win-x86` Native AOT 插件。它提供 SemVer 比较、MSI 查询、降权启动和基于 Shell COM 的快捷方式操作，不要求使用者安装额外 SDK 或打包工具。

## 独立 MSI API

普通 .NET 项目可以只引用 MSI 后端包，直接打包已准备好的目录，无需引用 MSBuild 便利元包或本仓库源码：

```xml
<PackageReference Include="DotNet.Bundler.Wix" Version="0.1.0-alpha.34" />
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
            RuntimeIdentifier = "win-x64",
            InputDirectory = "publish/win-x64",
            MainExecutable = "MyApp.exe",
            Formats = [PackageFormat.Msi]
        }
    ]
};

var artifacts = await new WixBundler().BuildAsync(request);
```

`WixBundleConfiguration` 配置 MSI 安装范围、代码页和快捷方式；`WixBundlerOptions` 可为测试覆盖工具缓存或工具归档路径。WiX 3.14.1 工具随包提供，当前 MSI 构建要求 Windows 宿主。当前开发版本尚未公开到默认 NuGet 源时，先从本仓库 pack 并指定本地包源。上述包引用与实际编译由复制到仓库外目录的 `tests/Msi.Api.PackageFixture` 自动验证；MSI 产品身份和安装行为以 MSI 专用文档为准。

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
| `BundlerNsisCompression` | 否 | `lzma`；也支持 `zlib`、`bzip2` 和 `none` |
| `BundlerNsisInstallerIcon` | 否 | 第一个 `BundlerIcon` `.ico` |
| `BundlerNsisUninstallerIcon` | 否 | 安装器图标 |
| `BundlerNsisHeaderImage` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisSidebarImage` | 否 | NSIS 默认图片；`.bmp` |
| `BundlerNsisUninstallerHeaderImage` | 否 | 安装器 Header 图片；`.bmp` |
| `BundlerNsisInstallerHooks` | 否 | 可选的 `.nsh` 生命周期宏文件 |
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
| `BundlerWindowsSigningPfxFile` | 否 | PFX/P12 代码签名证书路径 |
| `BundlerWindowsSigningPfxPasswordEnvironmentVariable` | 否 | 保存 PFX 密码的环境变量名 |
| `BundlerWindowsSigningCertificateThumbprint` | 否 | Windows `My` 证书存储区中的证书指纹 |
| `BundlerWindowsSigningCertificateStoreLocation` | 否 | `CurrentUser`；也支持 `LocalMachine` |
| `BundlerWindowsSigningTimestampUrl` | 否 | RFC 3161 时间戳服务 URL |
| `BundlerWindowsSigningCommand` | 否 | 外部签名 provider 的可执行文件；与 PFX/指纹二选一 |
| `@(BundlerWindowsSigningFile)` | 否 | 输入目录内需额外签名的 DLL、sidecar 或辅助程序相对路径 |
| `@(BundlerWindowsSigningCommandArgument)` | 外部命令时必需 | 独立参数；至少一项含 `{path}` 或 `%1` |

多个格式使用分号分隔，例如 `<BundlerFormats>nsis;msi</BundlerFormats>`。Task 会解析完整请求，再由 Core 规划需要执行的打包步骤。MSI 单独请求由 WiX 后端处理；当前组合请求仍按已实现的编排契约验证，不能假定它会隐式复用单格式入口。

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

扩展名可以带开头的点，每个 Item 表示一项关联，匹配时不区分大小写。NSIS 会注册应用专属 ProgID、“打开方式”候选项和 Windows 默认应用 Capabilities，但不会覆盖用户当前选择的默认程序。`MimeType` 是供需要 MIME 类型的后端使用的通用元数据；Windows NSIS 不会把它写入共享的扩展名注册表项。自定义 URL 协议安装后可直接通过 `myapp:...` 启动；卸载时只有协议命令仍指向本次安装目录才会删除，因此不会破坏后来接管该协议的程序。

对 NSIS 而言，第一个 `.ico` 文件是安装器和卸载器的回退图标，也可以分别用 NSIS 专属属性覆盖。Header 图片建议为 150×57 BMP，Sidebar 图片建议为 164×314 BMP。`BundlerLicenseFile` 会增加许可证页面。描述、主页、版权、产品版本和文件版本会写入相应的可执行文件或“应用和功能”元数据。每个资源安装到相对的 `TargetPath`，省略时使用源文件名。目标路径冲突或逃逸安装目录会在打包前报错。

## 通用打包流程与 NSIS 定制

所有格式共用同一条管线：校验配置、生成包含格式依赖关系的计划、创建隔离工作目录、调用后端、确认产物存在、清理工作目录。现有 NSIS 和 MSI 后端复用该管线；增加 macOS 或 Linux 格式时继续扩展后端和入口映射，不复制整套调度代码。

可编辑的 NSIS 源模板存放在 `templates/nsis/installer.nsi`。发布包时，该模板、语言文件和完整的多宿主 NsisToolset 压缩包会嵌入 `DotNet.Bundler.Nsis`，因此独立 API 和 MSBuild 使用者得到完全相同的资源，也不会把工具复制到项目输出目录。安装器语言按 Tauri 的能力范围配置：`BundlerNsisLanguages` 是分号分隔的语言列表，第一项是系统语言不匹配时的回退语言；只有启用多个语言并把 `BundlerNsisDisplayLanguageSelector` 设为 `true` 时才显示语言选择器。包内提供 Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian 和 Vietnamese。对外使用 `Persian`，内部会映射到 NSIS 的 `Farsi` 标识。

```xml
<PropertyGroup>
  <BundlerNsisCompression>zlib</BundlerNsisCompression>
  <BundlerNsisLanguages>English;SimpChinese;Japanese</BundlerNsisLanguages>
  <BundlerNsisDisplayLanguageSelector>true</BundlerNsisDisplayLanguageSelector>
</PropertyGroup>
```

`BundlerNsisLanguageFile` 可以完整替换某个已选择语言的内置文案。同一语言只能有一个自定义文件；文件必须恰好包含模板要求的全部 `LangString`，缺失、重复、未知键或错误的 `LANG_*` 常量都会在打包时失败：

```xml
<ItemGroup>
  <BundlerNsisLanguageFile Include="installer-languages\German.nsh" Language="German" />
</ItemGroup>
```

默认模板提供当前用户安装、可选择并记住上次位置的安装目录、非本应用的非空目录警告、DPI 感知、压缩、可选的开始菜单与桌面快捷方式、运行程序检测和关闭、“应用和功能”卸载信息、可选删除应用数据、静默卸载以及完成页启动程序。运行程序检测通过 Windows Restart Manager 注册安装目录中的当前主程序和显式声明的旧主程序完整路径，不再按映像文件名全局结束进程，因此不会关闭其他目录下的同名程序。快捷方式的默认选择、参数、工作目录、图标、稳定 AppUserModelID 和开始菜单目录均可配置；工作目录和图标必须存在于最终安装载荷中。更新只刷新仍存在且仍指向当前主程序或显式声明旧主程序的快捷方式；卸载采用相同所有权检查，因此同名快捷方式被其他程序接管后会保留。旧产品名和旧主程序名用于显式迁移改名版本。任务栏/开始菜单自动取消固定受 Windows 版本行为影响，目前只列为人工验收项，不宣称保证支持。未选择删除应用数据时，卸载只删除程序目录中属于构建载荷的路径：程序后来在新路径创建的文件会保留；如果创建或覆盖的是构建载荷中的同名路径，卸载时仍会删除。选择删除应用数据时，会递归删除整个程序安装目录，以及 `%APPDATA%\<identifier>` 和 `%LOCALAPPDATA%\<identifier>`。

NSIS 安装和升级在修改持久状态前会把原安装目录、产品相关注册表项以及受管理的快捷方式写入用户或计算机范围的事务 journal。复制、Hook、注册或快捷方式阶段失败时会立即恢复旧状态；如果安装器进程被直接终止，下一次启动同一产品的安装器会先恢复未提交事务。journal 与当时选择的安装目录绑定，恢复时目录不一致会安全失败；首次自定义 `/D` 安装若在写入安装记录前中断，重试时应继续传入同一 `/D`。事务提交先把 journal 原子重命名为 `.committed`，再尽力删除快照；提交后的清理失败不会错误回滚已经完成的安装，而由下一次启动重试清理。构建输入、资源、安装快照、恢复树、journal 和工具缓存统一采用“不跟随链接”的策略：遇到 symlink、junction 或其他重解析点即在越界读写前失败，清理链接时只删除链接本身。快照会临时占用接近现有安装目录大小的额外磁盘空间。普通文件的内容、基础属性和时间戳会进入安装快照，但不承诺逐项保真恢复自定义 ACL、ADS、稀疏文件等任意文件系统元数据；需要这些语义的应用应把它们当作产品专属迁移，而不是依赖通用打包器猜测。旧 MSI 卸载是外部且不可逆的迁移边界；没有原 MSI 包时无法自动恢复，因此只保证迁移后新 NSIS 状态失败时会被清理，不宣称能重新安装已移除的 MSI。

卸载采用独立的前向恢复 journal，而不是回滚事务。开始删除前会保存安装目录、删除应用数据选择和一份恢复卸载器；进入 active 后若 Hook 失败或进程中断，已完成的删除不会被伪装成可撤销，下次启动同一产品安装器时会先校验注册表中的安装目录，再用 journal 副本幂等完成剩余删除。卸载注册项保留到 finalizing 阶段作为受保护的路径锚点，完成后才删除并原子提交 journal。卸载 Hook 因此必须可重复执行；选择 `/DELETEAPPDATA` 和 `/REBOOTOK` 进入系统待删除队列的意图都会跨恢复保留。

版本必须符合 SemVer 2.0，并且三个数字核心段都必须处于 Windows 版本资源允许的 `0-65535` 范围。安装器会在所选用户或计算机注册表上下文中检测现有安装，并通过包内使用 `NsisPlugin` 构建的 Native AOT 插件比较 `DisplayVersion`。交互安装允许用户选择先卸载或原位覆盖；静默同版本安装执行原位修复，静默升级会先卸载旧的构建载荷，同时保留应用数据。默认禁止降级，可通过 `BundlerNsisAllowDowngrades` 开启。

仓库的 Windows 集成矩阵会在事务快照、事务激活、载荷恢复、注册表恢复和 active journal 清理检查点注入一次性可控故障，验证未修改旧状态的安全失败以及下次启动的重入恢复。这些是确定性测试检查点，不代替真实 ACL 拒绝、磁盘耗尽或断电/重启环境验收。

跨版本配置变化后，恢复目标必须与创建 active journal 的原安装器清单一致。新版安装器遇到不同的关联、协议或快捷方式清单时会保留 journal 并返回 `6`。若原安装器支持 `/RECOVERONLY`，用创建 journal 的原安装器执行 `"<原安装器.exe>" /S /RECOVERONLY /D=<原安装目录>`；该模式只恢复旧状态，退出码为 `0` 后再运行新版安装器。更早、不支持该开关的原安装器仍可按原安装范围和目录重试，由它先自动恢复再继续自身安装；不能将其当作“仅恢复”模式。若原安装器不可用或自身安装也无法完成，必须保留 journal 并取得可信原包或人工排障，不能删除 journal 或放弃清单校验强行继续。安装范围必须与原安装器一致。

恢复前会核对安装快照摘要，卸载前向恢复副本会与安装时登记在卸载注册项中的卸载器哈希比对。摘要和哈希用于发现静态篡改或损坏；`currentUser` 用户同时可修改自己的 journal 与 HKCU 锚点，不构成抵御同一用户恶意篡改的安全边界。提权安装的实际边界还取决于 ProgramData journal 和 HKLM 的 ACL，见人工验收文档。

### 安装器命令行协议

这些参数属于生成后的 NSIS 安装器接口，不是 MSBuild 属性：

| 参数 | 行为 |
| --- | --- |
| `/S` | NSIS 原生静默模式，不显示窗口 |
| `/P` | 被动安装或卸载，只显示进度并跳过需要输入的页面 |
| `/UPDATE` | 自动更新模式；未同时指定 `/S` 时隐含 `/P`，原位覆盖并保留现有快捷方式状态和应用数据 |
| `/NS` | 不创建桌面和开始菜单快捷方式 |
| `/RECOVERONLY` | 只用当前安装器清单恢复 active 安装 journal，不安装新版本；应与 `/S` 和原安装目录的 `/D=` 一起使用 |
| `/R` | 成功后以桌面用户而非安装器管理员令牌启动应用；只允许与 `/S`、`/P` 或 `/UPDATE` 一起使用 |
| `/ARGS=<参数行>` | 与 `/R` 配合，把参数直接传给应用，不经过 `cmd.exe` 或 PowerShell |
| `/D=<目录>` | NSIS 原生安装目录参数，必须是整条命令的最后一个参数 |

`/ARGS` 也兼容不带等号的写法，此时它后面的全部文本都会成为应用参数。需要同时使用 `/D` 时应使用 `/ARGS=<参数行>`，并仍把 `/D` 放在最后。自动模式只接受空目录或带当前产品安装标记的目录；它不会用无交互方式确认覆盖无关的非空目录。

安装器稳定退出码为：`0` 成功、`1` 用户取消、`2` 一般失败或快照内容完整性失败、`3` 参数或自动安装目录无效、`4` 版本策略阻止、`5` 无法关闭正在运行的应用、`6` active journal 与当前安装器恢复清单不同、`3010` 成功但需要重新启动 Windows。安装成功且重启标志已经置位时，安装事务会先提交并清理 journal，再返回 `3010`；即使指定 `/R`，也不会在重启前启动应用。实际卸载进程也把一般失败映射为 `2`、把 `/REBOOTOK` 已接受的删除映射为 `3010`。但直接启动安装目录中的 NSIS `Uninstall.exe` 会先经过自复制 launcher，外层进程不可靠地传播实际退出码；仓库的恢复与验收脚本会先复制卸载器并以 `_?=` 直接模式同步等待真实进程。不要把外层 launcher 的 `0` 当成卸载已完成的证据，应同时检查产品状态或使用受控的直接模式。

当前普通安装载荷仍由 NSIS `File` 指令直接写入目标目录，它不会把无法覆盖的锁定文件自动转换成重启后替换。这是当前的明确安全策略：Windows 延迟替换需要管理员上下文，不能为 `currentUser` 安装提供一致保证；排入共享系统队列也只能证明请求被接受，不能证明重启时一定成功，更不能安全纳入当前回滚。安装器会检测这种跳过并返回 `2`；交互模式会提示关闭可能占用安装目录文件的应用后重试。若文件锁也阻止即时回滚，则保留 active journal，待释放锁后的下一次启动先恢复旧状态，绝不把新旧文件混合状态报告为成功。因此上述契约不能扩写为“已经支持锁定文件原位升级”：当前可验证的重启来源是旧 MSI 返回值、生命周期 Hook，以及提权卸载的 `/REBOOTOK` 删除。真实系统队列验收必须在可丢弃并允许重启的管理员 Windows 环境执行 `tests/Windows.Nsis.Reboot/Verify.ps1`；脚本不会编辑或清空共享的 `PendingFileRenameOperations`。

如果产品以前使用 MSI 发布，应配置历史安装包的准确标识，不按产品名猜测：

```xml
<PropertyGroup>
  <BundlerNsisLegacyMsiProductCodes>{PRODUCT-CODE-GUID}</BundlerNsisLegacyMsiProductCodes>
  <BundlerNsisLegacyMsiUpgradeCodes>{UPGRADE-CODE-GUID}</BundlerNsisLegacyMsiUpgradeCodes>
</PropertyGroup>
```

两个属性都支持填写多个以分号分隔的 GUID。ProductCode 精确表示一个 MSI 产品；UpgradeCode 会在安装器运行时查找所有已安装的关联产品。包内原生插件通过 Windows Installer API 精确查询。MSI 迁移到 NSIS 时必须先卸载所有匹配的 MSI，再安装 NSIS 载荷；Windows Installer 的“成功但需要重启”返回码会被正确处理，降级仍遵循统一策略。这里不会采用产品名和发布者匹配，因为可能误删无关软件。真实迁移标识必须从历史 MSI 中取得，仓库内测试夹具不能代替生产标识验证。

`BundlerNsisInstallMode` 控制 Windows 安装范围。`currentUser` 不提权，卸载信息和快捷方式写入当前用户上下文；`perMachine` 请求管理员权限，安装到 Program Files，并使用所有用户 Shell 上下文和 HKLM 注册表；`both` 使用 NSIS 自带的 MultiUser 页面让用户选择。由于安装器必须具备切换到计算机范围的能力，`both` 启动时会请求最高可用权限。x64 和 arm64 包使用 64 位注册表视图。

可选的 `BundlerNsisInstallerHooks` 文件可以把 `NSIS_HOOK_PREINSTALL`、`NSIS_HOOK_POSTINSTALL`、`NSIS_HOOK_PREUNINSTALL`、`NSIS_HOOK_POSTUNINSTALL` 中任意几项定义为 NSIS 宏，安装器会在相应生命周期边界调用。Hook 使用安装器当前权限执行。安装 Hook 可以用 `SetErrors` 或 `Abort` 报告失败，安装器会回滚已激活的事务；直接使用 `Quit` 或终止进程无法继续执行即时回滚，但 active journal 会由下一次安装启动恢复。卸载 Hook 用 `SetErrors` 报告失败时会保留前向恢复 journal；进程被终止时同样由下一次安装启动继续。由于恢复可能再次执行尚未完成的卸载阶段，卸载 Hook 必须按幂等方式编写。

Windows Authenticode 签名由独立的 `DotNet.Bundler.Signing.Windows` 包实现。启用签名后，Bundler 把输入复制到临时工作区，依次签主 EXE、显式列入 `BundlerWindowsSigningFile` 的 DLL/sidecar、Bundler 自带 NSIS 插件、导出的卸载器和最终安装器；不会修改原始发布目录，也不会擅自重签其余第三方文件。任一步失败都会删除最终安装器。内置 PFX 和证书存储区指纹只能二选一；PFX 密码只通过环境变量读取，不应写进项目文件或命令行。生产发布强烈建议设置可信的 RFC 3161 时间戳服务，否则证书过期后签名无法继续证明签署时证书有效。

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

云 HSM、USB Token 或远程签名可改用外部 provider。每个参数使用独立 Item，支持 `{path}`/`%1`、`{artifactKind}`、`{target}`、`{productName}` 占位符；不要把令牌或密码写入参数，provider 应从环境变量、系统身份或自己的安全存储读取。provider 输出和参数不会被写入普通错误消息。

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

下面的命令会在当前用户的 `My` 证书存储区创建一个有效期一天的一次性代码签名证书，用它构建 `HelloBundledApp`，并检查主程序、安装器和安装后的卸载器。它只用于验证签名流程；自签名证书没有受信任 CA 的证书链，因此 `Get-AuthenticodeSignature` 通常会报告 `UnknownError` 或“不受信任的根证书”，也不会让真实用户看到可信发布者。

```powershell
# 在仓库根目录创建一次性测试证书。
$certificate = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=Hello Bundled App Test Publisher" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter ([DateTime]::Now.AddDays(1))
$thumbprint = $certificate.Thumbprint

dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release `
  -p:HelloBundledAppSigningCertificateThumbprint=$thumbprint

$installer = Resolve-Path `
  "samples/HelloBundledApp/artifacts/win-x64/nsis/Hello Bundled App-1.0.0-setup.exe"
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
$installerSignature | Select-Object Status, StatusMessage
$installerSignature.SignerCertificate | Select-Object Subject, Thumbprint

if ($installerSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "安装器没有使用预期证书签名。"
}
```

运行安装器并完成安装后，继续验证卸载器；如果安装时修改了目录，请替换下面的路径：

```powershell
$mainExecutable = "$env:LOCALAPPDATA\Programs\Hello Bundled App\HelloBundledApp.exe"
$mainSignature = Get-AuthenticodeSignature -LiteralPath $mainExecutable
if ($mainSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "主程序没有使用预期证书签名。"
}

$uninstaller = "$env:LOCALAPPDATA\Programs\Hello Bundled App\Uninstall.exe"
$uninstallerSignature = Get-AuthenticodeSignature -LiteralPath $uninstaller
if ($uninstallerSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "卸载器没有使用预期证书签名。"
}

# 验收完成后删除一次性测试证书。
Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -Force
```

若要观察 UAC 发布者页面，可在构建时同时传入 `-p:HelloBundledAppInstallMode=perMachine`。自签名证书仍会显示为未知或不受信任的发布者；正式发布必须换成受信任 CA 签发的代码签名证书，并配置 RFC 3161 时间戳。

内置签名器直接调用 Windows 的 Authenticode API，因此启用它时构建宿主必须是 Windows；外部命令 provider 和自定义 `IBundleSigner` 可在 provider 支持的其他宿主运行。不启用签名时，Linux 和 macOS 上的 NSIS 构建不受影响。独立 API 使用者可设置每个 `BundleTargetConfiguration.SigningFiles`，并为 `NsisBundlerOptions.Signer` 使用内置或自定义实现。

若要定制，可把模板复制出来，并将 `BundlerNsisTemplate` 设为其绝对路径。自定义模板必须保留签名两阶段编译所需的 `uninstaller_finalize_command`、`uninstaller_import_define` 和 `signed_uninstaller` 占位符。使用配置的压缩方式时应保留 `compression_directive`。其他变量包括 `product_name`、`version`、`numeric_version`、`publisher`、`identifier`、`main_executable`、`process_name`、`install_folder`、`install_mode`、`target_architecture`、`allow_downgrades`、`legacy_msi_product_codes`、`legacy_msi_upgrade_codes`、`input_glob`、`output_file`、`estimated_size`、`plugin_directory`、`uninstall_payload`、`language_macros`、`language_files`、`display_language_selector`，写法为 `{{name}}`。

这是面向 Windows 的可用基线，并不等于 Tauri 功能对等。

## 仓库命令

```powershell
dotnet build Bundler.slnx
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release
powershell -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.34
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -PackageVersion 0.1.0-alpha.34 -ConfirmLocalInstall
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -PackageVersion 0.1.0-alpha.34 -ConfirmLocalInstall
```

NSIS Windows 集成测试会把专用测试程序安装到包含中文和空格的目录，验证载荷、外部资源、元数据、注册表、快捷方式和进程关闭，分别执行保留数据与彻底删除数据的卸载，并在 `finally` 中清理测试状态。

MSI 集成测试每轮从本地包源还原，并核对隔离缓存中的实际包版本。基础脚本先把独立 API fixture 放在仓库外，只引用 `DotNet.Bundler.Wix` 生成真实 MSI；再用便利元包的 MSBuild fixture 执行真实安装/卸载。生命周期脚本生成两版本和同版本异内容包，检查升级、降级/异包拒绝、桌面注册、快捷方式及用户文件保留。`-ConfirmLocalInstall` 只允许这些受限测试。干净宿主、ARM64、UAC 和高影响故障仍按 MSI 人工验收文档执行。

人工验收入口见 [`docs/manual-testing-index.md`](docs/manual-testing-index.md)；NSIS 历史用例仍在 [`docs/manual-testing.md`](docs/manual-testing.md)，MSI 用例在 [`docs/msi-manual-testing.md`](docs/msi-manual-testing.md)。仓库可自动化的测试仍由上述命令执行，不转为人工清单。

Abstractions 保存跨包稳定契约。Core 实现与格式无关的校验、规划、编排、工作目录生命周期和通用 ZIP 工具缓存。NSIS 项目包含公共后端 API、NSIS 专属配置、模板、进程执行和资源。MSBuild 与开发用 CLI 都只是这些包的适配层，不实现 NSIS 打包逻辑。

## 安全与许可证

NsisToolset 发布包的校验值已经固定在源码中。NuGet 包会同时携带第三方声明和 NSIS 上游许可证。本仓库自身采用何种开源许可证尚未确定。

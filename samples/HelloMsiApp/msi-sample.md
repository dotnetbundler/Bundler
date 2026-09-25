# HelloMsiApp：Windows MSI 功能演示

这是 `DotNet.Bundler` 当前 Windows + MSI 能力的可操作示例，不是自动化测试。
项目只通过普通 `PackageReference` 消费本地打出的 NuGet 包；
没有本仓库源码的普通项目也能按相同方式使用后端。
MSI 使用包内固定的 WiX 3.14.1 工具子集；
构建宿主必须是 Windows。
示例应用设置 `SelfContained=false`，运行应用需要目标电脑已有相应的 .NET 运行时；
Bundler 不会下载或安装应用运行时。

## 生成安装包

在仓库根目录执行：

```powershell
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release
```

默认产物为 `samples/HelloMsiApp/artifacts/feature-demo/win-x64/msi/Hello MSI App-1.0.0.msi`。
`feature-demo` 将本次完整演示与仓库早期同版本示例产物隔开，让上面的直接 `publish` 命令可运行；
命令行仍可用 `BundlerOutputPath` 指向其他目录。
与 NSIS 示例一样，项目通过根 `Directory.Build.props` 取得当前开发包版本，通过 `Bundler.LocalPackages.props` 设置本地 `RestoreSources=artifacts/packages`；
`publish` 消费已还原的 NuGet 包，不会自动重编仓库 `src/`。
实现变更应先按开发规则迭代包版本并重新打包；
缺少当前版本的本地包时先执行 `pack`。

只验证示例构建契约而**不安装** MSI 时，运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1`。
脚本用隔离 NuGet 缓存从本地包源还原，分别生成英语 current-user、中文 current-user 和英语 per-machine 包，检查数据库中的资源、图标、自定义 UI 对话框（含许可与目录选择路由）、快捷方式、Environment/Feature、关联/协议、品牌位图及独立身份；
输出保留在本轮 `%TEMP%` 目录。
此检查不代替真实安装测试；
真实安装/升级/修复/卸载的新能力断言由独立 fixture 的 `VerifyWinMsi6.ps1` 执行。

MSI 后端拒绝在同一路径以相同应用版本覆盖不同内容。
修改本示例后若已有旧 MSI，可通过 `-p:BundlerOutputPath=新的绝对目录` 指定新输出目录。
移动旧 MSI 时要连同其 `.bundler-manifest` 一起保留；
这只处理构建产物，不会卸载电脑上已安装的应用。
已有相同产品版本的旧示例若内容不同，请先在“设置 → 应用 → 已安装的应用”卸载旧版，再安装新示例。
正式发行不得向不同用户发布两个内容不同但产品版本相同的 MSI。

## 默认演示内容

- x64、`currentUser`、英语单语言 MSI，产品版本 `1.0.0`；包工具版本与应用版本相互独立；
- 名称、发布者、描述、主页、版权、应用和 MSI 图标；`Assets/app.ico` 是本示例绘制的几何图标；
- 自写的演示 RTF 许可页面，仅展示交互许可界面，不是实际产品许可；
- 显式开启桌面和开始菜单快捷方式；
- `.hellomsi` 文件关联、MIME 候选及 `hello-msi:` 协议候选处理程序，不抢占 Windows 默认应用；
- 安装目录中的 `demo.hellomsi`、`open-link.cmd` 与 `DemoResources\Readme.txt`；
- 范围内安装目录选择、品牌横幅/对话框位图、PATH 附加、开始菜单卸载入口和安装完成后勾选启动（默认演示均开启，详见下一节）；
- Windows Installer 原生的静默/被动安装、升级、修复、卸载与事务回滚；这些行为使用 MSI 专用 fixture 测试，本示例不包含故障注入。

本项目使用 `BundlerWixCodepage=0`，让 WiX 按所选 MSI 语言选择代码页。
默认英语许可 UI 所用的本地化资源是 1252，所以安装数据库中的名称、描述和目标路径使用英语；
资源文件的**内容**以及应用运行时收到的参数仍可使用中文。
传入 `zh-CN` 会生成独立的简体中文 MSI，但应用自带名称和描述不会被 Bundler 自动翻译。

## 语言、翻译覆盖与 FIPS

示例默认仍是单语言包（`HelloMsiAppLanguage`，默认 `en-US`）；
`HelloMsiAppLanguages` 接收分号分隔的 culture 列表，一次构建为每语言各产一个独立 MSI（各语言产品线身份隔离）：

```powershell
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  "-p:HelloMsiAppLanguages=en-US%3Bja-JP" `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-multi"
```

支持 38 个 WiX 内嵌 culture（如 `en-US`、`zh-CN`、`zh-TW`、`ja-JP`、`ko-KR`、`de-DE`、`fr-FR`、`ar-SA` 等；`hi-IN`/`kk-KZ` 的上游译文在本工具链下不可编译，不在支持集内）。
命令行属性中的分号必须写成 `%3B`。
`Assets/locale-zh-cn.wxl` 演示调用方翻译覆盖：仅在请求 `zh-CN` 时纳入，按 id 覆盖 Bundler 自有串（完成页勾选文本、Feature 名等），其余文本由 WiX 内嵌译文兜底；
覆盖文件须经 culture/键集/编码校验，未知 id 或编码不兼容会被拒绝。
`HelloMsiAppFipsCompliant=true` 会把 `-fips` 透传给 `candle.exe`（`light.exe` 无此开关）；该选项只表示使用 WiX 的 FIPS 兼容编译路径，不构成 FIPS 认证声明。
每个语言包沿用既有的独立身份、输出锁、清单、指纹与同版本碰撞检查。

## 安装目录、界面与可选功能

示例默认演示 WIN-MSI-6 的全部用户能力，均可用 `HelloMsiApp*` 开关关闭：

| 开关（默认值 `true`） | 映射的 `BundlerWix*` 属性 | 演示效果 |
| --- | --- | --- |
| `HelloMsiAppInstallDirectorySelection` | `BundlerWixInstallDirectorySelection` | 交互安装出现目录选择页；可选择 `%LOCALAPPDATA%` 内的子目录，范围外或允许根本身被拒绝。静默 `INSTALLFOLDER=` 走同一范围校验。 |
| `HelloMsiAppBannerBitmap` / `HelloMsiAppDialogBitmap` | `BundlerWixBannerBitmap` / `BundlerWixDialogBitmap` | `Assets/banner.bmp`（493×58）与 `Assets/dialog.bmp`（503×314）替换 WiX 默认品牌图；尺寸或格式不符时构建拒绝。置空字符串即恢复默认图。 |
| `HelloMsiAppAddToPath` | `BundlerWixAddToPath` | 用户 PATH 只追加本产品安装目录，卸载后恢复原值，不影响其他条目。per-machine 包写系统 PATH，属提权路径（未实测）。 |
| `HelloMsiAppUninstallShortcut` | `BundlerWixUninstallShortcut` | 开始菜单中生成"卸载 Hello MSI App"链接（`msiexec /x`）。 |
| `HelloMsiAppLaunchAfterInstall` | `BundlerWixLaunchAfterInstall` | 交互安装完成页出现"启动 Hello MSI App"勾选；只在勾选时以当前用户启动。`/qn`、`/passive`、修复、升级均无该界面也不会启动。 |

交互目录页与浏览/无效目录对话框使用自定义 dialog 序列；
示例含 RTF 许可时序列含许可页。
没有许可文件又开启这些 UI 能力时，Bundler 生成不含许可页的同类序列，不伪造许可条款。
静默安装时 `INSTALLFOLDER` 超出范围会直接以 `1603` 失败；
junction/重解析点作为安装目标的检测在当前版本未实现，见 MSI 能力矩阵的边界说明。

## 安装后操作

默认 current-user 安装目录为 `%LOCALAPPDATA%\Programs\com.example.hellomsiapp-x64`。
从桌面或开始菜单启动应用，会在控制台显示启动参数及额外资源是否存在，并把本次信息写入 `%LOCALAPPDATA%\com.example.hellomsiapp\last-launch.txt`。
这份用户数据不归 MSI 所有，正常卸载不会删除。

可以运行安装目录中的 `open-link.cmd`，或在 PowerShell 执行：

```powershell
Start-Process 'hello-msi:welcome'
Start-Process "$env:LOCALAPPDATA\Programs\com.example.hellomsiapp-x64\demo.hellomsi"
```

Windows 可能要求先在“设置 → 应用 → 默认应用”中选择 **Hello MSI App**；仅注册候选处理程序不保证系统已经把扩展名或协议分配给它。
若安装目录后来变化，请使用实际路径。
当前仓库尚无该默认应用界面及真实唤起的外部验收结论，见 MSI 人工测试清单。

双击 MSI 可交互安装；安装后在 **设置 → 应用 → 已安装的应用** 中找到“Hello MSI App”并选择卸载。
也可在 PowerShell 中使用 MSI 文件路径操作，并检查退出码：

```powershell
$msi = (Resolve-Path 'samples/HelloMsiApp/artifacts/feature-demo/win-x64/msi/Hello MSI App-1.0.0.msi').Path
$log = Join-Path $env:TEMP 'HelloMsiApp-install.log'
$process = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart', '/L*v', "`"$log`"") -Wait -PassThru
$process.ExitCode

# 安装源 MSI 必须仍可用；修复从该源恢复缺失的托管文件。
$process = Start-Process msiexec.exe -ArgumentList @('/fomus', "`"$msi`"", '/qn', '/norestart') -Wait -PassThru
$process.ExitCode

$process = Start-Process msiexec.exe -ArgumentList @('/x', "`"$msi`"", '/passive', '/norestart') -Wait -PassThru
$process.ExitCode
```

`/qn` 无界面、`/passive` 只显示进度；
`msiexec` 返回 Windows Installer 原生退出码，通常成功为 `0`，要求重启可能为 `3010`。
这些命令会真实修改电脑的软件安装状态；
先确认没有旧示例安装或其他用户在使用同一产品。
交互许可页面、缺源修复、UAC、真实重启和生产签名仍需在对应环境验收。

## 语言与安装范围

现阶段英语和简体中文是**分别生成的单语言产品线**，不能在同一 MSI 中运行时切换语言。
current-user 与 per-machine 也是独立身份；没有单包动态切换，也不自动跨范围迁移。
用不同输出目录保存各变体，避免相同版本产物冲突：

```powershell
# 独立中文包，输出文件名带 -zh-cn。
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  -p:HelloMsiAppLanguage=zh-CN `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-zh-CN"

# 独立 per-machine 包；构建可在普通 Windows 主机完成，真实安装需要 UAC。
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  -p:HelloMsiAppInstallScope=perMachine `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-perMachine"
```

per-machine 的数据库和产物已在开发机检查，提权安装/卸载仍待专用环境验收。
若使用自己已有的准确 UpgradeCode 可传 `BundlerWixUpgradeCode`；默认稳定身份已经可用于同产品正常升级，不能为碰巧同名的其他应用随意指定 GUID。

## x86、显式 MSI 版本与降级

`win-x86` 是与 x64、ARM64 分开的 MSI 产品线。
在 x64 Windows 上也可生成并安装 x86 包；示例应用设为非自包含，运行该应用仍需要相应的 x86 .NET 运行时。
使用独立输出目录演示：

```powershell
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  -p:RuntimeIdentifier=win-x86 `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-x86"
```

默认要求 `BundlerVersion` 是有效的三段 MSI 版本。
应用采用预发布版本或其数值超出 MSI 范围时，必须由发行方明确指定三段 `HelloMsiAppMsiVersion`；
不能用第四字段、自动去掉预发布后缀或把两个不同应用版本映射到同一个已发布 MSI 版本。
下例只演示配置，不更改示例项目固定的 `1.0.0` 应用版本：

```powershell
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  -p:RuntimeIdentifier=win-x86 -p:BundlerVersion=1.1.0-beta.1 `
  -p:HelloMsiAppMsiVersion=1.1.0 `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-mapped"
```

仅在确实需要回退时显式传 `-p:HelloMsiAppAllowDowngrades=true`，并用新输出目录构建。
这个开关会让旧 MSI 版本移除较新版本后安装自身；
默认仍拒绝降级。
正式发行应避免复用同一 MSI 版本，先在随机身份测试产品上验证升级和回退。
可运行 `tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -ConfirmLocalInstall`，它使用独立产品身份真实检查 x86 安装、显式版本升级、降级拒绝与允许、同版本碰撞和卸载。

## 签名

默认不签名，仓库不保存私钥。准备代码签名 PFX 后，通过环境变量提供密码，并使用新输出目录签名。生产发行应使用受信任证书及 RFC 3161 时间戳服务：

```powershell
$env:HELLO_MSI_APP_SIGNING_PASSWORD = '你的-PFX-密码'
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release `
  -p:HelloMsiAppSigningPfxFile='C:\证书\publisher.pfx' `
  -p:HelloMsiAppSigningPfxPasswordEnvironmentVariable=HELLO_MSI_APP_SIGNING_PASSWORD `
  -p:HelloMsiAppSigningTimestampUrl='https://你的-RFC3161-时间戳服务' `
  -p:BundlerOutputPath="$env:TEMP\HelloMsiApp-signed"
```

也可用 `HelloMsiAppSigningCertificateThumbprint` 选择证书存储区中的证书；
外部签名提供方可通过 `HelloMsiAppSigningCommand` 和分号分隔的 `HelloMsiAppSigningCommandArguments` 传入，参数中必须包含 `{path}` 或 `%1`。
凭据应由提供方从安全存储取得，不要写进参数。
Bundler 对主程序的隔离副本和最终 MSI 签名，不修改原始 `publish` 目录。
测试自签名证书只能验证技术链路，不能建立用户端可信发布者身份。
生产证书和时间戳的真实验收仍见 MSI 人工清单。

## 当前边界

MSI 的更多语言、自备 WiX 扩展仍是 WIN-MSI-7..9 计划，不在此示例中伪装为已实现；
安装时 junction/重解析点目录检测、交互 UI 的实际视觉/辅助功能验收也仍是人工清单项。
NSIS 的 Hook、语言选择器、安装器图片、自定义快捷方式参数和 journal 恢复是该后端的特定能力，不能直接搬到 MSI。
真实升级/降级和故障回滚由独立随机身份 fixture 验证；
公开示例应用版本保持 `1.0.0`，不会拿它覆盖旧版本做测试。
当前实现与计划状态分别见 [`docs/msi-capability-matrix.md`](../../docs/msi-capability-matrix.md)和 [`docs/msi-roadmap.md`](../../docs/msi-roadmap.md)。

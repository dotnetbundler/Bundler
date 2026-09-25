# HelloNsisApp：Windows NSIS 功能演示

这个项目不是自动化测试，而是 `DotNet.Bundler` 当前 Windows + NSIS 功能的可操作演示。
项目名为 `HelloNsisApp`，保留既有产品名 Hello Bundled App 和可执行文件 `HelloBundledApp.exe`，以免仅整理项目名称就改变安装身份。
项目只通过普通 `PackageReference` 使用本地生成的 NuGet 包。

## 生成安装程序

在仓库根目录执行：

```powershell
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release
```

安装程序位于：

```text
samples/HelloNsisApp/artifacts/win-x64/nsis/Hello Bundled App-1.0.0-setup.exe
```

构建时会从仓库固定版本的 NsisToolset 中把演示图片提取到 `obj`，用于展示安装器图标、卸载器图标、Header、卸载器 Header 和 Sidebar。
它们不会写入项目源码目录。

## 默认演示内容

默认安装程序直接展示以下功能：

- English、简体中文与日文语言选择器；简体中文由项目文件覆盖，日文使用包内置文案；
- 许可证页面、发布者、描述、主页、版权和版本元数据；
- 自定义安装器/卸载器图标、Header、卸载器 Header 和 Sidebar；
- 使用 `BundlerNsisCompression=zlib` 展示可配置的 NSIS 压缩算法；
- 自定义安装目录、非空目录警告和记忆上次安装目录；
- 可选桌面快捷方式和 `Bundler Examples` 开始菜单目录；快捷方式带启动参数、自定义工作目录、图标和稳定 AppUserModelID；
- 安装前通过主程序完整路径检测并关闭正在运行的应用，不影响其他目录中的同名进程；
- 安装前、安装后、卸载前、卸载后的 NSIS Hook；
- `.hello` 文件关联和 `hello-bundled:` 深链接；
- 安装目录自带 `demo.hello` 和 `运行深链接.cmd`，可以直接操作两项关联功能；
- 安装额外资源到 `演示资源\说明.txt`；
- 同版本重装、升级、降级控制；
- 卸载时选择是否删除应用数据；
- 静默安装和静默卸载。

NSIS 安装和升级会先建立包含旧载荷、产品注册表和快捷方式的事务快照。
安装 Hook 或后续写入失败时会恢复旧版本；
安装器进程意外终止后，下一次运行同一产品的安装器会先恢复 active journal。
提交时先把 journal 原子重命名为 `.committed`，清理失败不会回滚已经完成的安装，下一次启动会重试清理。
快照位于用户或计算机范围的 `DotNetBundler\transactions` 目录，并会临时占用接近现有安装目录大小的磁盘空间。
旧 MSI 卸载属于不可逆的外部迁移边界，不在自动恢复范围内。

卸载使用独立的前向恢复 journal：它保存安装目录、删除应用数据选择和恢复卸载器。
卸载 Hook 失败或进程中断后不会尝试还原已经删除的文件；下一次启动安装器会先完成旧卸载，再继续安装。
卸载 Hook 可能在恢复时再次运行，因此示例 Hook 只执行可重复的标记写入，不依赖“恰好一次”语义。

仓库集成测试还会在快照、激活、载荷恢复、注册表恢复和 active journal 清理检查点注入一次性可控故障，验证下次启动能重入恢复并清理 journal；真实 ACL 拒绝、磁盘耗尽和重启仍属于外部环境验收。

程序启动后会显示收到的参数、额外资源是否存在以及安装 Hook 是否执行，并将最近一次启动写入：

```text
%LOCALAPPDATA%\com.example.hellobundledapp\last-launch.txt
```

卸载时选择删除应用数据会同时删除这份记录。

## 快捷方式完整性

示例把快捷方式参数配置为 `--from-shortcut "你好 world"`，工作目录配置为安装目录内的 `演示资源`，AppUserModelID 配置为 `com.example.hellobundledapp.desktop`。
安装后从桌面或开始菜单启动程序，控制台输出和 `last-launch.txt` 都可验证参数；
快捷方式属性页可验证目标、起始位置和图标。

`BundlerNsisShortcutLegacyProductNames` 与 `BundlerNsisShortcutLegacyMainExecutables` 展示产品名、主程序名变更时的显式迁移入口。
`/UPDATE` 只更新仍存在且仍指向本安装当前/旧主程序的快捷方式：手动删除的快捷方式不会重建；
同名 `.lnk` 如果后来改为指向其他程序，卸载也不会删除。
Windows 各版本的任务栏和开始菜单固定机制并不稳定，当前示例只人工检查固定项行为，不把自动取消固定列为已保证能力。

## 文件关联与深链接

深链接安装后可以直接启动：

```powershell
Start-Process "hello-bundled:welcome"
```

文件关联：

```powershell
Start-Process "$env:LOCALAPPDATA\Programs\Hello Bundled App\demo.hello"
```

Windows 不允许安装器静默替换用户选择的默认程序。
第一次打开 `.hello` 文件时可能显示应用选择器，此时选择 **Hello Bundled App**。
程序会显示收到的文件路径。

也可以直接双击安装目录中的 `运行深链接.cmd`，该脚本会运行 `hello-bundled:welcome`。
如果安装时修改了目录，请从实际安装目录运行这两个演示文件。

## 安装范围

三种安装范围互斥，因此通过构建参数分别生成：

```powershell
# 当前用户安装，不需要管理员权限
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:HelloNsisAppInstallMode=currentUser

# 所有用户安装，请求管理员权限并安装到 Program Files
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:HelloNsisAppInstallMode=perMachine

# 安装时由用户选择当前用户或所有用户
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:HelloNsisAppInstallMode=both
```

每次生成使用相同的文件名，演示不同模式时应先复制或重命名上一次的产物。

## 重装、升级与降级

先安装 `1.0.0`，再用不同版本生成安装程序：

```powershell
# 生成升级包
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:Version=1.1.0

# 生成默认禁止降级的 0.9.0
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:Version=0.9.0

# 生成明确允许降级的 0.9.0
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -p:Version=0.9.0 -p:HelloNsisAppAllowDowngrades=true
```

再次运行同版本安装程序会展示重装策略；运行高版本和低版本安装程序会分别展示升级及降级策略。
让程序保持在“按任意键退出”界面，再运行安装或卸载程序，可以展示运行进程检测和关闭。

## 自动安装、更新与启动参数

NSIS 的 `/D=` 必须是最后一个参数：

```powershell
# 静默安装。
& '.\Hello Bundled App-1.0.0-setup.exe' /S '/D=C:\演示安装目录\Hello Bundled App'

# 被动安装只显示进度；/NS 禁止创建快捷方式。
& '.\Hello Bundled App-1.0.0-setup.exe' /P /NS '/D=C:\演示安装目录\Hello Bundled App'

# 自动更新并在成功后启动应用。应用收到的参数会写入
# %LOCALAPPDATA%\com.example.hellobundledapp\last-launch.txt。
& '.\Hello Bundled App-1.1.0-setup.exe' /UPDATE /R '/ARGS=--from-updater "你好 world"' '/D=C:\演示安装目录\Hello Bundled App'

# 默认卸载保留运行时数据；/DELETEAPPDATA 同时删除数据和完整安装目录。
& 'C:\演示安装目录\Hello Bundled App\Uninstall.exe' /S
& 'C:\演示安装目录\Hello Bundled App\Uninstall.exe' /P /DELETEAPPDATA
```

`/S` 不显示界面，`/P` 只显示进度，`/UPDATE` 在未指定 `/S` 时自动采用 `/P` 并保留已有快捷方式状态。
`/R` 只允许与这些自动模式一起使用；
`/ARGS=` 直接传给应用，不经过命令行 Shell。
安装器退出码为：`0` 成功、`1` 取消、`2` 一般失败、`3` 参数错误、`4` 版本策略阻止、`5` 无法关闭应用、`3010` 成功但需要重启。
返回 `3010` 前安装事务已经提交并清理，且 `/R` 不会在重启前启动程序。
实际卸载进程会为失败返回 `2`，若把 `/REBOOTOK` 删除排入系统队列则返回 `3010`；
直接运行安装目录中的 NSIS 卸载器时，外层自复制 launcher 不可靠地传播该退出码，不能只凭它的 `0` 判断完成。
当前明确采用“锁定载荷安全失败”策略，不宣称安装器能在重启后自动替换任意被锁定的安装载荷；
遇到这种文件时安装失败并通过 journal 恢复旧状态，交互模式会提示关闭占用安装目录文件的程序后重试，不会跳过文件后误报成功。

## 生命周期 Hook

示例的 Hook 会在临时目录创建以下标记：

```text
%TEMP%\HelloBundledApp-hook-preinstall.txt
%TEMP%\HelloBundledApp-hook-postinstall.txt
%TEMP%\HelloBundledApp-hook-preuninstall.txt
%TEMP%\HelloBundledApp-hook-postuninstall.txt
```

## 旧 MSI 迁移

迁移功能不能使用随意生成的 GUID 演示，否则可能匹配不到任何产品，甚至误卸载其他软件。
必须使用待迁移 MSI 的真实 ProductCode 或 UpgradeCode：

```powershell
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release `
  -p:HelloNsisAppLegacyMsiProductCodes='{真实的-PRODUCT-CODE}' `
  -p:HelloNsisAppLegacyMsiUpgradeCodes='{真实的-UPGRADE-CODE}'
```

仓库的自动化集成测试使用一次性 MSI Fixture 完整验证这条路径；本示例只保留安全、明确的参数入口。

## 安装器与卸载器签名

示例默认不签名，因为仓库不能携带发布者私钥。
准备代码签名 PFX 后，通过环境变量传入密码即可演示完整签名流程；密码不会进入项目文件或 MSBuild 命令行：

```powershell
$env:HELLO_BUNDLED_APP_SIGNING_PASSWORD = '你的-PFX-密码'
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release `
  -p:HelloNsisAppSigningPfxFile='C:\证书\publisher.pfx' `
  -p:HelloNsisAppSigningPfxPasswordEnvironmentVariable=HELLO_BUNDLED_APP_SIGNING_PASSWORD `
  -p:HelloNsisAppSigningTimestampUrl='https://你的-RFC3161-时间戳服务'
```

也可以用 `HelloNsisAppSigningCertificateThumbprint` 指定当前用户 `My` 证书存储区中的证书。
签名时会在临时副本中签署主程序和 Bundler 原生插件，再签卸载器和最终安装器；
原始 `publish` 目录不会被修改。
内置签名器无需安装 Windows SDK 或 `signtool.exe`，但必须在 Windows 主机运行。

外部签名服务可通过 `HelloNsisAppSigningCommand` 和分号分隔的 `HelloNsisAppSigningCommandArguments` 演示，例如 `sign;--file;{path}`。
至少一个参数必须含 `{path}` 或 `%1`；
还可使用 `{artifactKind}`、`{target}`、`{productName}`。
凭据应由 provider 从环境变量或安全存储读取，不能放进参数。

本地没有正式证书时，可以创建一次性自签名证书来验证签名链路：

```powershell
$certificate = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=Hello Bundled App Test Publisher" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter ([DateTime]::Now.AddDays(1))
$thumbprint = $certificate.Thumbprint

dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release `
  -p:HelloNsisAppSigningCertificateThumbprint=$thumbprint

$installer = Resolve-Path `
  "samples/HelloNsisApp/artifacts/win-x64/nsis/Hello Bundled App-1.0.0-setup.exe"
$signature = Get-AuthenticodeSignature -LiteralPath $installer
$signature | Select-Object Status, StatusMessage
$signature.SignerCertificate | Select-Object Subject, Thumbprint

if ($signature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "安装器没有使用预期证书签名。"
}

# 运行安装器后检查卸载器；如果修改过安装目录，请相应修改路径。
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

# 验收完成后删除一次性证书。
Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -Force
```

自签名证书只能证明 staged payload、插件、卸载器和安装器签名流水线有效。
它没有受信任的证书链，因此状态通常是 `UnknownError` 或“不受信任的根证书”，不能代替正式发布证书。

## 高级覆盖入口

`BundlerToolCachePath`、`BundlerNsisToolsetArchivePath`、`BundlerNsisCompilerPath`、`BundlerNsisDataDirectory` 和 `BundlerNsisTemplate` 是工具链或模板的高级覆盖入口。
示例默认不设置它们，因为默认路径本身要演示“NuGet 包自带多宿主 NSIS 工具且多个项目共享缓存”的正常行为。
需要调试自定义工具链或模板时，可以通过同名 MSBuild 属性传入。

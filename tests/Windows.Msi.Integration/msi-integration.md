# Windows MSI 集成回归

测试体已收编进 `tests/Bundler.IntegrationTests`（`MsiIntegrationTests`，xUnit v3）；
`Verify*.ps1` 全部只是薄入口，按 `--filter-class`/`-method` 转发到对应事实。
`Fixture/`、`Standalone/`、`Assets/` 仍在原目录，由测试代码复制到隔离工作区使用。

当前普通本机用法：

```powershell
# 薄入口：-ConfirmLocalInstall / -ConfirmDisposableVm 会置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyMaintenance.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi6.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi7.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi8.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1 -Configuration Release

# 直接入口
$env:BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL = '1'
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class MsiIntegrationTests
```

脚本名与 `[Fact]` 的对应：`Verify.ps1→SmokeInstallUninstallPreservesUserData`、`VerifyLifecycle→MajorUpgradeVariantCollisionAndDowngradeRejected`、
`VerifyMaintenance→DamageInjectionRepairAndLocalizedIdentity`、`VerifyWinMsi5→X86VersionMappingAndDowngradePolicy`、
`VerifyWinMsi6→FeatureSetScopePathAndCustomDirectory`、`VerifyWinMsi7→PerLanguageMsisInstallCoexistAndUninstallIndependently`、
`VerifyWinMsi8→ExtensionFragmentAndExpertTemplateInstallAndUninstall`、`VerifyPublicSample→PublicSampleMsiTableContract`。

fixture 初始化做三件事（`Lazy<bool> Ensure`，跳过语义落在事实体内）：
先 `dotnet build-server shutdown` 释放常驻 MSBuild 节点持有的任务程序集文件锁，再 build+pack 仓内 NuGet 包，
并逐项核对 `DotNet.Bundler.Wix` 包的程序集、许可证、对应源码、哈希清单及第三方声明与仓库文件一致。
MSI MSBuild fixture 显式导入随项目一起复制的 `Bundler.LocalPackages.props`，由该文件声明 `RestoreSources=$(BundlerPackageSource)`；
测试把项目文件、该 props、程序和 Assets 复制到每轮独立的集成工作区，避免继承仓库根 `Directory.Build.props` 或旧 obj，
并断言 `project.assets.json` 与缓存实际使用当前版本。
`Standalone/Msi.Api.PackageFixture.csproj` 连同该 props 复制到仓库外工作区，仅引用后端 NuGet 包，
通过公共 `WixBundler` API 和包内工具生成 MSI——不使用 `ProjectReference` 或 MSBuild 便利元包，是发布包独立消费的验收腿。

真装腿用 `msiexec /qn /norestart /L*v` 真实安装每轮随机 identifier 的 current-user 产品，
经 `WixToolset.Dtf` 校验注册表/产品状态/表结构，经 IShellLinkW 校验快捷方式；
卸载断言主程序、额外资源、注册状态、托管文件卸载和未知用户文件保留。
`finally` 清理只针对本轮 ProductCode、identifier 键与空目录；MSI、哈希和 verbose log 保留在集成工作区供核查。

各事实覆盖：

- `Verify.ps1`：冒烟装卸、资源内容与注册表元数据、用户文件保留；
- `VerifyLifecycle.ps1`：大变体升级、同 MSI 版本异内容包 1638 拒绝、默认降级 1603、注册/快捷方式/数据所有权；
- `VerifyMaintenance.ps1`：损坏包 1619/1620、注入 CustomAction 1058 的延迟故障 1603 全量回滚、被动安装、`/fomus` 静默修复、zh-CN 独立语言包并存；
- `VerifyWinMsi5.ps1`：`win-x86` 包生成、显式 MSI 版本映射、版本碰撞 1638、默认降级 1603、显式允许降级、32 位注册表视图、受管文件卸载；
- `VerifyWinMsi6.ps1`：目录选择范围校验（允许根本身/范围外根 1603 拒绝）、自定义子目录安装、PATH 仅追加且升级后恢复、开始菜单含受管卸载链接、HKLM Uninstall `InstallLocation`/`Contact`、v2 升级不传 `INSTALLFOLDER` 恢复已选目录、`/fomus` 修复、卸载恢复 PATH 与注销；
- `VerifyWinMsi7.ps1`：`BundlerWixLanguages=en-US%3Bja-JP` 双 MSI 的 ProductCode/UpgradeCode/ProductLanguage 隔离、文件名后缀、合并 wxl 与 `!(loc.*)` 引用入库、并存安装独立卸载；`Msi.Api.PackageFixture` 的 `Languages`/`LocaleFiles`/`FipsCompliant` 直 API 产 `en-US`/`de-DE` 并断言 `-fips` 仅传给 `candle`；
- `VerifyWinMsi8.ps1`：`Ext.` 前缀 + 声明式 fragment 的常规模式 MSI 构建、扩展标记文件与注册表装卸、专家模板参数的身份回读与真实装卸；
- `VerifyPublicSample.ps1`：公开示例三变体（英文/中文 current-user、英文 per-machine）的数据库结构、UI 序列、快捷方式、PATH 与位图断言，不安装——免 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL` 门禁但仍仅 Windows（表结构依赖 DTF）。

后续每增加或修改一个 MSI 用户能力，需同时增加快速单元/契约测试和当前机器能安全运行的真实 Windows 集成断言。
升级要用两个真实 MSI 版本检查旧文件与用户文件；快捷方式、文件关联、协议要检查系统状态和卸载所有权；签名、修复、失败回滚及退出码要检查可观察结果，不能只检查 WiX XML 或数据库行。
按 `docs/msi-roadmap.md` 的阶段加入，不提前实现未开始阶段。

UAC/per-machine、真实重启、系统级故障、生产证书、干净 Windows 10/11 和 ARM64 需要专用环境，按 `docs/msi-manual-testing.md` 记录为未执行；没有环境时不阻塞快速开发，也不写成测试通过。

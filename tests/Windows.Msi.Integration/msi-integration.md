# Windows MSI 集成回归

`Verify.ps1` 是 MSI 专用的真实 Windows 安装/卸载测试入口；它与 NSIS 的 `tests/Windows.Nsis.Integration` 分开维护。
当前普通本机用法：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyMaintenance.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi6.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1 -Configuration Release
```

`Verify.ps1` 先从当前工作区打出七个 NuGet 包，检查独立 `DotNet.Bundler.Wix` 包的程序集、许可证、对应源码、哈希清单及第三方声明。
`MsiTestSupport.ps1` 复用打包、fixture 复制、本地还原检查、MSI 属性读取和安装调用。
MSI MSBuild fixture 显式导入随项目一起复制的 `Bundler.LocalPackages.props`，由该文件声明 `RestoreSources=$(BundlerPackageSource)`；
脚本将项目文件、该 props、程序和 Assets 复制到每轮独立的仓库外临时目录，避免继承仓库根 `Directory.Build.props` 或旧 obj。
脚本传入本轮本地包目录、包版本和独立缓存，并断言 `project.assets.json` 与缓存实际使用当前版本。
随后把 `tests/Msi.Api.PackageFixture` 连同该 props 复制到仓库外临时目录，仅引用后端 NuGet 包，通过公共 `WixBundler` API 和包内工具生成 MSI；
它不使用 `ProjectReference` 或 MSBuild 便利元包。
最后才安装每轮新建的 current-user MSBuild 测试产品，预检安装目录和 ProductCode 未被占用，通过 `msiexec` 静默安装并卸载。
断言主程序、额外资源内容、产品名称/版本/注册状态、托管文件卸载和未知用户文件保留。
`finally` 只针对本轮 ProductCode、未知文件和空目录清理；保留 MSI、哈希和 verbose log 供核查。

`VerifyLifecycle.ps1` 同样从本轮本地包源和独立缓存还原，检查包版本，使用各自独立的临时项目和 obj 生成两个版本及同版本异内容包，真实检查升级、降级/异包拒绝、注册、快捷方式及数据所有权。
`VerifyMaintenance.ps1` 检查损坏包、测试包副本的延迟故障回滚、被动安装/卸载、静默修复及英语/简体中文包并存；生产包不包含故障动作。
三个脚本通过 `MsiTestSupport.ps1` 与 NSIS 集成入口共用 `tests/AssertLocalRestore.ps1`；
省略 `-PackageVersion` 时从根 `Directory.Build.props` 读取当前版本。
Pack 辅助流程逐项核对 WiX NuGet 包中的许可证、对应源码、哈希清单、供应说明及第三方声明与仓库文件一致。
每次运行都要确认退出码、产品状态、目录及日志。
跨格式共同规则见 `docs/development-rules.md`。

`VerifyWinMsi5.ps1` 是 WIN-MSI-5 专用回归入口：它从隔离本地包源消费 alpha 包，分别通过仓库外的 MSI API fixture 和 MSBuild fixture 生成 `win-x86` 包，检查显式 MSI 版本映射、同 MSI 版本碰撞（1638）、默认降级拒绝（1603）、显式允许降级、32 位注册表视图、受管文件卸载和未知用户文件保留。
该脚本会安装、升级、降级和卸载本轮随机身份的产品，MSI 与 verbose log 保留在临时目录；它不替代 `VerifyLifecycle.ps1` 的 x64 生命周期回归。

`VerifyWinMsi6.ps1` 是 WIN-MSI-6 专用回归入口：从本轮本地包源和隔离缓存还原仓库外 MSBuild fixture，生成开启目录选择、快捷方式、PATH、卸载入口、启动勾选和 ARP 元数据的随机身份 current-user 产品，以及同产品线的 v2 升级包。
脚本实际验证：静默 `INSTALLFOLDER=` 指向允许根本身（`%LOCALAPPDATA%`）和范围外根（`C:\Program Files\...`）均被范围校验拒绝（1603）；允许根内自定义子目录安装成功且载荷落位；用户 PATH 只追加本产品目录且既有条目完全保留；开始菜单（含受管卸载链接）与桌面快捷方式创建；
HKLM Uninstall 键含 `InstallLocation`/`Contact`；
v2 升级不传 `INSTALLFOLDER` 也恢复到已选目录并保留未知用户文件；`/fomus` 静默修复；
卸载后 PATH 恢复原值、快捷方式/目录清理、未知用户文件保留、产品注销。
脚本不进入真实交互 UI；勾选启动、对话框流转与 junction 目标按 `MSI-MT-11` 人工验收。
MSI 与 verbose log 保留在临时目录。

`VerifyPublicSample.ps1` 只构建公开示例的三种 MSI 变体（英文/中文 current-user、英文 per-machine）并断言数据库结构、UI 序列、快捷方式、PATH 与位图；不安装示例产品。

后续每增加或修改一个 MSI 用户能力，需同时增加快速单元/契约测试和当前机器能安全运行的真实 Windows 集成断言。
升级要用两个真实 MSI 版本检查旧文件与用户文件；快捷方式、文件关联、协议要检查系统状态和卸载所有权；签名、修复、失败回滚及退出码要检查可观察结果，不能只检查 WiX XML 或数据库行。
按 `docs/msi-roadmap.md` 的阶段加入，不提前实现未开始阶段。

UAC/per-machine、真实重启、系统级故障、生产证书、干净 Windows 10/11 和 ARM64 需要专用环境，按 `docs/msi-manual-testing.md` 记录为未执行；没有环境时不阻塞快速开发，也不写成测试通过。

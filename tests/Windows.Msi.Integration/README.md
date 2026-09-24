# Windows MSI 集成回归

`Verify.ps1` 是 MSI 专用的真实 Windows 安装/卸载测试入口；它与 NSIS 的 `tests/Windows.Nsis.Integration` 分开维护。当前普通本机用法：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
```

脚本只安装每轮新建的 current-user 测试产品。它从当前工作区打包 NuGet、仅用本地包源还原 fixture、生成 MSI，预检安装目录和 ProductCode 未被占用，然后通过 `msiexec` 静默安装并卸载。断言主程序、额外资源内容、产品名称/版本/注册状态、托管文件卸载和未知用户文件保留。`finally` 只针对本轮 ProductCode、未知文件和空目录清理；保留 MSI、哈希和 verbose log 供核查。每次运行都要确认退出码以及清理后的产品状态与目录。

后续每增加或修改一个 MSI 用户能力，需同时增加快速单元/契约测试和当前机器能安全运行的真实 Windows 集成断言。升级要用两个真实 MSI 版本检查旧文件与用户文件；快捷方式、文件关联、协议要检查系统状态和卸载所有权；签名、修复、失败回滚及退出码要检查可观察结果，不能只检查 WiX XML 或数据库行。按 `docs/msi-roadmap.md` 的阶段加入，不提前实现未开始阶段。

UAC/per-machine、真实重启、系统级故障、生产证书、干净 Windows 10/11 和 ARM64 需要专用环境，按 `docs/msi-manual-testing.md` 记录为未执行；没有环境时不阻塞快速开发，也不写成测试通过。

# Windows MSI 集成回归

`Verify.ps1` 是 MSI 专用的真实 Windows 安装/卸载测试入口；它与 NSIS 的 `tests/Windows.Nsis.Integration` 分开维护。当前普通本机用法：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall
```

`Verify.ps1` 先从当前工作区打出七个 NuGet 包，检查独立 `DotNet.Bundler.Wix` 包的程序集、许可证、对应源码、哈希清单及第三方声明。与 NSIS 一样，MSI MSBuild fixture 在 `.csproj` 中以 `RestoreSources=$(BundlerPackageSource)` 声明还原源，脚本传入本轮本地包目录和独立缓存，并断言 `project.assets.json` 与缓存实际使用当前版本。随后把 `tests/Msi.Api.PackageFixture` 复制到仓库外临时目录，仅引用后端 NuGet 包，通过公共 `WixBundler` API 和包内工具生成 MSI；它不使用 `ProjectReference` 或 MSBuild 便利元包。最后才安装每轮新建的 current-user MSBuild 测试产品，预检安装目录和 ProductCode 未被占用，通过 `msiexec` 静默安装并卸载。断言主程序、额外资源内容、产品名称/版本/注册状态、托管文件卸载和未知用户文件保留。`finally` 只针对本轮 ProductCode、未知文件和空目录清理；保留 MSI、哈希和 verbose log 供核查。

`VerifyLifecycle.ps1` 同样从本轮本地包源和独立缓存还原，检查包版本，再用独立测试身份生成两个版本和同版本异内容包，真实检查升级、降级/异包拒绝、注册、快捷方式及数据所有权。两个脚本与 NSIS 集成入口共用 `tests/AssertLocalRestore.ps1`；`-PackageVersion` 默认值必须与 `Directory.Build.props` 同步。每次运行都要确认退出码、产品状态、目录及日志。跨格式共同规则见 `docs/development-rules.md`。

后续每增加或修改一个 MSI 用户能力，需同时增加快速单元/契约测试和当前机器能安全运行的真实 Windows 集成断言。升级要用两个真实 MSI 版本检查旧文件与用户文件；快捷方式、文件关联、协议要检查系统状态和卸载所有权；签名、修复、失败回滚及退出码要检查可观察结果，不能只检查 WiX XML 或数据库行。按 `docs/msi-roadmap.md` 的阶段加入，不提前实现未开始阶段。

UAC/per-machine、真实重启、系统级故障、生产证书、干净 Windows 10/11 和 ARM64 需要专用环境，按 `docs/msi-manual-testing.md` 记录为未执行；没有环境时不阻塞快速开发，也不写成测试通过。

# Windows NSIS 集成回归

`Verify.ps1` 是 NSIS 专用的真实 Windows 安装/卸载测试入口；它与 MSI 的 `tests/Windows.Msi.Integration` 分开维护。
当前普通本机用法：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release
# 只清理遗留测试状态，不执行打包与安装：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -CleanupOnly
```

脚本不需要 `-ConfirmLocalInstall`；它会真实安装固定身份的测试产品，不要在装有其同名产品或正使用该安装目录的机器上运行。

`Verify.ps1` 先打包当前工作区的 NuGet 包，并逐包断言清单条目：便利元包与 MSBuild 包的 buildTransitive 文件、netstandard2.0 程序集、NSIS 工具集与许可证、第三方声明。
集成根目录为 `artifacts\windows-nsis-integration`，包缓存在其下的 `packages` 子目录，每轮重建。
fixture 与 API 项目经 `Bundler.ProjectReference.targets` 以项目引用接入仓内 `src/`，仍使用独立 `packages` 缓存隔离第三方还原；
打出的 nupkg 仅用于包内容清单断言，不再被仓内项目消费。

脚本通过三条消费路径生成本轮安装器：

- `tests/Nsis.Api.PackageFixture`：以项目引用直引 `src/Bundler.Nsis`，通过公共 API 构建，验证直接 API 消费。
- `Fixture/BundlerNsisIntegrationFixture.csproj`：MSBuild 任务消费 fixture（项目引用接入），随参数生成 current-user、per-machine、both、升级、允许降级、Unicode 产品、签名、无快捷方式默认值及各类故障注入变体。
- `LegacyMsiFixture/LegacyMsiFixture.wixproj`：用 WiX SDK 5.0.2 构建的一次性旧 MSI 产品（非 Bundler 产物），用于按 ProductCode 与 UpgradeCode 两条精确路径验证旧 MSI 迁移。
- `LegacyMsiFixtureV2/LegacyMsiFixtureV2.wixproj`：同一 UpgradeCode 的第二个一次性 MSI（0.8.0、独立 ProductCode/安装目录），验证多版本并存取最高版本判定与迁移循环全部清理。
- 同一 fixture 项目的 `BundlerFormats=msi` 变体产出 Bundler MSI，验证 NSIS→MSI 目录延续（读卸载键 `InstallLocation`）、Bundler `InstallDir` 注册优先与范围外回落。

集成断言覆盖：

- 静默 current-user 安装/卸载、同版本原位修复、升级与默认降级拒绝、显式允许降级；
- Unicode 产品名/目录/开始菜单与多语言选择器；
- 安装器自动化协议（`/S`、`/P`、`/NS`、`/R`、`/ARGS`）与稳定退出码（完整表见 `docs/nsis-roadmap.md` §4.9）；
- 快捷方式创建、读取、所有权保护、改名迁移与按目标而非名称的删除判定；
- 文件关联与 URL 协议注册、卸载时的所有权检查；
- 生命周期 Hook 与安装/卸载事务回滚：事务快照、激活、载荷恢复、注册表恢复、journal 清理各检查点的一次性故障注入，以及安装器进程树中断后的下次启动恢复；
- 卸载前向恢复：post-uninstall Hook 失败与进程中断后保留 journal 与恢复卸载器，下次安装先幂等完成旧卸载；
- 旧 MSI 迁移：ProductCode/UpgradeCode 精确路径、`LegacyMsiAutoDetect` 自动检测命中（DisplayName+Publisher+msiexec）、名称/发布者不匹配的负例、多版本并存取最高版本并全部清理；
- NSIS→MSI 目录延续、Bundler `InstallDir` 优先级与范围外回落；
- journal 篡改（快捷方式 `path.txt`、注册表 snapshot subkey）在恢复前被拒绝；
- 重解析点/junction 在快照、journal 与构建输入中的安全拒绝；
- Restart Manager 只关闭安装目录内主程序，不影响另一路径同名进程；
- 锁定非主程序载荷时安全失败返回 `2`、保留 active journal，释放锁后下次启动先恢复再继续；
- 自签名测试证书对主程序、插件、卸载器与安装器的完整签名链路。

清理按固定 identifier（`com.dotnetbundler.integrationfixture`）与集成根目录执行，删除前校验路径确在预期范围内，拒绝清理意外路径；未知用户文件与外部拥有的快捷方式不删除。
安装器、日志与故障注入产物保留在 `artifacts\windows-nsis-integration` 下供核查。

## 重启用例

`tests/Windows.Nsis.Reboot/Verify.ps1` 只在可抛弃虚拟机或专用测试机执行，分两个阶段：

```powershell
# 阶段一（提权 PowerShell）：制造真实锁定文件删除并断言返回 3010
tests\Windows.Nsis.Reboot\Verify.ps1 -Phase Prepare -InstallerPath <perMachine 安装器> -ConfirmDisposableMachine
# 正常重启后执行阶段二核对最终状态
tests\Windows.Nsis.Reboot\Verify.ps1 -Phase Verify -ConfirmDisposableMachine
```

Prepare 通过临时副本与 `_?=` 参数直接等待真实卸载进程；仅在输出 `PASS prepare` 后才允许重启，且不得手工改动 `PendingFileRenameOperations`。
详细规程见 `docs/nsis-manual-testing.md` 的 MT-03。

## 维护规则

后续每增加或修改一个 NSIS 用户能力，需同时增加快速单元/契约测试和当前机器能安全运行的真实 Windows 集成断言。
真实 UAC/per-machine、生产证书、真实历史 MSI、ARM64/其他宿主与真实 ACL/磁盘故障需要专用环境，按 `docs/nsis-manual-testing.md` 和 `docs/nsis-open-items.md` 记录为外部待验收；没有环境时不阻塞快速开发，也不写成测试通过。
跨格式共同规则见 `docs/development-rules.md`。

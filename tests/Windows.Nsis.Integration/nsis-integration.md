# Windows NSIS 集成回归

测试体已收编进 `tests/Bundler.IntegrationTests`（`NsisIntegrationTests`，xUnit v3）；
`Verify.ps1` 只是薄入口，转发到 `dotnet test --filter-class NsisIntegrationTests`。
fixture、故障注入 hook、旧版 MSI 迁移用 wixproj 仍在 `Fixture/`、`LegacyMsiFixture*/` 下，由测试代码调用。

当前普通本机用法：

```powershell
# 薄入口（跑脚本即同意本机真装，等价于设置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release

# 直接入口
$env:BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL = '1'
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class NsisIntegrationTests
```

真装腿会真实安装固定身份的测试产品（含写当前用户的注册表、快捷方式、应用数据目录），
不要在装有其同名产品或正使用该安装目录的机器上运行；
未设置 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 或非 Windows 宿主时相关事实显式 SKIP。
跨宿主可跑的 `RepositoryPackagesCarryNsisBackendAndAssets` 包装配断言无此门禁。

集成根目录为 `artifacts\windows-nsis-integration`，包缓存在其下的 `packages` 子目录，每轮重建。
fixture 与 API 项目经 `Bundler.ProjectReference.targets` 以项目引用接入仓内 `src/`，仍使用独立 `packages` 缓存隔离第三方还原；
打出的 nupkg 仅用于包内容清单断言，不再被仓内项目消费。

测试在 fixture 初始化时一次性产出全部安装器（`Lazy<bool> Ensure`，跳过语义落在事实体内）：

- `Bundler.ApiTests` 的 `NsisApiTests`：经 `dotnet test --filter-class` 调起，验证直接 API 消费；
- `Fixture/BundlerNsisIntegrationFixture.csproj`：MSBuild 任务消费 fixture，生成约 30 个变体——current-user、per-machine、both、升级、允许降级、Unicode 产品、签名、无快捷方式默认值、各检查点故障注入、旧 MSI 迁移、MSI 连续性；
- `LegacyMsiFixture*/LegacyMsiFixture*.wixproj`：WiX SDK 5.0.2 的一次性旧 MSI（非 Bundler 产物），验证按 ProductCode/UpgradeCode 的精确迁移与多版本并存取最高版本；
- `BundlerFormats=msi` 变体产出 Bundler MSI，验证 NSIS→MSI 目录延续、Bundler `InstallDir` 优先与范围外回落。

集成断言覆盖（每条腿一个 `[Fact]`）：

- 静默 current-user 安装/卸载、同版本原位修复、升级与默认降级拒绝、显式允许降级；
- Unicode 产品名/目录/开始菜单与多语言选择器；
- 安装器自动化协议（`/S`、`/P`、`/NS`、`/R`、`/ARGS`、`/UPDATE`、`/RECOVERONLY`）与稳定退出码（完整表见 `docs/nsis-roadmap.md` §4.9）；
- 快捷方式创建、读取、所有权保护、改名迁移与按目标而非名称的删除判定；
- 文件关联与 URL 协议注册、卸载时的所有权检查；
- 生命周期 Hook 与安装/卸载事务回滚：快照、激活、载荷恢复、注册表恢复、journal 清理各检查点的一次性故障注入，以及安装器进程树 taskkill 后的下次启动恢复；
- 卸载前向恢复：post-uninstall 失败与进程中断后保留 journal 与恢复卸载器，下次安装先幂等完成旧卸载；
- 旧 MSI 迁移：ProductCode/UpgradeCode 精确路径、`LegacyMsiAutoDetect` 命中（DisplayName+Publisher+msiexec）、名称/发布者不匹配的负例、多版本并存取最高版本并全部清理；
- NSIS→MSI 目录延续、Bundler `InstallDir` 优先级与范围外回落；
- journal 篡改（文件 `path.txt`、注册表 snapshot subkey、快照内容）在恢复前被拒绝；
- 重解析点/junction 在快照、journal、载荷与卸载/DELETEAPPDATA 路径中的安全拒绝；
- Restart Manager 只关闭安装目录内主程序，不影响另一路径同名进程；
- 锁定非主程序载荷时安全失败返回 `2`、保留 active journal，释放锁后下次启动先恢复再继续；
- commit 原子改名后清理失败不判败、`.committed` 残留由下次启动清理且不判为回滚；
- 自签名测试证书对主程序、插件、卸载器与安装器的完整签名链路（`CertificateRequest` 生成一次性代码签名证书入 `CurrentUser\My`，验证用 `Get-AuthenticodeSignature`）。

每个事实在 `finally` 中执行与固定 identifier 对齐的清理（注册表、快捷方式、应用数据、journal、legacy MSI），
删除前校验路径确在预期范围内；未知用户文件与外部拥有的快捷方式不删除。
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

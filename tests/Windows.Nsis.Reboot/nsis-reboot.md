# Windows NSIS 重启验证（tests/Windows.Nsis.Reboot）

`Verify.ps1` 是 NSIS 真实重启链路的人工/外部验证入口：对锁定文件执行卸载，验证卸载器 `3010` 退出码、`PendingFileRenameOperations` 排队与重启后清理语义。
本脚本**有意修改机器级待重命名队列**，只允许在可抛弃的 Windows VM 中运行。

## 运行（两阶段，均在提权 PowerShell 中）

```powershell
# 阶段一：准备——安装 perMachine 集成 fixture，对锁定文件卸载制造 3010
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Reboot/Verify.ps1 `
    -Phase Prepare -InstallerPath <fixture 安装器路径> -ConfirmDisposableMachine

# 重启机器

# 阶段二：复核——断言重启后队列执行、目录/注册表/事务状态符合预期
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Reboot/Verify.ps1 `
    -Phase Verify -ConfirmDisposableMachine
```

`-ConfirmDisposableMachine` 为强制门禁：脚本拒绝在非可抛弃机器上运行，并要求提权会话；
`-InstallDirectory` 必须位于 `%ProgramFiles%` 下。
阶段状态经 `%ProgramData%\DotNetBundler\reboot-validation\<Identifier>.json` 在重启前后传递。

## 断言面

- `Prepare`：安装 fixture 后对锁定文件执行卸载——断言卸载返回 `3010`、产品路径进入 `PendingFileRenameOperations`、状态文件落盘。
- `Verify`（重启后）：pending rename 已执行——产品目录不存在、事务 journal 目录已清、卸载注册表键已删、队列中无残留产品项。
- 本脚本验证的是卸载期锁定文件删除（`/REBOOTOK`）的重启清理路径；安装期"锁定载荷安全失败"策略见 `docs/nsis-locked-payload-policy.md`。

归属：NSIS 人工/外部清单的重启项；不纳入本机自动化例行验证。

# NSIS 锁定安装载荷策略

## 决策

当前 NSIS 后端对无法覆盖的锁定安装载荷采用“安全失败并恢复”，不将安装替换自动排入 Windows 的 `PendingFileRenameOperations`。

- 交互安装会提示用户关闭可能正在使用安装目录内文件的程序，然后重试。
- 静默或被动安装不显示对话框，返回稳定退出码 `2`。
- 可以即时回滚时恢复旧载荷、注册表和快捷方式。
- 如果同一文件锁也阻止回滚，保留 active journal；释放文件锁后，下次启动同一产品的安装器会先恢复旧状态。

## 不采用通用重启替换的原因

1. Windows `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)` 只能在管理员或 LocalSystem 上下文使用；
   本项目同时支持不提权的 `currentUser` 安装，因此它不能提供一致能力。
2. 调用成功只表示重命名请求已写入系统注册表，不能表示重启时的文件操作必然成功。
3. 系统按登记顺序执行共享队列中的操作。
   Win32 API 文档没有提供可供安装事务保存并按本产品撤销的专用句柄；直接重写共享注册表值会竞态破坏其他安装器的操作。
4. 一旦为旧文件和新文件排入重启操作，当前的立即回滚和下次启动恢复就不再能无条件保证重启后的最终状态。

参考：

- Microsoft `MoveFileEx`：<https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-movefileexw>
- NSIS `File`/`Rename`/`Delete`/`RMDir` 指令：<https://nsis.sourceforge.io/Docs/Chapter4.html>

## 重新考虑该决策的条件

只有同时满足以下条件时，才重新设计重启替换：

- 产品要求明确接受 `currentUser` 与提权安装的能力差异，或停止支持不提权安装。
- 重启前向恢复有独立的持久化状态机，不复用 active rollback journal 表示已提交状态。
- 能在可抛弃 Windows 虚拟机中覆盖成功、部分排队、排队失败、安装器崩溃、重启时失败、再次运行以及与其他 pending rename 项共存的情况。

这个决策不影响卸载器已有的 `/REBOOTOK` 删除。
卸载的目标是删除已不再使用的文件，不需要在重启后把新载荷与注册表、快捷方式一起提交为同一版本。
卸载使用独立的前向恢复 journal：失败或中断时保留原安装目录、删除应用数据选择和恢复卸载器，下次安装启动继续完成删除。
它不会把已经进入共享 pending rename 队列的操作伪装成可回滚事务。

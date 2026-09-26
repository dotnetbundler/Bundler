# Bundler NSIS 原生插件

此项目构建安装器运行时使用的 Unicode `win-x86` Native AOT 插件。
它使用 `NsisPlugin` 1.0.2，与 `DotNet.Bundler.Nsis` 测试共用 SemVer 源码，通过原生 Windows Installer API 查询显式配置的旧 MSI 产品，并在提权安装后以交互桌面用户的令牌启动已安装应用。

插件用已安装可执行文件的完整路径调用 Windows Restart Manager，避免终止其他目录中的同名进程。
它还通过 Shell COM API 创建、检查、更新、迁移并安全删除 Windows 快捷方式，包括 AppUserModelID 元数据。

安装事务通过同一插件快照并恢复载荷文件、产品注册表状态和快捷方式。
成功提交时，先把活动 journal 原子重命名为相邻的 `.committed` 文件，再尽力清理；
即使清理失败，也不会回滚已完成的安装，下次启动安装器会重试清理。
恢复具备重入性：如载荷、注册表或 journal 清理恢复失败，活动 journal 会保留，供下次启动继续处理。

插件还负责独立的前向卸载 journal。
它验证已注册的安装路径，以 NSIS `_?=` 直接模式启动保存的恢复卸载器，等待真实退出码，并且只在子进程进入最终完成阶段后原子提交 journal。
已删除或通过 `/REBOOTOK` 排队删除的文件不会被宣称可以回滚。
仓库集成 fixture 使用内部一次性标记测试这些边界；
这些标记只是测试钩子，不属于受支持的安装器 API。

该项目有意排除在 `Bundler.slnx` 之外：仓库已统一以 .NET 10 SDK 构建，而重建原生插件还需要 Windows 原生工具链。

```powershell
powershell -File tools/Bundler.Nsis.Plugin/Build.ps1
```

每次修改插件实现后，应同时记录重新生成的 `third_party/nsis/plugins/x86-unicode/DotNetBundlerNsis.dll` 及更新后的校验值；提交须等用户明确要求。

# Windows MSI 人工验收（计划用例）

> MSI 后端尚未实现，以下用例均未执行。阶段完成时填写实际证据；不将计划写成通过。可自动化部分必须先在 MSI 专用测试中覆盖。

只在可抛弃 VM/专用测试机运行。每轮记录 Git SHA、包版本、MSI/WiX 工具 SHA-256、OS build/架构、账户权限、MSI SHA-256、verbose log 路径、开始/结束快照和清理结果。生产私钥与密码不得写入仓库或日志。用例 ID 永不复用。

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| MSI-MT-01 | MSI-1 | 在干净 Windows x64 和 ARM64 宿主上离线运行捆绑的 WiX 3.14.1 构建；核对 Framework 预装条件、工具错误与包体积。不得手工安装 SDK 来掩盖依赖。 | 系统版本、依赖清单、工具/产物哈希、构建日志 |
| MSI-MT-02 | MSI-1 | 在干净 VM 安装最小 current user MSI，再卸载；核对文件、卸载项、用户创建文件保留及无越权删除。此项也必须有自动化 E2E。 | `msiexec /i` 与 `/x` 的 verbose log、文件和注册表快照 |
| MSI-MT-03 | MSI-2 | 标准用户与管理员分别安装 current user/per machine 独立包，观察 UAC、Program Files/HKLM 与 LocalAppData/HKCU，卸载并核对 ACL。 | UAC 截图、账户与路径/注册表/ACL 记录 |
| MSI-MT-04 | MSI-2 | 用两个真实发布版本升级，测试降级和同版本不同包；核对旧包移除、用户文件保留、Shortcut/关联/URL 所有权。 | 两包 GUID/版本/哈希、安装数据库与日志 |
| MSI-MT-05 | MSI-3 | 使用生产证书和时间戳签 payload/最终 MSI，安装前后验证 Authenticode 链；分别验证失败签名不发布伪成功包。 | 脱敏签名主体、链、时间戳、校验命令结果 |
| MSI-MT-06 | MSI-3 | 交互、`/qn`、`/passive` 安装/卸载/修复，核对 UI 语言、取消、退出码、修复源缺失提示与用户数据。 | 语言/显示设置、截图、各模式日志与退出码 |
| MSI-MT-07 | MSI-3 | 在快照 VM 制造锁定文件、空间不足、损坏 MSI 和故障注入；需要重启时实际重启后再检查产品状态。不得在开发机修改全局重启队列。 | 前后快照、退出码、verbose log、重启后文件/注册表 |
| MSI-MT-08 | MSI-4 | 在 Windows 10/11 x64、ARM64 目标与构建宿主逐格重复安装、升级、修复、卸载；核对缺失组合是否从支持声明移除。 | 每格 OS build、架构、结果、失败原因 |
| MSI-MT-09 | MSI-4 | 对支持语言进行母语/专业审校和辅助功能检查，包括缩放、键盘、屏幕阅读器；只报告实际审过的语言。 | 审校人、locale、截图、缺陷与修复记录 |

命令基线（产品代码、路径和参数从对应测试产物记录中替换，不在开发机直接运行）：

```powershell
msiexec.exe /i '<package.msi>' /qn /L*v '<install.log>'
msiexec.exe /f '<package.msi>' /qn /L*v '<repair.log>'
msiexec.exe /x '<package.msi>' /qn /L*v '<uninstall.log>'
```

每轮结束只清理本轮创建的测试产品、日志和临时目录；保留用户原有内容，确认无测试安装残留。人工结论用“通过/失败/未执行/条件不适用”，附证据，不把未执行写为通过。

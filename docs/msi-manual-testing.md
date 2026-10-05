# Windows MSI 人工验收（计划用例）

> WIN-MSI-1..9 全部完成（`.msi` 冻结基线 `0.1.0-alpha.43`）；
> alpha.40 已验证 x86 目标、显式 MSI 版本映射、同版碰撞、默认降级拒绝、显式降级和用户文件所有权，alpha.41 已验证范围内安装目录、自定义 UI 表结构、可选 Feature、PATH 精确追加/还原与仅交互启动勾选的产物和静默行为。
> 以下人工/外部环境用例均未执行——对应各阶段未覆盖的外部条件，未执行项不得宣称已完成。
> 用户目前没有干净 Windows 10/11、ARM64 原生用户端或提权测试 VM，不将本机结果扩写为这些平台通过。
> 路线见 `docs/msi-roadmap.md` 第 10 节。

`MSI-MT-02` 的自动化前置为 `MsiLocalPackagesTests`（`dotnet test tests/Bundler.LocalPackagesTests/Bundler.LocalPackagesTests.csproj -c Release --filter-class "*MsiLocalPackagesTests*"`）。
普通本机 current-user fixture 需显式传入 `-ConfirmLocalInstall`；VM 可传 `-ConfirmDisposableVm`，脚本会核查虚拟机标识。
2026-09-24 本机运行通过，日志与哈希见 `docs/msi-roadmap.md` 第 6 节；这不代替干净 VM 的兼容性复测。

`MSI-MT-04` 的本机自动前置为 `MsiLocalPackagesTests` 生命周期腿（`Requires=localinstall` trait + `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 同意闸）。
脚本构建两个版本及同版本异内容包，检查升级、降级/异包拒绝、快捷方式、文件与协议候选注册、用户文件保留和卸载清理；结果见 `docs/msi-roadmap.md` 第 7 节。
人工复测还要在 Windows 默认应用界面选择处理程序，实际打开文件/协议，观察 UAC、账户权限及同路径被其他程序接管的行为；原生 MSI 快捷方式不保证保留被替换的同路径链接。

`MSI-MT-05..07` 的本机自动前置为 `MsiLocalPackagesTests` 维护腿（同上同意闸）与 `tests/Bundler.Tests/WixTests.cs`。
前者使用独立 current-user 产品，验证损坏包的原生失败码、对测试包副本注入延迟失败后的回滚、被动安装/卸载、静默修复，以及带测试 RTF 许可的中文包与英文包并存。
后者用短期自签名测试证书真实签名 PE 与 MSI。
该脚本不触发重启、提权或生产签名；日志、哈希和本机结果见 `docs/msi-roadmap.md` 第 8 节。
人工复测仍要覆盖真实 UI、证书信任链、缺失源、锁定文件及重启。

`MSI-MT-11` 的本机自动前置为 `MsiLocalPackagesTests`（同上同意闸）与 `tests/Bundler.Tests/WixTests.cs`。
前者以随机身份真实验证静默 `INSTALLFOLDER` 范围拒绝（根目录本身与 per-machine 根均 1603）、自定义目录安装、PATH 精确追加/卸载还原、开始菜单（含卸载入口）与桌面快捷方式、ARP `InstallLocation`/`Contact`、升级恢复已选目录、静默修复与未知用户文件保留。
后者断言自定义 UI 表结构与校验动作。
该脚本不进入真实交互 UI：对话框流转（部分已由交互腿自动覆盖）、`InvalidDirDlg` 显示、勾选启动行为、位图显示、缩放/辅助功能仍需人工执行；junction 路径行为已于 2026-10-05 实证（MSI-OI-12），见下表。

下列人工用例只在可抛弃 VM/专用测试机运行。
每轮记录 Git SHA、包版本、MSI/WiX 工具 SHA-256、OS build/架构、账户权限、MSI SHA-256、verbose log 路径、开始/结束快照和清理结果。
生产私钥与密码不得写入仓库或日志。用例 ID 永不复用。

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| MSI-MT-01 | MSI-1 外部待验收，MSI-4 支持矩阵 | 在干净 Windows x86/x64 和 ARM64 宿主上，仅用解压后的 WiX 3.14.1 二进制与预备好的 `.wxs`/载荷编译；核对 Framework 预装条件和工具错误。不将调用方构建流程的依赖算作 WiX 自身依赖；不得手工安装额外 WiX/Framework 来掩盖依赖。 | 系统版本、依赖清单、工具/产物哈希、`candle`/`light` 日志 |
| MSI-MT-02 | MSI-4 兼容性复测；本机自动前置已通过 | 在干净 VM 再次安装最小 current user MSI，再卸载；核对文件、产品注册、用户创建文件保留及无越权删除。 | `msiexec /i` 与 `/x` 的 verbose log、文件和注册表快照 |
| MSI-MT-03 | MSI-2 | 标准用户与管理员分别安装 current user/per machine 独立包，观察 UAC、Program Files/HKLM 与 LocalAppData/HKCU，卸载并核对 ACL。 | UAC 截图、账户与路径/注册表/ACL 记录 |
| MSI-MT-04 | MSI-2；本机自动前置已通过，外部未执行 | 用两个真实发布版本升级，测试降级和同版本不同包；核对旧包移除、用户文件保留、Shortcut/关联/URL 注册。选择默认处理程序后实际打开文件和协议；测试同名路径被接管时 Windows Installer 的原生删除行为。 | 三包 GUID/版本/哈希、安装数据库与日志、默认应用界面截图/唤起结果 |
| MSI-MT-05 | MSI-3 | 使用生产证书和时间戳签 payload/最终 MSI，安装前后验证 Authenticode 链；分别验证失败签名不发布伪成功包。 | 脱敏签名主体、链、时间戳、校验命令结果 |
| MSI-MT-06 | MSI-3 | 交互、`/qn`、`/passive` 安装/卸载/修复，核对 UI 语言、取消、退出码、修复源缺失提示与用户数据。 | 语言/显示设置、截图、各模式日志与退出码 |
| MSI-MT-07 | MSI-3 | 在快照 VM 制造锁定文件、空间不足、损坏 MSI 和故障注入；需要重启时实际重启后再检查产品状态。不得在开发机修改全局重启队列。 | 前后快照、退出码、verbose log、重启后文件/注册表 |
| MSI-MT-08 | MSI-4 | 在 Windows 10/11 x64、ARM64 目标与构建宿主逐格重复安装、升级、修复、卸载；核对缺失组合是否从支持声明移除。 | 每格 OS build、架构、结果、失败原因 |
| MSI-MT-09 | MSI-4 | 对支持语言进行母语/专业审校和辅助功能检查，包括缩放、键盘、屏幕阅读器；只报告实际审过的语言。 | 审校人、locale、截图、缺陷与修复记录 |
| MSI-MT-10 | MSI-5 外部待验收；本机自动前置已通过 | 在干净 Windows x86/x64 构建宿主和原生 x86/ARM64 用户端，运行 x86 包的安装、升级、显式版本映射、默认降级拒绝、显式允许降级、同版碰撞、修复和卸载；观察 per-machine UAC、32 位注册表视图及用户文件保留。 | 每组合的 OS build/架构、ProductCode/UpgradeCode、MSI 哈希、verbose log、注册表视图、退出码和清理结果 |
| MSI-MT-11 | WIN-MSI-6 外部待验收；junction 子项 2026-10-05 已实证 | 双击 MSI 走完整交互流程：许可页（有/无两种包）、InstallDir 页选允许根内子目录、Browse 对话框、范围外路径触发 InvalidDirDlg；勾选与取消"启动应用"各验一次，确认 `/qn`/`/passive`/修复均不启动；位图、缩放、键盘/屏幕阅读器；per-machine UI 提权场景一并记录。~~junction 目标观察~~已实证（MSI 不解引用、透写真实目标、卸载保留 reparse point，详见 MSI-OI-12）。 | 截图/录屏、各流程退出码、是否启动应用的观察记录、verbose log |
| MSI-MT-12 | WIN-MSI-7 外部待验收；FIPS 子项 2026-10-05 已实证 | 对支持语言表中抽选的非拉丁语言（至少一种东亚与一种 RTL，如 `ja-JP`/`ar-SA`）双击交互安装：核对 WiX 内嵌译文显示、Bundler 自有串默认/覆盖渲染、快捷方式图标显示、卸载快捷方式名本地化。~~FIPS 策略宿主开/关构建~~已实证（`-fips` 透传捕获、关闭时 CNDL0308、产物 /i→/x 零残留，详见 MSI-OI-13）。 | 每语言截图/录屏、verbose log |
| MSI-MT-13 | WIN-MSI-8 外部待验收；本机自动前置已通过 | 专家模式接入真实第三方 WiX 内容（含自定义动作/扩展命名空间/merge module 的实际项目模板）时逐项人工核对：调用方逻辑的回滚/所有权语义、第三方二进制许可归属、安装失败时行为；常规模式白名单拒绝清单抽验确认无误放行。 | 模板/fragment 清单与来源、许可核对记录、构建/安装/失败场景日志与退出码 |

人工命令基线（产品代码、路径和参数从对应测试产物记录中替换；普通本机自动化使用上文脚本）：

```powershell
msiexec.exe /i '<package.msi>' /qn /L*v '<install.log>'
msiexec.exe /f '<package.msi>' /qn /L*v '<repair.log>'
msiexec.exe /x '<package.msi>' /qn /L*v '<uninstall.log>'
```

每轮结束只清理本轮创建的测试产品、日志和临时目录；保留用户原有内容，确认无测试安装残留。
人工结论用“通过/失败/未执行/条件不适用”，附证据，不把未执行写为通过。

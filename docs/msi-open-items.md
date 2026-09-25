# MSI 外部输入与验收待办

MSI 后端已完成 **WIN-MSI-1..7 当前 Windows 11 x64 本机范围**，`alpha.37` 为既有 x64/ARM64 身份基线，`alpha.40` 增加 x86、显式 MSI 版本映射和可选降级，`alpha.41` 增加范围内安装目录、自定义 UI 序列、可选 Feature、PATH 精确追加与交互启动勾选，`alpha.42` 增加 38 语言独立产物、调用方 `.wxl` 覆盖、快捷方式图标与 FIPS 构建选项。
当前开发机上的独立 fixture 已通过真实 current-user 安装/卸载、两版本生命周期、x86 版本映射/降级、修复、被动运行、中文包并存、受限故障回滚，自定义目录安装、静默范围拒绝、PATH 保留与升级恢复目录，以及 en-US/ja-JP 双语并存安装与独立卸载；per-machine 仍仅静态检查产物。
WIN-MSI-8..9 是后续功能计划，见 `docs/msi-roadmap.md` 第 10 节及 `docs/msi-tauri-capability-audit.md`；下表仍只记录外部环境和证据，不把尚未实施的功能混作环境待办。
当前没有干净 Windows 10/11、ARM64 原生用户端或提权测试 VM；这些缺口不阻塞本机可执行开发，不扩大支持声明。
人工步骤见 `docs/msi-manual-testing.md`。

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MSI-OI-01 | MSI-1 已完成本机核查 | 官方 WiX 3.14.1 归档、实际 SHA-256、全部捆绑文件许可证、体积测量 | 来源、哈希、逐文件源码归属、包内容与大小、本地无还原编译结果见 `docs/msi-roadmap.md` 第 6 节及 `third_party/wix/msi-wix-provenance.md` |
| MSI-OI-02 | MSI-1 记录，MSI-4 支持矩阵 | 干净 Windows 10/11 x86、x64 与 ARM64 宿主的 WiX 3.14.1 自身依赖；Windows 7 记录 Framework 缺失边界 | 仅用 WiX 二进制与准备好的 `.wxs`/载荷完成 `candle`/`light` 编译，记录 OS build、预装 Framework、架构、日志及失败边界；不把调用方 MSBuild/.NET SDK 算作 WiX 依赖 |
| MSI-OI-03 | MSI-1 本机已通过，MSI-4 扩展矩阵 | 独立 Windows VM/CI 复跑最小安装/卸载 | 其他宿主的 verbose log、状态/清理断言；本机烟雾结果见 `docs/msi-roadmap.md` 第 6 节 |
| MSI-OI-04 | MSI-2，外部待验收 | 标准用户和管理员账户，Windows UAC/策略差异 | current user/per machine 的权限与目录/注册表证据；本机仅已验证 per-machine 数据库表 |
| MSI-OI-05 | MSI-2，本机已通过；外部仍待复测 | 真实旧发行包或受控两版本发行包 | major upgrade、同版本异包/降级拒绝、资源归属、用户数据及默认应用实际选择/唤起证据；本机结果见 MSI 路线第 7 节 |
| MSI-OI-06 | MSI-3 本机测试签名通过；外部待验收 | 生产证书与时间戳设施 | 链、时间戳、签名验证；私钥不入库。测试证书不构成生产信任证据 |
| MSI-OI-07 | MSI-3 本机安全故障通过；外部待验收 | 可抛弃的重启/锁定文件/磁盘故障/缺源 VM | 3010 或明确失败码、重启后状态、回滚日志；本机已验证损坏包与测试包的延迟失败回滚 |
| MSI-OI-08 | MSI-4 | Windows 10/11 x64、ARM64 支持矩阵 | 每组合的构建、安装、升级、修复、卸载记录；未测组合限定支持声明 |
| MSI-OI-09 | MSI-4 | 本地化审校和辅助功能使用者 | 支持语言集的 UI、缩放和可访问性记录 |
| MSI-OI-10 | MSI-4 记录，公开发布前复核 | WiX v3 已结束免费社区维护且本项目不使用付费支持 | 每次公开发布前复核上游安全/兼容公告与风险处置；若无法接受无补丁风险，先提出免费且许可可履行的替代工具链，不默默扩大支持声明。依据：[WiX v3 官方状态](https://docs.firegiant.com/wix/wix3/) |
| MSI-OI-11 | MSI-5 本机已通过，外部待验收 | 干净 Windows x86/x64、原生 x86/ARM64 用户端及 per-machine UAC | x86/映射版本/降级/卸载逐组合 verbose log、注册表视图、OS build 和清理证据；本机 x64 宿主证据见 `VerifyWinMsi5.ps1` |
| MSI-OI-12 | WIN-MSI-6 本机自动范围已通过，外部待验收 | 真实交互 UI 环境（含辅助功能检查者）、per-machine UAC 交互安装 | 许可/无许可两序列的对话框流转、InstallDir/Browse/InvalidDir 行为、勾选与取消启动勾选的实际效果、位图显示与缩放、`ADDLOCAL` Feature 选择的实际交互、junction 安装目标的观察记录；本机自动证据见 `VerifyWinMsi6.ps1` 与 MSI-MT-11 |
| MSI-OI-13 | WIN-MSI-7 参数透传已断言，外部待验收 | 启用 Windows FIPS 策略的可抛弃宿主或 VM | `BundlerWixFipsCompliant`/`FipsCompliant` 构建在策略启用宿主的完整 `candle`/`light` 日志、产物哈希与安装结果；本机仅验证 `-fips` 参数透传，不构成认证声明 |

没有相应环境时保留待验收，不把预测写成通过；
MSI-OI-01 已通过本机工程核查，阶段二约 13.8 MB 的体积已获用户接受；
阶段三新增 UI 扩展后包大小实测 14,397,524 字节，阶段四最终 `alpha.37` 包为 14,397,760 字节、SHA-256 `8892C11C9F18958E1A7916049BC524467C7E65EF82DDC9B9C0B151666C7D9025`。
干净宿主、ARM64 和其他 Windows 版本没有实际证据时，不得扩大支持声明。

2026-09-24 阶段一历史进度：MSI-OI-01 当时已核对 14 个分发文件与上游源码/许可归属，重打包检查许可证、完整源码、声明，`--no-restore` 复跑 49 项测试并实际编译 MSI；
所用 NuGet 依赖已在本机缓存，未验证全新机器的离线项目还原。
阶段一哈希与大小见 MSI 路线第 6 节，阶段三新增 UI 文件与最终包见第 8 节。
MSI-OI-02 已静态检查 `candle.exe`、`light.exe` 和 `wix.dll` 均目标 `.NET Framework 4.5`，两个 EXE 要求 32 位进程；
当前只取得非干净 Windows 11 x64 主机编译证据，干净 Windows 10/11 与 ARM64 宿主未测，依用户说明暂不执行。
Windows 7 SP1 未预装 4.5，故不符合 WiX 自身的严格零额外安装。
MSI-OI-03 的本机自动烟雾测试已通过，独立 VM/CI 复跑尚未执行；脚本为 `tests/Windows.Msi.Integration/Verify.ps1`。

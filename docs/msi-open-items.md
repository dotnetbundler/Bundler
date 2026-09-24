# MSI 外部输入与验收待办

MSI 后端已完成 **WIN-MSI-1..3 本机范围内的退出条件**；当前 Windows x64 开发机上的独立 fixture 已通过真实 current-user 安装/卸载、两版本生命周期、修复、被动运行、中文包并存与受限故障回滚，per-machine 仅静态检查产物。下表记录仍需的外部环境和证据；用户已确认当前没有干净 Windows 10/11 或 ARM64 环境，也没有提权测试 VM，不扩大支持声明。人工步骤见 `docs/msi-manual-testing.md`。

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MSI-OI-01 | MSI-1 已完成本机核查 | 官方 WiX 3.14.1 归档、实际 SHA-256、全部捆绑文件许可证、体积测量 | 来源、哈希、逐文件源码归属、包内容与大小、本地无还原编译结果见 `docs/msi-roadmap.md` 第 6 节及 `third_party/wix/README.md` |
| MSI-OI-02 | MSI-1 记录，MSI-4 支持矩阵 | 干净 Windows 10/11 x86、x64 与 ARM64 宿主的 WiX 3.14.1 自身依赖；Windows 7 记录 Framework 缺失边界 | 仅用 WiX 二进制与准备好的 `.wxs`/载荷完成 `candle`/`light` 编译，记录 OS build、预装 Framework、架构、日志及失败边界；不把调用方 MSBuild/.NET SDK 算作 WiX 依赖 |
| MSI-OI-03 | MSI-1 本机已通过，MSI-4 扩展矩阵 | 独立 Windows VM/CI 复跑最小安装/卸载 | 其他宿主的 verbose log、状态/清理断言；本机烟雾结果见 `docs/msi-roadmap.md` 第 6 节 |
| MSI-OI-04 | MSI-2，外部待验收 | 标准用户和管理员账户，Windows UAC/策略差异 | current user/per machine 的权限与目录/注册表证据；本机仅已验证 per-machine 数据库表 |
| MSI-OI-05 | MSI-2，本机已通过；外部仍待复测 | 真实旧发行包或受控两版本发行包 | major upgrade、同版本异包/降级拒绝、资源归属、用户数据及默认应用实际选择/唤起证据；本机结果见 MSI 路线第 7 节 |
| MSI-OI-06 | MSI-3 本机测试签名通过；外部待验收 | 生产证书与时间戳设施 | 链、时间戳、签名验证；私钥不入库。测试证书不构成生产信任证据 |
| MSI-OI-07 | MSI-3 本机安全故障通过；外部待验收 | 可抛弃的重启/锁定文件/磁盘故障/缺源 VM | 3010 或明确失败码、重启后状态、回滚日志；本机已验证损坏包与测试包的延迟失败回滚 |
| MSI-OI-08 | MSI-4 | Windows 10/11 x64、ARM64 支持矩阵 | 每组合的构建、安装、升级、修复、卸载记录；未测组合限定支持声明 |
| MSI-OI-09 | MSI-4 | 本地化审校和辅助功能使用者 | 支持语言集的 UI、缩放和可访问性记录 |

没有相应环境时保留待验收，不把预测写成通过；MSI-OI-01 已通过本机工程核查，阶段二约 13.8 MB 的体积已获用户接受；阶段三新增 UI 扩展后包大小实测 14,397,524 字节。干净宿主、ARM64 和其他 Windows 版本没有实际证据时，不得扩大支持声明。

2026-09-24 阶段一历史进度：MSI-OI-01 当时已核对 14 个分发文件与上游源码/许可归属，重打包检查许可证、完整源码、声明，`--no-restore` 复跑 49 项测试并实际编译 MSI；所用 NuGet 依赖已在本机缓存，未验证全新机器的离线项目还原。阶段一哈希与大小见 MSI 路线第 6 节，阶段三新增 UI 文件与最终包见第 8 节。MSI-OI-02 已静态检查 `candle.exe`、`light.exe` 和 `wix.dll` 均目标 `.NET Framework 4.5`，两个 EXE 要求 32 位进程；当前只取得非干净 Windows 11 x64 主机编译证据，干净 Windows 10/11 与 ARM64 宿主未测，依用户说明暂不执行。Windows 7 SP1 未预装 4.5，故不符合 WiX 自身的严格零额外安装。MSI-OI-03 的本机自动烟雾测试已通过，独立 VM/CI 复跑尚未执行；脚本为 `tests/Windows.Msi.Integration/Verify.ps1`。

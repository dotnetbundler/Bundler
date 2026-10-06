# MSI 外部输入与验收待办

MSI 后端已完成 **WIN-MSI-1..9 当前 Windows 11 x64 本机范围**（`0.1.0-alpha.43` 为冻结基线），`alpha.37` 为既有 x64/ARM64 身份基线，`alpha.40` 增加 x86、显式 MSI 版本映射和可选降级，`alpha.41` 增加范围内安装目录、自定义 UI 序列、可选 Feature、PATH 精确追加与交互启动勾选，`alpha.42` 增加 38 语言独立产物、调用方 `.wxl` 覆盖、快捷方式图标与 FIPS 构建选项，`alpha.43` 增加受控 WiX 扩展与专家模式。
当前开发机上的独立 fixture 已通过真实 current-user 安装/卸载、两版本生命周期、x86 版本映射/降级、修复、被动运行、中文包并存、受限故障回滚，自定义目录安装、静默范围拒绝、PATH 保留与升级恢复目录，以及 en-US/ja-JP 双语并存安装与独立卸载；per-machine 仍仅静态检查产物。
WIN-MSI-9 已完成 Tauri 通用能力审计与再冻结，见 `docs/msi-roadmap.md` 第 10 节；下表仍只记录外部环境和证据。
当前没有干净 Windows 10/11、ARM64 原生用户端或提权测试 VM；这些缺口不阻塞本机可执行开发，不扩大支持声明。
人工步骤见 `docs/msi-manual-testing.md`。

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MSI-OI-01 | MSI-1 已完成本机核查 | 官方 WiX 3.14.1 归档、实际 SHA-256、全部捆绑文件许可证、体积测量 | 来源、哈希、逐文件源码归属、包内容与大小、本地无还原编译结果见 `docs/msi-roadmap.md` 第 6 节及 `third_party/wix/msi-wix-provenance.md` |
| MSI-OI-02 | MSI-1 记录，MSI-4 支持矩阵 | 干净 Windows 10/11 x86、x64 与 ARM64 宿主的 WiX 3.14.1 自身依赖；Windows 7 记录 Framework 缺失边界 | 仅用 WiX 二进制与准备好的 `.wxs`/载荷完成 `candle`/`light` 编译，记录 OS build、预装 Framework、架构、日志及失败边界；不把调用方 MSBuild/.NET SDK 算作 WiX 依赖 |
| MSI-OI-03 | MSI-1 本机已通过，MSI-4 扩展矩阵 | 独立 Windows VM/CI 复跑最小安装/卸载 | 其他宿主的 verbose log、状态/清理断言；本机烟雾结果见 `docs/msi-roadmap.md` 第 6 节 |
| MSI-OI-04 | MSI-2，外部待验收 | 标准用户和管理员账户，Windows UAC/策略差异 | current user/per machine 的权限与目录/注册表证据；本机仅已验证 per-machine 数据库表 |
| MSI-OI-05 | MSI-2，本机已通过；外部仍待复测 | 真实旧发行包或受控两版本发行包 | major upgrade、同版本异包/降级拒绝、资源归属、用户数据及默认应用实际选择/唤起证据；本机结果见 MSI 路线第 7 节 |
| MSI-OI-06 | MSI-3 本机测试签名通过；外部待验收 | 生产证书与时间戳设施 | 链、时间戳、签名验证；私钥不入库。测试证书不构成生产信任证据。**时间戳子项已消解**：2026-10-06 win-x64 宿主自签证书+`signtool /tr http://timestamp.digicert.com /td sha256` 真实嵌入 RFC 3161 副签名（TimeStamperCertificate=DigiCert 颁发，对照/负例正确，证据见 `docs/paid-credentials-open-items.md` §二）；剩余仅生产 CA 信任链子项 |
| MSI-OI-07 | MSI-3 本机安全故障通过；外部待验收 | 可抛弃的重启/锁定文件/磁盘故障/缺源 VM | 3010 或明确失败码、重启后状态、回滚日志；本机已验证损坏包与测试包的延迟失败回滚 |
| MSI-OI-08 | MSI-4 | Windows 10/11 x64、ARM64 支持矩阵 | 每组合的构建、安装、升级、修复、卸载记录；未测组合限定支持声明 |
| MSI-OI-09 | MSI-4 | 本地化审校和辅助功能使用者 | 支持语言集的 UI、缩放和可访问性记录 |
| MSI-OI-10 | MSI-4 记录，**2026-10-05 用户裁决封存**：v3 维持现状不再复议 | WiX v3 已结束免费社区维护且本项目不使用付费支持 | 裁决记录：v4/v5 需 .NET 8+ 运行时（Windows 预载只有 .NET Framework，自包含体积不合"工具随包"定位）、纯托管写出器工程量过大——替代方案已否决，不再提；与 Tauri/electron-builder 同姿态捆绑 v3。若上游出具体安全/兼容事故由用户另行指示，本项不作主动复核。依据：[WiX v3 官方状态](https://docs.firegiant.com/wix/wix3/) |
| MSI-OI-11 | MSI-5 本机已通过，外部待验收 | 干净 Windows x86/x64、原生 x86/ARM64 用户端及 per-machine UAC | x86/映射版本/降级/卸载逐组合 verbose log、注册表视图、OS build 和清理证据；本机 x64 宿主证据见 `MsiLocalPackagesTests` 的 x86/版本/降级腿 |
| MSI-OI-12 | WIN-MSI-6 本机自动范围已通过，junction 子项 2026-10-05 实证，其余外部待验收 | 真实交互 UI 环境（含辅助功能检查者）、per-machine UAC 交互安装 | junction 子项已消解：2026-10-05 win-x64 实证（r31）——MSI 不规范化重解析点（INSTALLFOLDER/Dir target/ARPINSTALLLOCATION 记 junction 路径原样），写入透到真实目标（20 文件双侧可见），junction 完好时 /x 清真实目标+ARP 注销但保留 reparse point 本身，ARP 落 HKLM（currentUser 亦然），孤立情形（junction+目标删而注册残留）下同 ProductCode /i 变 no-op resume（InstallFiles 过但 0 文件重铺）/x 仅清注册；交互腿 `MsiLocalPackagesTests.InteractiveWizardInstallsAndRemoves` 已覆盖许可/InstallDir/Install/Finish 流转；**仍待验收**：Browse 对话框、范围外路径触发 `InvalidDirDlg`、`ADDLOCAL` Feature 选择的实际交互、勾选与取消启动勾选效果、位图显示、缩放/辅助功能检查者、per-machine UAC |
| MSI-OI-13 | WIN-MSI-7 | ~~启用 Windows FIPS 策略的可抛弃宿主或 VM~~ 已验证 | **已消解**：2026-10-05 win-x64 r31——`FipsAlgorithmPolicy.Enabled` 0→1 读回确认；Security 4688 进程审计捕获 candle.exe 真实命令行含 `-fips`（light 无此开关）；`=false` 对照在策略下 candle exit 308 + **CNDL0308**（明确要求 -fips 或禁用策略）证实旋钮必要性；FIPS 产物在策略下 /i exit=0+载荷+ARP→/x exit=0 零残留。仍不构成 FIPS 认证声明，仅证参数透传与策略下可构建可安装。 |

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
MSI-OI-03 的本机自动烟雾测试已通过，独立 VM/CI 复跑尚未执行；测试为 `MsiLocalPackagesTests`。

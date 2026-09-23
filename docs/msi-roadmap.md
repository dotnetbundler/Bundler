# Windows MSI 后端实施路线（WiX 3.14.1 暂定）

> 状态：规划已确认，尚未开始 `WIN-MSI-1`；本文所有 MSI 能力均为计划，不能据此宣称已实现或已人工验收。
> 规范入口：`docs/roadmap.md`；逐项能力见 `docs/msi-capability-matrix.md`，外部条件见 `docs/msi-open-items.md`，人工步骤见 `docs/msi-manual-testing.md`。

## 1. 已核实事实、选择及风险

2026-09-23 核对分支 `codex/modular-bundler-backends`、HEAD `11dbde55e38fc880c5699caa5b7f0b6e513d569d`、包版本 `0.1.0-alpha.33`、干净工作区。代码有 `PackageFormat.Msi`、Core 规划与 `IBundleBackend`，但没有 MSI 后端、MSI MSBuild 映射或 MSI E2E。`tests/Windows.Nsis.Integration/LegacyMsiFixture` 的 `WixToolset.Sdk/5.0.2` 只生成 NSIS 迁移测试所需旧 MSI，不构成正式选型。以上是本次规划时的快照，执行前必须重核。

用户已暂定 **WiX 3.14.1**，仅在 Windows 上构建 MSI；不要求非 Windows 宿主生成 MSI。选它是为了复用成熟的 WiX 3 工具链和 Windows Installer 原生语义，缩短实现工作量。这是产品选择，不等于已验证零环境、ARM64 宿主或未来安全维护。WiX 3 已归档且免费社区维护结束；因此在 WIN-MSI-1 设置实际分发门槛，后续每次冻结前复核风险。若门槛失败，停止该工具选型并记录替代决策，不通过静默安装 SDK/运行时、附带未核许可文件或声称不受支持的宿主来绕过。

产品只打包调用方已准备的文件。不内建 WebView2、VC Runtime、.NET Runtime 等任意应用运行时依赖的发现、下载或安装。MSBuild 是当前入口；直接 API 和未来 CLI 必须复用同一个 Core 与 MSI 后端。CLI 在路线所列打包格式完成后再做。能力对齐以通用桌面用户需求为准，不复制 Tauri 或 NSIS 的内部实现；尤其不在 MSI 上复刻 NSIS journal 或强行使用任意脚本自定义操作。

## 2. WiX 工具供应与发布门槛

| 项目 | 决策与 WIN-MSI-1 验证 |
| --- | --- |
| 来源/版本 | 只从 WiX 官方 `wix3141rtm` release 取得官方二进制归档；固定精确版本及文件清单。不得把现有 WiX 5 fixture 当作后端工具。 |
| 许可证 | WiX 3.14.1 上游 `LICENSE.TXT` 为 Microsoft Reciprocal License；其条款要求对分发的含 WiX 代码文件向接收者提供对应源码，并保留原有版权、专利、商标及归属声明。须逐项核对实际捆绑文件及第三方依赖的许可，设计可履行的对应源码提供方式、许可证/声明随包方式及体积成本；审计未通过不得发布。不宣称法律保证，不使用需要付费授权的工具/服务。 |
| 完整性 | 在可信来源取得一次归档，人工核对来源和实际 SHA-256；将归档哈希、内文件哈希与来源记录固定在仓库。下载或核验失败则不发布。不在运行时下载。 |
| 分发/缓存 | 测量完整归档、真正必需子集、对应源码提供方式及压缩后 NuGet 增量；只捆绑经许可且可独立工作的最小子集。复用 Core 内容寻址、跨进程锁和逐文件校验缓存；断网构建必须可用。先量化体积再决定是否达到产品可接受阈值，不预设“约 40 MB”就是最终包增量。 |
| 宿主依赖 | WiX 3 编译工具依赖 .NET Framework，MSI 最终用户不因此需要 Framework。精确最低 Framework 4.x 版本尚未从发行二进制实测，不能猜测。在干净 Windows 10/11 x64、Windows ARM64 虚拟机核对工具启动、编译、签名工具可用性；记录预装组件、x86 仿真及失败信息。所谓“零环境”仅指无需额外手工安装 WiX/.NET SDK，若 OS 本身缺必要 Framework 则明确报错并给出受支持基线，不自动安装。 |
| 生命周期 | WiX 3.14.1 归档、免费社区支持已结束；发布时记录已知风险、Windows 更新兼容性和后续迁移方案。任何紧急漏洞或目标宿主失败可触发重新选型。 |

官方来源：[WiX 3.14.1 release](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[WiX 3.14.1 许可证](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)、[WiX 3 维护状态](https://docs.firegiant.com/wix/wix3/)、[WiX 3 工具入门](https://docs.firegiant.com/wix3/tutorial/)。源代码有 ARM64 目标编译选项，但不证明 ARM64 构建宿主可用：[candle 源码](https://github.com/wixtoolset/wix3/blob/wix3141rtm/src/tools/candle/candle.cs)。

**尚待实测后决定**：WiX 必需子集加上 MS-RL 对应源码提供方式的总分发体积、精确 Framework/ARM64 宿主条件，以及其对“尽量多设备、零额外付费”的实际影响。用户尚未给出 NuGet 体积上限；先测量并报告数据，再请用户决定是否接受，不能自行编造阈值或把暂定选型视为不可改变。若免费无补丁状态无法满足届时安全要求，应重新评估工具链。

## 3. 第一阶段前必须固定的 MSI 语义

这些是计划契约，WIN-MSI-1 首次写代码前以 Windows Installer 官方规则和可执行测试核实，并在公开 API 中固定。若实测推翻，应先改本文与能力矩阵，不生成带错误身份的发行包。

1. **身份**：应用 `Identifier` 是产品族输入；同一产品的安装范围与目标架构形成独立 MSI 产品线。建议默认使用项目固定命名空间的 UUIDv5：由规范化 `Identifier + scope + architecture` 推导稳定 `UpgradeCode`，由该产品线及映射后的三段 MSI 版本和 `ProductLanguage` 推导 `ProductCode`。每次 major upgrade 改 ProductCode，同一版同语言拒绝不同载荷重发；每次内容不同的 MSI 包生成新 `PackageCode`。允许显式指定既有 UpgradeCode 仅用于有证据的历史产品迁移，必须检查 GUID 冲突与所有权，不能靠显示名猜测。固定命名空间、字符串规范化及大小写规则在首个发布包前冻结并写入测试向量；不得依赖每次随机生成的 UpgradeCode 造成升级断链。
2. **版本**：MSI `ProductVersion` 只比较前三个数字字段。首版只接受可精确映射的稳定 `major.minor.patch`；含第四段、预发布或 build metadata 的版本明确拒绝，不默默截断。超过 MSI 字段上限也拒绝。稳定版本须单调增加，测试边界值、溢出与同版本不同载荷。升级默认 major upgrade，阻止降级；同版本重打包不作为升级路径。若以后确需预发布 MSI，先设计独立、单调且无碰撞的 MSI 版本映射再改公开契约。
3. **安装范围**：先提供明确的 current user 和 per machine **两个独立产物配置**，默认 current user；不承诺同一 MSI 在安装时自由切换范围。前者只写用户可拥有的位置与 HKCU，不请求提权；后者写 Program Files/HKLM 并遵循 UAC。跨范围升级不是自动迁移。x64、ARM64 各自独立产物及身份；现有公共目标模型无 x86，暂不宣称 x86 目标支持，若真实需求要求 x86 须另改公共模型和验证矩阵。对仍受支持的 Windows 版本、x64 与 ARM64 宿主的实际支持以验证矩阵为准。
4. **目录/组件**：默认安装根分别为 `%LOCALAPPDATA%\Programs\<产品目录>` 与对应架构的 Program Files。可配置目录必须通过范围、绝对路径、重解析点与所有权校验；不以用户给出的不安全路径提升卸载删除范围。每个文件或资源组件有稳定 GUID、稳定 key path、明确安装目录和归属；改名、移动、共享组件或改变 key path 必须遵守 Windows Installer 组件规则。卸载只移除本产品拥有的资源，默认保留用户数据和未知文件。
5. **事务**：使用 Windows Installer 原生安装/回滚/修复/卸载机制。优先声明式 WiX 表与标准动作；自定义操作仅用于确有必要且无原生替代的能力，必须写明执行时机、提权上下文、回滚/幂等/卸载所有权及测试。不得用自定义操作下载依赖、任意递归清理目录或覆盖系统共享状态。损坏包、缺少源、锁定文件、重启与修复按 MSI 原生结果定义，不套用 NSIS 退出码。

依据：[ProductCode](https://learn.microsoft.com/en-us/windows/win32/msi/productcode)、[Major upgrades](https://learn.microsoft.com/en-us/windows/win32/msi/major-upgrades)、[为未来 major upgrade 准备](https://learn.microsoft.com/en-us/windows/win32/msi/preparing-an-application-for-future-major-upgrades/)、[安装上下文](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context)、[组件规则](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-components)、[安装包结构](https://learn.microsoft.com/en-us/windows/win32/msi/installation-package)。

## 4. 阶段计划

所有阶段均需对**每项新增或修改功能**增加自动化测试；直接 API、MSBuild、产物数据库与真实 Windows 行为分别验证。真实安装测试仅在专用 VM/CI 沙箱内运行，并在 `finally` 清理测试产品，不能安装到开发机的真实产品位置。人工测试不能替代可自动化测试。每阶段结束更新 `PROJECT_CONTEXT.md`、能力矩阵、外部待办、示例和中文文档，记录实际命令、环境及结果。

### WIN-MSI-1：可用的最小 MSI

- **前置**：完成第 2、3 节的工具/身份决策；有干净 Windows x64 VM 或隔离 CI；核对当前 Git 与公共模型。
- **目标/交付**：独立 MSI 后端和直接 API、MSBuild 映射，文件目录、主程序、名称/发布者/版本、范围 current user、默认安装目录、稳定身份、`.msi` 产物契约；WiX 3.14.1 固定归档、许可、SHA-256、最小子集、共享缓存和断网构建。第一阶段立即验证真实安装**和卸载**，包括产物哈希、文件、卸载注册、清理所有权。
- **新增自动化**：输入校验、身份/版本边界、组件 GUID 稳定性、路径拒绝、工具缓存篡改/并发、WiX 命令调用及错误、MSBuild/API 同一 Core 结果、MSI 数据库表/ICE。Windows x64 集成测试从新 VM 创建包，`msiexec /i` 静默安装、检查载荷与注册、`msiexec /x` 静默卸载及保留未知文件；失败时收集 verbose log。测试不能仅检查 WiX XML。
- **人工边界**：首次检查干净宿主 Framework 条件、包体积与许可证清单；正常桌面交互与 UAC 外观可人工记录。专用 VM 结果是阶段必需证据，不得推到 WIN-MSI-4。
- **不做**：升级、per machine、快捷方式、关联/协议、语言、签名、CLI、任意运行时安装。
- **退出**：工具分发门槛通过、所有新增测试和相关回归通过、真实安装与卸载烟雾测试通过、未支持项明确报错；无自动测试环境时阶段不得标完成。

### WIN-MSI-2：生命周期与桌面集成

- **前置**：WIN-MSI-1 真实烟雾测试通过，身份契约冻结，有两版本测试产物与提权 VM。
- **目标/交付**：major upgrade、阻止降级与同版本不同包、per machine 独立产物、范围/架构所有权、快捷方式、文件关联与 URL 协议的声明式注册和卸载；保留用户数据及他人接管资源。
- **新增自动化**：版本/身份矩阵、Upgrade 表、组件稳定性、配置冲突和路径保护、快捷方式与注册表表数据。专用 Windows VM 上实际从 v1→v2 升级、降级被拒、两范围安装/卸载、快捷方式和关联/协议拥有权；分别检验 HKCU/HKLM、UAC、旧载荷与用户文件。
- **人工边界**：真实标准用户与管理员、策略限制、文件关联默认应用提示、Windows 版本/架构差异见 MSI 人工文档。
- **不做**：跨 current user/per machine 自动迁移、用显示名清理旧产品、任意删除应用数据、签名/多语言、CLI。
- **退出**：无孤儿组件或越权删除，升级/卸载可复现、可清理；新增行为对应测试通过，专用 VM E2E 通过。

### WIN-MSI-3：发布与维护行为

- **前置**：WIN-MSI-2 生命周期稳定；有测试证书、隔离故障注入 VM。
- **目标/交付**：复用 Windows 签名组件签 payload 与最终 MSI；语言资源和 MSI UI 的明确支持集；安静/被动安装、卸载、修复/维护模式与原生退出码；失败回滚、锁定文件和重启策略。
- **新增自动化**：签名顺序与失败后无伪成功产物、每种语言资源及非法 locale、`/qn`/`/passive`/修复参数与返回码契约。Windows VM 实测签名验真、静默/被动安装与卸载、`msiexec /f` 修复、升级失败注入后的文件/注册表/产品状态、损坏包或缺源、锁定文件与 `3010` 情形；检查详细日志。任何新自定义操作都需故障/回滚用例。
- **人工边界**：生产证书/时间戳、真实重启、UI 多语言与辅助技术、企业策略见 MSI 人工文档。
- **不做**：Bundler 自定义退出码覆盖 Windows Installer、生产私钥入库、在线下载运行时、CLI。
- **退出**：声明的静默、维护、签名及失败语义在测试 VM 可复现；未覆盖平台标为待验收，不写成成功。

### WIN-MSI-4：完整矩阵与格式冻结

- **前置**：WIN-MSI-1..3 的功能与 E2E 通过；有独立 Windows 10/11 x64 与 ARM64 宿主/目标验证计划。
- **目标/交付**：整理全部能力矩阵、用户文档、示例、许可证/供应链审计、体积测量、Windows 版本与架构矩阵、真实升级/卸载/修复/失败记录，冻结 MSI 配置和行为。仅修复验证发现的问题且每个修复附回归测试；不在冻结阶段新增功能。
- **新增自动化**：补齐矩阵缺口，执行所有 MSI 单元/契约、Pack/API 消费、离线构建、真实 Windows 安装/卸载/升级/回滚集成；扫描测试残留、签名/哈希及 ICE 警告。
- **人工边界**：依 `MSI-MT-*` 在有条件的平台验证生产证书、UAC、真实重启、Windows 10/11/ARM64、辅助功能等，记录 OS build、架构、SHA-256、日志和清理。未取得证据的组合必须保留“外部待验收”并限制支持声明。
- **不做**：CLI、macOS/Linux、无证据扩大支持矩阵、把人工未验收项标为已通过。
- **退出**：全部承诺能力及适用自动化门槛通过，残余外部项逐条记录责任和支持边界；`docs/msi-capability-matrix.md` 冻结，并更新总路线的下一格式。

## 5. 验证分层与交接

| 验证层 | 内容 |
| --- | --- |
| 本地自动 | 输入/映射/模板、WiX 工具哈希与缓存、数据库表、身份/版本/组件规则、测试证书签名、离线编译；每次功能变更增加对应测试。 |
| 专用 Windows CI/VM | 第一阶段真实安装和卸载，此后升级、per machine/UAC、静默/被动、修复、故障回滚、损坏包、锁定文件与重启、不同 OS/架构；保留 verbose log 和状态断言。 |
| 人工验收 | 生产证书和信任链、真实用户 UAC、交互 UI/语言、企业策略、真实升级来源、真实重启及难以自动覆盖的 ARM64/多 Windows 版本；按独立 MSI 用例执行。 |

接班者先读 `PROJECT_CONTEXT.md`、`docs/roadmap.md`、本文、MSI 矩阵/待办/人工文档，再核 Git、代码与测试。**只有用户明确说“开始 WIN-MSI-1”才进入实现**。开始时先报告实际状态和工具选型门槛，完成整个阶段的新增测试与真实安装/卸载烟雾测试后再报告结果。未经用户明确要求不提交或推送。

# Windows MSI 后端实施路线（WiX 3.14.1 暂定）

> 状态：`WIN-MSI-1` 已完成本机范围内的退出条件（2026-09-24）；本机专用 fixture 的真实 current-user 安装/卸载、工具供应审计和本地无还原编译均已通过。干净 Windows/ARM64 宿主未验收，不在支持声明内。`WIN-MSI-2..4` 均未开始。
> 规范入口：`docs/roadmap.md`；逐项能力见 `docs/msi-capability-matrix.md`，外部条件见 `docs/msi-open-items.md`，人工步骤见 `docs/msi-manual-testing.md`。

## 1. 已核实事实、选择及风险

2026-09-23 规划时核对分支 `codex/modular-bundler-backends`、HEAD `11dbde55e38fc880c5699caa5b7f0b6e513d569d`、包版本 `0.1.0-alpha.33`、干净工作区。当时代码有 `PackageFormat.Msi`、Core 规划与 `IBundleBackend`，但没有 MSI 后端、MSI MSBuild 映射或 MSI E2E。`tests/Windows.Nsis.Integration/LegacyMsiFixture` 的 `WixToolset.Sdk/5.0.2` 只生成 NSIS 迁移测试所需旧 MSI，不构成正式选型。该段是规划时的历史快照，当前进度以第 6 节及实际 Git/测试为准。

用户已暂定 **WiX 3.14.1**，仅在 Windows 上构建 MSI；不要求非 Windows 宿主生成 MSI。选它是为了复用成熟的 WiX 3 工具链和 Windows Installer 原生语义，缩短实现工作量。这是产品选择，不等于已验证零环境、ARM64 宿主或未来安全维护。WiX 3 已归档且免费社区维护结束；因此在 WIN-MSI-1 设置实际分发门槛，后续每次冻结前复核风险。若门槛失败，停止该工具选型并记录替代决策，不通过静默安装 SDK/运行时、附带未核许可文件或声称不受支持的宿主来绕过。

产品只打包调用方已准备的文件。不内建 WebView2、VC Runtime、.NET Runtime 等任意应用运行时依赖的发现、下载或安装。MSBuild 是当前入口；直接 API 和未来 CLI 必须复用同一个 Core 与 MSI 后端。CLI 在路线所列打包格式完成后再做。能力对齐以通用桌面用户需求为准，不复制 Tauri 或 NSIS 的内部实现；尤其不在 MSI 上复刻 NSIS journal 或强行使用任意脚本自定义操作。

## 2. WiX 工具供应与发布门槛

| 项目 | 决策与 WIN-MSI-1 验证 |
| --- | --- |
| 来源/版本 | 只从 WiX 官方 `wix3141rtm` release 取得官方二进制归档；固定精确版本及文件清单。不得把现有 WiX 5 fixture 当作后端工具。 |
| 许可证 | WiX 3.14.1 上游 `LICENSE.TXT` 为 Microsoft Reciprocal License；其条款要求对分发的含 WiX 代码文件向接收者提供对应源码，并保留原有版权、专利、商标及归属声明。须逐项核对实际捆绑文件及第三方依赖的许可，设计可履行的对应源码提供方式、许可证/声明随包方式及体积成本；审计未通过不得发布。不宣称法律保证，不使用需要付费授权的工具/服务。 |
| 完整性 | 在可信来源取得一次归档，人工核对来源和实际 SHA-256；将归档哈希、内文件哈希与来源记录固定在仓库。下载或核验失败则不发布。不在运行时下载。 |
| 分发/缓存 | 测量完整归档、真正必需子集、对应源码提供方式及压缩后 NuGet 增量；只捆绑经许可且可独立工作的最小子集。复用 Core 内容寻址、跨进程锁和逐文件校验缓存；断网构建必须可用。当前约 13.8 MB 的 WiX NuGet 包体积已获用户接受；若分发内容或体积明显变化，重新测量并记录。 |
| 宿主依赖 | 本项只评价 WiX 3.14.1 的 `candle.exe`/`light.exe`，不计入调用 WiX 的产品或构建流程的依赖。发行二进制中两个 EXE 和 `wix.dll` 的目标属性均测得 `.NET Framework 4.5`；两个 EXE 的 CLI 标志要求 32 位进程。WiX 的该 Framework 需求不因生成 MSI 而传给最终用户。在 Windows x64、ARM64 干净 VM 核对工具启动、编译及 x86 仿真，记录 OS build 和预装依赖。若 OS 缺少必需 Framework，则不能称为零额外安装，不自动安装。 |
| 生命周期 | WiX 3.14.1 归档、免费社区支持已结束；发布时记录已知风险、Windows 更新兼容性和后续迁移方案。任何紧急漏洞或目标宿主失败可触发重新选型。 |

官方来源：[WiX 3.14.1 release](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[WiX 3.14.1 许可证](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)、[WiX 3 维护状态](https://docs.firegiant.com/wix/wix3/)、[WiX 3 工具入门](https://docs.firegiant.com/wix3/tutorial/)。源代码有 ARM64 目标编译选项，但不证明 ARM64 构建宿主可用：[candle 源码](https://github.com/wixtoolset/wix3/blob/wix3141rtm/src/tools/candle/candle.cs)。

**已决定/尚待验收**：用户在 2026-09-24 接受当前约 13.8 MB 的 WiX NuGet 包体积。所选 14 个分发文件的源码/许可工程审计见第 6 节及 `third_party/wix/README.md`；更换文件必须重审。当前 WiX 文档所述 Open Source Maintenance Fee 从 WiX v6 才开始，不能套用到 WiX 3.14.1：[WiX 版本说明](https://docs.firegiant.com/wix/whatsnew/)。`candle.exe`、`light.exe` 和 `wix.dll` 的目标框架属性均为 `.NETFramework,Version=v4.5`，但干净 Windows/ARM64 宿主的启动条件仍需 VM 验证。若免费无补丁状态无法满足届时安全要求，应重新评估工具链。

### WiX 3.14.1 自身的“零环境”边界（2026-09-24）

这里只评价将官方 WiX 3.14.1 二进制解压后，在全新激活的 Windows 上直接运行 `candle.exe` 和 `light.exe` 编译 WiX 输入文件是否需要额外安装组件。**不把调用者的 MSBuild、.NET SDK、应用构建流程或被打包程序依赖算作 WiX 依赖。**二进制归档无需运行 WiX 安装程序。当前随包两个 EXE 及 `wix.dll` 的程序集元数据均标记 `.NET Framework 4.5`；两个 EXE 的 CLR 标志均含 `32BITREQUIRED`。这是本地静态检查，不代替干净系统实测。

| 全新 Windows 构建机 | 只看 WiX 工具自身 | 证据与待验证项 |
| --- | --- | --- |
| Windows 7 SP1 | 系统未预装 .NET Framework 4.5；解压 WiX 二进制仍需另外安装兼容的 Framework，故不满足严格零额外安装。 | 微软系统预装表；尚未在干净 Windows 7 实测。 |
| Windows 10 | 系统预装 .NET Framework 4.6 或后续版本，满足已检查到的 4.5 目标框架条件；理论上可解压后直接运行 WiX，不必安装 .NET SDK 或 WiX 安装程序。 | 这是由二进制元数据和系统预装表作出的推断；须在不同版本的干净 x86/x64 VM 实测 `candle` 与 `light` 完整编译。 |
| Windows 11 | 系统预装 .NET Framework 4.8/4.8.1，同样满足已检查到的 4.5 目标框架条件；理论上可解压后直接运行。 | 已在一台非干净 Windows 11 x64 开发机运行；干净 x64 VM 与 ARM64 上的 x86 仿真和完整编译尚未验证。 |

这张表只回答 WiX 编译器自身的运行条件，不推出任何调用 WiX 的打包产品在相同机器上的可用性。来源：[WiX 3.14.1 免安装二进制归档](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[微软 .NET Framework 系统要求与预装版本](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)、[.NET Framework 版本与 Windows 对应关系](https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies)。

## 3. 第一阶段前必须固定的 MSI 语义

这些是计划契约，WIN-MSI-1 首次写代码前以 Windows Installer 官方规则和可执行测试核实，并在公开 API 中固定。若实测推翻，应先改本文与能力矩阵，不生成带错误身份的发行包。

1. **身份**：应用 `Identifier` 是产品族输入；同一产品的安装范围与目标架构形成独立 MSI 产品线。建议默认使用项目固定命名空间的 UUIDv5：由规范化 `Identifier + scope + architecture` 推导稳定 `UpgradeCode`，由该产品线及映射后的三段 MSI 版本和 `ProductLanguage` 推导 `ProductCode`。每次 major upgrade 改 ProductCode，同一版同语言拒绝不同载荷重发；每次内容不同的 MSI 包生成新 `PackageCode`。允许显式指定既有 UpgradeCode 仅用于有证据的历史产品迁移，必须检查 GUID 冲突与所有权，不能靠显示名猜测。固定命名空间、字符串规范化及大小写规则在首个发布包前冻结并写入测试向量；不得依赖每次随机生成的 UpgradeCode 造成升级断链。
2. **版本**：MSI `ProductVersion` 只比较前三个数字字段。首版只接受可精确映射的稳定 `major.minor.patch`；含第四段、预发布或 build metadata 的版本明确拒绝，不默默截断。超过 MSI 字段上限也拒绝。稳定版本须单调增加，测试边界值、溢出与同版本不同载荷。升级默认 major upgrade，阻止降级；同版本重打包不作为升级路径。若以后确需预发布 MSI，先设计独立、单调且无碰撞的 MSI 版本映射再改公开契约。
3. **安装范围**：先提供明确的 current user 和 per machine **两个独立产物配置**，默认 current user；不承诺同一 MSI 在安装时自由切换范围。前者只写用户可拥有的位置与 HKCU，不请求提权；后者写 Program Files/HKLM 并遵循 UAC。跨范围升级不是自动迁移。x64、ARM64 各自独立产物及身份；现有公共目标模型无 x86，暂不宣称 x86 目标支持，若真实需求要求 x86 须另改公共模型和验证矩阵。对仍受支持的 Windows 版本、x64 与 ARM64 宿主的实际支持以验证矩阵为准。
4. **目录/组件**：默认安装根分别为 `%LOCALAPPDATA%\Programs\<产品目录>` 与对应架构的 Program Files。可配置目录必须通过范围、绝对路径、重解析点与所有权校验；不以用户给出的不安全路径提升卸载删除范围。每个文件或资源组件有稳定 GUID、稳定 key path、明确安装目录和归属；改名、移动、共享组件或改变 key path 必须遵守 Windows Installer 组件规则。卸载只移除本产品拥有的资源，默认保留用户数据和未知文件。
5. **事务**：使用 Windows Installer 原生安装/回滚/修复/卸载机制。优先声明式 WiX 表与标准动作；自定义操作仅用于确有必要且无原生替代的能力，必须写明执行时机、提权上下文、回滚/幂等/卸载所有权及测试。不得用自定义操作下载依赖、任意递归清理目录或覆盖系统共享状态。损坏包、缺少源、锁定文件、重启与修复按 MSI 原生结果定义，不套用 NSIS 退出码。

依据：[ProductCode](https://learn.microsoft.com/en-us/windows/win32/msi/productcode)、[Major upgrades](https://learn.microsoft.com/en-us/windows/win32/msi/major-upgrades)、[为未来 major upgrade 准备](https://learn.microsoft.com/en-us/windows/win32/msi/preparing-an-application-for-future-major-upgrades/)、[安装上下文](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context)、[组件规则](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-components)、[安装包结构](https://learn.microsoft.com/en-us/windows/win32/msi/installation-package)。

## 4. 阶段计划

所有阶段均需对**每项新增或修改功能**增加自动化测试；直接 API、MSBuild、产物数据库与真实 Windows 行为分别验证。与 NSIS 一致，普通 current-user 集成测试使用每轮独立身份和安装目录，可在本地 Windows 自动运行；执行前检查产品未注册，`finally` 按 ProductCode 清理，仅删除本轮创建的文件，并保留日志。对当前机器能安全执行的安装、卸载、后续升级和资源所有权行为，要持续扩充真实 Windows 集成断言，不能只检查 WiX XML、数据库或复用旧测试。真实重启、UAC/per-machine、全局系统队列、ACL/磁盘故障等高影响场景只在可抛弃 VM/专用测试机执行；没有该环境时写入 MSI 专用人工/外部清单和支持边界，**不阻塞快速开发进入下一阶段**，也不把它们冒充已通过。人工测试不能替代当前环境可自动化的测试。每阶段结束更新 `PROJECT_CONTEXT.md`、能力矩阵、外部待办、示例和中文文档，记录实际命令、环境及结果。

### WIN-MSI-1：可用的最小 MSI

- **前置**：完成第 2、3 节的工具/身份决策；有可运行本地 current-user 集成测试的 Windows x64 宿主；核对当前 Git 与公共模型。
- **目标/交付**：独立 MSI 后端和直接 API、MSBuild 映射，文件目录、主程序、名称/发布者/版本、范围 current user、默认安装目录、稳定身份、`.msi` 产物契约；WiX 3.14.1 固定归档、许可、SHA-256、最小子集、共享缓存和断网构建。第一阶段立即验证真实安装**和卸载**，包括产物哈希、文件、卸载注册、清理所有权。
- **新增自动化**：输入校验、身份/版本边界、组件 GUID 稳定性、路径拒绝、工具缓存篡改/并发、WiX 命令调用及错误、MSBuild/API 同一 Core 结果、MSI 数据库表/ICE。Windows x64 本地集成测试每轮生成独立身份，`msiexec /i` 静默安装、检查载荷与产品注册、`msiexec /x` 静默卸载及保留未知文件；失败时收集 verbose log 和清理结果。测试不能仅检查 WiX XML。
- **人工边界**：干净宿主 Framework 条件、ARM64 原生宿主/目标、不同 Windows 版本、交互 UI 和 UAC 外观由可抛弃 VM/专用测试机记录；无环境时保留外部待验收并限定支持声明。包体积与许可证清单属本阶段产品/供应门槛，不能仅转入人工待办。
- **不做**：升级、per machine、快捷方式、关联/协议、语言、签名、CLI、任意运行时安装。
- **退出**：工具分发门槛通过、所有新增测试和相关回归通过、本机专用 fixture 的真实安装与卸载烟雾测试通过、未支持项明确报错；外部平台/高影响测试逐项标注未验收，不能扩大支持声明。

### WIN-MSI-2：生命周期与桌面集成

- **前置**：WIN-MSI-1 真实烟雾测试通过，身份契约冻结，有两版本测试产物；per-machine/UAC 的真实系统观察另需提权 VM。
- **目标/交付**：major upgrade、阻止降级与同版本不同包、per machine 独立产物、范围/架构所有权、快捷方式、文件关联与 URL 协议的声明式注册和卸载；保留用户数据及他人接管资源。
- **新增自动化**：版本/身份矩阵、Upgrade 表、组件稳定性、配置冲突和路径保护、快捷方式与注册表表数据。本地 Windows 先以独立 current-user fixture 实测 v1→v2、降级拒绝、快捷方式及关联/协议所有权；per-machine/HKLM/UAC 仅在提权 VM/专用测试机验证，记录旧载荷与用户文件状态。
- **人工边界**：真实标准用户与管理员、策略限制、文件关联默认应用提示、Windows 版本/架构差异见 MSI 人工文档。
- **不做**：跨 current user/per machine 自动迁移、用显示名清理旧产品、任意删除应用数据、签名/多语言、CLI。
- **退出**：无孤儿组件或越权删除，适合本地的升级/卸载可复现、可清理；新增行为对应自动化测试通过。per-machine 等没有真实环境的行为只可标为外部待验收，不能宣称全环境通过。

### WIN-MSI-3：发布与维护行为

- **前置**：WIN-MSI-2 生命周期稳定；有测试证书、隔离故障注入 VM。
- **目标/交付**：复用 Windows 签名组件签 payload 与最终 MSI；语言资源和 MSI UI 的明确支持集；安静/被动安装、卸载、修复/维护模式与原生退出码；失败回滚、锁定文件和重启策略。
- **新增自动化**：签名顺序与失败后无伪成功产物、每种语言资源及非法 locale、`/qn`/`/passive`/修复参数与返回码契约。本地专用 fixture 实测测试证书签名、静默/被动安装与卸载、`msiexec /f` 修复及可安全注入的失败回滚；损坏包、缺源、锁定文件、真实重启和 `3010` 的系统级场景在可抛弃 VM/专用测试机记录详细日志。任何新自定义操作都需故障/回滚用例。
- **人工边界**：生产证书/时间戳、真实重启、UI 多语言与辅助技术、企业策略见 MSI 人工文档。
- **不做**：Bundler 自定义退出码覆盖 Windows Installer、生产私钥入库、在线下载运行时、CLI。
- **退出**：适合本地自动化的静默、维护、测试签名及失败语义可复现；高影响场景和未覆盖平台标为待验收，不写成成功。

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
| 本地自动 | 输入/映射/模板、WiX 工具哈希与缓存、数据库表、身份/版本/组件规则、离线编译，以及独立 current-user fixture 的真实安装/卸载；后续扩展适合本地的升级、测试证书签名、静默/被动及修复。每次功能变更增加对应测试。 |
| 专用 Windows CI/VM | per-machine/UAC、真实重启、系统级故障/回滚、损坏包、锁定文件、不同 OS/架构及干净宿主依赖；保留 verbose log 和状态断言。 |
| 人工验收 | 生产证书和信任链、真实用户 UAC、交互 UI/语言、企业策略、真实升级来源、真实重启及难以自动覆盖的 ARM64/多 Windows 版本；按独立 MSI 用例执行。 |

接班者先读 `PROJECT_CONTEXT.md`、`docs/roadmap.md`、本文、MSI 矩阵/待办/人工文档，再核 Git、代码与测试。**只有用户明确说“开始 WIN-MSI-1”才进入实现**。开始时先报告实际状态和工具选型门槛，完成整个阶段的新增测试与真实安装/卸载烟雾测试后再报告结果。未经用户明确要求不提交或推送。

## 6. WIN-MSI-1 实施及完成记录（2026-09-24）

用户已明确说“提交，然后开始”。规划文档提交为 `75738ed`。随后按用户要求把原实施分支拆成 `codex/nsis-development`（指向父提交 `11dbde5`）与当前 `codex/msi-development`（包含 `75738ed`）；旧 `codex/nsis` 已删除，`master` 已快进至 `11dbde5`。2026-09-24 用户要求提交本阶段结果；后续接班者应核对实时 HEAD 与状态，不把第一阶段结果写成整个 MSI 格式完成。

- 从官方 `wix314-binaries.zip`（41,297,555 字节，SHA-256 `6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31`）提取必需工具，子集 `third_party/wix/wix3141-tools.zip` 为 1,038,060 字节，SHA-256 `25AE0BB2A21FAC6B486C4B06155C9F463F2D845E7036BE0E9B1C98F4E48EA494`。官方标签对应源码归档 `wix3141-source.zip` 为 13,599,826 字节，SHA-256 `A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F`，连同 MS-RL 许可证放入 WiX NuGet 包。逐文件哈希见 `third_party/wix/SHA256SUMS`。原始来源、许可证及免费社区维护状态见第 2 节。
- 本机核对工具子集共 14 项，逐项哈希测试与原始发行归档匹配；逐项找到 `wix3141rtm` 对应源码文件或项目，12 份适用的源码/配置/项目头部声明 MS-RL，`darice.cub` 原文件与许可证文本亦在源码归档内。生成的 WiX NuGet 包实含 `licenses/wix/LICENSE.TXT`、`README.md`、`SHA256SUMS`、完整 `wix3141-source.zip` 和 `THIRD-PARTY-NOTICES.md`；未发现该子集另有单独许可。重新打包并检查上述条目，包大小 13,786,712 字节；用户于 2026-09-24 确认约 13.8 MB 可接受。逐项归属记录见 `third_party/wix/README.md`。这是对选定文件的工程审计，不等同于法律保证；更换文件须重新审计。依据：[官方发行页](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[官方 MS-RL 文本](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)。
- 本机从发行二进制读取 `candle.exe`、`light.exe` 及 `wix.dll` 的目标框架属性，均为 `.NETFramework,Version=v4.5`；两个 EXE 还要求 32 位进程。本机实际启动了 `candle.exe`/`light.exe`；这**不等于**干净 Windows 或 ARM64 宿主已验证。x64 主机上已编译 x64 和 ARM64 目标 MSI，ARM64 数据库摘要为 `Arm64;1033`；ARM64 用户端安装仍未验证。
- 已加入 `DotNet.Bundler.Wix` 直接 API、MSBuild 适配、工具缓存、最小 current user MSI、稳定 UUIDv5 身份、文件与资源组件、HKCU key path、目录移除表、元数据和 `.ico` 图标。首版阻止另一版本并存及 `ALLUSERS=1` 初次安装，升级仍留在 WIN-MSI-2。重复相同输入时复用哈希验证的本地产物；同输出位置、同版本的不同载荷被拒，但跨机器的同版本发布冲突仍需发行方维护版本记录。
- WiX 3 的数据库不能安全地把 UTF-8 当作通用 MSI 代码页。默认 1252；有中文字符串/路径时可显式设置 `BundlerWixCodepage=936` 或直接 API `Codepage=936`。不支持 UTF-7/UTF-8 配置。已用中文产品名、文件名和外部资源生成并读取数据库；UI 语言仍为 1033，真实安装显示/编码需 VM 验证。依据：[WiX 3 代码页](https://docs.firegiant.com/wix3/overview/codepage/)、[Windows Installer 代码页处理](https://learn.microsoft.com/en-us/windows/win32/msi/code-page-handling-windows-installer-)。
- 本机 `dotnet build Bundler.slnx -c Release --no-restore` 通过（0 警告、0 错误）；`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release` 通过（49 项）；`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages` 通过。新增测试包括身份/版本、工具与源码哈希、缓存污染与并发、实际 WiX 编译和 MSI 数据库表。随后以仅指向本地产物的包源 restore、publish `tests/Windows.Msi.Integration/Fixture/BundlerMsiSmoke.csproj`，成功产出 MSI。`light` 仅抑制 ICE91：该警告假定同一目录可能用于 per-machine，而本阶段产物固定 per-user；其余 ICE 错误仍失败。
- 对照 NSIS 的本机集成方式，`tests/Windows.Msi.Integration/Verify.ps1` 新增 `-ConfirmLocalInstall`；每轮生成随机 reverse-DNS identifier，读取 MSI ProductCode 并预检未注册，实际安装后验证主程序与产品名/注册状态，写入未知用户文件，再按 ProductCode 卸载，确认载荷删除、未知文件保留、产品注销，最后只清理本轮未知文件和空测试目录。2026-09-24 在 Lenovo Windows 11 Pro build 26200 x64 本机运行两轮均通过；最终脚本版本对应 MSI SHA-256 `C91CF458CC2FBC613218A66917CF9AD6FF8848C2850C810A77CB1A8637B48B29`，测试 ProductCode `{38977A58-42B9-566A-9E32-B3BA1A5C9E74}`，verbose log 保存在 `%TEMP%\Bundler-Msi-Smoke-bc99a6b4dfe94474a325de8d9a61ee9d\install.log` 与 `uninstall.log`。执行后再次查询 ProductState 为 `-1`、安装目录不存在。无开关或在物理机仅传 `-ConfirmDisposableVm` 均在安装前拒绝。
- 按快速开发期的回归要求，本轮扩充同一 MSI 集成 fixture：额外打包 `docs/marker.txt`，安装后核对资源内容及 Windows Installer 注册版本，卸载后断言资源文件也被移除，继续断言未知用户文件被保留。执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall`，在同一 Windows 11 Pro build 26200 x64 本机通过；本轮 MSI SHA-256 为 `F8D51147916A739AA57DAFAC8E367FDE3D1700A86B00C3F0AB61E7DF391B6078`，ProductCode `{065FA6CA-54A4-582E-8B30-DF6941AC881C}`，日志位于 `%TEMP%\Bundler-Msi-Smoke-203db572a9834693b0eb4c90d2c28eca`。这是新增测试断言后的结果；其他平台仍未验收。
- 随后把“测试结束仍有安装目录”改为脚本失败条件，并再次运行同一集成命令，退出码 0。最终脚本版本对应 MSI SHA-256 `85A4C74532D976EEAD5D7A5AB3851950D00A304253218113005ED0BE2AA3E6A3`，ProductCode `{8D25144D-5579-5016-8D2B-50DFDF8C52B0}`，日志在 `%TEMP%\Bundler-Msi-Smoke-ac72702d3e874a9e9238878eb35366fb`；脚本已断言产品注销、托管文件删除、未知文件保留及本轮安装目录清理。留存的两个独立运行记录便于回归核查。
- 共用 MSBuild 适配层的 NSIS 回归也已重跑：`powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.33`，最终输出 `PASS Windows NSIS install/uninstall integration`，退出码 0。结束后检查其固定安装目录、旧 MSI fixture、事务目录、应用数据目录和卸载注册项，均无残留。这是当前工作区的回归结果，不借用先前提交的测试记录。
- **2026-09-24 本机范围内退出条件已达到**：在此前真实安装/卸载、49 项自动化测试及 NSIS 回归基础上，本轮再次运行 `dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore`，49 项均通过，包含从新缓存提取内嵌 WiX 并实际编译/读取 MSI；代码中的工具解析只读取内嵌资源或显式本地归档，无运行时网络下载。本地 NuGet 依赖已存在，故这证明无需在线还原即可编译，不证明全新计算机无 NuGet 缓存时能还原项目。`dotnet pack src/Bundler.Wix/Bundler.Wix.csproj -c Release --no-restore -o artifacts/packages` 通过，并复核包中许可证、完整源码、哈希及声明。选定 14 个工具文件的许可归属审计见上文。阶段一最小 current-user 能力可标完成。

**未验收的支持范围**：用户确认现无干净 Windows 10/11 或 ARM64 环境，本次不执行这些 VM 测试。WiX 自身在这些宿主上的直接运行、ARM64 用户端安装，以及 UAC/真实重启/故障等高影响场景继续留在 `docs/msi-open-items.md`，不作为本次本机阶段退出阻塞，也不写成已支持。下一阶段 `WIN-MSI-2` 尚未开始。

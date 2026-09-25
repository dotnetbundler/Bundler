# Windows MSI 后端实施路线（WiX 3.14.1 暂定）

> 状态：`WIN-MSI-1..4` 的当前 Windows 11 x64 本机自动化范围已完成（2026-09-25），`alpha.37` 是既有能力的冻结基线。用户随后确认先补齐 Tauri 通用 MSI 能力，`WIN-MSI-5..9` 已规划但**尚未实施**；默认下一阶段为 `WIN-MSI-5`。原阶段证据见第 6..9 节，新增路线见第 10 节。
> 当前开发分支：`msi-development`；历史记录中的 `codex/msi-development` 是改名前的名称。
> 规范入口：`docs/roadmap.md`；Tauri 对照见 `docs/msi-tauri-capability-audit.md`，逐项能力见 `docs/msi-capability-matrix.md`，外部条件见 `docs/msi-open-items.md`，人工步骤见 `docs/msi-manual-testing.md`。

## 1. 已核实事实、选择及风险

2026-09-23 规划时核对分支 `codex/modular-bundler-backends`、HEAD `6946daea16dff84113988b7838f36fe08a03498e`、包版本 `0.1.0-alpha.33`、干净工作区。当时代码有 `PackageFormat.Msi`、Core 规划与 `IBundleBackend`，但没有 MSI 后端、MSI MSBuild 映射或 MSI E2E。`tests/Windows.Nsis.Integration/LegacyMsiFixture` 的 `WixToolset.Sdk/5.0.2` 只生成 NSIS 迁移测试所需旧 MSI，不构成正式选型。该段是规划时的历史快照，当前进度以第 6 节及实际 Git/测试为准。

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

**已决定/尚待验收**：用户在 2026-09-24 接受当时约 13.8 MB 的 WiX NuGet 包体积；WIN-MSI-3 增加第 15 个官方 UI 扩展文件后实测约 14.4 MB，增量和哈希见第 8 节。前 14 个文件的审计见第 6 节，第 15 个见第 8 节及 `third_party/wix/msi-wix-provenance.md`；更换文件必须重审。当前 WiX 文档所述 Open Source Maintenance Fee 从 WiX v6 才开始，不能套用到 WiX 3.14.1：[WiX 版本说明](https://docs.firegiant.com/wix/whatsnew/)。`candle.exe`、`light.exe` 和 `wix.dll` 的目标框架属性均为 `.NETFramework,Version=v4.5`，但干净 Windows/ARM64 宿主的启动条件仍需 VM 验证。若免费无补丁状态无法满足届时安全要求，应重新评估工具链。

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
- **目标/交付**：major upgrade、阻止降级与同版本不同包、per machine 独立产物、范围/架构所有权、快捷方式、文件关联与 URL 协议的声明式注册和卸载；保留用户数据，不占用共享默认关联和他人注册项。MSI 对受管理快捷方式的同路径接管没有内容级保护，界限见第 7 节。
- **新增自动化**：版本/身份矩阵、Upgrade 表、组件稳定性、配置冲突和路径保护、快捷方式与注册表表数据。本地 Windows 先以独立 current-user fixture 实测 v1→v2、降级拒绝、快捷方式及关联/协议所有权；per-machine/HKLM/UAC 仅在提权 VM/专用测试机验证，记录旧载荷与用户文件状态。
- **人工边界**：真实标准用户与管理员、策略限制、文件关联默认应用提示、Windows 版本/架构差异见 MSI 人工文档。
- **不做**：跨 current user/per machine 自动迁移、用显示名清理旧产品、任意删除应用数据、签名/多语言、CLI。
- **退出**：无已知孤儿组件或对共享默认关联/他人注册值的越权删除，适合本地的升级/卸载可复现、可清理；新增行为对应自动化测试通过。受管理快捷方式同路径替换风险需明确披露；per-machine 等没有真实环境的行为只可标为外部待验收，不能宣称全环境通过。

### WIN-MSI-3：发布与维护行为

- **前置**：WIN-MSI-2 生命周期稳定；本机生成短期测试证书。高影响故障注入需隔离 VM，按第 4 节通用验证规则列为外部验收，不阻塞本机安全实现。
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
| 本地自动 | 输入/映射/模板、WiX 工具哈希与缓存、数据库表、身份/版本/组件规则、离线编译，以及独立 current-user fixture 的真实安装/卸载、升级、测试证书签名、静默/被动及修复。每次功能变更增加对应测试。 |
| 专用 Windows CI/VM | per-machine/UAC、真实重启、系统级故障/回滚、损坏包、锁定文件、不同 OS/架构及干净宿主依赖；保留 verbose log 和状态断言。 |
| 人工验收 | 生产证书和信任链、真实用户 UAC、交互 UI/语言、企业策略、真实升级来源、真实重启及难以自动覆盖的 ARM64/多 Windows 版本；按独立 MSI 用例执行。 |

接班者先读 `PROJECT_CONTEXT.md`、`docs/roadmap.md`、本文、MSI Tauri 审计、矩阵/待办/人工文档，再核 Git、代码与测试。WIN-MSI-1/2/3/4 的历史实施结果分别记录在第 6/7/8/9 节；用户后来决定按第 10 节继续 WIN-MSI-5..9，`MAC-APP` 顺延。每阶段完成新增自动化和适用的真实安装测试后报告结果；未经用户明确要求不开始后续阶段代码、不提交或推送。

## 6. WIN-MSI-1 实施及完成记录（2026-09-24）

用户已明确说“提交，然后开始”。规划文档提交为 `71b028c`。随后按用户要求把原实施分支拆成 `codex/nsis-development`（指向父提交 `6946dae`）与当前 `codex/msi-development`（包含 `71b028c`）；旧 `codex/nsis` 已删除，`master` 已快进至 `6946dae`。2026-09-24 用户要求提交本阶段结果；后续接班者应核对实时 HEAD 与状态，不把第一阶段结果写成整个 MSI 格式完成。

- 从官方 `wix314-binaries.zip`（41,297,555 字节，SHA-256 `6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31`）提取必需工具，子集 `third_party/wix/wix3141-tools.zip` 为 1,038,060 字节，SHA-256 `25AE0BB2A21FAC6B486C4B06155C9F463F2D845E7036BE0E9B1C98F4E48EA494`。官方标签对应源码归档 `wix3141-source.zip` 为 13,599,826 字节，SHA-256 `A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F`，连同 MS-RL 许可证放入 WiX NuGet 包。逐文件哈希见 `third_party/wix/SHA256SUMS`。原始来源、许可证及免费社区维护状态见第 2 节。
- 本机核对工具子集共 14 项，逐项哈希测试与原始发行归档匹配；逐项找到 `wix3141rtm` 对应源码文件或项目，12 份适用的源码/配置/项目头部声明 MS-RL，`darice.cub` 原文件与许可证文本亦在源码归档内。生成的 WiX NuGet 包实含 `licenses/wix/LICENSE.TXT`、`README.md`、`SHA256SUMS`、完整 `wix3141-source.zip` 和 `THIRD-PARTY-NOTICES.md`；未发现该子集另有单独许可。重新打包并检查上述条目，包大小 13,786,712 字节；用户于 2026-09-24 确认约 13.8 MB 可接受。逐项归属记录见 `third_party/wix/msi-wix-provenance.md`。这是对选定文件的工程审计，不等同于法律保证；更换文件须重新审计。依据：[官方发行页](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[官方 MS-RL 文本](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)。
- 本机从发行二进制读取 `candle.exe`、`light.exe` 及 `wix.dll` 的目标框架属性，均为 `.NETFramework,Version=v4.5`；两个 EXE 还要求 32 位进程。本机实际启动了 `candle.exe`/`light.exe`；这**不等于**干净 Windows 或 ARM64 宿主已验证。x64 主机上已编译 x64 和 ARM64 目标 MSI，ARM64 数据库摘要为 `Arm64;1033`；ARM64 用户端安装仍未验证。
- 已加入 `DotNet.Bundler.Wix` 直接 API、MSBuild 适配、工具缓存、最小 current user MSI、稳定 UUIDv5 身份、文件与资源组件、HKCU key path、目录移除表、元数据和 `.ico` 图标。首版阻止另一版本并存及 `ALLUSERS=1` 初次安装，升级仍留在 WIN-MSI-2。重复相同输入时复用哈希验证的本地产物；同输出位置、同版本的不同载荷被拒，但跨机器的同版本发布冲突仍需发行方维护版本记录。
- WiX 3 的数据库不能安全地把 UTF-8 当作通用 MSI 代码页。默认 1252；有中文字符串/路径时可显式设置 `BundlerWixCodepage=936` 或直接 API `Codepage=936`。不支持 UTF-7/UTF-8 配置。已用中文产品名、文件名和外部资源生成并读取数据库；UI 语言仍为 1033，真实安装显示/编码需 VM 验证。依据：[WiX 3 代码页](https://docs.firegiant.com/wix3/overview/codepage/)、[Windows Installer 代码页处理](https://learn.microsoft.com/en-us/windows/win32/msi/code-page-handling-windows-installer-)。
- 本机 `dotnet build Bundler.slnx -c Release --no-restore` 通过（0 警告、0 错误）；`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release` 通过（49 项）；`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages` 通过。新增测试包括身份/版本、工具与源码哈希、缓存污染与并发、实际 WiX 编译和 MSI 数据库表。随后以仅指向本地产物的包源 restore、publish `tests/Windows.Msi.Integration/Fixture/BundlerMsiSmoke.csproj`，成功产出 MSI。`light` 仅抑制 ICE91：该警告假定同一目录可能用于 per-machine，而本阶段产物固定 per-user；其余 ICE 错误仍失败。
- 对照 NSIS 的本机集成方式，`tests/Windows.Msi.Integration/Verify.ps1` 新增 `-ConfirmLocalInstall`；每轮生成随机 reverse-DNS identifier，读取 MSI ProductCode 并预检未注册，实际安装后验证主程序与产品名/注册状态，写入未知用户文件，再按 ProductCode 卸载，确认载荷删除、未知文件保留、产品注销，最后只清理本轮未知文件和空测试目录。2026-09-24 在 Lenovo Windows 11 Pro build 26200 x64 本机运行两轮均通过；最终脚本版本对应 MSI SHA-256 `C91CF458CC2FBC613218A66917CF9AD6FF8848C2850C810A77CB1A8637B48B29`，测试 ProductCode `{38977A58-42B9-566A-9E32-B3BA1A5C9E74}`，verbose log 保存在 `%TEMP%\Bundler-Msi-Smoke-bc99a6b4dfe94474a325de8d9a61ee9d\install.log` 与 `uninstall.log`。执行后再次查询 ProductState 为 `-1`、安装目录不存在。无开关或在物理机仅传 `-ConfirmDisposableVm` 均在安装前拒绝。
- 按快速开发期的回归要求，本轮扩充同一 MSI 集成 fixture：额外打包 `docs/marker.txt`，安装后核对资源内容及 Windows Installer 注册版本，卸载后断言资源文件也被移除，继续断言未知用户文件被保留。执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall`，在同一 Windows 11 Pro build 26200 x64 本机通过；本轮 MSI SHA-256 为 `F8D51147916A739AA57DAFAC8E367FDE3D1700A86B00C3F0AB61E7DF391B6078`，ProductCode `{065FA6CA-54A4-582E-8B30-DF6941AC881C}`，日志位于 `%TEMP%\Bundler-Msi-Smoke-203db572a9834693b0eb4c90d2c28eca`。这是新增测试断言后的结果；其他平台仍未验收。
- 随后把“测试结束仍有安装目录”改为脚本失败条件，并再次运行同一集成命令，退出码 0。最终脚本版本对应 MSI SHA-256 `85A4C74532D976EEAD5D7A5AB3851950D00A304253218113005ED0BE2AA3E6A3`，ProductCode `{8D25144D-5579-5016-8D2B-50DFDF8C52B0}`，日志在 `%TEMP%\Bundler-Msi-Smoke-ac72702d3e874a9e9238878eb35366fb`；脚本已断言产品注销、托管文件删除、未知文件保留及本轮安装目录清理。留存的两个独立运行记录便于回归核查。
- 共用 MSBuild 适配层的 NSIS 回归也已重跑：`powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.33`，最终输出 `PASS Windows NSIS install/uninstall integration`，退出码 0。结束后检查其固定安装目录、旧 MSI fixture、事务目录、应用数据目录和卸载注册项，均无残留。这是当前工作区的回归结果，不借用先前提交的测试记录。
- **2026-09-24 本机范围内退出条件已达到**：在此前真实安装/卸载、49 项自动化测试及 NSIS 回归基础上，本轮再次运行 `dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore`，49 项均通过，包含从新缓存提取内嵌 WiX 并实际编译/读取 MSI；代码中的工具解析只读取内嵌资源或显式本地归档，无运行时网络下载。本地 NuGet 依赖已存在，故这证明无需在线还原即可编译，不证明全新计算机无 NuGet 缓存时能还原项目。`dotnet pack src/Bundler.Wix/Bundler.Wix.csproj -c Release --no-restore -o artifacts/packages` 通过，并复核包中许可证、完整源码、哈希及声明。选定 14 个工具文件的许可归属审计见上文。阶段一最小 current-user 能力可标完成。

**阶段一结项时的未验收范围**：当时用户确认现无干净 Windows 10/11 或 ARM64 环境，未执行这些 VM 测试。WiX 自身在这些宿主上的直接运行、ARM64 用户端安装，以及 UAC/真实重启/故障等高影响场景继续留在 `docs/msi-open-items.md`，不作为本机阶段退出阻塞，也不写成已支持。阶段二的后续进度见第 7 节。

## 7. WIN-MSI-2 本机实施记录（2026-09-24）

**事实与实现**：`MajorUpgrade` 使用 `afterInstallInitialize`，以 Windows Installer 事务卸载同一 UpgradeCode 的旧版本；拒绝降级。原 WIN-MSI-1 的 current-user 组件 GUID 字符串保持稳定。per-machine 采用独立 UpgradeCode/ProductCode/组件 GUID、`ProgramFiles64Folder` 和 HKLM；当前只编译并检查数据库，未做提权安装。快捷方式为显式选择的开始菜单及桌面链接，前者放在 identifier 专属目录，后者名称带 identifier；current-user 使用非 advertised 链接及 HKCU 组件键路径，per-machine 使用 advertised 链接及文件组件键路径以通过 ICE19/43/57。MSBuild 属性 `BundlerWixStartMenuShortcut`、`BundlerWixDesktopShortcut` 对应直接 API 同名布尔配置，默认均为 false。文件扩展名写入自身 ProgID、`OpenWithProgids` 和应用 Capabilities，若提供 MIME 类型则登记 MIME 候选能力；URL 协议写入自身 ProgID 和 `UrlAssociations`，不改同名公共 scheme 根、既有默认程序或他人 ProgID。用户需在 Windows 默认应用设置中选择处理程序；协议实际选择/唤起仍需人工观察。卸载只移除 MSI 声明的值/文件和空目录，不删除未知用户文件。快捷方式若被他人在**完全相同路径**替换，Windows Installer 原生卸载可能移除该路径；本阶段不承诺内容级接管保护，不使用不可回滚的自定义动作。

**同版本限制**：同一输出目录已有内容不同的 MSI 会在构建时拒绝；运行时 Windows Installer 对同一 ProductCode 的不同包返回 `1638`。新包还写入并搜索自身定义摘要，在已安装摘要存在且不一致时设置 LaunchCondition；卸载允许通过。缺失摘要（如旧版产物或人为删改注册表）时不以摘要阻断维护，因此跨机器发布仍须由发行方保证一个版本只发布一份内容。此摘要是冲突检查，不是安全签名或抵抗同用户篡改的保证。

**本机自动证据**：`dotnet build Bundler.slnx -c Release --no-restore` 0 警告/错误；`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 全部 50 项通过，包括 per-machine MSI 含快捷方式/关联/协议的真实编译、数据库 HKLM/Program Files/Shortcut 检查、Upgrade/Registry 表检查及身份范围隔离。最终 `tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -ConfirmLocalInstall` 在 Windows 11 Pro build 26200 x64 实际构建三份包：v1 `1.0.0` SHA-256 `5870AAA0AA61E4E006B28A02DF8DDA0A043B629C1AB218BB4AF178AF28C31FEE`、v2 `1.1.0` SHA-256 `EBCC0CBAC13DD37E43D5B30095534997F52AB689718FBE19D89ED7E0F3BE98B8`、同版本不同内容包 SHA-256 `76A7B9FF7C15518107D0567DA4A9C3ABB428D12891081D1D44860FDB37EEE3E9`。v1→v2 升级、旧 ProductCode 注销、旧资源删除、新资源存在、未知文件保留、降级拒绝、同版本异包拒绝、快捷方式与处理程序命令及注册值创建/卸载清理均通过。测试唯一 identifier 为 `com.example.bundler.msi.lifecycle.36184a3766ca472191fd19c7c098fe30`；verbose log 留在 `%TEMP%\Bundler-Msi-Lifecycle-36184a3766ca472191fd19c7c098fe30`。同版本异包返回 `1638`；降级返回 `1603` 并在日志中明确报告新版本已安装。阶段一 smoke 脚本也以新后端重跑通过，SHA-256 `9BE37859ADB7EDAD0525225EFF3FBEEC2127030E8059D2AB41E9C1DC0B3920E8`，日志 `%TEMP%\Bundler-Msi-Smoke-72ed6e225add4ecfbb204f0e9bc6c7e3`。共享 MSBuild 适配层的 NSIS Windows 集成脚本也通过，退出码 0。这些仅证明上述当前主机与自动化范围。

**后续/外部**：per-machine 的 UAC、标准用户/管理员、干净 Windows/ARM64、默认应用 UI 中的协议选择与实际唤起均按 `docs/msi-manual-testing.md` 和 `docs/msi-open-items.md` 留待专用环境；不作为当前快速开发本机范围退出阻塞，也不宣称已经通过。进入 WIN-MSI-3 前复核当前 Git、运行新测试及集成脚本，保留上述路径所有权和 MSI 事务边界。签名、语言、完整静默/被动/修复/故障行为属于 WIN-MSI-3。

公开示例 `samples/HelloMsiApp` 已用本地包源、隔离 NuGet 缓存和独立输出路径重新 restore/publish，MSI 生成通过；示例展示快捷方式、文件/MIME 候选和 URL 候选配置。生成测试包并不代表已验证默认应用 UI 或实际协议唤起。

**版本迭代补正**：用户指出 WIN-MSI-2 实现仍沿用 alpha.33 的问题。按 NSIS 开发线的迭代惯例，仓库 `BundlerPackageVersion` 升为 `0.1.0-alpha.34`，当前代码重新 pack 为七个同版本 NuGet 包。此前曾把 Hello MSI 示例应用版本从 `1.0.0` 升到 `1.1.0`，并以隔离本地源还原、发布；用户随后明确要求公开示例应用版本保持稳定，故示例已恢复 `1.0.0`，升级/降级由独立 fixture 测试。此前生成的 `Hello MSI App-1.1.0.msi` 仅为历史产物。MSI 示例现与 NSIS 示例一样在项目文件中固定本地还原源和当前开发包版本。直接 `dotnet publish` 使用本地包及缓存，不会自动重打当前源码；同一包版本反复打包有旧缓存风险。上文阶段二 MSI 哈希属于版本补正前的测试记录，补正后的验证以本节后续记录为准。

**版本补正后复测**：`dotnet build Bundler.slnx -c Release --no-restore` 0 警告/错误，`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages` 产出七个 alpha.34 包；50 项 `Bundler.Tests` 通过。`tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.34` 输出 `PASS Windows NSIS install/uninstall integration`，退出码 0。`tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall` 在 Windows 11 Pro build 26200 x64 本机输出 PASS，实际验证 v1→v2 升级、同版本异包与降级拒绝、快捷方式及桌面注册创建/卸载、用户文件保留。此次 v1/v2/异包 SHA-256 依次为 `8BC7FCD96F8B197A6EB82D59566A3FACC0B2844E040C5DEFA4F786788F5AD427`、`323BE18F59CAC75FDDA9A53B8C2FF95E41BC25A96DE149F75B4E13FE25DD7E6A`、`32FE4CC2B6B3AE6AA5745B94F7DFDCC02BB73CBBA702A25476300D6408264FAC`；测试标识 `com.example.bundler.msi.lifecycle.c7c55c4bfaaf4889ab488deba4356199`，verbose log 位于 `%TEMP%\Bundler-Msi-Lifecycle-c7c55c4bfaaf4889ab488deba4356199`。该复测不涉及用户安装的 Hello MSI App。

**示例流程统一复测**：Hello MSI 示例原先没有 NSIS 示例的本地 `RestoreSources`，alpha.34 不在已配置公网源时普通 `dotnet publish` 报 NU1101。补齐本地源、固定 alpha.34 引用并恢复示例应用版本 `1.0.0` 后，原命令已成功还原七个 alpha.34 依赖。旧阶段一 `Hello MSI App-1.0.0.msi` 的内容与当前阶段二定义不同，后端按同版本产物保护拒绝覆盖；保留旧 MSI、manifest 和 wixpdb 至示例 `artifacts/previous-msi/` 后，原命令连续两次通过，新 MSI SHA-256 为 `E9C99B485E225D503DAB0C3690B729BD4C82CC71EFAEB317F9C776848D967281`。当时仍使用旧项目名的 NSIS 示例 `dotnet publish .\samples\HelloBundledApp\HelloBundledApp.csproj -c Release` 也通过。示例构建不执行 MSI 安装；真实升级继续使用独立 lifecycle fixture。

**跨层测试补齐**：对照 NSIS 的 `Nsis.Api.PackageFixture`，新增 `tests/Msi.Api.PackageFixture`，其项目只引用 `DotNet.Bundler.Wix` NuGet 包。MSI smoke 脚本把 fixture 复制到仓库外临时目录，从本轮本地包源与隔离缓存还原，核对 `project.assets.json` 与实际包文件，再直接调用 `WixBundler` 生成 MSI。脚本同时核对 WiX 包中程序集、许可证、对应源码、哈希清单和声明，MSBuild fixture 的实际包版本也加入核对；生命周期脚本采用相同包源/缓存断言。2026-09-24 Windows 11 Pro build 26200 x64 本机 smoke 通过：独立 API MSI SHA-256 `B9093A971C9CE707633E01E6F72A018C40379FAA7F8945236665C95C6E23F115`，真实安装/卸载的 MSBuild MSI SHA-256 `B33351F60C47B4D60CB1D3731E332870DBB668415851060E3F8E71EFB37AA1B0`，测试 ProductCode `{3E60AAFE-523B-5852-BD1D-B9655D93E928}`，日志与包位于 `%TEMP%\Bundler-Msi-Smoke-5ded5624895d4de78d8a8405387d2d71`。生命周期复测通过，v1/v2/异包 SHA-256 分别为 `9EFDE607A54648D016E225A907F2D757658958F77FB2C3F8122E8966D5D27B7E`、`C1C365B97AEE7B2DA0688E5EA607DF746E61E60DBE476E42FCB7FAF051D59CF3`、`502A73D2DBC4CE53ECE2C10B1EEC46679B5CCE3412D58146D6DC9D2B055647E5`，标识 `com.example.bundler.msi.lifecycle.be778c25cac143018dbcf854660b3c86`，日志位于同名 `%TEMP%\Bundler-Msi-Lifecycle-*` 目录。首次新增缓存断言误以为 NuGet 只有一个 packageFolders，实际还列出 Visual Studio fallback；修正后要求当前 Bundler 包确实存在于本轮隔离缓存，两个集成脚本均通过。跨格式规范集中在 `docs/development-rules.md`，下一阶段仍为 WIN-MSI-3；本次补齐测试不等于完成 MSI 格式冻结。

**最终共享还原检查复跑**：共享 `tests/AssertLocalRestore.ps1` 增加禁止意外公网源的断言后，再次运行两个 MSI Windows 入口，均在 Windows 11 Pro build 26200 x64 通过。Smoke 的独立 API/MSBuild MSI SHA-256 依次为 `91071954ED7CBB7FA90B62EBC775A4176382FA65A53ABFF1B16E3A679F38EE06`、`DD7BAE8709DCA5B5299A39A7A77792E426DACBDC244F37745220A02D0ADECBDD`，ProductCode `{1BAE73D8-BE20-5B82-8064-149E01BCBDE8}`，包、隔离缓存和安装/卸载日志在 `%TEMP%\Bundler-Msi-Smoke-e286c715a3d04cb1afaf89807337ba2a`。Lifecycle 的 v1/v2/异包 SHA-256 为 `953C7CF84C42865C3404D08D63B3482156DD8D5FDD40932EBEE080B75EF928C1`、`A88EDF3C168B10C4B2D705877A69961DF10984A3F02EB19310C92AF0AE19B582`、`0B31B9232989AD69F5B489E664ABF6ACDC2AE9CC049B3B614CB13B2081EE0D27`，日志在 `%TEMP%\Bundler-Msi-Lifecycle-f1203328eac740ffbd099e7201142927`。快速测试现为 51 项，含版本同步回归。NSIS 全量回归第二次复跑通过；第一次在原有 rollback journal 清理断言失败，原因尚未确认，见 `PROJECT_CONTEXT.md`。以上本机证据不扩展为其他 Windows 宿主或 per-machine 安装验收。

**包源约定一致性**：MSI MSBuild fixture 曾仅由集成脚本的 `dotnet restore --source` 传入本地包源，与 NSIS fixture 的 `RestoreSources=$(BundlerPackageSource)` 不一致且没有格式上的理由。现已统一：NSIS/MSI 的 API 与 MSBuild fixture 均在项目文件声明该属性，脚本传入本轮本地包目录，继续以隔离缓存和 assets 断言核对。新增快速回归断言保护四个 fixture 的这一配置；修改后本机 smoke 和 lifecycle 完整通过。最新命令、哈希和日志记录见 `PROJECT_CONTEXT.md` 的包源约定补正段。此次是测试入口修正，不改变 MSI 产品语义，也不扩展 WIN-MSI-2 的外部验收范围。

**WIN-MSI-2 结构与测试整理（2026-09-24）**：本次仅重组已有 WiX 后端与测试，未提前实施 WIN-MSI-3。构建/输出、产品 XML 与路径规范化分别放在 `WixBundleBackend`、`WixProductDocument`、`WixPackagePaths`；公共 API 与 MSI 身份、组件及安装语义不变。快速测试仍使用 `tests/Bundler.Tests` 的统一入口，WiX 用例单列 `WixTests.cs` 是为了避免继续扩张原 `Program.cs`，并把独立可观察的安全/复用场景拆成独立测试。MSI 集成仍以 `Verify.ps1` 为烟雾入口、`VerifyLifecycle.ps1` 为阶段专用入口；共用 `MsiTestSupport.ps1`，每轮仅从本地包源还原仓库外 fixture，生命周期三份包使用互不复用的项目中间目录。独立后端 API fixture 仍只引用 `DotNet.Bundler.Wix` NuGet 包。两类后端保持相同测试入口、包源核对、日志和清理习惯，测试内容按 MSI 语义决定。包版本随 WiX 包内容升至 alpha.35，示例应用版本不变。实际命令、56 项快速测试结果、真实安装与升级的哈希及日志见 `PROJECT_CONTEXT.md` 第 14.4 节；外部平台和 UAC 验收状态不变。

本阶段语义依据：[WiX 3 MajorUpgrade](https://docs.firegiant.com/wix3/xsd/wix/majorupgrade/)、[WiX 3 Shortcut](https://docs.firegiant.com/wix3/xsd/wix/shortcut/)、[Microsoft Default Programs 注册规则](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)。本机测试是对这些规格在当前环境的实现核查，不代替其他 Windows 版本的真实验收。

## 8. WIN-MSI-3 本机实施记录（2026-09-24）

**实现事实与产品边界**：MSI 后端使用现有 `IBundleSigner`。有签名器时复制并校验输入目录，先签私有副本中的主程序及显式 `SigningFiles`，WiX 编译后再签最终 MSI；失败删除新 MSI 和 manifest，原始输入不变。MSBuild 的 MSI 路径复用 `CreateWindowsSigner()`，与 NSIS 共用 PFX、证书存储区或外部签名命令配置。由于任意 `IBundleSigner` 无法提供可稳定比较的签名身份，已有同版本签名输出一律拒绝复用，防止新签名请求得到旧签名包；发行方应换输出目录或提升应用版本。仅代码签名测试使用短期自签名证书，生产证书和时间戳未验收。

语言支持集固定为 `en-US` 和 `zh-CN`，一次构建一个语言 MSI。英文历史的 UpgradeCode/ProductCode/组件 GUID 生成不变。简体中文默认代码页 936，ProductLanguage 2052，升级身份、组件、安装目录、桌面注册和输出名加语言隔离，避免两个语言包争用同一路径；英文默认代码页仍为 1252，显式非 936 的简体中文配置拒绝。若发行方显式设置 UpgradeCode，须为各语言指定互不冲突的 GUID。WiX 3 官方 `WixUIExtension.dll` 加入同一固定官方归档子集；只有应用提供 `.rtf` 许可证时才启用内置 `WixUI_Minimal` 和相应语言资源。无许可证时保留 Windows Installer 原生基础 UI，不伪造应用许可条款。程序名、描述、许可正文等应用文本由发行方提供和翻译；交互显示、母语审校与辅助功能仍待人工验收。`third_party/wix/msi-wix-provenance.md` 固定官方资产、增量文件 SHA-256、对应源码及 MS-RL 审计。最终 alpha.36 WiX 包实测为 14,397,524 字节，比前版 13,790,283 字节增加 607,241 字节；SHA-256 `A43C43F1731F6ABB45163EECE6A25866666C9301C06572AE0523D003A7862B99`。未引入付费扩展或运行时下载。

静默/被动/修复沿用 Windows Installer 原生命令和退出码。MSI 包不嵌入 Bundler 私有运行时代码或自定义故障动作，`/qn`、`/passive`、`/fomus`、`/x` 与 `/norestart` 由调用方传给 `msiexec`。本机测试只对独立随机身份的 current-user 产品操作。故障测试把正常 MSI 复制成**测试专用副本**，只在该副本加入 `InstallFiles` 后的延迟失败动作；日志确认实际 `FileCopy` 已执行，Windows Installer 返回 1603 且移除产品注册与托管文件。正式生成的 MSI 不含该动作。损坏包返回原生 1620。锁定文件、缺失修复源、UAC、真实重启及 3010 在可抛弃 VM 验证，不通过本机测试推断。

**本机证据**：Windows 11 Pro build 26200 x64；`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 的 MSI 新测试覆盖两语言数据库及 UI、签名顺序/失败清理、真实测试证书签 PE 与 MSI，最终总数和结果见 `PROJECT_CONTEXT.md` 当前阶段记录。`tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall` 通过，独立 API MSI SHA-256 `845DC3424DE66E33B7EE0830CE1AFB00D07C945BCE2CC66ABB01556DDE2CE9C0`，真实安装的 MSBuild MSI SHA-256 `31E20D9181E9B29357D6479E201E290BF23831348271A43C907B68FEB966A399`，ProductCode `{F4380809-F773-50F3-8AD0-AAE85FDFB878}`，日志在 `%TEMP%\Bundler-Msi-Smoke-2652c69c944c4da98faf0084d7a0af5e`。`VerifyLifecycle.ps1 -ConfirmLocalInstall` 通过，v1/v2/同版本异包 SHA-256 分别为 `92D298276B487260EC27D47C99516BC9314398D2D157A43655ABF4B3EAE13CA2`、`8DF670271284E2E0A9E8D1948418FEC29E09FEBD2CC6E73559BC98582285CBD4`、`821896880D72FBAE23B5EC45B980E81C2A01E6283E0F2511ED22D3298FFCF15C`，日志在 `%TEMP%\Bundler-Msi-Lifecycle-95a3db36aa934c5aa470fba5b9b72ff0`。

新增 `VerifyMaintenance.ps1 -ConfirmLocalInstall` 最终通过：正常英文 MSI SHA-256 `F036861FBEFF6D35D52EE50A757D6060269AC555E802B3D18A329F84C656B66F`，ProductCode `{CC390551-454A-554C-93FC-9E8A8CB617D8}`，损坏包返回 1620，延迟故障返回 1603 且无残留，被动安装/卸载、静默修复、中文许可证包与英文包并存及分别卸载均通过。日志和包在 `%TEMP%\Bundler-Msi-Maintenance-b264dcc8107d425fa85236de3f4e6e85`。MSI 三个入口均从 alpha.36 本地包源与隔离缓存还原仓库外 fixture；当前 NSIS 集成也在本轮共用 MSBuild 映射改动后通过。发布示例应用仍固定 `1.0.0`，本阶段只迭代打包工具到 `0.1.0-alpha.36`。

**下一阶段与未验收项**：WIN-MSI-4 执行支持矩阵与格式冻结，不新增功能。生产证书/时间戳、真实交互 UI 与母语审校、干净 Windows 10/11/ARM64、per-machine UAC、锁定文件和真实重启/3010 仍在 `docs/msi-manual-testing.md` 与 `docs/msi-open-items.md`，未取得证据的组合不能标为通过。规格依据：[WiX 3 UI 与语言](https://docs.firegiant.com/wix3/wixui/dialog_reference/wixui_minimal/)、[WiX 3 cultures](https://docs.firegiant.com/wix3/howtos/ui_and_localization/specifying_cultures_to_build/)、[MSI 产品语言及本地化身份](https://learn.microsoft.com/en-us/windows/win32/msi/localizing-a-windows-installer-package)、[msiexec 标准参数](https://learn.microsoft.com/en-us/windows/win32/msi/standard-installer-command-line-options)、[Windows Installer 回滚](https://learn.microsoft.com/en-us/windows/win32/msi/rollback-installation) 与 [延迟动作语义](https://learn.microsoft.com/en-us/windows/win32/msi/deferred-execution-custom-actions)。

## 9. WIN-MSI-4 本机冻结审计（2026-09-25）

**范围和结论**：用户以“开始”启动本阶段。起点为 `codex/msi-development` 的 `89a535c`、干净工作区、`0.1.0-alpha.36`。本轮冻结的是现有 MSI 配置、身份及当前主机已验证行为，不把一台 Windows 11 开发机的结果推广为干净 Windows 10/11、ARM64、per-machine UAC 或生产证书的兼容性结论。未完成的外部组合继续留在 MSI 专用人工清单和外部待办；不以缺少 VM 阻塞后续格式，也不把未执行写成通过。MSI 不新增用户能力、CLI 或 NSIS 功能。

**冻结前修复**：原 `WixProcessRunner` 捕获但丢弃成功退出时的 WiX 输出，ICE/编译警告不会进入构建结果。WiX 3 编译和链接现均使用 `-wx`，任何未豁免警告使构建失败；current-user MSI 仍仅豁免 `ICE91`，因为该 ICE 对只用于 per-user 上下文的用户目录文件警告不构成问题。[WiX 3 Candle 警告开关](https://docs.firegiant.com/wix3/msbuild/task_reference/candle/)、[Light 验证与警告开关](https://docs.firegiant.com/wix3/overview/light/)、[Microsoft ICE91 解释](https://learn.microsoft.com/en-us/windows/win32/msi/ice91)。首次启用严格告警后，真实构建暴露 `CNDL1091`：显式 `Package/@Id` 即使每次随机，也会警告存在复用风险；按 [WiX 3 Package 规则](https://docs.firegiant.com/wix3/xsd/wix/package/)取消显式赋值，改由 WiX 每次构建自动生成 PackageCode。公开 `WixIdentity` 不再误导性地返回由后端预先生成但不应写入的 PackageCode；UpgradeCode/ProductCode、英文既有身份和组件 GUID 均保持不变。同一产品版本在独立输出目录两次构建的 ProductCode/UpgradeCode 相同、PackageCode 不同，新增测试实证。发行方仍只能对外发布同一版本的一份内容。

**自动化与包审计**：Windows 11 Pro build 26200 x64，本机 Release 快速测试 **62/62 通过**，包含新增的真实 WiX 预处理警告拒绝及 PackageCode 回归；`dotnet build Bundler.slnx -c Release --no-restore -v:q` 为 0 警告/0 错误，`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages -v:q` 产生七个 `0.1.0-alpha.37` 包。随包 README/供应说明定稿后最后一次重打的 `DotNet.Bundler.Wix` 包为 **14,397,760 字节**、SHA-256 **`8892C11C9F18958E1A7916049BC524467C7E65EF82DDC9B9C0B151666C7D9025`**；早先尚未定稿的本轮包哈希已失效，不作为发行证据。三个 MSI 集成入口均在每轮 Pack 后核对包中 WiX 许可证、对应源码归档、逐文件哈希表、供应说明和第三方声明与仓库文件 SHA-256 一致；随包说明定稿后又对最终 `artifacts/packages` 包独立做只读 ZIP 审计，许可证、源码、说明、清单、第三方声明与根 README 的内容哈希全部匹配。独立 API fixture 仅引用已打出的 `DotNet.Bundler.Wix` 包，MSBuild fixture 使用本地包源和隔离 NuGet 缓存。随包 WiX 3.14.1 子集、许可和源码未变；[官方发行资产](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[MS-RL](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)与逐文件审计见 `third_party/wix/msi-wix-provenance.md`。官方页面仍声明 WiX 3 自 2025-02-06 起不再获得免费社区修复，包括安全修复；本项目未加入付费扩展或服务，未来安全风险需在发布时重新评估：[WiX v3 状态](https://docs.firegiant.com/wix/wix3/)。

**真实 Windows 结果**：`Verify.ps1 -ConfirmLocalInstall` 通过，独立 API MSI SHA-256 `DD12C69DAF317C8A17D0E66AC41E049575231BF2F8ECBEB0EADF6274F99AD9FE`，MSBuild 安装包 SHA-256 `F9FA67652EF00457215422E70BF110D3BA92C18FDBF9459637797877DAB32514`，ProductCode `{23113B22-BA65-57BF-9B9D-46C4918D8BD9}`，日志在 `%TEMP%\Bundler-Msi-Smoke-ba31f5ed3c134e13953230dad51494e0`。`VerifyLifecycle.ps1 -ConfirmLocalInstall` 通过，v1/v2/异包 SHA-256 分别为 `12A987E79B870421C43B7A4DB193E1E8AA5940DF691E633496DD7AE5F8767ADE`、`156FD28DB8A01F384EF332F93E1D6CE4F9E3E5FF4A8603E1C7A894D1643A6EAF`、`EA6345A4C28EB2260598FB790A416D8803F58CB7DD3B905B5D0010D443F73A26`，日志在 `%TEMP%\Bundler-Msi-Lifecycle-03fd99a5f7924c0bafc3ade2297cb327`。`VerifyMaintenance.ps1 -ConfirmLocalInstall` 通过：英文 MSI SHA-256 `196E11AE80AC463EA5A379E4E80DB81F23D23E9EFCB038FA3879932DC1AF2F9D`，中文 MSI SHA-256 `E467C6E874C3F4C665F53365D0AADBB22DECCFA88296BE6DA7ACCF13858DE3C3`；损坏包返回 1620，测试副本的延迟故障返回 1603 且回滚无托管残留，被动安装/卸载、静默修复和双语言并存通过，日志在 `%TEMP%\Bundler-Msi-Maintenance-5d8ca0ebc2124110bae770bfd54ff5d8`。四个已知 MSI ProductCode 及三组本轮安装目录再次只读检查均不存在；第一次检查输出通过后本机 PowerShell 预测器崩溃，随后以 `powershell.exe -NoProfile` 完整复跑并正常退出 0。测试包和 verbose log 保留在上述临时目录供复核。

**跨层回归与交接**：Windows NSIS 全量安装/卸载集成在同步 `alpha.37` 包引用后退出 0。公开 `HelloMsiApp` 与 `HelloBundledApp` 都从 `artifacts/packages` 还原七个 `alpha.37` 包并发布，示例应用版本仍为 `1.0.0`；示例 MSI/NSIS SHA-256 分别为 `35E0063D91AFCDB940AFBE6BC6EBB9369AD5FDA24B0886F3C00AB6DEDF9FE51D` 和 `32450EE94470D5488446AEF843DB0A115F0E12FB1D20542273B6ABC2D10D5D4C`，位于 `%TEMP%\Bundler-Public-Sample-Msi-alpha37` 与 `%TEMP%\Bundler-Public-Sample-Nsis-alpha37`，未安装公开示例。首次未提权快速测试在进入断言前因沙箱无法读取本机 `Microsoft SDKs` 目录而失败，授予当前本机执行权限后复跑；严格警告初跑的 `CNDL1091` 是本阶段发现并修复的产品缺陷，不能改写为从未发生。阶段结束不自动提交或推送。

**外部支持边界与当时下一步**：per-machine/UAC、干净 Windows 10/11 x64/ARM64 的 WiX 自身零环境和真实安装、生产签名/时间戳、交互 UI/母语审校/辅助功能、缺源、锁定文件/磁盘故障、真实重启和 3010 仍未验收；逐项入口见 `docs/msi-manual-testing.md`，状态见 `docs/msi-open-items.md`。WiX v3 免费维护结束的安全风险作为 MSI-OI-10 保留到公开发布前复核。MSI alpha 格式配置和本机自动化基线冻结，未测组合不能对外宣称支持已验证。本节记录的下一阶段曾为 `MAC-APP`；用户后续决策见第 10 节。CLI 仍在全部计划格式完成之后。

## 10. Tauri 通用 MSI 能力补齐路线（2026-09-25 确认，未实施）

**后续产品决定，非第 9 节历史结论的改写**：用户确认忽略当前无环境可测的兼容性项目作为开发阻塞，先补齐 Tauri 中适用于通用 MSI 打包器的用户能力，应用运行时依赖自动部署仍不做；受控 WiX fragments 为常规能力，完整模板/原始 merge module 仅作为显式专家模式。`WIN-MSI-4` 的 `alpha.37` 本机结果与身份测试向量继续有效；新能力未实施前不得在 README 或能力矩阵标为已支持。固定上游快照、逐项分类及专家模式风险见 [`docs/msi-tauri-capability-audit.md`](msi-tauri-capability-audit.md)。本轮只落规划文档，没有新增 MSI 代码、NuGet 包版本或测试结果。默认**下一实施阶段**为 `WIN-MSI-5`，需用户明确启动；`MAC-APP` 排在 WIN-MSI-9 之后。

### 跨阶段不可变约束

1. Windows 构建宿主和已审计 WiX 3.14.1 继续沿用；不运行时下载工具、应用依赖，不引入付费服务。新增 WiX 官方资源先核源、MS-RL 对应源码、哈希、许可证声明和包体积。学习 Tauri 的用户能力，不复制其模板、翻译或运行时下载实现。
2. 英文、简体中文及现有 x64/ARM64、scope 的 UpgradeCode/ProductCode、组件 GUID 与版本映射必须通过固定测试向量保持兼容；新 x86 和新 locale 有独立身份。不能靠修改已有安装线的 identity 算法填新能力。
3. 直接 API 在只引用 `DotNet.Bundler.Wix` 的仓库外项目中可用；MSBuild 只是同一后端的输入映射。正式 CLI 仍在所有计划格式之后。每个实现阶段修改包内容即递增 `BundlerPackageVersion`，同步 NuGet 依赖、测试 fixture、公开示例包引用及中文说明；公开示例应用版本保持稳定。
4. 每项新/改能力都加可区分旧行为的自动化断言：公共模型/验证、WiX 源与 MSI 数据库、独立包 API、MSBuild 映射以及本机可安全执行的真实 current-user 安装/升级/修复/卸载。共用 Core 或便利元包受影响时复跑 NSIS 集成。随机测试身份、隔离包源/缓存、精确清理和保留日志沿用 `docs/development-rules.md`。仅运行旧测试不构成阶段完成。
5. 无当前环境的 UAC、ARM64 原生宿主、生产证书、真实重启等仍按 MSI 专用人工/外部清单保留“未执行”；不据此阻塞当前机器可完成的阶段，也不把这些条件算作已通过。

### WIN-MSI-5：目标架构与版本生命周期

- **前置**：核对 `adce4f0` 后实际 Git、当前包版本、目标模型与固定身份向量；以 MSI 三段版本和 major upgrade 官方规则复核新配置。`docs/msi-tauri-capability-audit.md` 为已确认范围。
- **目标/交付**：公共目标模型增加 `win-x86`，MSI 包使用正确的 x86 目录/注册表视图、组件属性和独立产品线；NSIS 不因共享模型扩展而自动接受 x86。允许显式传入**三段有效 MSI 版本**以映射应用自身版本，拒绝第四字段、回退/碰撞及无定义的预发布自动映射。`AllowDowngrades` 默认 false、仅显式选择时为 true；同版本不同内容仍拒绝。既有身份与默认安装行为不变。公开 API、MSBuild、样例配置/说明同阶段完成。
- **新增自动化/真实行为**：目标解析和格式支持矩阵、x86 数据库 `Template Summary`/组件/路径、各语种及架构 identity 向量、版本边界/重复/降级开关；仓库外 x86 API fixture 与 MSBuild 本地包消费。当前 x64 Windows 上构建并真实安装/卸载随机 current-user x86 fixture，执行可复现的 v2→v1 允许/拒绝降级及原 x64 升级回归，核对旧文件、用户文件与产品注册。
- **不做/退出**：不支持自动跨 x86/x64/ARM64 或 current-user/per-machine 迁移，不把 MSI 第四版本字段当升级版本。上述自动化及本机安全的真实生命周期通过、无既有身份漂移、独立包可消费、文档与版本同步后退出；其他宿主继续只保留准确验收状态。

### WIN-MSI-6：安装目录、界面与桌面选项

- **前置**：WIN-MSI-5 身份/生命周期通过；先确定 UI 与静默 `INSTALLFOLDER` 的允许根、恢复原安装目录和回滚规则。默认目录不改，范围内路径校验不能被静默参数绕过。
- **目标/交付**：current-user/per-machine 各自范围内可选择安装目录；WiX 原生 InstallDir UI 的品牌横幅、对话框图片及许可显示，图片尺寸/格式严格校验；快捷方式与 PATH 等 MSI Feature 为显式可选项，可选受管卸载快捷方式。PATH 用 Environment 表仅附加并移除本产品条目，不覆写用户的其他值。安装完成后启动程序仅在**用户交互勾选**时于用户会话执行；`/qn`、`/passive`、升级、修复和提权服务上下文不自动启动。补齐确有 MSI 卸载界面意义的元数据。不得复制 Tauri UI 模板。
- **新增自动化/真实行为**：非法/越界/重解析目录及图片拒绝，属性/Feature/UI/Environment 表断言；API、MSBuild 和公开示例同义映射。随机 current-user fixture 本机分别在默认和自选目录安装、升级、修复、卸载，检查旧目录/用户文件；用隔离进程环境验证 PATH 原值、追加、升级和卸载的精确保留；验证静默/被动/修复不会启动应用。交互显示、勾选及缩放可在当前机执行则记录，否则保留 MSI 专用人工条目，不阻塞其他已验证能力。
- **不做/退出**：不提供跨 scope 自动迁移、任意系统目录覆盖、静默启动或以自定义动作代替可用的 MSI 原生表。产品所有权与安装事务测试、包消费/示例及受影响回归通过后退出。

### WIN-MSI-7：语言、输入资源和构建选项

- **前置**：WIN-MSI-6 的 UI/目录契约稳定；清点固定 WiX 3.14.1 归档可用 locale、任何需要增补文件的来源/许可/大小，确认不复制 Tauri 翻译。
- **目标/交付**：在实际受支持 WiX 语言范围内以 locale 列表生成**分别独立的单语言 MSI**；调用方可提供经过键集合、编码及 culture 校验的翻译资源。既有 `en-US`/`zh-CN` ID、目录和文件名不变；新 locale 各有固定独立升级/组件身份。新增安全图标输入转换与 FIPS 构建开关，逐条检查 `candle`/`light` 的相关选项；不宣称 FIPS 认证。签名原有 SHA-256/RFC 3161 与失败清理保持不变。
- **新增自动化/真实行为**：支持语言清单、缺失/重复/非法键、未知 culture、Unicode/代码页与图标格式，语言间身份和 PackageCode 区分；多产物 API 与 MSBuild 消费、本机至少一种新增语言与英语并存安装/升级/卸载，验证各自文件与注册。FIPS 参数和本机可执行编译测试；若当前宿主没有策略环境，不把策略兼容冒充通过。重测最终 NuGet 的 WiX 文件来源、源码、许可、哈希和体积。
- **不做/退出**：不把多个语言塞进同一个 MSI 造成身份混淆，不增加弱摘要或运行时下载。全部新增 locale 的可自动化契约、至少一个新增 locale 的真实生命周期、现有语言回归及包供应审计通过后退出；语言翻译人工审校仍单列。

### WIN-MSI-8：受控 WiX 扩展与专家模式

- **前置**：前述身份、目录、语言和组件规则稳定；先把**常规模式 WiX 元素/引用白名单**、ID 命名空间、文件来源、组件 key path/安装所有权、hash 与版本变化的规则写入本格式文档及测试向量，再开放输入。
- **目标/交付**：常规模式可提供经解析校验的 `.wxs` fragments 与明确 Component/Feature 引用，以受管声明式扩展原生 MSI 资源；不允许自定义动作、任意脚本、全目录删除或改写内建身份。显式专家模式接受完整自备模板和原始 merge module，标注调用方自备逻辑的回滚/所有权责任；Bundler 仍校验可验证的身份/版本/范围、输入路径、工具/签名、警告、产物及失败清理。两模式的 API、MSBuild 名称与文档必须一眼可分，默认常规模式。
- **新增自动化/真实行为**：无效 XML、重解析点、外部引用、ID 冲突、隐藏自定义动作、未解析引用、身份篡改、不同扩展内容同版本、编译/链接警告失败；扩展输入及引用文件进入构建指纹。本机使用安全的注册表/文件 fragment 做安装、升级、修复、卸载和故障清理；专家模式用自备无害模板及 merge fixture 证明能编译、签名和失败不发布半成品，不假称任意用户模板都已验证。若扩展引入第三方二进制，责任与许可归属单独检查。
- **不做/退出**：不把专家模式的原始动作描述成受管、可回滚或 Bundler 内建运行时安装；不默认执行应用提供的脚本。两模式边界及测试通过、独立包消费/示例可操作、工具分发审计通过后退出。

### WIN-MSI-9：完整通用能力审计与再冻结

- **前置**：WIN-MSI-5..8 的可本机自动执行门槛均通过，冻结前逐条核对新旧配置、产物身份和版本迭代记录。
- **目标/交付**：以固定 Tauri 快照更新 `docs/msi-tauri-capability-audit.md`，每个通用用户能力明确为“等价已实现”“有意采用更安全语义”“另立跨格式产品路线”或“明确不适用”；整理 API、MSBuild、公开示例、README、MSI 矩阵、人工清单与外部待办。重新审计 WiX 来源/许可、最终包内容/体积、严格警告和无付费/运行时下载边界。冻结扩展后的 MSI alpha 配置与本机支持基线。
- **新增自动化/真实行为**：只修审计中发现的缺陷并为每项修复添回归；执行快速测试、全部 MSI 包消费与 current-user smoke/lifecycle/maintenance/新能力集成、受影响 NSIS 集成、两个公开示例的本地包还原/发布、安装残留和 NuGet 随包文件哈希核对。记录主机、命令、退出码、包与 MSI 哈希、日志和清理；无现成环境的人工组合继续标“未执行”。
- **不做/退出**：不在冻结阶段临时加功能，不把 Tauri 专属 updater、应用运行时或其他未实施能力写成已支持；不要求凭空完成无环境验收。矩阵每条有实现/排除依据、新增自动化和本机真实操作通过、文档间状态一致后退出，默认下一格式恢复为 `MAC-APP`。提交和推送仍只按用户明确指令执行。

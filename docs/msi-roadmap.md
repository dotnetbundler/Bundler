# Windows MSI 后端实施路线（WiX 3.14.1）

> 状态：`WIN-MSI-1..6` 的当前 Windows 11 x64 本机自动化范围已完成（2026-09-26）；
> `alpha.37` 是既有身份基线，`alpha.40` 增加 x86、显式版本映射和可选降级，`alpha.41` 增加范围内安装目录、自定义 UI、可选 Feature、PATH 与交互启动勾选，`alpha.42` 增加 38 语言独立产物、调用方 `.wxl` 覆盖、快捷方式图标与 FIPS 构建选项，`alpha.43` 增加受控 WiX 扩展与专家模式，`alpha.44` 增加 Tauri 对齐的跨格式收尾（MSI 读前 NSIS 安装目录延续）。
> `WIN-MSI-1..9` 全部完成，MSI alpha 基线冻结于 `0.1.0-alpha.43`；已全部并入 `main`。
> 阶段实施证据以 git 提交与 PR 记录为准。
> 原开发分支 `msi-development`（已并入 `main`）；历史记录中的 `codex/msi-development` 是改名前的名称。
> 规范入口：`docs/roadmap.md`；
> 上游 Tauri 审计已完成并固化进本文件与能力矩阵，逐项能力见 `docs/msi-capability-matrix.md`，外部条件与人工/特殊验收统一登记在 `docs/special-acceptance.md`；

## 1. 已核实事实、选择及风险

2026-09-23 规划时核对分支 `codex/modular-bundler-backends`、HEAD `6946daea16dff84113988b7838f36fe08a03498e`、包版本 `0.1.0-alpha.33`、干净工作区。
当时代码有 `PackageFormat.Msi`、Core 规划与 `IBundleBackend`，但没有 MSI 后端、MSI MSBuild 映射或 MSI E2E。
`tests/Windows.Nsis.Integration/LegacyMsiFixture` 的 `WixToolset.Sdk/5.0.2` 只生成 NSIS 迁移测试所需旧 MSI，不构成正式选型。
该段是规划时的历史快照，当前进度以第 6 节及实际 Git/测试为准。

用户已暂定 **WiX 3.14.1**，仅在 Windows 上构建 MSI；不要求非 Windows 宿主生成 MSI。
选它是为了复用成熟的 WiX 3 工具链和 Windows Installer 原生语义，缩短实现工作量。
这是产品选择，不等于已验证零环境、ARM64 宿主或未来安全维护。
WiX 3 已归档且免费社区维护结束；**2026-10-05 用户裁决封存：v3 维持现状不再复议**——
v4/v5 需 .NET 8+ 运行时（Windows 预载只有 .NET Framework，自包含体积不合"工具随包"定位）、
纯托管写出器工程量过大，替代方案已否决，与 Tauri/electron-builder 同姿态捆绑 v3；
若上游出具体安全/兼容事故由用户另行指示，本项不作主动复核。
若门槛失败，停止该工具选型并记录替代决策，不通过静默安装 SDK/运行时、附带未核许可文件或声称不受支持的宿主来绕过。

产品只打包调用方已准备的文件。
不内建 WebView2、VC Runtime、.NET Runtime 等任意应用运行时依赖的发现、下载或安装。
MSBuild 是当前入口；直接 API 与正式 CLI 复用同一个 Core 与 MSI 后端。
CLI 在全部格式冻结后交付（`CLI-C1`）。
能力对齐以通用桌面用户需求为准，不复制 Tauri 或 NSIS 的内部实现；尤其不在 MSI 上复刻 NSIS journal 或强行使用任意脚本自定义操作。

## 2. WiX 工具供应与发布门槛

| 项目 | 决策与 WIN-MSI-1 验证 |
| --- | --- |
| 来源/版本 | 只从 WiX 官方 `wix3141rtm` release 取得官方二进制归档；固定精确版本及文件清单。不得把现有 WiX 5 fixture 当作后端工具。 |
| 许可证 | WiX 3.14.1 上游 `LICENSE.TXT` 为 Microsoft Reciprocal License；其条款要求对分发的含 WiX 代码文件向接收者提供对应源码，并保留原有版权、专利、商标及归属声明。须逐项核对实际捆绑文件及第三方依赖的许可，设计可履行的对应源码提供方式、许可证/声明随包方式及体积成本；审计未通过不得发布。不宣称法律保证，不使用需要付费授权的工具/服务。 |
| 完整性 | 在可信来源取得一次归档，人工核对来源和实际 SHA-256；将归档哈希、内文件哈希与来源记录固定在仓库。下载或核验失败则不发布。不在运行时下载。 |
| 分发/缓存 | 测量完整归档、真正必需子集、对应源码提供方式及压缩后 NuGet 增量；只捆绑经许可且可独立工作的最小子集。复用 Core 内容寻址、跨进程锁和逐文件校验缓存；断网构建必须可用。当时约 13.8 MB 的 WiX NuGet 包体积已获用户接受（当前实测约 14.4 MB）；若分发内容或体积明显变化，重新测量并记录。 |
| 宿主依赖 | 本项只评价 WiX 3.14.1 的 `candle.exe`/`light.exe`，不计入调用 WiX 的产品或构建流程的依赖。发行二进制中两个 EXE 和 `wix.dll` 的目标属性均测得 `.NET Framework 4.5`；两个 EXE 的 CLI 标志要求 32 位进程。WiX 的该 Framework 需求不因生成 MSI 而传给最终用户。在 Windows x64、ARM64 干净 VM 核对工具启动、编译及 x86 仿真，记录 OS build 和预装依赖。若 OS 缺少必需 Framework，则不能称为零额外安装，不自动安装。 |
| 生命周期 | WiX 3.14.1 归档、免费社区支持已结束；v3 维持现状的封存裁决见 §1（2026-10-05）。发布时记录已知风险、Windows 更新兼容性和后续迁移方案；紧急漏洞或目标宿主失败仍可由用户触发重新选型。 |

官方来源：[WiX 3.14.1 release](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[WiX 3.14.1 许可证](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)、[WiX 3 维护状态](https://docs.firegiant.com/wix/wix3/)、[WiX 3 工具入门](https://docs.firegiant.com/wix3/tutorial/)。
源代码有 ARM64 目标编译选项，但不证明 ARM64 构建宿主可用：[candle 源码](https://github.com/wixtoolset/wix3/blob/wix3141rtm/src/tools/candle/candle.cs)。

**已决定/尚待验收**：用户在 2026-09-24 接受当时约 13.8 MB 的 WiX NuGet 包体积；WIN-MSI-3 增加第 15 个官方 UI 扩展文件后实测约 14.4 MB，增量和哈希见第 8 节。
前 14 个文件的审计见第 6 节，第 15 个见第 8 节及 `third_party/wix/msi-wix-provenance.md`；更换文件必须重审。
当前 WiX 文档所述 Open Source Maintenance Fee 从 WiX v6 才开始，不能套用到 WiX 3.14.1：[WiX 版本说明](https://docs.firegiant.com/wix/whatsnew/)。
`candle.exe`、`light.exe` 和 `wix.dll` 的目标框架属性均为 `.NETFramework,Version=v4.5`，但干净 Windows/ARM64 宿主的启动条件仍需 VM 验证。
若免费无补丁状态无法满足届时安全要求，应重新评估工具链。

### WiX 3.14.1 自身的“零环境”边界（2026-09-24）

这里只评价将官方 WiX 3.14.1 二进制解压后，在全新激活的 Windows 上直接运行 `candle.exe` 和 `light.exe` 编译 WiX 输入文件是否需要额外安装组件。
**不把调用者的 MSBuild、.NET SDK、应用构建流程或被打包程序依赖算作 WiX 依赖。**
二进制归档无需运行 WiX 安装程序。
当前随包两个 EXE 及 `wix.dll` 的程序集元数据均标记 `.NET Framework 4.5`；两个 EXE 的 CLR 标志均含 `32BITREQUIRED`。
这是本地静态检查，不代替干净系统实测。

| 全新 Windows 构建机 | 只看 WiX 工具自身 | 证据与待验证项 |
| --- | --- | --- |
| Windows 7 SP1 | 系统未预装 .NET Framework 4.5；解压 WiX 二进制仍需另外安装兼容的 Framework，故不满足严格零额外安装。 | 微软系统预装表；尚未在干净 Windows 7 实测。 |
| Windows 10 | 系统预装 .NET Framework 4.6 或后续版本，满足已检查到的 4.5 目标框架条件；理论上可解压后直接运行 WiX，不必安装 .NET SDK 或 WiX 安装程序。 | 这是由二进制元数据和系统预装表作出的推断；须在不同版本的干净 x86/x64 VM 实测 `candle` 与 `light` 完整编译。 |
| Windows 11 | 系统预装 .NET Framework 4.8/4.8.1，同样满足已检查到的 4.5 目标框架条件；理论上可解压后直接运行。 | 已在一台非干净 Windows 11 x64 开发机运行；干净 x64 VM 与 ARM64 上的 x86 仿真和完整编译尚未验证。 |

这张表只回答 WiX 编译器自身的运行条件，不推出任何调用 WiX 的打包产品在相同机器上的可用性。
来源：[WiX 3.14.1 免安装二进制归档](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)、[微软 .NET Framework 系统要求与预装版本](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)、[.NET Framework 版本与 Windows 对应关系](https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies)。

## 3. 第一阶段前必须固定的 MSI 语义

这些是计划契约，WIN-MSI-1 首次写代码前以 Windows Installer 官方规则和可执行测试核实，并在公开 API 中固定。
若实测推翻，应先改本文与能力矩阵，不生成带错误身份的发行包。

1. **身份**：应用 `Identifier` 是产品族输入；同一产品的安装范围与目标架构形成独立 MSI 产品线。
   建议默认使用项目固定命名空间的 UUIDv5：由规范化 `Identifier + scope + architecture` 推导稳定 `UpgradeCode`，由该产品线及映射后的三段 MSI 版本和 `ProductLanguage` 推导 `ProductCode`。
   每次 major upgrade 改 ProductCode，同一版同语言拒绝不同载荷重发；每次内容不同的 MSI 包生成新 `PackageCode`。
   允许显式指定既有 UpgradeCode 仅用于有证据的历史产品迁移，必须检查 GUID 冲突与所有权，不能靠显示名猜测。
   固定命名空间、字符串规范化及大小写规则在首个发布包前冻结并写入测试向量；不得依赖每次随机生成的 UpgradeCode 造成升级断链。
2. **版本**：MSI `ProductVersion` 只比较前三个数字字段。
   首版只接受可精确映射的稳定 `major.minor.patch`；含第四段、预发布或 build metadata 的版本明确拒绝，不默默截断。
   超过 MSI 字段上限也拒绝。
   稳定版本须单调增加，测试边界值、溢出与同版本不同载荷。
   升级默认 major upgrade，阻止降级；同版本重打包不作为升级路径。
   若以后确需预发布 MSI，先设计独立、单调且无碰撞的 MSI 版本映射再改公开契约。
3. **安装范围**：先提供明确的 current user 和 per machine **两个独立产物配置**，默认 current user；不承诺同一 MSI 在安装时自由切换范围。
   前者只写用户可拥有的位置与 HKCU，不请求提权；后者写 Program Files/HKLM 并遵循 UAC。
   跨范围升级不是自动迁移。
   x64、ARM64、x86 各自独立产物及身份；对仍受支持的 Windows 版本与各架构宿主的实际支持以验证矩阵为准。
4. **目录/组件**：默认安装根分别为 `%LOCALAPPDATA%\Programs\<产品目录>` 与对应架构的 Program Files。
   可配置目录必须通过范围、绝对路径、重解析点与所有权校验；不以用户给出的不安全路径提升卸载删除范围。
   每个文件或资源组件有稳定 GUID、稳定 key path、明确安装目录和归属；改名、移动、共享组件或改变 key path 必须遵守 Windows Installer 组件规则。
   卸载只移除本产品拥有的资源，默认保留用户数据和未知文件。
5. **事务**：使用 Windows Installer 原生安装/回滚/修复/卸载机制。
   优先声明式 WiX 表与标准动作；自定义操作仅用于确有必要且无原生替代的能力，必须写明执行时机、提权上下文、回滚/幂等/卸载所有权及测试。
   不得用自定义操作下载依赖、任意递归清理目录或覆盖系统共享状态。
   损坏包、缺少源、锁定文件、重启与修复按 MSI 原生结果定义，不套用 NSIS 退出码。

依据：[ProductCode](https://learn.microsoft.com/en-us/windows/win32/msi/productcode)、[Major upgrades](https://learn.microsoft.com/en-us/windows/win32/msi/major-upgrades)、[为未来 major upgrade 准备](https://learn.microsoft.com/en-us/windows/win32/msi/preparing-an-application-for-future-major-upgrades/)、[安装上下文](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context)、[组件规则](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-components)、[安装包结构](https://learn.microsoft.com/en-us/windows/win32/msi/installation-package)。

## 4. 验证分层与交接

| 验证层 | 内容 |
| --- | --- |
| 本地自动 | 输入/映射/模板、WiX 工具哈希与缓存、数据库表、身份/版本/组件规则、离线编译，以及独立 current-user fixture 的真实安装/卸载、升级、测试证书签名、静默/被动及修复。每次功能变更增加对应测试。 |
| 专用 Windows CI/VM | per-machine/UAC、真实重启、系统级故障/回滚、损坏包、锁定文件、不同 OS/架构及干净宿主依赖；保留 verbose log 和状态断言。 |
| 人工验收 | 生产证书和信任链、真实用户 UAC、交互 UI/语言、企业策略、真实升级来源、真实重启及难以自动覆盖的 ARM64/多 Windows 版本；按独立 MSI 用例执行。 |

接班者先读 `AGENTS.md`、`docs/development-rules.md`、`PROJECT_CONTEXT.md`、`docs/roadmap.md`、本文、MSI Tauri 审计、矩阵/待办/人工文档，再核 Git、代码与测试。
各阶段实施证据以 git 提交与 PR 记录为准；未经用户明确要求不开始后续阶段代码、不提交或推送。

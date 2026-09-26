# 项目历史记录

本文件完整保存原 `PROJECT_CONTEXT.md` 的分轮档案（原第 7..14 节），章节号保持原样以便追溯旧交叉引用。
所有版本、提交、验证结果仅代表其记录日期时的证据；当前事实以 `PROJECT_CONTEXT.md`、Git 和测试为准。
其中 §9/§10/§11 描述的是当时状态，现行版本分别以 `docs/nsis-open-items.md`、`docs/roadmap.md`、`docs/development-rules.md` 为准。

## 7. 已完成阶段与提交历史

当前 MSI 实施线：`msi-development`。
NSIS 开发线：`nsis-development`。
原 `codex/modular-bundler-backends` 已按用户要求拆分；
旧 `codex/nsis` 分支已删除，`master` 已快进至 NSIS 开发线的 `6946dae`。
这些分支指针仍须实时核查。

| 提交      | 内容                         |
| --------- | ---------------------------- |
| `7928d5b` | NSIS 品牌资源和包元数据      |
| `3d2a602` | NSIS 生命周期 Hook           |
| `11f36c8` | 将 NSIS 重构为可复用后端包   |
| `a543acb` | 完成模块化 Bundler 包结构    |
| `8c82f13` | 嵌入多宿主 NsisToolset       |
| `688966e` | 语义版本安装策略             |
| `a362886` | 旧 MSI 迁移                  |
| `2de3752` | 文件关联与深链接             |
| `db3da24` | 在示例中展示 NSIS 功能       |
| `d32a816` | Windows Authenticode 签名    |
| `76ecaa9` | 安装器自动化协议             |
| `af01dc0` | 快捷方式生命周期和所有权安全 |
| `471e5d0` | 增加项目交接基线文档         |
| `b25a514` | 安装事务和进程中断恢复       |
| `5c0d2cf` | 传播重启退出码并增加重启验收 |
| `7ace218` | 锁定载荷安全失败和恢复       |
| `f0fafb9` | 安装持久化错误检测         |
| `3283d51` | 原子提交和提交后清理       |
| `6e6da77` | 锁定载荷策略和交互提示     |
| `708285a` | 集中人工与外部环境验收手册 |
| `1225eec` | 覆盖事务恢复检查点         |
| `b6a7221` | 卸载失败与中断后的前向恢复 |
| `aa2e3e9` | 固化通用打包器产品边界与路线 |
| `c50201d` | NSIS 能力审计、压缩配置与共享配置修复 |
| `f124ee7` | 完整 Windows 签名流水线 |
| `783b835` | 完整内置多语言与本地化验证 |
| `71a5c90` | 冻结 NSIS 安全打包基线 |

`3723cd7` 是安装范围支持的历史提交，位于这条提交链的更早位置。

## 8. 最近一次验证证据

`0.1.0-alpha.33` 在 2026-09-23 的实际验证：41 项 Release 单元/契约测试通过；
Pack 成功，六个同版本 NuGet 包存在；
完整 Windows NSIS 安装/卸载集成测试通过。
集成新增用配置 A 的 1.2 安装器生成 active journal，配置不同的 B 1.3 安装器返回 `6` 并保留现场，原安装器 `/S /RECOVERONLY` 恢复旧版本与 EXE 哈希且不继续升级。
载荷文件和注册表快照内容分别被篡改时返回 `2` 并保留 journal；
恢复卸载器副本被篡改时同样拒绝执行。
测试还覆盖原有目标路径和注册表目标篡改、失败注入、签名、语言等回归。
首次新测试运行因断言把“中断时版本”误认为“恢复后版本”而失败；
修正断言后完整集成通过。
实际运行命令：

```powershell
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.33
```

以下保留 `0.1.0-alpha.32` 的历史验证记录，不应扩写成未执行过的平台兼容承诺：

```powershell
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.32
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -r win-x64 --force
```

结果：

- 41 项单元/契约测试全部通过，覆盖原有 22 种语言与完整 NSIS 契约回归，并新增验证同一快照名、文件路径和注册表目标贯穿备份、校验与恢复，且模板不再暴露 journal 定向的通用回滚入口；
- 生成六个 `0.1.0-alpha.32` 本地 NuGet 包；
- Windows NSIS 安装/卸载集成测试通过；
- 自签名集成实际验证最终安装器、安装后主 EXE 和卸载器均包含预期证书；构建日志确认 Bundler 原生插件也经过内置 signer；
- Windows 集成在 zh-CN UI 环境运行仅含 Japanese 的安装器，成功保留 Unicode 产品名、安装路径、开始菜单目录、快捷方式参数、卸载显示名和描述，并完成清理；
- `Persian` 对外名称已在内部映射到 NSIS 3.12 的 `Farsi`/`LANG_FARSI`，由真实编译回归保护；
- `HelloBundledApp` 使用 English、SimpChinese 自定义文件和内置 Japanese 展示语言选择器，并从当前本地包发布；
- 烟雾测试从 `.lnk` 读取并验证参数、工作目录、图标和 AppUserModelID；
- 集成覆盖用户手动删除快捷方式后更新不重建、旧名称迁移、外部程序接管同名快捷方式后卸载保留；
- Native AOT COM 修复后完整流程通过；
- Restart Manager 精确关闭安装目录内主程序，另一路径同名进程保持运行；
- post-install Hook 失败后立即恢复旧 EXE、版本注册表、运行时数据和快捷方式；
- 直接终止安装器进程后保留 active journal，下一次启动自动恢复并清理；
- Windows 集成分别篡改 active journal 的快捷方式 `path.txt` 和注册表 snapshot subkey；
  两次恢复均实际返回 `2`、保留 journal，并保持清单外文件和 HKCU sentinel 不变，恢复原始 metadata 后正常恢复；
- 可控 reboot flag 场景返回 `3010`，载荷和注册表保持已提交状态、journal 已清理且 `/R` 未启动应用；
- 升级时显式临时复制并同步等待旧卸载器的回归路径通过；
- 锁定非主程序载荷时稳定返回 `2`，保留 active journal；释放锁后的下一次启动恢复原文件、版本注册表并清理 journal；
- 契约测试验证交互安装在载荷写入失败时引用中英文 `PayloadWriteFailed` 提示，再进入同一事务回滚；静默锁定载荷集成回归仍返回 `2`；
- Native AOT 插件已重建，SHA-256 为 `ED3A50B0466CEDF319AA51384E7B7BDB14ED0D5938487E28561D2DD494E423B3`；
- 可控快捷方式和注册表持久化失败均返回 `2`，并恢复 1.1.0 版本、原 EXE 哈希和快捷方式状态；
- 可控提交清理失败仍返回 `0` 并保留 `.committed`，下次安装器启动先清理它，不回滚已安装的 1.2.0；
- 事务快照和激活故障均在修改持久状态前返回 `2`，保持 1.1.0 载荷、注册表、运行时数据和快捷方式不变，且不留下 active journal；
- 载荷恢复、注册表恢复和 active journal 清理故障均在预期检查点返回 `2` 并保留 active journal；下次启动 1.1.0 安装器后恢复旧载荷、版本注册表和运行时数据，并清理 journal；
- post-uninstall Hook 故障返回实际卸载进程退出码 `2`，保留 active 前向 journal、恢复卸载器与注册表锚点；下一次安装先完成卸载并清理 journal；
- post-uninstall 进程树被终止后保留相同恢复状态；下一次安装幂等完成旧卸载，再写入新载荷；
- Windows 集成新增安装快照、transaction journal、普通卸载载荷树和 `/DELETEAPPDATA` 应用数据树 junction：安装器/卸载器均返回 `2`，保持原状态和外部 sentinel 不变，不遗留 transaction journal 或 forward journal；
- 完整正常安装回归通过。真实 ACL/权限拒绝、磁盘耗尽和真实重启仍未在本机验证。
- 集成脚本的 `finally` 已清理其安装目录、注册表、进程、临时 Hook 标记和测试证书；保留生成的 `artifacts/windows-nsis-integration` 作为构建产物。

文档变更至少执行：

```powershell
git diff --check
```

功能变更应重新执行单元、Pack 和 Windows NSIS 集成脚本。
涉及 UAC、Windows 版本差异、真实证书或真实旧 MSI 的结论必须单独列出测试环境和证据。

## 9. 已实现但仍需外部验收的事项

这些 NSIS 事项的外部输入摘要保存在 `docs/nsis-open-items.md`，完整的人工执行顺序、命令、预期结果、证据和清理要求集中在 `docs/nsis-manual-testing.md`。
总入口见 `docs/manual-testing-index.md`，MSI 采用独立文档。
新增无法在普通本地自动化环境完成的已实现能力时，必须同步对应格式的两个文档；
可自动化的测试仍由仓库测试承担。

1. **正式 Authenticode**：使用生产签名身份、私钥保护设施和公开 RFC 3161 服务验证公开信任链、时间戳策略、硬件/云签名行为。
2. **生产旧 MSI 迁移**：使用真实发布过的 ProductCode/UpgradeCode，以及 x86/x64、current-user/per-machine 旧包验证识别和权限行为。
3. **提权安装矩阵**：在明确的提权 CI 或人工环境中验证 `perMachine`、`both`、HKLM、Program Files、卸载与 `/R` 降权启动。
4. **固定项清理**：在支持的 Windows 10/11 构建和组策略下验证开始菜单/任务栏已固定项的升级与卸载行为。
5. **真实重启删除**：在可抛弃、已提权的 Windows 虚拟机运行 `tests/Windows.Nsis.Reboot/Verify.ps1` 的 `Prepare` 和 `Verify` 两阶段，验证锁定文件触发 `3010`、系统 pending rename 项和重启后删除完成；
   脚本不会修改或清空共享的 `PendingFileRenameOperations`。

## 10. 未完成路线与建议顺序

正式路线见 `docs/roadmap.md`。当前顺序为：

1. `WIN-MSI-1..5`：按 `docs/msi-roadmap.md` 已完成当前主机本机自动化范围并形成 MSI alpha 冻结基线；
   Windows 宿主使用 WiX 3.14.1，外部兼容矩阵仍待验收；默认下一阶段为 `WIN-MSI-6`；
2. `WIN-MSI-6..9`：完成已确认的 MSI 通用桌面能力并冻结格式；
3. `MAC-APP`、`MAC-DMG`；macOS 决策若正式纳入 PKG，则在 Linux 前完成 `MAC-PKG`；
4. `LINUX-DEB`、`LINUX-RPM`、`LINUX-APPIMAGE`；
5. 所有上述打包格式完成后再进入 `CLI-C1`，把现有 CLI 原型产品化。

Tauri 能力按“通用打包能力、格式特定能力、Tauri runtime 专属能力”分类。
签名是正式路线的一部分；
WebView2、VC Runtime 等任意应用运行时依赖的自动发现、下载和安装当前明确不做。
真实重启、UAC、生产证书、真实旧 MSI、多宿主/ARM64 和真实 ACL/磁盘耗尽保留在外部验收队列，不反复阻塞快速开发进入下一阶段。

### 仍不得宣称完成

- 已完成跨环境/生产验收并可广泛发行的 MSI/WiX 后端；
  当前 WIN-MSI-1..5 的本机范围已验证，x86 已在 x64 宿主完成真实生命周期，但原生 x86/ARM64 宿主、per-machine UAC、外部宿主、生产签名、真实 UI 和重启仍待验收；
- macOS `.app`/DMG；
- Linux DEB/AppImage；
- 正式发布并受支持的 CLI（仓库当前只有功能有限的原型）；
- 22 种内置语言的母语/专业内容审校、RTL 和完整 UI 缩放矩阵（MT-11）；
- 所有 Windows 版本的自动取消固定；
- 所有宿主和架构的真实 CI；
- 正式证书和生产旧 MSI 的端到端验收；
- 包含锁定文件安装替换、经真实重启确认的待删除完成、完整 ACL/ADS 保真、链接迁移和可回滚卸载在内的全场景恢复；重解析点当前是已验证的安全拒绝策略，不是链接迁移能力。

## 11. 文档、测试与提交约定

跨格式的文档、测试分层、版本、示例、NuGet 包消费、报告、Git 与安全清理规则集中在 `docs/development-rules.md`，此处不再维护第二份可能漂移的清单。
当前测试入口：快速单元/契约 `tests/Bundler.Tests`；
独立 API 包消费 `tests/Nsis.Api.PackageFixture` 和 `tests/Msi.Api.PackageFixture`；
真实 Windows 测试分别在 `tests/Windows.Nsis.Integration`、`tests/Windows.Msi.Integration`。
MSI 本机安装需 `-ConfirmLocalInstall`；
外部环境边界仍按各格式人工文档记录。

## 12. 已知历史问题与已采取方向

- 早期卸载注册命令曾出现把 `$"...\Uninstall.exe$"` 当作文件名的错误引用；后续模板必须始终验证注册表中的卸载命令能被 Windows 实际解析。
- 早期每个项目中间目录解压工具的思路已放弃，改为 NuGet 内嵌压缩包 + 用户级内容寻址共享缓存。
  示例为了复用 NSIS 自带图片，会单独把演示图片解压到自身 `obj`，这不是工具链重复解压策略。
- 早期安装器显示英文或语言选择器行为不符合预期，现通过显式语言列表、选择器开关和完整中文 `LangString` 文件处理；完整多语言仍在路线中。
- “程序目录里运行时创建的文件是否被卸载”取决于它是否属于构建载荷记录：新路径默认保留，同名覆盖载荷会删除；选择删除应用数据则整个安装目录都删除。
- “未知发布者”提示并不是每次都出现；它受文件是否带网络来源标记、SmartScreen、系统策略、签名和启动入口影响，不能用是否弹窗单独判断签名是否存在。
  应使用 `Get-AuthenticodeSignature` 验证。

## 13. 任务/对话衔接信息

- 本文档创建时的当前任务是原长对话的继任整理入口。
- 曾用于记录 NSIS 路线的任务唯一标识为 `01a09dd0-8e9b-7e31-bd0e-97b39d3b3e64`，其当时标题为 `NSIS：后续功能实施路线`。
  标题可被用户修改，唯一标识才是稳定定位依据。
- 快捷方式阶段的继任任务唯一标识为 `01a0ba2e-9d6c-7563-94c9-4996cba4d0e9`，其工作已完成并提交为 `af01dc0`。
- 用户后续若没有明确要求记录到其他地方，NSIS 后续信息应记录在当前工作任务以及本文档，不再把已归档旧任务当作默认记录位置。

## 14. AI 接管协议

新的 AI 收到“开始接管 Bundler 项目”后，必须先完整阅读：

1. `PROJECT_CONTEXT.md`；
2. `AGENTS.md` 和 `docs/development-rules.md`；
3. `docs/roadmap.md`、`README.md`；
4. `docs/manual-testing-index.md` 和当前格式的路线、能力矩阵、open-items、人工测试文档；
   NSIS 历史内容仍在 `docs/nsis-upstream-reference.md`、`docs/nsis-capability-matrix.md`、`docs/nsis-open-items.md` 和 `docs/nsis-manual-testing.md`；
5. 与准备处理的阶段直接相关的代码和测试。

本文档是项目交接基线，但代码和自动化测试才是最终事实。
如果文档、代码、测试或 Git 状态不一致，接管者必须先调查并向用户说明差异，不能自行假设，也不能要求用户重新复述本文档已经包含的信息。

### 14.1 开始前检查

```powershell
git branch --show-current
git rev-parse --short HEAD
git status --short
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
```

预期基线：

- MSI 分支：`msi-development`，从 `71b028c` 继续；NSIS 开发分支：`nsis-development`，指向 `6946dae`。
  两者均须实时核查。
- NSIS 冻结起点：`71a5c90`；当前 HEAD 应实时核查，不把本文档的历史提交误认为最新提交
- 包版本：`0.1.0-alpha.35`（交接快照；Git HEAD 与包版本均须实时核查）
- 安装事务、Restart Manager、对应测试、示例和文档已经实现并提交；不得重新制作原型或把这些能力当作未完成项。

上述分支、提交和版本是本文档最后整理时的快照。如果仓库已经向前推进，应调查后续提交和改动，并更新本文档，而不是强行退回该提交。

### 14.2 修改前必须向用户汇报

正式修改前，接管者必须先汇报：

1. 对当前架构的理解；
2. 已完成能力与未完成边界；
3. 下一阶段准备解决的问题；
4. 计划修改的项目、文件范围和测试；
5. 发现的文档、代码、测试或 Git 状态差异。

只有完成这次核对后才能开始正式实现。不要重新实现已经完成的 NSIS、签名、自动化协议或快捷方式原型。

### 14.3 默认下一阶段

以下为 WIN-MSI-1/2 时的历史接管记录；**当前下一阶段以文首及最新阶段记录为准**。

用户已明确说“提交，然后开始”；
规划文档提交为 `71b028c`，`WIN-MSI-1` 代码已完成本机范围阶段一；
用户已要求提交，实际 HEAD 和工作区状态以 Git 为准。
NSIS 迁移 fixture 的 `WixToolset.Sdk/5.0.2` 不代表正式选型；
本阶段使用 WiX 3.14.1。
当前已有直接 API、MSBuild 映射、固定工具子集与源码分发、最小 current user MSI 构建和数据库自动化验证。
按用户要求与 NSIS 测试分层一致，2026-09-24 在当前 Windows x64 开发机使用每轮独立产品身份执行真实静默安装与卸载，检查文件、产品注册、未知用户文件保留及清理，均通过；
脚本为 `tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall`，日志、MSI SHA-256、ProductCode 和环境见 `docs/msi-roadmap.md` 第 6 节。
当前工作区的 49 项自动化测试及完整 Windows NSIS 安装/卸载集成回归也已通过，NSIS 固定测试路径和卸载注册项无残留。
干净宿主 Framework、ARM64 宿主/用户端、UAC 与高影响故障仍待独立人工/外部验收，不宣称广泛支持。
用户已于 2026-09-24 接受当前约 13.8 MB 的 WiX NuGet 包体积；
第一阶段已完成本机范围退出条件：14 个工具子集文件逐项对应官方源码和许可，生成的 WiX NuGet 包包含许可证、源码及声明；
49 项测试以 --no-restore 再次通过，涵盖新缓存解包 WiX 和实际编译 MSI，打包复核也通过。
干净 Windows/ARM64 环境依用户说明暂不执行，保留待验收且不扩大支持声明。
评价 WiX 3.14.1 自身的零环境要求时，只看 candle.exe/light.exe 及其依赖，不混入 Bundler 的 MSBuild 或应用构建环境：两个 EXE 及 wix.dll 均目标 .NET Framework 4.5；
Windows 7 SP1 未预装所需 Framework，Windows 10/11 预装版本理论上足够，但干净宿主实际编译仍需 VM 验证，详见 docs/msi-roadmap.md 第 2 节。
CLI 在计划的 MSI、macOS 和 Linux 打包格式完成后再做。

快速开发期按 NSIS 的分层测试方式继续 MSI：本机可安全运行的功能在同阶段增加自动化和真实安装/卸载回归，不把代码生成/数据库断言当作系统行为证据；
缺环境的测试单列 `docs/msi-manual-testing.md`，不阻塞下一阶段，也不扩大支持声明。
WIN-MSI-1 的详细证据见 `docs/msi-roadmap.md` 第 6 节。

WIN-MSI-2 已按用户“开始”在当前 Windows 11 Pro build 26200 x64 主机推进：`MajorUpgrade` 两版本真实安装/升级/卸载，降级和同版本异内容包拒绝，桌面/开始菜单快捷方式、关联和协议候选注册、用户文件保留均经 `tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -ConfirmLocalInstall` 验证；
产物哈希与 verbose log 见 `docs/msi-roadmap.md` 第 7 节。
per-machine 只已生成独立身份、Program Files/HKLM 的包并检查数据库，未执行 UAC/真实提权安装。
协议实际在默认应用 UI 中选择并唤起、快捷方式同路径被接管行为也仍需人工；
原生 MSI 不提供 NSIS 式的内容级快捷方式所有权保护。
新增数据库测试使当前自动化共 50 项并通过；
阶段一 smoke 也以新后端重跑通过。
下一阶段为 `WIN-MSI-3`，处理签名、语言、修复/维护与失败/回滚/重启，不做 CLI 或 NSIS 扩展。
未经用户明确要求不提交或推送。

阶段二实现时遗漏 NuGet 包版本迭代，用户指出后按 NSIS 的既有规则将仓库统一版本从 `0.1.0-alpha.33` 升为 `0.1.0-alpha.34`。
随后用户明确要求示例应用版本不随工具开发迭代，因此 Hello MSI 示例的 `BundlerVersion` 保持 `1.0.0`；
升级/降级由独立集成 fixture 的版本参数测试。
示例和 NSIS 示例一样在项目文件中指定仓库 `artifacts/packages` 为 `RestoreSources`，并固定引用当前开发包版本。
NuGet 的 `project.assets.json` 和缓存决定 `dotnet publish` 实际加载哪个已打包版本，不直接引用正在修改的 `src/`。
当前机器先前缓存的 alpha.33 元数据来源是仓库 `artifacts/packages`。
alpha.34 已 pack 出七个同版本包；
此前用隔离缓存还原示例验证七个依赖均为 alpha.34，曾生成 `Hello MSI App-1.1.0.msi`，那是纠正示例版本前的历史产物，不作为当前示例版本。
工具包版本与 MSI 产品版本是两套独立标识；
此前安装的示例 `1.0.0` 不会因只升级 NuGet 包版本而自动成为新 MSI 产品。
本轮未运行该示例的卸载命令；
本轮末查询其 ProductCode 的 ProductState 为 `-1`（未安装），应以实时系统状态为准。

版本补正后再次执行 Release 构建与 pack、50 项 Bundler.Tests、alpha.34 的 Windows NSIS 安装/卸载集成、Windows MSI lifecycle 集成，均通过；
后者覆盖真实升级、卸载、降级和同版本异包拒绝。
当前机器、MSI SHA-256、临时日志目录等可核对证据见 `docs/msi-roadmap.md` 第 7 节。
Hello MSI App 未被这些测试操作。

按用户要求统一公开示例工作流：NSIS 与 MSI 示例都在 `.csproj` 中固定当前开发包版本、设置本地 `RestoreSources`，从仓库根目录执行 `dotnet pack Bundler.slnx -c Release -o artifacts/packages` 后即可直接 `dotnet publish`。
MSI 示例曾缺少 `RestoreSources`，alpha.34 不在公网源导致 NU1101；
补齐后原样 `dotnet publish .\samples\HelloMsiApp\HelloMsiApp.csproj` 已成功还原。
随后发现旧阶段一 `1.0.0` MSI 及其 manifest 占用输出路径，按 MSI 同版本异内容保护不能覆盖；
已将这两个历史构建产物及 wixpdb 保存在该示例 `artifacts/previous-msi/`，保留用户已安装的应用不动。
原命令再次执行两次均成功，生成当前 `Hello MSI App-1.0.0.msi`；
NSIS 示例的普通 Release publish 也通过。
以后升级/降级只由独立测试 fixture 操作，不迭代公开示例应用版本。
各格式的构建产物冲突仍按格式自身规则处理。

按用户指出的 NSIS 包消费测试基线，已补 `tests/Msi.Api.PackageFixture`：只引用 `DotNet.Bundler.Wix`，在 MSI smoke 脚本中复制到仓库外临时目录，使用本轮打出的本地包与隔离缓存，直接调用 `WixBundler` 生成 MSI。
MSI 的 MSBuild fixture 与 lifecycle 脚本也核对 `project.assets.json`、实际包版本和隔离缓存；
smoke 脚本检查 WiX 包的程序集及许可/源码/哈希/声明。
2026-09-24 当前 Windows 11 Pro build 26200 x64 本机再次运行 MSI smoke 和生命周期集成，两者均通过；
独立 API MSI 哈希、真实安装产物 ProductCode、生命周期包哈希及日志见 `docs/msi-roadmap.md` 第 7 节。
本次测试差异由跨格式规范 `docs/development-rules.md` 固化，根 `AGENTS.md` 指向该规范。
MSI 尚未冻结，下一阶段仍是 WIN-MSI-3。
未经明确要求不提交或推送。

本轮最终验证还加入共享的 `tests/AssertLocalRestore.ps1`，同时由 NSIS 与 MSI 集成入口核对本地源、包版本和隔离缓存，并拒绝意外公网源；
51 项快速测试通过，其中新增公开示例/API fixture/集成脚本包版本同步断言。
MSI smoke 复跑通过，独立 API 包实际编译 MSI 的 SHA-256 为 `91071954ED7CBB7FA90B62EBC775A4176382FA65A53ABFF1B16E3A679F38EE06`，MSBuild 包编译及真实安装/卸载 MSI 为 `DD7BAE8709DCA5B5299A39A7A77792E426DACBDC244F37745220A02D0ADECBDD`，ProductCode `{1BAE73D8-BE20-5B82-8064-149E01BCBDE8}`；
证据在 `%TEMP%\Bundler-Msi-Smoke-e286c715a3d04cb1afaf89807337ba2a`。
MSI lifecycle 复跑通过，v1/v2/异包 SHA-256 依次为 `953C7CF84C42865C3404D08D63B3482156DD8D5FDD40932EBEE080B75EF928C1`、`A88EDF3C168B10C4B2D705877A69961DF10984A3F02EB19310C92AF0AE19B582`、`0B31B9232989AD69F5B489E664ABF6ACDC2AE9CC049B3B614CB13B2081EE0D27`；
证据在 `%TEMP%\Bundler-Msi-Lifecycle-f1203328eac740ffbd099e7201142927`。
NSIS 全量集成在共享检查加入后第一次复跑于旧有的“Committed rollback journal was not cleaned up”断言失败，同一脚本第二次复跑通过；
未查明第一次失败原因，不能把它当作稳定无故障证据，也不能归因于包源检查。
下一次复现时应先保存 active journal 和安装器状态再清理测试现场。

最后重新 `dotnet pack Bundler.slnx -c Release -o artifacts/packages`，在独立 NuGet 缓存、独立输出目录中分别 `dotnet publish` HelloBundledApp 和 HelloMsiApp，并以共享还原断言确认两个公开示例都只使用本地包源和 alpha.34；
两种安装器均生成成功，未安装公开示例。
验证目录 `%TEMP%\Bundler-Public-Samples-4ab3e0334a87448e8c4496a66a2b34d6`，NSIS/MSI 示例 SHA-256 分别为 `DFA8EAE223FE1E5F2B054687B4EB6227EAEB5503E096E31743BCDD2E16DED0CC`、`2CACDF6816FD41BA57C57279DBC2F433E0711A60990D1ABC145727EE59457715`。
最终 `dotnet build Bundler.slnx -c Release --no-restore` 为 0 警告、0 错误。

**包源约定补正（2026-09-24）**：用户发现 MSI 的 MSBuild 集成 fixture 没有 NSIS 的 `<RestoreSources>$(BundlerPackageSource)</RestoreSources>`，此前只有 MSI 脚本的 `dotnet restore --source` 指定包源。
核对后没有 MSI 特有理由，现已在 `BundlerMsiSmoke.csproj` 加入与 NSIS 相同的声明，MSI smoke/lifecycle 脚本传 `BundlerPackageSource`，API fixture 同样由项目属性选择本地源，不再依靠脚本隐藏的 `--source`。
51 项快速测试中的版本/包源对齐断言已覆盖 NSIS/MSI 两类 fixture；
实际 `project.assets.json` 和隔离缓存仍由 `tests/AssertLocalRestore.ps1` 核验。
当前 Windows 11 Pro build 26200 x64 上重新运行 MSI smoke 与 lifecycle，均通过真实安装/卸载、升级与拒绝场景。
Smoke 的独立 API/MSBuild MSI SHA-256 为 `7228EFEFA6F1B9D67D31C7804BE050813ADA4A0FF23CFBDA982A64B45BA539DE`、`E24D9D739322146ED38F1D77B2D95917AA01357E1E5DD5C4BB8BB4AE2376150C`，日志 `%TEMP%\Bundler-Msi-Smoke-f6e19c853c1445828ff8ca2083cc20ec`；
lifecycle v1/v2/异包 SHA-256 为 `C364CC5A416ECB5FEC5F71C9254351FA496FFD623187E105FD599535F4E8A581`、`E106C9A9D3C35B162E4CE5EEB48719D2B9D40039192017C30C2514C56842AE9B`、`2217F66F46F973EBD4A42FDDF3C3CD9AC8A0A7EAD5ADFE80B2C950B1F71ECF21`，日志 `%TEMP%\Bundler-Msi-Lifecycle-d27c202d862c48aa9508a92a8c5bfa66`。
普通沙箱下第一次启动测试时 MSBuild 无法读取本机 `AppData\Local\Microsoft SDKs`，这是沙箱访问失败、测试未执行；
取得所需本机读取权限后 51 项测试通过。
此次只改测试 fixture、脚本与规则文档，NuGet 包内容未变，包版本保持 alpha.34。

用户进一步澄清：希望各后端的**测试风格**统一，包括目录、命名、fixture、包源、入口脚本、断言、日志和清理习惯；
不同格式的具体测试内容和数量按其能力决定，不要求逐项对应。
根 `AGENTS.md` 提供入口，`docs/development-rules.md` 第 5 节记录规范；
确有组织或调用方式上的特殊原因时说明即可。
此处仅记录决策，规范细节以该文件为准。

### 14.4 WiX 结构与测试重写（2026-09-24）

本轮在 `codex/msi-development` 的 `44f18e6` 基础上整理 WIN-MSI-2 已有实现，**未启动 WIN-MSI-3，也未改变 MSI 产品语义**。
`WixBundleBackend` 保留构建、输出校验与文件收集；
`WixProductDocument` 承担 WiX XML 生成，`WixPackagePaths` 集中路径规范化。
快速测试仍由 `tests/Bundler.Tests` 同一入口执行，WiX 用例从过长的 `Program.cs` 移到 `WixTests.cs`，MSI 数据库读取器单独存放；
把原先混在一个构建用例里的已验证产物复用、同版本载荷变化、重解析点拒绝和编译器缓存恢复拆为独立回归，并新增公开 API 层的非法路径拒绝。
测试用例按 MSI 语义设计，不要求 NSIS 有一一对应的场景。

两份 Windows MSI 脚本共用 `MsiTestSupport.ps1`。
MSBuild fixture 现在明确复制项目、程序和资源到每轮仓库外目录；
lifecycle 的 v1/v2/异包分别拥有独立项目和 `obj`，同时共用本轮隔离 NuGet 缓存。
首次隔离运行揭示 fixture 原来隐含依赖仓库级 `ImplicitUsings`，已在 fixture 项目中显式声明；
修复后真实 current-user smoke 与 lifecycle 均通过。
工具包内容变化，版本升为 `0.1.0-alpha.35`；
公开示例应用版本保持 `1.0.0`。
56 项快速测试通过；
七个 alpha.35 NuGet 包 pack 通过。
Windows 11 Pro build 26200 x64 上，smoke 的独立 API/MSBuild MSI SHA-256 分别为 `29FCAB8789ED0927E28E8697D5A7FEAE848DCB99FAB4E9BB801EFA8F30C1CC37`、`2C8FD6AF20184D18ADF75B1270FC26F1C4A94AF6E84D43A699CF036731F6EF9E`，ProductCode `{E56AC800-8B14-5155-8833-9F382F6F3611}`，包与 verbose log 留于 `%TEMP%\Bundler-Msi-Smoke-143cd6323c00486c821be65e32c671e4`；
lifecycle 的 v1/v2/异包 SHA-256 分别为 `BFE26B5DF9E2648BD8FFAFAF8CDE6643655DAFF3F12314F1DC2DA31EAC81A9A1`、`6DF340A3115C0D88CE800AE16CC22574FC3CB7E1A1BEB9DF65794E70B08257D8`、`BB4BE96F33AC87FBDC3F17B0E49FC3FEBF4DD76FCC4961C40E85F89AA9348BFC`，日志留于 `%TEMP%\Bundler-Msi-Lifecycle-03d852c11f204657a8649a88569f18de`。
两者均退出 0，分别实测安装/卸载及升级、降级/异包拒绝、桌面注册与用户数据保留。

NSIS 全量 Windows 集成在先打出仓库本地 alpha.35 包后退出 0，输出 `PASS Windows NSIS install/uninstall integration`；
第一次启动因本地包尚未生成而在测试前报 `Package not found`，不属于安装断言失败。
`HelloMsiApp` 与 `HelloBundledApp` 从本地 alpha.35 包源还原并分别生成 MSI/NSIS；
其 `project.assets.json` 显示七个 Bundler 包均为 alpha.35，本地源为 `artifacts/packages`。
示例 MSI SHA-256 为 `FEA2458256C6796DDF29F1D278E805F7FE2F67F4CF9A794D1D864F7159AEA180`，NSIS 安装器为 `C05306150F03913310E2E0BEC284C2332FBA52C54DF7455C6EB798CC9FC74151`；
公开示例未安装。
WiX 包大小 13,790,283 字节。
per-machine 安装、干净 Windows/ARM64、生产签名等外部验收边界不变；
下一阶段仍为 WIN-MSI-3。

### 14.5 执行约束

执行时遵守根目录 `AGENTS.md` 与 `docs/development-rules.md`。
本文只保留当前状态和证据；完成阶段后更新状态，即使尚未提交也要保证下一次仅凭仓库文档即可接续。

### 14.6 WIN-MSI-3 本机结项与下一阶段（2026-09-24）

当前分支 `codex/msi-development`，阶段开始时 HEAD `84c1e46` 且工作区干净；
本节阶段三变更后来提交为 `89a535c`，当前状态仍以 `git status` 和 `git rev-parse HEAD` 实时核对。
阶段三包版本为 `0.1.0-alpha.36`，公开示例的应用版本仍为 `1.0.0`。
只修改 MSI 后端、共享 MSBuild 的 MSI 映射、工具供应、版本引用、相应测试与文档；
未做 CLI 或继续扩展 NSIS。

实现事实：`WixBundlerOptions.Signer` 复用已有 Windows 签名组件，先签隔离载荷再签最终 MSI；
签名失败清理输出，同版本已签产物不静默复用。
`WixBundleConfiguration.Language` 提供英文 `1033` 和简体中文 `2052` 单语言产物；
中文使用独立升级身份、组件、目录和文件名，英文历史身份未改。
应用提供 RTF 许可时使用随包固定的官方 `WixUIExtension.dll` 最小交互 UI；
未提供许可时不代应用展示许可条款。
原生 `msiexec` 负责 `/qn`、`/passive`、`/fomus`、失败码与回滚；
生产 MSI 不加入测试故障动作或自定义运行时代码。
WiX 归档新增一个官方文件，哈希/源码/许可审计见 `third_party/wix/msi-wix-provenance.md` 和 `THIRD-PARTY-NOTICES.md`。

本机 Windows 11 Pro build 26200 x64：`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 最终 **60 项全部通过**，包含新增的语言身份/数据库、签名顺序、真实短期自签名证书签 PE/MSI、签名失败及同版本拒绝。
`tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall`、`VerifyLifecycle.ps1 -ConfirmLocalInstall` 和新增 `VerifyMaintenance.ps1 -ConfirmLocalInstall` 均通过。
维护脚本从隔离本地 alpha.36 包源构建随机 current-user MSI；
损坏包返回 1620，测试副本的延迟失败在日志记录 `FileCopy` 后返回 1603 且无托管残留；
`/passive` 安装/卸载、`/fomus /qn` 修复、中文 RTF 包与英文包并存及分别卸载通过。
三轮 MSI SHA-256、ProductCode、日志目录见 `docs/msi-roadmap.md` 第 8 节。
NSIS Windows 集成使用同一版本本地包后退出 0，输出 `PASS Windows NSIS install/uninstall integration`；
最初一次在打出 alpha.36 本地包前于测试前报 `Package not found`，随后先 pack 再完整复跑通过。
任何测试证书都未进入仓库。

最终 `dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages` 产出七个 alpha.36 包。
`DotNet.Bundler.Wix` 包大小 14,397,524 字节、SHA-256 `A43C43F1731F6ABB45163EECE6A25866666C9301C06572AE0523D003A7862B99`；
实际包包含与仓库一致的 `THIRD-PARTY-NOTICES.md`、WiX MS-RL、对应源码归档和 SHA256SUMS。
公开 `HelloMsiApp` 与 `HelloBundledApp` 都从项目声明的本地 `artifacts/packages` 恢复七个 alpha.36 依赖并发布成功；
示例安装器分别留在 `%TEMP%\Bundler-Public-Sample-Msi-alpha36` 与 `%TEMP%\Bundler-Public-Sample-Nsis-alpha36`，SHA-256 分别为 `F13B8951EA105C80B4035E4B059260BC4EA51BA8C2D94EFBFD4B82B01DBAAF46` 和 `AFDF83FAC325C88E537A2AC2A32359559CBD6834C22F85B34A9B6D8145BDBD51`。
公开示例未安装；
真实安装使用随机 fixture。

未验收：生产证书/时间戳、真实双语言交互 UI/辅助功能、UAC/per-machine、干净 Windows 10/11/ARM64、缺失修复源、锁定文件和实际重启/3010。
相关人工步骤与外部待办分别保存在 `docs/msi-manual-testing.md` 和 `docs/msi-open-items.md`，不能写成已通过。
该段为阶段三结束时的历史交接；
阶段四的实际状态和下一步见下节。
未经用户明确要求不提交或推送。

### 14.7 WIN-MSI-4 本机冻结基线（2026-09-25）

用户已说“开始”启动 WIN-MSI-4。
起点为 `codex/msi-development` 的 `89a535c`、干净工作区、`0.1.0-alpha.36`。
本轮修复 WiX 成功退出时警告被吞的问题：`candle`/`light` 使用 `-wx`，仅对纯 current-user MSI 保留有依据的 `ICE91` 例外。
严格编译首轮发现 `CNDL1091`；
移除显式 `Package/@Id`，由 WiX 自动生成不同 PackageCode，现有 UpgradeCode/ProductCode 与组件身份不变。
新增预处理警告拒绝和独立构建 PackageCode 区分测试；
公开 `WixIdentity` 不再返回后端不应预先指定的 PackageCode。
所有 NuGet 包版本升至 `0.1.0-alpha.37`，示例应用版本仍为 `1.0.0`。

本机 Windows 11 Pro build 26200 x64：Release 快速测试 62/62，通过；
解决方案 `--no-restore` 构建 0 警告/0 错误，Pack 生成七个 alpha.37 包。
随包说明定稿后最后一次重打的 WiX 包 14,397,760 字节、SHA-256 `8892C11C9F18958E1A7916049BC524467C7E65EF82DDC9B9C0B151666C7D9025`。
MSI 的 smoke、lifecycle、maintenance 三个入口均从隔离本地包源构建，每轮按哈希检查 NuGet 包内的许可证、对应源码、SHA256SUMS、供应说明和第三方声明；
真实 current-user 安装、卸载、升级、降级/异包拒绝、修复、被动操作、中文并存及受限故障回滚均通过。
独立直接 API 消费者只引用 `DotNet.Bundler.Wix` 包。
NSIS Windows 全量集成、两个公开示例的本地 alpha.37 包消费与发布也通过。
示例未安装；
已知随机 MSI ProductCode 和安装目录的只读残留复核正常退出 0，均不存在。
具体命令、各产物哈希、ProductCode、日志目录和首次权限/警告失败记录见 `docs/msi-roadmap.md` 第 9 节。

MSI alpha 格式的本机验证范围与配置/身份规则已冻结；
`docs/msi-capability-matrix.md` 逐行标注已自动验证、外部待验收或不支持，并明确只有一台非干净 Windows 11 x64 主机的实际证据。
干净 Windows 10/11、ARM64 真实安装、per-machine UAC、生产证书、真实交互 UI、缺源、锁定文件及重启/3010 仍在 MSI 专用人工和外部清单中，不能扩大支持声明。
按 `docs/roadmap.md`，下一个默认阶段为 `MAC-APP`；
macOS 原生构建/测试环境和 PKG 是否纳入路线须在开始该格式前核对。
本阶段完成后用户已明确要求提交；
未要求推送。

### 14.8 MSI 通用能力补齐路线确认（2026-09-25，规划阶段）

第 14.7 节的 `MAC-APP` 下一阶段判断是 WIN-MSI-4 提交时的**历史结论**。
用户随后澄清：现有环境无法测试的项目不阻塞快速开发；
先补齐 Tauri 中适用于通用 Windows MSI 打包器的能力，应用运行时依赖自动部署仍明确不做。
用户接受常规模式的受控 WiX fragments/引用，以及显式开启、由调用方承担自备安装逻辑责任的完整模板/原始 merge module 专家模式。
新路线为 `WIN-MSI-5..9`，完成后再进入 `MAC-APP`。
固定 Tauri 参考、逐项选择和风险见 `docs/msi-tauri-capability-audit.md`；
阶段前置/交付/新增测试/退出条件见 `docs/msi-roadmap.md` 第 10 节；
当前与计划状态见 `docs/msi-capability-matrix.md`。
无环境的人工项继续在 MSI 专用清单/外部待办中准确保留，不冒充已通过。

本轮核对时分支 `codex/msi-development`，HEAD `adce4f0b160f6ed52a9b6152186fbb032521308c`，包版本 `0.1.0-alpha.37`，开始时工作区干净。
本轮仅落规划文档，不修改后端代码、测试或 NuGet 版本，不执行新能力测试；
现有 WIN-MSI-4 的 62 项及集成结果是历史基线而非 WIN-MSI-5..9 结果。
下一次用户明确要求“开始 WIN-MSI-5”时才按已落地路线推进整个阶段：先核 Git/包/身份向量，再做 x86、版本映射和可选降级、对应自动化、独立包消费、本机真实安装生命周期、示例/文档和版本迭代。
未经用户明确要求不提交或推送。

### 14.9 公开 MSI 示例补齐（2026-09-25）

用户要求先提交第 14.8 节的规划，再核对 MSI 公开示例是否像 NSIS 示例一样完整。
规划文档已提交为 `7484346`；
随后对照当时尚未改名的 `samples/HelloBundledApp` 与当前 MSI 后端 API，发现原 `HelloMsiApp` 仅展示快捷方式、关联和协议声明，缺乏可实际打开的演示资源、应用参数反馈、图标、许可页面、签名配置及分范围/语言的操作说明。
当前工作区补齐 `samples/HelloMsiApp` 的可操作演示、根 README 链接、本交接及 `VerifyPublicSample.ps1` 自动化，**未修改后端代码、未启动 WIN-MSI-5、未迭代 NuGet 包版本，示例应用仍为 1.0.0**。
NSIS 专有 Hook、安装器图片、语言选择器、快捷方式参数和 journal 不适用于现有 MSI；
WIN-MSI-5..9 计划能力不提前声称已支持。

Windows 11 x64 本机验证：从已有本地 `0.1.0-alpha.37` NuGet 包构建默认 `en-US/currentUser`、`zh-CN/currentUser` 和 `en-US/perMachine` 三份示例 MSI，均成功；
另一次 `dotnet restore` 使用隔离缓存并通过 `Assert-LocalBundlerRestore` 检查本地包源及 `DotNet.Bundler`/MSBuild/Wix 包版本，再 `dotnet publish --no-restore` 成功。
Windows Installer 数据库只读检查三份包的产品语言/独立 ProductCode 与 UpgradeCode、演示文件、RTF 许可 UI、产品图标和两个快捷方式，默认包还检查关联/协议的自身候选注册表项。
默认、中文和 per-machine 三份包分别位于 `%TEMP%\Bundler-HelloMsiApp-Sample-f638475803d84cf094c55a982f35727e`、`%TEMP%\Bundler-HelloMsiApp-zhCN-cbf1b025d4e1469ab3b47d2f6e5ed88e`、`%TEMP%\Bundler-HelloMsiApp-perMachine-472e58ccbb9346a79404877293301328`，SHA-256 分别为 `EAFDCBCA878DD5771D2B83D9BA29BDC7216B0E29560964B319D3B490D2F89682`、`AA01F2F187E7C18047B85F8101F48CB2D6ED78317423ABB7737A2449AF30180E`、`D896093B9E482F22AE07FDD5429A7DF6B939224821B21189CA8D2176EA682EBD`。
隔离还原产物 `%TEMP%\Bundler-HelloMsiApp-Isolated-691cf376f8374be0b01b2b10501db8ec\output` 的 SHA-256 为 `F20B0E8A62D9BCA4CCDAAA5D2CACE5249A14E3520C95D667B273A4D672262380`。
三份公开示例**均未实际安装或人工验收 UI**；
真实安装、升级、修复、故障行为的既有证据仍来自随机 fixture。

首次为默认英语包加入 RTF UI 后，WiX `light` 报 `LGHT0311`：英语 UI 本地化资源要求数据库 1252，原中文描述与目标路径无法编码。
示例将默认安装数据库文本改为英语、`BundlerWixCodepage=0` 交由现有语言配置选择（英语 1252、简体中文 936）；
中文可保留在文件内容和运行参数里。
重跑上述三种构建后均通过。
仓库原 `artifacts/win-x64/msi` 中有早期同版本 MSI，不应覆盖；
示例默认 `BundlerOutputPath` 改为 `artifacts/feature-demo`，在本机直接执行 `dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release` 成功，产物 SHA-256 `FB193DCC3E43861BA5BE6DC768CCD6A8EE9EB815EEFE2A2F6F20A626C9896F34`。
新增 `VerifyPublicSample.ps1` 首跑因脚本把缺失的 `ALLUSERS` 属性与空字符串直接比较而失败，修正为空字符串规范化后复跑三变体全部通过；
最后一轮日志与产物在 `%TEMP%\Bundler-Msi-PublicSample-e220eaf6eb0b45c3b2e92a273460d205`，三包 SHA-256 分别为 `031FF0C6E280145D0210DE67531CC3C926F5DB0FB83DDB58722F419D8776F750`、`72C49B649B5B5B8E3FF0ED11CC6AF9022A141CAE637F24425C31CE0B8E241CF7`、`98CB5331BEBADAC64F9FDE3B2D7D68F28A1A7015E71395307984922F399BC382`；
脚本只读检查数据库，不安装 MSI。
若未来要支持英语 UI 搭配中文数据库字段，需要单独审计 WiX 本地化与代码页规则，不能仅在示例中继续写 936。
用户现已明确要求提交本轮示例改动；
未要求推送，提交哈希以 Git 为准。

### 14.10 中文文档与格式命名整理（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `0e37880`、工作区干净；
**仅做文档组织、中文化和随包文档配置调整**，未开始 WIN-MSI-5，未改变 NSIS/MSI 安装语义。
根 `README.md` 改为中文跨格式入口，旧英文内容移出，原中文镜像不再重复维护。
NSIS 历史人工清单改名 `docs/nsis-manual-testing.md`，原 `MT-01..MT-11` 编号不变；
示例、MSI 集成说明、NSIS 插件与三类第三方来源说明均改为带格式名称的文件名。
NSIS 上游审计、外部待办、插件说明及第三方声明译为中文；
原始 `COPYING`/`LICENSE`/`LICENSE.TXT` 许可文本未改。
跨格式命名与语言规则写入 `docs/development-rules.md`；
活动链接和包内文件列表随之调整。

因根 README、WiX 来源说明及第三方声明进入 NuGet 包，工具包版本从 `0.1.0-alpha.37` 迭代至 `0.1.0-alpha.38`，两个示例应用版本仍是 `1.0.0`。
`Directory.Build.props`、示例引用、API fixture 默认值及 Windows 集成入口默认值已同步。
Windows 11 x64 本机：`dotnet build Bundler.slnx -c Release -v:q` 通过，0 警告/0 错误；
`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 全部通过；
`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages -v:q` 生成七个 alpha.38 包。
逐包 ZIP 检查确认中文 `README.md` 与仓库一致、原中文镜像不再随包；
WiX/MSBuild 包内 `licenses/wix/msi-wix-provenance.md` 与仓库逐字节一致，现有 MSI 包审计辅助函数再次通过。
WiX 包为 14,398,423 字节，SHA-256 `213BB494106CC7B82F77B96539AF6A435D6E61575E38A836A08B7E66E75BB398`。

公开 MSI 示例的 `VerifyPublicSample.ps1` 使用本地 alpha.38 包和隔离缓存，英语当前用户、简体中文当前用户和英语整机三种只读数据库检查均通过；
本轮未安装 MSI。
NSIS 公开示例从本地 alpha.38 包源 `dotnet publish` 成功，产生的会话专用输出已按确切路径清理。
仓库 Markdown 相对链接全部解析成功，全部维护中的 Markdown 均含中文，旧文档路径搜索无命中，`git diff --check` 无空白错误。
首次沙箱内 build 因无法读取本机 `C:\Users\Lin\AppData\Local\Microsoft SDKs` 被拒；
获准在沙箱外读取后成功，未发现项目编译错误。
本轮变更随后提交为 `414864c`，未推送。
下一实施阶段仍为 WIN-MSI-5；
外部环境的既有人工验收边界不变。

### 14.11 测试与示例项目命名、本地包配置收敛（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `414864c`、工作区干净。
原 NSIS 示例只演示 NSIS，因此项目目录和 `.csproj` 改为 `samples/HelloNsisApp/HelloNsisApp.csproj`；
NSIS 集成 fixture 的项目文件改为 `BundlerNsisIntegrationFixture.csproj`。
跨格式的 `tests/Bundler.Tests` 保留泛用名，既有 MSI 项目名已带格式名。
为只整理项目名、不改变安装身份，NSIS 示例仍使用 `AssemblyName=HelloBundledApp`、原产品名与标识符、`1.0.0` 应用版本；
NSIS fixture 仍使用 `AssemblyName=BundlerIntegrationFixture`。
示例专属 MSBuild 参数和图片准备目标从 `HelloBundledApp*` 改为 `HelloNsisApp*`，对应中文命令已同步；
这些是示例构建入口名称，不改变安装身份。
旧示例目录在本轮开始前已有被忽略的构建产物，整体移动因文件占用失败；
仅移动九个受 Git 跟踪的源码文件，旧产物原位保留。

根 `Directory.Build.props` 是 `BundlerPackageVersion` 的唯一当前值，版本迭代到 `0.1.0-alpha.39`。
新增跨格式 `Bundler.LocalPackages.props`，集中设置示例的仓库本地包源及 fixture 的 `RestoreSources=$(BundlerPackageSource)`。
两个示例的 `PackageReference` 均引用 `$(BundlerPackageVersion)`；
五个 Windows 集成入口省略 `-PackageVersion` 时由共用 PowerShell helper 读取根 props。
四个包消费 fixture 显式导入共享 props；
复制到仓库外的 MSI MSBuild/API fixture 同时复制该文件，并由脚本传入包版本、包源和隔离缓存。
fixture 仍自行声明必需的 SDK 属性，不依赖仓库根 props。
配置只服务开发包消费，不随 NuGet 包发布；
后端和安装逻辑未改。
具体规则见 `docs/development-rules.md`。

本机 Windows 11 Pro build 26200 x64：Release 解决方案构建 0 警告/0 错误，更新后的快速测试全部通过；
Pack 生成七个 alpha.39 包，包内 README 与仓库一致，Bundler 依赖版本均为 alpha.39。
WiX 包 14,398,397 字节，SHA-256 为 `3A13CA19A63D8336538E56453ACB01D5BE76CD6D4F0DE383AF572DED5DFD9657`。
NSIS 公开示例从本地包成功发布，仍生成 `HelloBundledApp.dll` 与 `Hello Bundled App-1.0.0-setup.exe`，安装器 SHA-256 为 `D80BAA724FB4D64C96A107F3D1D3A7312B8CCAC0CBF81277D7DF27ACD2DC5468`；
本轮专用示例输出已按确切路径清理。
MSI 公开示例省略版本参数运行 `VerifyPublicSample.ps1`，英语当前用户、中文当前用户、英语整机三种只读数据库检查通过，未安装公开示例，产物保留在 `%TEMP%\Bundler-Msi-PublicSample-f2b16791c841400a82adec761ce5fbb4`。

真实 Windows 集成入口也均省略版本参数并退出 0：MSI `Verify.ps1 -ConfirmLocalInstall` 通过随机 current-user 安装/卸载和仓库外直接 API 包消费，MSI SHA-256 为 `ECBAFF26859A5458784A5D8035B4C9912E0A766D2667DA1A3E568A7D2277094D`，日志在 `%TEMP%\Bundler-Msi-Smoke-394db4e914b14e04b1c18b929ad7d0f1`；
`VerifyLifecycle.ps1 -ConfirmLocalInstall` 通过升级、降级/异包拒绝与数据所有权，日志在 `%TEMP%\Bundler-Msi-Lifecycle-552806128459422086e891156501983b`；
`VerifyMaintenance.ps1 -ConfirmLocalInstall` 通过损坏包 1620、故障回滚、被动安装/卸载、静默修复与语言并存，日志在 `%TEMP%\Bundler-Msi-Maintenance-34b34c995872424093c8ca0257bfbd8e`。
NSIS `Verify.ps1` 全量安装/卸载集成通过，产物在 `artifacts/windows-nsis-integration`。
Markdown 相对链接、旧项目路径及 `git diff --check` 均核对通过。
未启动 WIN-MSI-5；
本轮变更随后提交为 `30cae4d`，未推送；
原有外部人工验收边界不变。

### 14.12 跨格式规则重整与 NSIS 路线归档（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `30cae4d`、工作区干净，工具包版本仍是 `0.1.0-alpha.39`。
用户要求将先规划完整后端路线、适用的 Tauri 通用能力审计、独立后端包直接消费与离线工具随包供应、完整可操作示例，以及此前有效但分散的规则写成稳定规范。
本轮只重写协作/路线文档，未改代码、包内容或示例，也未启动 `WIN-MSI-5`；
因此不迭代 NuGet 版本。

`docs/development-rules.md` 是跨格式规则的唯一规范入口，`AGENTS.md` 保留接管摘要；
总 `docs/roadmap.md` 只保留产品边界、格式顺序和阶段入口。
原总路线中的 NSIS 能力基线、旧阶段映射与 `NSIS-R1..R4` 记录原样迁至 `docs/nsis-roadmap.md`，人工测试索引增加格式路线链接；
NSIS 历史证据不因此变成当前新验证。
新后端必须先完成直到格式冻结的路线并与用户确认关键选择，才开始第一阶段代码。
当前下一实施阶段仍为 `WIN-MSI-5`，已确认的 MSI 方案以 `docs/msi-roadmap.md` 第 10 节为准；
外部验收仍按各格式专用清单。

本轮以 Git 旧版总路线为基准核对 NSIS 历史迁移内容，仅更改标题编号和一处已失效的“本文开头”引用，正文保留；
仓库 Markdown 相对链接全部可解析，`git diff --check` 无空白错误。
由于只修改未随 NuGet 包分发的协作和路线文档，没有新增或修改打包功能，本轮不重新执行安装集成测试，也不迭代包版本。
本轮变更随后提交为 `23c4b8a`，未推送。

另对照可读取的旧“Nsis 开发”与早期架构任务记录，补回两条容易遗漏的要求：公开示例覆盖当前格式所有适用的用户能力（互斥或需外部条件的场景给可复现说明），以及新增人类语言代码/脚本注释使用中文；
MSBuild Task 在进程内调用 Core/后端，不另起 .NET CLI 驱动。
旧时要求维护中英文 README 已被后来的中文单文档决定替代，故未重新引入。

### 14.13 提交消息统一为中文（2026-09-25）

用户确认改写**所有项目分支**的提交消息：主题统一为 `type(scope): 中文描述`，单后端改动使用 `nsis`、`msi` 等格式 scope。
执行前工作区干净；
本地有 `master`、`codex/nsis-development`、`codex/msi-development` 三个项目分支，无远端和标签。
当前 MSI 分支包含 50 个提交，两个 NSIS/master 分支共用前 39 个。
已将这些提交的消息逐项改为中文，保留每个提交的文件树、作者/提交者身份与时间；
改写后 `master` 和 `codex/nsis-development` 指向 `6946dae`，MSI 分支在附加本次文档校正提交前指向 `23c4b8a`。
当前文档中引用的 58 处旧提交 SHA 已映射到对应新 SHA，具体映射保存在忽略目录 `artifacts/history-message-map.json`。

### 历史恢复记录（不代表当前状态）

完整历史恢复包为 `artifacts/history-before-message-rewrite.bundle`，已通过 `git bundle verify`，SHA-256 为 `19A38501608257FA652881DCD77C29C75C18A3DB41E693D46B999197EC6FAD0F`。
Codex 管理的快照/检查点、旧 `refs/original` 和停在旧提交的独立工作树不是项目分支，予以保留作为历史恢复点；
因此 `git log --all` 仍可能显示其旧消息，正常三个项目分支的历史已统一。
若要清理或改写这些应用管理的引用，应单独评估其用途。
当前包版本和阶段以文首及最新阶段记录为准。

### 14.14 开发分支移除 `codex/` 前缀（2026-09-25）

用户要求所有项目分支名不带 `codex/` 前缀。
核对时只有三个本地项目分支，且没有远端或标签：`master` 保持原名，`codex/nsis-development` 改为 `nsis-development`（`6946dae`），当前 `codex/msi-development` 改为 `msi-development`（`e85ebd0`）。
另一 Codex 工作树处于分离 HEAD，没有分支要改。
两次重命名均未修改提交历史、文件树或包内容；
历史实施记录中的旧分支名保留为当时事实，当前分支名以文首和 `git branch -vv` 为准。
本轮变更随后提交为 `3cca7a0`，未推送；
下一实施阶段仍为 `WIN-MSI-5`。

### 14.15 WIN-MSI-5：x86、版本映射与可选降级（2026-09-25）

本轮起点为分支 `msi-development`、HEAD `3cca7a0`、包版本 `0.1.0-alpha.39`；
用户要求“提交然后开始下一步”。
先提交分支改名文档为 `3cca7a0 docs: 同步开发分支改名记录`，随后按 MSI 路线启动 WIN-MSI-5。
实现和包内容变更将版本迭代到 `0.1.0-alpha.40`；
公开示例应用版本保持 `1.0.0`。

实现事实：公共 `BundleTarget`/`CpuArchitecture` 增加 `win-x86`/`X86`，Core 只允许 x86 与 MSI 组合，NSIS 不因共享模型扩展而接受 x86。
WiX 使用 `-arch x86`；
x86 per-machine 使用 `ProgramFilesFolder`、current-user 使用既有用户目录，安装目录和组件/产品身份包含 x86 RID；
x64/ARM64 既有 identity vector 未改。
`WixBundleConfiguration.MsiVersion` 与 MSBuild `BundlerWixMsiVersion` 支持显式三段 MSI 版本，第四字段、预发布自动映射和越界值拒绝；
未指定时保留原稳定三段版本映射。
`AllowDowngrades`/`BundlerWixAllowDowngrades` 默认 false，显式 true 才允许降级；
该场景只定向豁免 WiX ICE61，其他编译/链接警告仍失败。
独立 API、MSBuild、示例说明和包消费 fixture 已同步。

新增自动化覆盖公共 RID/格式矩阵、x86 英文/中文 identity vector、显式版本边界、x86 Intel 模板与 32 位组件/目录/注册表视图、MSBuild 属性映射、版本/降级策略。
获准读取本机 SDK 路径后，`tests/Bundler.Tests` 快速测试全量通过；
普通沙箱再次运行时因访问 `C:\Users\Lin\AppData\Local\Microsoft SDKs` 被拒，属于环境权限限制，不是测试断言失败。

真实 Windows 证据：`tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall` 在 Windows 11 Pro build 26200 x64 上从本地 `alpha.40` 包源验证仓库外 API 与 MSBuild x86 消费；
随机 current-user 产品完成 v1 安装、预发布应用显式映射到 v2 升级、同 MSI 版本异内容拒绝（1638）、默认降级拒绝（1603）、显式允许降级（0）、卸载（0），并检查 32 位注册表视图、受管文件和未知用户文件保留。
日志和五个 MSI 保留在 `%TEMP%\Bundler-Msi-WinMsi5-c9fb31e8471a4996878963d70a4b8e7b`。
原 x64 `VerifyLifecycle.ps1 -ConfirmLocalInstall` 用 alpha.40 回归通过，日志在 `%TEMP%\Bundler-Msi-Lifecycle-ba0954338b454f1ab5312db811500ea0`。
本轮尝试先把 alpha.40 包写入 `artifacts/packages` 再运行 NSIS 集成，但当前受限执行环境拒绝读取 `C:\Users\Lin\AppData\Local\Microsoft SDKs`，因此 pack 在 MSB4184 处失败，NSIS alpha.40 回归未执行；
这不是代码回归证据，待有 SDK 访问权限时按 `tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release` 补跑。
干净 Windows、原生 x86/ARM64 用户端、per-machine UAC、生产证书和真实重启仍是人工/专用环境边界，不能由本机结果扩大支持声明。

本轮变更随后提交为 `a3657b5 feat(msi): 完成 WIN-MSI-5 架构与版本生命周期`（其后另有 docs-only 提交 `f73c2a5`），未推送；下一阶段为 `WIN-MSI-6`。
本节末段所述 NSIS alpha.40 回归因当时环境限制未执行，补跑结果见后续阶段记录。

### 14.16 接管核对后的清理、文档纠正与全量验证（2026-09-25）

本轮起点为 `msi-development`、HEAD `f73c2a5`、包版本 `0.1.0-alpha.40`，非实施阶段，未修改后端代码、示例或包内容，因此不迭代 `BundlerPackageVersion`。

**残留移出**：以下均未跟踪且内容为被忽略的构建产物或空目录，已移至 `%TEMP%\Bundler-repo-leftovers-2026-09-25\`（含 `samples\HelloBundledApp`、`tests\Bundler.Core.Tests`、`src\Bundler.Core\Backends\Windows`、`src\Bundler.Core\Templates` 对应相对路径），确认后可删除该备份目录。
`HelloBundledApp` 是 §14.11 改名时因文件占用遗留的产物目录；
`Bundler.Core.Tests` 是旧测试项目合并进 `tests/Bundler.Tests` 后的空壳；
两个 `src/Bundler.Core` 空目录无任何内容。

**文档纠正**：§14.3 中"以最新的 14.8 节为准"改为"以最新阶段记录为准"；
§14.10、§14.11、§14.12、§14.14、§14.15 末尾写作时的"本轮未提交"自述分别补记实际提交 `414864c`、`30cae4d`、`23c4b8a`、`3cca7a0`、`a3657b5`。
WIN-MSI-6 两项前置决策经用户确认并写入 `docs/msi-roadmap.md` 第 10 节：可选安装目录 current-user 限定 `%LOCALAPPDATA%` 内子目录、per-machine 限定 Program Files 内子目录，静默 `INSTALLFOLDER` 走同一校验；
无 RTF 许可时目录选择使用不含许可页的自定义 dialog 序列。

**本机验证（Windows 11 Pro build 26200 x64，`0.1.0-alpha.40`）**：

```powershell
dotnet build Bundler.slnx -c Release -v:q            # 0 警告 / 0 错误
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore   # 65 项全部 PASS
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 7 个 alpha.40 包
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyMaintenance.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1 -Configuration Release
```

结果：上述命令均退出 0。
`DotNet.Bundler.Wix.0.1.0-alpha.40.nupkg` 14,398,984 字节。
NSIS 全量安装/卸载集成输出 `PASS Windows NSIS install/uninstall integration`，补齐 §14.15 因环境限制未执行的 alpha.40 回归。
MSI smoke 的独立 API/MSBuild MSI SHA-256 为 `AC52D86F132750D8AEB179727BD2D73CCE3CB0102C3425E9C5653E93323CB269`、`924A2D3BAE154FB0E3B4B0F02DDFE9CE96F5BC59289E9E83830AB2EC1ED78212`，ProductCode `{7B4D4756-8590-52D6-8F6A-6444AF3EC79C}`，日志在 `%TEMP%\Bundler-Msi-Smoke-fe2ae33a8ac543d893816e1aca3d5276`。
Lifecycle v1/v2/异包 SHA-256 为 `412BE983C99DEB5CA1F9965CE18ED6FEA441C44B8C4F0A6E4DB5AF5BB215EDA3`、`9DA62316E10FEF158A86EB5D860BC0ED95353A52F65B7FAB80DE7A23888CF33B`、`EA14041D2DF96DC8DACB51334A2A3185FC37FD885155ED4AA5829A57CE484186`，日志在 `%TEMP%\Bundler-Msi-Lifecycle-9d4a4afd2ddd4792851dfe5c66fe91e3`。
Maintenance MSI SHA-256 `F467AC9549A23A600120CD8771DA14B94D67F6419DC2422E490CD9BFA6B953F6`，损坏包 1620、故障回滚 1603、被动安装/卸载、静默修复、中文并存通过，日志在 `%TEMP%\Bundler-Msi-Maintenance-b603717f858e48f7899a6ea295a8bcf2`。
WIN-MSI-5 x86 脚本五份 MSI SHA-256 见 `%TEMP%\Bundler-Msi-WinMsi5-1ef8c58e339946718ea33c42f2d58f6e`，v1 安装、映射 v2 升级、同版碰撞 1638、默认降级拒绝 1603、允许降级与卸载均 0。
`VerifyPublicSample.ps1` 英语/中文/per-machine 三包只读数据库检查通过，未安装。
本轮六个测试 ProductCode 结束后复核 ProductState 均为 `-1`，无安装残留。
上一轮在受限环境的 `Microsoft SDKs` 读取失败在本机当前环境未复现。

本轮未提交或推送；下一阶段仍为 `WIN-MSI-6`。

### 14.17 WIN-MSI-6：安装目录、界面与桌面选项（2026-09-26）

本轮起点为 `msi-development`、HEAD `f73c2a5`、包版本 `0.1.0-alpha.40`；
用户说"开始"启动本阶段。
前置两项设计已由用户确认：可选安装目录限定允许根（current-user 在 `%LOCALAPPDATA%` 子目录、per-machine 在 Program Files 子目录，静默 `INSTALLFOLDER=` 同一校验）；
无 RTF 许可时用不含许可页的自定义 dialog 序列。
实现和包内容变更将版本迭代到 `0.1.0-alpha.41`；
公开示例应用版本保持 `1.0.0`。

**实现事实**：

- `WixProductDocument` 从 WiX 3.14.1 官方源码核对 `WixUI_InstallDir`/`WixUI_Minimal`/`InstallDirDlg`/`BrowseDlg`/`InvalidDirDlg`/`Common.wxs` 实际结构后，生成自有 `BundlerInstallDialogSet`（Welcome→[LicenseAgreement]→[InstallDir]→VerifyReady→Exit+Browse/InvalidDir/维护对话框，引用 `WixUI_Common`）。
  启用目录选择/无许可启动勾选/自定义位图时用该序列；
  有许可插入 `LicenseAgreementDlg` 且 `LicenseAccepted="1"` 才放行。
  仅启用原有许可 UI 时仍走 `WixUI_Minimal`。
- 范围校验在 execute-sequence `BundlerInstallDirScope`（Type 19，`CostFinalize` 后）统一执行，条件 `NOT (INSTALLFOLDER ~<< <范围根>) OR INSTALLFOLDER ~= <范围根>`；
  交互 InstallDir Next 还要求同一条件并在 `WixUIValidatePath` 后弹 `InvalidDirDlg`。
  静默 `INSTALLFOLDER=` 实测不可绕过；
  升级经 `RegistrySearch`（Type=directory）恢复已选目录。
  **已知边界**：安装时 junction/重解析点目标无法用原生条件判定，未实现，记录为能力边界（详见 msi-roadmap §10 实施记录）。
- 位图：`WixUIBannerBmp` 493×58、`WixUIDialogBmp` 503×314；`WixBundler` 构建时校验存在性/非重解析点/`.bmp` 扩展/BMP 头尺寸，均入产物指纹。
- 可选 Feature：`Shortcuts`（桌面/开始菜单非 advertised 快捷方式组件，HKCU KeyPath 满足 ICE43，per-machine 亦同）、`PathEnvironment`（Environment `=-PATH`/`=-*PATH`，`[~];[INSTALLFOLDER]` 只增删本产品条目）、`UninstallShortcut`（`msiexec /x [ProductCode]`）。
- 启动勾选：`WIXUI_EXITDIALOGOPTIONALCHECKBOX` 条件 DoAction `BundlerLaunchAfterInstall`（FileKey+`ExeCommand=""`、asyncNoWait、Impersonate），条件排除 `Installed`/`WIX_UPGRADE_DETECTED`；
  `/qn`、`/passive`、修复、升级不触发。
- ARP：`ARPNOMODIFY=1`、`ARPCONTACT`、既有 comments/url/icon；
  `ARPINSTALLLOCATION` 由 Type 51 `BundlerSetArpInstallLocation`（`[INSTALLFOLDER]`）在 `CostFinalize` 后写入，实测在 HKLM Uninstall 键生成 `InstallLocation`。
- MSBuild 新增 `BundlerWixInstallDirectorySelection`、`BundlerWixBannerBitmap`、`BundlerWixDialogBitmap`、`BundlerWixAddToPath`、`BundlerWixUninstallShortcut`、`BundlerWixLaunchAfterInstall` 三层贯通；
  `Msi.Api.PackageFixture` 覆盖直接 API 新配置；
  `HelloMsiApp` 默认演示全部新能力并自带 `Assets/banner.bmp`/`dialog.bmp`。

**开发期间发现并修复的实现缺陷**（均有测试覆盖）：`ARPINSTALLLOCATION` 直接 Property 值含 `[INSTALLFOLDER]` 被 CNDL1077 拒绝→改 Type 51 CA；
advertised Shortcut 不允许 Target（CNDL0035）→统一改非 advertised+显式 Target；
per-machine 非 advertised 快捷方式组件 KeyPath 触发 ICE43/57→快捷方式组件统一 HKCU KeyPath；
`Part="last"` 的实际落库为 `Name=-*PATH`、`Value=[~];[INSTALLFOLDER]`→断言按编译结果修正；
UI-free MSI 无 Dialog 表导致 1615→用 `_Tables` 存在性断言。

**本机证据（Windows 11 Pro build 26200 x64，`0.1.0-alpha.41`）**：

```powershell
dotnet build Bundler.slnx -c Release -v:q            # 0 警告/0 错误
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release   # 71/71 PASS
dotnet pack Bundler.slnx -c Release -o artifacts/packages                  # 7 个 alpha.41 包
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall          # PASS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall  # PASS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyMaintenance.ps1 -Configuration Release -ConfirmLocalInstall # PASS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall    # PASS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi6.ps1 -Configuration Release -ConfirmLocalInstall    # PASS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1 -Configuration Release                    # PASS（三变体只读检查）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release                             # PASS
```

`VerifyWinMsi6.ps1` 实测要点：静默 `INSTALLFOLDER` 指向 `%LOCALAPPDATA%` 根本身与 `C:\Program Files\...` 均 1603；
`LocalAppData\BundlerTests\msi6-<id>` 自定义目录安装成功；
用户 PATH 只追加本产品目录且卸载后恢复原值；
桌面/开始菜单（含卸载）快捷方式创建；
HKLM Uninstall 键含 `InstallLocation`/`Contact`；
v2 升级不传目录也恢复已选目录且未知用户文件保留；
`/fomus` 修复；
卸载清理无残留。
MSI 与日志保留在 `%TEMP%\Bundler-Msi-WinMsi6-b98167652a894602bccd4bfa83f190c6`。
`VerifyMaintenance.ps1` 因新 MSI 已自带 CustomAction 表，改为先查 `_Tables` 再按需建表；
`VerifyPublicSample.ps1` 断言同步到自定义 UI/PATH/快捷方式。
NSIS 全量集成首轮在中途断言失败一次，清理本轮残留事务 seal 后复跑完全通过，未改 NSIS 代码，按环境性事件记录。

**未验收/交接**：真实交互 UI 流转、勾选启动、InvalidDirDlg、位图显示、缩放/辅助功能、junction 安装目标行为、per-machine 交互/UAC、干净宿主、生产证书、真实重启仍为人工/外部项（`MSI-MT-11`、`MSI-OI-12`），不视为已通过。
本轮变更未提交或推送；
下一阶段为 `WIN-MSI-7`。

### 14.18 MAC 规划轮：macOS 调研、决策与文档（2026-09-26，仅文档）

本轮起点为用户指令：基于 `main`（`993b0ad`）创建 `mac-app-development` 分支，只做调研决策与文档、不写实现。
产出与提交均在本分支。

**环境自检（云 macOS VM 26.5.2 arm64 + Xcode 26.6 + .NET 10.0.401，实测）**：
`hdiutil`/`pkgbuild`/`productbuild`/`productsign`/`codesign`/`xcrun notarytool`/`xcrun stapler`/`plutil`/`ditto`/`security`/`osascript`/`xar`/`pkgutil`/`installer`/`spctl`/`lipo`/`iconutil`/`actool`/`assetutil` 全部在位；
缺口：无 codesigning 身份（`security find-identity -v -p codesigning`=0）、Rosetta 未激活、无 `pwsh`（macOS 集成脚本改用 bash）。
真实 Developer ID 签名/公证、osx-x64 原生运行属外部待验收，记入 `docs/mac-app-open-items.md`。

**Tauri 审计**：沿用仓库固定快照 `7dbfc1f`（复核 `dev` HEAD `9f8922a`，macOS bundler 表面无实质漂移）；
逐项审计 `.app`/`DMG` 用户可观察能力并标注选择/阶段归属 → `docs/mac-tauri-capability-audit.md`。
上游 `PackageType` 无 `.pkg`；`providerShortName`/`LSRequiresCarbon` 为上游死配置不采纳。

**PKG 决策**：`.pkg` 是 macOS 唯一原生受管安装格式（收据库、系统域、提权、MDM 部署），与 `.app`/.dmg 拖放分发面不重叠；
建议纳入 `PackageFormat.Pkg` 并置于 `MAC-DMG` 之后、Linux 之前，受管模式不开放任意安装脚本 → `docs/mac-format-decision.md`，待用户确认。

**MAC-APP 路线**：`MAC-APP-1..5`（结构/元数据 → 分发与桌面集成 → codesign/notarization → 原生 macOS E2E → 冻结）；
关键偏差登记：Apple 工具不可再分发，采用“宿主工具检测+版本下限”策略替代字面“工具随包供应”；
默认产物不签名、公证默认关闭显式开启；
`.app` 无安装事务/卸载器/收据语义；
universal 只校验不合成 → `docs/mac-app-roadmap.md`，配套 `mac-app-capability-matrix.md`/`mac-app-manual-testing.md`/`mac-app-open-items.md`。
`docs/roadmap.md` MAC 节、`PROJECT_CONTEXT.md`、`docs/manual-testing-index.md` 已同步。

本轮仅文档，未动代码；所有标注“待用户确认”的决策点在 `mac-format-decision.md` 第 6 节与 `mac-app-roadmap.md` 第 2、3 节汇总。
下一阶段为 `MAC-APP-1`，需用户确认决策后明确启动。

### 14.19 MAC 规划轮决策确认（2026-09-26，同日）

用户对 14.18 规划轮的拍板结果（已写入对应文档）：

1. PKG 确认纳入 `PackageFormat.Pkg`，顺序 `MAC-DMG` 之后、Linux 之前；枚举值在 `MAC-APP-1` 起手时进公共模型。
2. 工具供应：不内嵌 Apple 工具，采用宿主检测+版本下限策略；补充原则“尽量支持更多设备”——优先系统自带工具，Xcode 专属工具只服务可选能力且必须可降级。
3. 后端分包：按格式分包 `Bundler.MacApp`/`Bundler.MacDmg`/`Bundler.MacPkg`，共享签名基础设施另立 `Bundler.Signing.Mac`（对齐 `Bundler.Signing.Windows` 先例）。
4. universal：不加 `osx-universal` 枚举，`osx-x64`/`osx-arm64` 双产物；fat 输入校验保留为 MAC-APP-2 能力。
5. 用户澄清：“尽量支持更多设备”指构建工具本身的宿主覆盖范围，产出物兼容哪些设备由应用开发者决定。
   同日最终确认：`CFBundleVersion` 默认=版本号可覆盖；`LSMinimumSystemVersion` 调用方显式配置、未配置不写入（不代设下限）；构建宿主=未签名 `.app` 任意宿主（跨宿主产物附权限位警告），需 Apple 工具的步骤限 macOS。
   上游 Tauri 经源码核实为 macOS-only：`bundle/macos` 模块 `#[cfg(target_os = "macos")]` 门控，非 macOS 宿主请求仅警告跳过；本方案有意比上游宽一档。
6. 宿主下限讨论：曾考虑为很老宿主自带 Notary API 客户端（公证不依赖 `notarytool`）；用户最终拍板**下限对齐 Tauri 同等标准即可**——公证沿用 `xcrun notarytool`/`stapler`（Xcode 13+，宿主约 macOS 11.3+），不做自带客户端。
   另：宿主能力下限的分层计算口径（打包方式自身/后端运行时/入口层/可选特性降级）已入 `docs/development-rules.md` 第 3 节。
   MAC 规划轮至此全部决策确认完毕，待 `MAC-APP-1` 启动指令。

### 14.20 MAC-APP-1：最小可用 `.app`（2026-09-26，分支 `mac-app-development`）

实现最小可用 `.app` 后端并本机实测通过（云 macOS VM 26.5.2 arm64 + Xcode 26.6 + .NET 10.0.401）：

1. 公共层：`PackageFormat.Pkg` 入枚举（osx 矩阵放行；win/linux 拒绝）；`BundlePlanner` 对 `Dmg`/`Pkg` 自动插入中间 `App` 步骤；`BundleConfigurationValidator` 新增 `outputDirectory` 不得位于 `inputDirectory` 内的拒绝规则（防整树递归复制）。
2. `src/Bundler.MacApp`（netstandard2.0，`DotNet.Bundler.MacApp` 包）：`MacAppBundler`/`MacAppBundleConfiguration` 公开 API、`MacAppBundleBackend` 走 `BundlePipeline`；
   载荷语义：输入树保相对结构进 `Contents/MacOS/`，资源→`Contents/Resources/`，`.framework`/`.dylib`→`Contents/Frameworks/`，`Contents` 显式映射拒绝顶层保留名/`..`/绝对路径/跨通道冲突；
   符号链接与 reparse 拒绝；主可执行限单层文件名 + Mach-O 魔数校验；POSIX 宿主 `chmod 755`（Windows 宿主告警降级）；`plutil -lint` 仅 macOS 宿主执行。
   Info.plist 全键（含 `NSHighResolutionCapable`）XML 生成；`PkgInfo`=`APPL????`；`.icns` 透传或 PNG 位图合成（ic07..ic12 类型映射，不依赖 iconutil）。
3. MSBuild：`BundlerFormats=app` + `BundlerMacAppBundleName/DisplayName/ShortVersion/BuildVersion/MinimumSystemVersion/Category/IconName` + `BundlerMacContent`/`BundlerMacFramework` 项组；osx RID 默认 `MainExecutable=$(TargetName)` 无 `.exe`；混合格式报错文案改为通用表述。
4. 测试与示例：`tests/Bundler.Tests/MacAppTests.cs` 新增 22 条（结构/plist 回读/校验拒绝/载荷映射/图标合成/权限位/确定性重建/MSBuild 文本断言/Pkg 规划）；`tests/MacApp.Api.PackageFixture`（NuGet 消费直接 API）；`tests/MacOS.App.Integration/Verify.sh`（bash 全流程）+ `samples/HelloMacApp`。
5. 本机验证：`Verify.sh` 全绿——包内容断言→fixture 发布产 `.app`→结构/plist 全键回读→Mach-O `+x`→直接执行输出标记→`open -W` 接受→重建 Info.plist 指纹一致→删除即卸载→独立 API fixture 再产 `.app`。
6. 既有套件回归：macOS 宿主上此前 5 条 NSIS/WiX 用例失败；逐条核查后定性——4 条为真实跨宿主缺陷（NSIS 安装路径校验用宿主分隔符/非法字符集致 POSIX 上 `..` 与 Windows 非法字符逃逸、`EnsurePayloadFile/Directory` 用 `\\` 路径查 POSIX 文件系统、输入树与资源目标冲突集合分隔符不一致、`SafeFileName` 宿主相关、两处 license 断言未做 CRLF/LF 归一），已修复（新增 `WindowsFileNames` 统一 Windows 文件名规则）；1 条 `ValidatesMsiPublishingInputs` 属真 Windows-only（`WixBundler` 刻意宿主门控最先），移入 OS 门控区块。修复后 macOS 宿主全套 66 项全绿。
7. 边界按路线拒绝：文件关联/URL scheme（MAC-APP-2）、签名（MAC-APP-3）、`.app` 无 license 语义，均明确 `NotSupportedException`；`BundlerIntegrationOutput` 等参数复用既有 fixture 约定。

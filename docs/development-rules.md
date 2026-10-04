# Bundler 开发与交接规则

本文件是**跨格式协作规则的唯一规范入口**。
`AGENTS.md` 只提供接管入口；`docs/roadmap.md` 决定格式顺序，格式路线保存设计和阶段状态，`PROJECT_CONTEXT.md` 保存当前事实，`docs/project-history.md` 保存历史记录。
代码和测试用于核实事实，历史记录不能代替当前状态。
用户当前任务的明确要求优先。

**文档归属**：泛用规则只放本文件，其他文档不重复规则内容，需要时链接回本文件。
格式专属的规则、契约、路线、能力状态、人工清单、外部待办、上游审计放对应 `docs/<format>-*.md`；产品边界与格式顺序放 `docs/roadmap.md`；
当前事实（版本、分支、阶段、最近验证、未决问题、默认下一步、项目结构）放 `PROJECT_CONTEXT.md`；
按时间排序的过往记录与当时证据放 `docs/project-history.md`；
面向使用者的能力与用法放 `README.md` 和各 `samples/*/*-sample.md`；
第三方来源、许可与哈希放 `third_party/<tool>/*-provenance.md` 和 `THIRD-PARTY-NOTICES.md`。
新文档找不到归属时按“规则 / 计划 / 当前事实 / 历史 / 某格式 / 用户文档 / 第三方”归类，都不合适时先向用户说明再定，不新建语义重叠的文件。

## 1. 接管与判断

1. 先读 `AGENTS.md`、本文件、`PROJECT_CONTEXT.md`、`docs/roadmap.md`、`README.md`，再读当前格式的路线、能力矩阵、人工测试、外部待办、上游参考及相关代码和测试。
   格式入口见 `docs/manual-testing-index.md`。
2. 核对分支、HEAD、工作区、根 `Directory.Build.props` 的 `BundlerPackageVersion`、实际 API、NuGet 包内容、示例与测试入口。
   交接快照和旧对话不是当前事实；文档与实现冲突时先调查，再修正状态。
   保留用户已有的无关改动。
3. **先判断再执行**：先检查问题有没有错误前提、逻辑跳跃、遗漏条件和跨层影响；
   不要迎合用户，独立判断，区分**已核实事实、拟定方案、预测、主观观点和外部待验收**。
   涉及数字、人物、规格或结论时尽量核实来源。
   不同意时直说，给出依据、风险和替代方案；
   主动提醒用户可能忽略的变量、成本和判断偏差。
   先理解需求和项目；
   有不了解或无法从代码、文档和可靠来源确认的信息，先提出问题并确认没有理解偏差，再开始正式执行。
   修改前向用户说明当前差距、判断、文件范围、验证方式和限制。
   影响产品语义、架构或成本的未定选择，先提出推荐选项与取舍，等用户确认；
   能自行核实的细节不反复提问。
4. 一个阶段或缺陷要处理完整因果链：公共模型、后端、入口映射、独立包消费、示例、自动化、适用的真实系统行为和文档。
   某层不适用时说明原因，不只修首先看到的报错。
   数据丢失、安全或主流程缺陷在当前阶段处理；独立增强项按路线登记。
5. **不得重复猜测或自行建立另一套规则**：先查阅本文件、格式路线、能力矩阵、人工测试、外部待办、`PROJECT_CONTEXT.md`、代码和测试；
   已有明确规定时按仓库规则执行，不用聊天记忆、个人习惯或临时提示词覆盖它。
   文档没有覆盖或彼此冲突时，明确指出缺口和冲突，核对代码与测试，并在需要时请用户决定；
   不能默默发明新流程、状态或产品语义。

## 2. 新后端必须先完成整条路线

**开始新后端第一阶段代码前，先制定覆盖该安装格式直至冻结的完整路线，讨论关键选择，并将确认后的方案写入仓库。
** `PackageFormat` 枚举值、通用接口或其他测试所用工具 fixture，不等于已有该后端，也不构成正式工具选型。
实施中出现改变产品语义的新需求时，先修订路线并确认选择，不边写代码边猜规则。

完整路线至少包含：

1. **能力与边界**：输入/输出、构建宿主、安装目标及用户场景。
   可参考固定提交的 Tauri 等产品，对元数据、安装/卸载、身份/版本、升级/降级、范围/权限、目录、桌面集成、签名、语言、静默/被动、退出码、失败/回滚、维护等逐项判定适用性、阶段、完成条件和明确不支持项。
   比较用户可观察结果，不复制上游字段、模板、翻译和内部机制；
   不为表面对齐引入不安全行为。
2. **工具供应与可行性**：核查官方/可信来源、版本、许可证和对应源码/声明义务、免费条件、包体积、SHA-256 与逐文件完整性、离线包供应/缓存、构建宿主自身依赖和目标设备范围。
   分析单个工具环境需求时，不混入调用方 MSBuild、SDK、NuGet 还原或应用构建需求。
   未实测平台列验证计划，不写成支持。
   若候选涉及付费必需服务、许可不清或不能合法再分发，先提出免费且许可可履行的替代方案；
   不加入侵权文件。
3. **格式基础语义**：身份、版本映射、升级/降级、组件和文件所有权、范围/权限、安装目录、卸载、事务/恢复及自定义扩展边界。
   会妨碍未来升级兼容的选择须在第一阶段产物出现前明确。
   格式差异留在后端，不照搬 NSIS 或 Tauri 的内部实现。
4. **完整阶段表**：从最小可用到格式冻结，逐阶段写目标、前置条件、交付物、明确不做事项、直接 API 与应用层覆盖、新增自动化、当前宿主真实测试、专用 CI/VM 和人工边界、退出条件。
   安装格式第一阶段就做真实安装/卸载烟雾测试，最终阶段做矩阵与冻结。
5. **可接班文档**：在总路线登记顺序，建立格式专用路线、能力矩阵、人工测试、外部待办及必要的上游审计；记录决策依据、未决问题、测试入口和完成标准。
   关键选择经用户确认才写为决定；规划完成不自动授权开始第一阶段代码。

每个新会话或新接班者的第一轮先完成项目理解和现状核对，向用户报告项目分层、当前事实、已实现/未实现边界、整体规划和下一步；
未完成这次理解报告前不修改代码。
每个后端或格式先制定覆盖到格式冻结的整体路线，确认关键产品选择后，再按阶段顺序推进；
一次只推进一个完整阶段，不在同一轮跨入下一阶段，也不把阶段拆成只修局部的小轮次。
每阶段开始前重读路线和实际状态，向用户报告阶段目标、差距、设计、预计文件、测试及退出条件；
得到该阶段启动指令后完成整个获授权阶段。
阶段结束即更新事实与默认下一步，并按本文件的阶段结束报告模板汇报。
当前格式顺序和计划见 `docs/roadmap.md`。

## 3. 分层、直接 API 与工具随包供应

- 产品面向调用方已准备好的普通应用目录，不限定被打包应用的语言或框架。
  `DotNet.Bundler.Abstractions` 定义公共契约；
  `DotNet.Bundler.Core` 负责格式无关的验证、规划、编排和工具缓存；
  `DotNet.Bundler.Nsis`、`DotNet.Bundler.Wix` 等后端各自实现格式语义。
  共享抽象须有真实跨格式需求，不能为假想复用扩大公共模型。
- **后端 NuGet 包本身必须可直接使用**：仓库外普通项目只引用相应后端包、没有本仓库源码或 `ProjectReference`、不引用 `DotNet.Bundler.MSBuild`，就能通过公共 API 生成该格式产物。
  `DotNet.Bundler.MSBuild` 是当前应用层，只把属性和 Item 映射到同一 Core/后端；
  Task 在 MSBuild 进程内调用它们，不再启动额外的 .NET CLI 驱动，实际格式编译器可作为工具进程启动。
  `DotNet.Bundler` 是便利元包。
  已完成的正式 CLI（`DotNet.Bundler.Cli`）与 MSBuild Task 同为应用层适配器，不决定后端设计。
  后端 API 不依赖 `.csproj`、`dotnet publish`、MSBuild 类型或应用使用 .NET。
- **默认打包必需工具和资源随对应后端包分发**：普通使用者无需另装 NSIS/WiX，也无需运行时联网下载打包工具。
  参照 NSIS 的内嵌工具与校验缓存模式，但须按各工具许可证和宿主条件设计。
  固定可信来源、版本、哈希、包内容、合法再分发及必要源码/声明；
  缓存污染、损坏和并发有安全处理。
  显式本地工具覆盖可作为受控高级入口。
  NuGet 包获取和调用方 SDK/MSBuild 环境属于另一层，不能由“包内工具离线”推断整台零环境机器无需其他依赖。
- 选择工具以**合法免费、无付费必需服务、尽量覆盖更多构建宿主和安装设备**为目标；
  实际支持范围以原生验证为准。
- **目标与格式门禁只按实际情况限制**（2026-09-30 用户确认原则）：
  允许的限制依据仅两类——①工具链真实能力（打包工具或宿主确实做不到，如 WiX 需 Windows 宿主、`dmg`/`pkg` 需 macOS）；②产物语义真实性（能产出文件但安装/运行语义不成立的组合——如 musl 载荷打进 `.deb`/`.rpm`（musl 生态无对应发行版消费面）——默认拒绝，显式豁免须用户明确要求）。
  除上述两类外不替工具预判：目标 RID 不做组合白名单——`BundleTarget.TryParse` 按 `<os>-<arch>` 语法解析（`DesktopOperatingSystem`/`CpuArchitecture` 枚举即 OS 与架构全集，枚举外值解析失败，出现需求时按行补枚举与后端映射）；
  工具能力由后端自报，不用与后端脱节的静态表替后端说不——防止矩阵与后端口径漂移（`win-x86` 门禁即此类人造围栏，2026-09-30 已拆除）。
  已落地（`7feea33`/`e7dfeae`/`02b2521`）：语法化解析 + 裸 `osx` 通用目标 + `linux-musl-x64/arm64`（仅 archive）。
- **构建宿主下限的三层口径**（格式路线文档按此分层记录，互不混淆）：
  1. **打包工具（能力）下限**：该格式所需打包工具本身支持的最低宿主——是否绑定宿主 OS、必需工具的最早可用宿主版本；声明时不得并入 Bundler 自身约束；
  2. **后端下限**：后端程序集（`netstandard2.0`）能被宿主上哪些 .NET 运行时加载并执行——只看后端，不含 MSBuild/CLI 应用层；
  3. **入口下限**：CLI、MSBuild Task 等各入口自身要求的宿主/SDK 下限，各自独立记录；
  4. 官方支持口径随 OS 厂商支持期滚动：以工具链/运行时官方支持矩阵为准，EOL 版本只记“可运行”不作支持承诺；
  5. 可选特性的宿主门槛逐条做可降级处理（降级+警告），不抬高必需链路下限。
  MSBuild Task 及其直接加载程序集遵守 `netstandard2.0` 资产契约。
  任务程序集须同时被 .NET Framework `MSBuild.exe` 和 `dotnet msbuild`（.NET SDK）双宿主加载，`netstandard2.0` 是两者的公共面；
  测试、示例、fixture、原型工具等可执行项目使用 .NET 10。
  不自动发现、下载、安装、修复或卸载任意**应用运行时/先决条件**（WebView2、VC++、.NET、JRE 等）；
  调用方可准备普通文件载荷。
  签名是打包发布能力，但生产私钥、HSM/云账户及第三方服务由发行方提供。
- 格式受管模式须定义身份、所有权与失败边界。
  用户自备脚本/模板或专家模式要显式标明责任范围，不能把任意用户逻辑宣称为 Bundler 内建安全能力。
  MSI 用 Windows Installer 原生事务，NSIS 的 journal 不自动成为其他格式的设计。
- 签名私钥、PFX 密码、访问令牌等秘密不得进入仓库、示例项目、普通命令行或可回显日志；签名失败不得留下被误认为成功的最终产物。
  生产证书与公开信任链只按真实发布环境验收。
- 快速开发期可依据实际用例调整早期 alpha API 与配置，不为未发布兼容性阻塞正确设计；已发布安装身份、用户数据与升级路径仍须按格式迁移规则保护。

## 4. 版本、配置与完整示例

- 实现、修复或随包文档调整导致 NuGet 包内容变化时，只在根 `Directory.Build.props` 递增 `BundlerPackageVersion`；
  包依赖、仓外包消费 fixture 的 `PackageReference` 与测试脚本默认值引用此值。
  结项前核对所有包及用户文档；
  不要在同一 ID/版本上发布不同内容并指望 NuGet 缓存刷新。
  一个未提交阶段使用同一新版本。
  工具包版本与被打包应用版本独立；
  公开示例应用版本保持稳定，升级/降级由独立 fixture 测试。
- 外部依赖版本集中在 `Directory.Packages.props`（中央包管理）声明，`PackageReference` 不带 `Version=`（否则 NU1008）；
  引用仓内 `DotNet.Bundler*` 包时用 `VersionOverride="$(BundlerPackageVersion)"` 覆盖中央版本。
  src/ 各工程共享元数据（`Authors`、`PackageVersion`、`PackageReadmeFile`、`netstandard2.0` TFM、README 与 THIRD-PARTY-NOTICES 打包项）统一在 `src/Directory.Build.props`（链式导入根 props），
  个别工程按项目名条件豁免；不在单 csproj 里重复这些值。
- 仓内示例与 fixture 一律以项目引用消费打包系统：共享接线在 `Bundler.ProjectReference.targets` 维护，
  它引用 `src/Bundler.MSBuild`（`ReferenceOutputAssembly=false`）、把 `_BundlerTaskAssembly` 指向源树构建输出并导入 `buildTransitive` 的 props/targets；
  `BuildReference=false` 使消费方不重建任务工程——任务程序集由 `dotnet build Bundler.slnx` 预建，
  避免常驻 MSBuild 节点持锁时消费方重写同一输出（MSB3021/3027）；
  消费项目只加一行 `GetPathOfFileAbove` 导入即可直接 `dotnet publish`，无需先 pack。
  API fixture 直引对应后端项目；`tests/Bundler.Tests` 同样使用项目引用。
- 仓外复制的独立包消费 fixture（模拟外部用户消费发布的 nupkg，是包契约的验收腿）仍走 `PackageReference`：
  还原垫片只保留在 `Bundler.LocalPackages.props`，由脚本连同项目复制并传入本轮包源、版本与隔离缓存，
  真实还原来源由 `Assert-LocalBundlerRestore` 逐包核验。
  缺包先 Pack，不假定未发布版本在公网源，也不用项目引用掩盖包消费问题。
- MSBuild 默认值须按 RID 族推导：`BundlerMainExecutable` 对 `osx-*`/`linux-*` 取 `$(TargetName)`（无 `.exe` 后缀），其余取 `$(TargetName).exe`；
  新增 RID 族或新宿主后缀规则时同步检查该默认（2026-09-27 LINUX-DEB-1 修正 linux 漏项）。
- **每个后端有完整、可操作的专用示例项目**，可参考 `samples/HelloNsisApp` 的演示形式，不用测试 fixture 冒充示例。
  默认命令应直接构建并生成安装包；
  该格式当前公开且适用的**所有用户能力**都要在示例中有实际配置、可复现的变体命令或明确的操作演示，不能只列名称。
  互斥配置分开演示；
  包含可检查的资源、元数据、桌面集成、语言、安装范围、签名配置等适用内容，以及安装/卸载、验证和清理说明。
  真实证书、提权或专用环境写明前提和未验收边界，不放私钥。
  新增公开能力在同阶段更新示例和中文说明；
  不适用的格式特性无需硬塞。
- 示例是演示，自动回归须另有 fixture。
  只服务一种格式的项目、目录、`.csproj` 和说明采用 `Nsis`、`Msi` 等格式名；跨格式项目和文档才用泛名。
  重命名项目可能改变安装身份、产物名或升级关系，须单独核对。

## 5. 测试风格、证据与阶段完成

**统一测试风格和组织，不统一各格式的用例清单。
** 新后端参考现有目录、命名、fixture、包源、脚本入口、断言、日志和清理；有实际格式原因可采用专用方式，并在格式测试说明写明。
不要模拟另一格式不存在的行为。
断言用惯用形式：异常断言用 `Assert.ThrowsAny*`/`ThrowsAnyAsync*`（catch 匹配子类，语义与 `Assert.Throws*` 的精确类型要求不同），
接返回值断言字段或消息；相等断言用 `Assert.Equal` 而非 `Assert.True(a==b)`；不用 try/catch 再断言。

单格式 API fixture 收编为 `tests/Bundler.ApiTests` 的 `<Format>ApiTests` 测试类，系统集成测试体收编为 `tests/Bundler.IntegrationTests` 的 `<Format>IntegrationTests` 测试类，均为 `dotnet test` 入口、`--filter-class`/`-method` 选择、宿主门控经 `Assert.Skip`；
资源需求用 `[Trait("Requires", ...)]` 标注——`docker`（DockerRunner 容器矩阵腿）、`elevation`（ElevatedRunner/SudoRunner 真装腿）标在方法级，`localinstall`（整体需 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL` 同意）标在类级；
按资源裁剪用 `--filter-trait "Requires=<值>"`/`--filter-not-trait "Requires=<值>"`；
`tests/<Platform>.<Format>.Integration` 目录保留 fixture、资产与同名 `Verify.ps1`/`Verify.sh` 薄入口（仅转发 `dotnet test`），其他脚本名写明用途；
跨格式快速测试保留 `tests/Bundler.Tests`。
格式专用测试与示例项目名称显式带格式名，只有真正跨格式的项目使用泛名。

| 用途 | NSIS 当前入口 | MSI 当前入口 | macOS `.app` 当前入口 |
| --- | --- | --- | --- |
| 快速单元/契约 | `tests/Bundler.Tests` | `tests/Bundler.Tests` | `tests/Bundler.Tests` |
| 后端 API fixture（仓内项目引用） | `tests/Bundler.ApiTests/NsisApiTests.cs` | `tests/Bundler.ApiTests/MsiApiTests.cs` | `tests/Bundler.ApiTests/MacAppApiTests.cs` |
| MSBuild fixture（仓内项目引用） | `tests/Windows.Nsis.Integration/Fixture` | — | `tests/MacOS.App.Integration/Fixture` |
| 仓外独立包消费 fixture | — | `tests/Windows.Msi.Integration/Fixture` + `Standalone/` | — |
| 真实集成（测试体在 `tests/Bundler.IntegrationTests`；脚本为薄入口） | `tests/Windows.Nsis.Integration/Verify.ps1` | `tests/Windows.Msi.Integration/Verify.ps1`；生命周期、维护、示例检查有专用脚本 | `tests/MacOS.App.Integration/Verify.sh`（bash，macOS 宿主） |
| 专用环境与人工 | `tests/Windows.Nsis.Reboot`、NSIS 人工清单 | MSI 人工清单 | `docs/mac-app-manual-testing.md` |

1. 每项新增或修改功能必须**新增或更新对应自动化测试**；缺陷修复断言要区分修复前后。
   按功能覆盖验证/映射、真实打包、NuGet 包内容、仓库外直接 API 包消费、应用层消费和适用的系统生命周期。
   只运行旧测试、只查模板/数据库或只发布公开示例，不证明新系统行为。
2. API fixture 以项目引用直引对应后端项目；
   MSBuild fixture 经 `Bundler.ProjectReference.targets` 接入 `src/Bundler.MSbuild`。
   仓库外复制的独立包消费 fixture（`tests/Windows.Msi.Integration/Fixture` 与 `Standalone/` 模板）保留 `PackageReference` 与 props 垫片，
   测试代码传本轮 `BundlerPackageSource`、`BundlerPackageVersion` 和独立缓存，并用 `MsiSupport.AssertLocalBundlerRestore` 核验实际还原的包 ID、版本、源与缓存。
   仓库外复制项目连同 props 复制；
   项目自身声明必要 SDK 属性，不靠根 props、旧 `obj` 或隐藏的 `--source` 才工作。
3. 集成测试按 Pack、隔离还原、生成产物、执行系统行为、断言最终状态组织，经 `tests/Bundler.IntegrationTests/Tooling`（`ProcessRunner`、`DockerRunner`、`ElevatedRunner`、`IntegrationWorkspace`、`MsiSupport`、`ShellLink`）实现；
   真装腿用 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 门禁（等价于旧脚本的 `-ConfirmLocalInstall`），薄入口脚本按其原开关语义置位。
   每轮使用独立输出和产品身份，预检无碰撞。
   记录命令、宿主/架构、包与应用版本、哈希、退出码、日志、清理及未验证范围。
   NuGet 可列出 SDK/VS fallback 文件夹，不能要求 `packageFolders` 只有缓存一项。
4. 安装格式最小阶段就做**真实安装/卸载烟雾测试**。
   新增生命周期功能在当前机器能安全执行时当阶段实测，并跑新增测试及受影响的跨后端回归。
   缺少 UAC、真实重启、生产证书、其他 Windows/架构、物理断网或高破坏故障环境时，列入对应格式人工/专用 CI/VM 清单；
   不阻塞本机可完成的开发，也不扩大支持声明。
   高风险试验只在可抛弃 VM/专机执行。
5. 集成失败即使复跑通过，也记录首次失败、调查和复跑，不能仅放宽断言。
   保留失败现场，明确归属后只清理本轮创建的安装、证书、注册表、进程和目录；删除/移动前核对解析后的绝对路径，不清理用户旧产物。
6. 阶段完成须满足路线的实现、包消费、入口映射、示例、中文文档、新增自动化、当前可执行真实测试、受影响回归和状态更新。
   外部未验收项准确保留，不能把当前可测功能推给人工，也不能把未测平台写成通过。

### 阶段结束报告模板

每个阶段完成后，先更新仓库中的事实、路线、能力矩阵、人工测试、外部待办和 `PROJECT_CONTEXT.md`，再向用户报告。
报告至少按以下顺序说明；没有内容的项写明“不适用”，不能省略导致接班者无法判断：

1. **阶段结论**：阶段是否达到退出条件；已完成、部分完成、未完成和明确不做的能力分别列出。
2. **项目理解与范围**：本阶段实际遵循的产品边界、关键决策和没有采用的替代方案；若执行中发现原计划有错误前提或遗漏，说明修正依据。
3. **实现改动**：公共模型、后端、应用层映射、独立包消费、示例、测试和文档的具体改动；未修改但经过核对的层说明原因。
4. **版本与产物**：`BundlerPackageVersion`、应用示例版本、工具版本、生成产物及必要的哈希或身份信息；区分工具包版本和被打包应用版本。
5. **自动化测试**：实际命令、测试范围、通过/失败、退出码和首次失败及复跑情况；不能只写“测试通过”。
6. **真实系统验证**：宿主 OS、版本、架构、权限和其他关键环境，真实安装/升级/修复/卸载等结果、退出码、日志和清理结果。
7. **未执行与外部边界**：因缺少 VM、UAC、生产证书、其他 Windows/架构、真实重启或其他条件而未执行的项目，放入对应格式人工测试或外部待办；不得写成已支持。
8. **文档与接班状态**：已更新的文档、当前事实、未决问题、测试入口和默认下一阶段；确认文档、代码、测试和 Git 状态一致，或列出仍待处理的差异。
9. **Git 操作**：当前分支、HEAD、工作区状态，以及是否提交。阶段报告不等于提交授权；提交和推送仍分别需要用户明确要求。

## 6. 文档、上游与交接

- 仓库维护的 Markdown 和新增的人类语言代码/脚本注释使用中文；代码标识、产品名、命令和上游引用可保留原文。
  第三方原始许可证、版权声明和源码原样保存，不因中文化改写法定文本；不为统一语言批量改写外部源码注释。
- 文档正文**一句一行**：句号、问号、叹号处断句；分号并列的多个独立陈述也拆开。
  表格行、代码块、列表标记保持原状；同段续行直接另起一行，Markdown 渲染时仍属同段。
  不写多句挤在一行的超长段落。
- 迁移或重排既有文档时保持内容等价：按原文件逐句移动或断句，不凭摘要重写；历史记录保留原日期、版本、哈希与当时结论，不改写成当前验证。
- 泛用文件名只用于跨格式内容。
  格式专用路线、能力矩阵、人工测试、外部待办、上游审计、示例说明、测试说明与工具来源均带格式名；
  `docs/manual-testing-index.md` 只作入口，不混放 NSIS、MSI、macOS、Linux 步骤及结论。
  NSIS 历史 `MT-01..MT-11` 不重编号，新增 ID 用格式前缀。
  移动文件时检查活动引用、包内容清单和校验脚本。
- 参考 Tauri 等上游时，固定提交、日期、官方来源、许可、用户能力、采用/拒绝理由、计划阶段与测试证据，写入**对应格式**审计。
  上游变化重新核对；不把旧快照说成最新，也不复制其代码、模板或翻译。
  没有明确用户结果的字段不强行映射。
- 能力矩阵统一使用可核实的状态：**已实现**须有自动化证据；**部分实现**说明缺口；**计划实现**绑定阶段；**外部待验收**指实现存在但缺专用环境证据；**不适用**用于框架或格式不相关能力；**明确拒绝**给出安全、所有权或产品边界理由。
  单台开发机结果不能推广为全部 Windows 或其他宿主支持。
- 总路线保存跨格式顺序；
  格式路线保存确认的决策、阶段目标/退出条件与证据；
  能力矩阵逐项标明实现、计划和外部边界；
  格式人工清单与 open-items 只收外部条件。
  `PROJECT_CONTEXT.md` 记录当前事实、包版本、最近验证、未决问题和默认下一步。
  每阶段结束即更新这些文件，即使暂不提交，也要让下一位仅凭仓库、Git、代码和测试继续工作。
  历史证据保留日期/版本，不改写成当前验证。
- 提交主题统一使用 `type(scope): 中文描述`；
  只有确属单一后端的改动才使用 `nsis`、`msi` 等后端 scope，例如 `feat(msi): 完成 WIN-MSI-2 生命周期与包验证`。
  跨格式改动不用单后端 scope；
  冒号为半角，后接一个空格，正文如有也使用中文，代码标识和产品名可保留原文。
- 未经用户明确要求不提交或推送；“提交”只授权提交，不授权推送。
  提交前核对 Git 状态、diff 和生成物，只纳入本阶段已验证改动。
  不用破坏性 Git 命令或宽泛递归清理代替审查。
  改写已有提交会改变 SHA；执行前核查分支、标签、远端与其他工作树，并保存可验证的恢复备份。

## 7. 当前主要命令入口

以下是当前仓库入口，不替代格式路线的测试表。
Windows 脚本省略 `-PackageVersion` 时从根 `Directory.Build.props` 读取当前值；执行前仍须核对 Git 和本地包。

```powershell
dotnet build Bundler.slnx -c Release
dotnet test tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet test tests/Bundler.ApiTests/Bundler.ApiTests.csproj -c Release
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release

powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyMaintenance.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi6.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi7.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyWinMsi8.ps1 -Configuration Release -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyPublicSample.ps1 -Configuration Release

# 各 Verify.* 均为薄入口，等价于 dotnet test + --filter-class/-method 转发（见各 tests/*/ 说明文档）。

bash tests/MacOS.App.Integration/Verify.sh
```

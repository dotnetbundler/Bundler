# Bundler 开发与交接规则

本文件是跨格式开发规则的唯一规范入口。`PROJECT_CONTEXT.md` 记录当前事实和验证证据，`docs/roadmap.md` 记录产品路线与阶段，格式专用文档记录各自语义；不要把历史测试结果当作当前事实。用户本次明确指令优先于本文件。

## 1. 接管与判断

1. 阅读根目录 `AGENTS.md`、本文件、`PROJECT_CONTEXT.md`、`docs/roadmap.md`、`README.zh-CN.md`；再读当前格式的路线、能力矩阵、人工测试、外部待办和相关代码/测试。人工文档入口为 `docs/manual-testing-index.md`。
2. 运行 `git branch --show-current`、`git rev-parse --short HEAD`、`git status --short`，读取 `Directory.Build.props` 的包版本。核对文档声明与实际 API、包依赖、测试及产物；快照可能过期。
3. 先检查请求的错误前提、遗漏条件和跨层影响。向用户说明当前状态、设计判断、改动范围、验证计划和外部限制；影响产品语义的选择先问，能从代码和测试核实的细节自行核实。
4. 一个阶段或修复要处理完整因果链：源码、独立包消费、入口映射、示例、自动化、真实系统行为和文档。若某层不适用，说明原因。不要只修用户首先观察到的一处报错。

## 2. 产品和包边界

- 目标是可处理调用方准备好的普通文件目录的通用桌面打包工具。`DotNet.Bundler.Abstractions` 定义公共契约；`DotNet.Bundler.Core` 负责格式无关的验证、规划、编排与工作目录；`DotNet.Bundler.Nsis`、`DotNet.Bundler.Wix` 等格式包分别提供可直接调用的公共 API、格式行为和所需构建工具。引用某个后端 NuGet 包的普通项目，即使没有本仓库源码或 MSBuild 集成，也应能生成该格式安装包。
- `DotNet.Bundler.MSBuild` 是当前应用层：把 MSBuild 属性和 Item 映射到公共请求，再调用同一 Core/后端。`DotNet.Bundler` 是便利元包。将来正式 CLI 也是应用层，复用同一实现；现有 CLI 只是原型，按路线在计划格式完成后处理。后端公共模型不能依赖 `.csproj`、`dotnet publish`、MSBuild 类型或调用方使用 .NET。
- MSBuild Task 及其直接加载的程序集提供 `netstandard2.0` 资产，进程内调用 Core/后端；格式必需的原生编译器可作为后端工具启动。工具随对应 NuGet 包提供，固定来源/版本/哈希/许可证，在用户级校验缓存使用；不要在运行时下载任意应用运行时或先决条件。
- 适用的用户能力可对齐参考产品，但不照搬 Tauri/NSIS 内部实现。MSI 使用 Windows Installer 原生事务和所有权规则；不为了形式一致复制 NSIS 的 journal、自定义操作或不适用功能。

## 3. 版本、包和示例

- 实现或修复导致 NuGet 包内容变化时，递增根目录 `BundlerPackageVersion`，同步被修改包的依赖版本、公开示例的 `PackageReference`、测试默认版本和中文说明。不要在同一个 ID/版本上重复发布不同包并指望 NuGet 缓存更新。一个阶段的未提交工作使用同一个新版本，结项前统一检查。
- 工具包版本与被打包应用版本独立。`HelloBundledApp`、`HelloMsiApp` 等公开示例应用版本保持稳定；升级/降级使用独立 fixture 的版本参数。正式发布的应用按目标格式规则递增产品版本。MSI 后端拒绝在相同输出路径以相同产品版本覆盖不同内容；开发期旧产物应移到明确位置或选用独立输出目录，不通过弱化冲突保护处理。
- 公开示例是可操作演示，不是自动化测试。当前 NSIS 与 MSI 示例均通过固定当前开发包版本的普通 `PackageReference`、仓库本地 `artifacts/packages` 还原源，在根目录执行 `dotnet pack Bundler.slnx -c Release -o artifacts/packages` 后直接 `dotnet publish <示例项目> -c Release`。新增公开能力要同步展示在对应示例；缺少本地包时先 pack，不能假设源码项目引用或公共 NuGet 源会提供未发布版本。

## 4. 测试风格与验证要求

**统一的是测试风格，不是测试用例清单。** 新后端先参考现有模块的目录、命名、fixture、包源、脚本入口、断言、日志和清理方式；没有实际理由就沿用这些习惯，让测试看起来属于同一个项目。各格式按自身能力与系统语义决定测试内容和数量，不要求相同用例，也不把 NSIS 的 journal 行为搬到 MSI。每项新增/修改功能仍须增加或更新对应自动化测试；缺陷修复要有能区分修复前后行为的断言。

下表是当前两个 Windows 后端的测试入口，供新后端沿用组织方式；它不是要求每个格式具备完全相同场景的清单。

| 测试用途 | 当前 NSIS 入口 | 当前 MSI 入口 |
| --- | --- | --- |
| 快速契约 | `tests/Bundler.Tests` | `tests/Bundler.Tests` |
| 独立后端包 API fixture | `tests/Nsis.Api.PackageFixture` | `tests/Msi.Api.PackageFixture` |
| MSBuild 包消费 fixture | `tests/Windows.Nsis.Integration/Fixture` | `tests/Windows.Msi.Integration/Fixture` |
| Windows 集成入口 | `tests/Windows.Nsis.Integration/Verify.ps1` | `tests/Windows.Msi.Integration/Verify.ps1`；生命周期另有 `VerifyLifecycle.ps1` |
| 专用环境/人工 | `tests/Windows.Nsis.Reboot`、NSIS 人工文档 | MSI 人工文档及未来专用测试 |

新增测试时尽量保持以下写法一致：

1. **目录与名称**：格式专用 API fixture 使用 `tests/<Format>.Api.PackageFixture`；系统集成测试使用 `tests/<Platform>.<Format>.Integration`，以 `Verify.ps1` 为入口，其他场景用说明用途的脚本名。快速测试沿用仓库现有测试入口和可读的用例名。不同格式的人工测试与结果分开存放。
2. **fixture 与包源**：后端 API fixture 只引用打出的对应后端包，通过公共 API 生成产物；MSBuild fixture 引用打出的应用层包。现有 Windows fixture 都在项目文件中声明 `<RestoreSources>$(BundlerPackageSource)</RestoreSources>`，脚本传入本轮打出的本地包目录及隔离缓存；检查实际还原的包 ID、版本、源与缓存。验证普通仓库外消费者时，明确复制所需项目文件与资源到每轮临时目录，fixture 项目自身声明必需的 SDK 属性，不依赖仓库级 Directory.Build.props 或旧 obj。不要让某个格式只能靠脚本里隐藏的 `--source` 才能找到开发包。
3. **脚本组织**：复用现有的包检查与断言辅助脚本；按 Pack、还原、构建产物、执行系统行为、核对状态的顺序组织，失败时报告明确原因。每轮使用独立输出目录；有产品身份的安装测试使用独立身份。执行前检查目标未被占用，只清理本轮创建的状态，保留可核查的日志和哈希。
4. **结果与交接**：记录运行命令、主机/架构、包和产物版本、哈希、日志、退出码、清理结果及未验证范围。公开示例用真实本地包源验证，但不代替测试 fixture。共用 Core、MSBuild 或便利元包有改动时，运行其他受影响后端的回归。

测试内容仍由格式和阶段决定：安装格式在最小阶段执行真实安装/卸载；新能力按其实际系统行为增加回归，不能只检查模板或数据库。当前机器能安全测试的内容当阶段执行；缺少 UAC、重启、生产证书或其他宿主时，按本格式人工文档记录未验收，不冒充通过。格式特有的功能直接设计专用测试，**无需为了形式一致制造另一后端的对应功能或测试**。若确需改变上面的组织或调用习惯，在本格式测试说明或路线中简述实际原因和采用的方式；涉及产品语义的分歧先与用户确认。

- 当前 Windows 集成脚本共用 `tests/AssertLocalRestore.ps1`。NuGet 可能同时列出 SDK/Visual Studio 的本地 fallback 文件夹，因此检查 Bundler 包确实进入本轮隔离缓存，不能错误地要求 `packageFolders` 只有一项。
- 阶段结束前运行新增测试和受影响的回归，验证所改功能涉及的包、入口与实际系统行为；测试范围由该格式路线和能力矩阵决定。
- 集成测试出现失败后即使复跑通过，也记录失败断言和复跑结果；未查明根因时不能把偶发失败改写成从未发生。需要排查时先保存失败现场，不能单靠放宽断言消除信号。

## 5. 文档、状态与 Git

- 当前维护根 `README.zh-CN.md`；不要求同步英文 `README.md`。NSIS 示例说明使用中文。面向用户的文档只描述实际可用能力；路线、预测与外部待验收分别记录。
- 新格式创建自己的路线、能力矩阵、外部待办和人工测试文档，统一从 `docs/manual-testing-index.md` 进入；保留 NSIS 历史用例 ID，不把不同格式的步骤混在同一清单。新增功能同步更新相应 API/入口属性说明、示例和测试入口。
- `PROJECT_CONTEXT.md` 记录实时阶段状态、最后验证、未决问题和下一步；`docs/roadmap.md` 记录跨格式阶段顺序；格式路线记录决策与阶段证据。事实、方案、预测分开，不能把未实施、未自动验证或未人工验收的能力写成已完成。结束一个阶段时即使不提交，也更新这些文档，使下次对话无需旧聊天记录。
- 未经用户明确要求不提交或推送。提交前核对工作区和 diff，只纳入本阶段已验证文件；保留无关改动。不使用破坏性的 Git 命令或宽泛递归清理。Windows 删除/移动本轮测试产物前核对解析后的绝对路径和归属。

## 6. 当前命令入口

```powershell
dotnet build Bundler.slnx -c Release
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.35
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.35 -ConfirmLocalInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.35 -ConfirmLocalInstall
```

命令中的版本是此文件最后更新时的示例值；执行前以 `Directory.Build.props`、实际包和 Git 状态为准。真实重启/UAC 等专用测试按格式文档的环境限制运行。

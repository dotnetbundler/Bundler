# Bundler 仓库协作入口

本文件是任何新会话、新开发者或新 AI 接管本仓库时的第一入口。
它的职责是指路：告诉你按什么顺序读什么、哪些规则不可违反、改动前要确认什么。
规则正文只维护在 `docs/development-rules.md`，本文件不复述全文，只列出必须先记住的硬约束。

## 0. 一句话现状

`DotNet.Bundler` 是通用桌面打包工具，按"一次一个安装格式"推进。
Windows NSIS 与 WiX/MSI（WIN-MSI-1..9）、macOS `.app`/`.dmg`/`.pkg` 均已冻结并入 `main`；Linux `.deb` 已冻结于 `linux-deb-development`（`LINUX-DEB-1..5`）并入 `main`，`.rpm` 已冻结于 `linux-rpm-development`（`LINUX-RPM-1..5`）；全部格式（NSIS/MSI/APP/DMG/PKG/DEB/RPM/AppImage/Archive/Alpine `.apk`）与 `CLI-C1`（冻结于 `0.1.0-alpha.62`，`.apk` 冻结于 `0.1.0-alpha.63`）均已完成并并入 `main`。
准确的当前阶段、包版本和下一步以 `PROJECT_CONTEXT.md` 文首为准——不要凭本文档或对话记忆判断现状。

## 1. 接管顺序（未完成前不得改代码）

1. 读本文件。
2. 读 `docs/development-rules.md`——全部泛用规则只在这里，任何别处看到的规则如与它冲突以它为准并先报告。
3. 读 `PROJECT_CONTEXT.md` 文首的当前事实（分支、包版本、当前阶段、下一步）。
4. 读 `docs/roadmap.md` 的产品边界与格式顺序。
5. 读 `README.md` 了解面向用户的当前能力。
6. 读当前格式的专属文档：`<format>-roadmap.md`、`<format>-capability-matrix.md`、`<format>-manual-testing.md`、`<format>-open-items.md`、上游审计/参考；
   格式清单见 `docs/manual-testing-index.md`。
7. 读与将做工作直接相关的代码、测试和 fixture。
8. 需要历史背景时才读 `docs/project-history.md`；它是按时间排序的记录，不代表当前状态。

然后核对真实状态，不把交接快照当事实：

```powershell
git branch --show-current
git rev-parse --short HEAD
git status --short
# 根 Directory.Build.props 中的 BundlerPackageVersion
# 实际 API、测试入口、示例、产物目录
```

## 2. 修改前必须向用户汇报

完成上面的阅读与核对后、动任何代码或文档前，用中文向用户报告：

1. 你对项目结构和当前后端的理解；
2. 当前真实实现状态和验证证据（含你实际执行的命令与结果）；
3. 当前阶段、整体规划和下一步；
4. 需要用户决定的关键问题——带你的推荐和取舍。

存在影响产品语义、架构或成本的未决问题时停下来等确认；没有关键问题时等用户明确给出阶段启动指令（如"开始 WIN-MSI-7"）再动手。
报告不等于授权；授权到哪个阶段就只完成哪个阶段。

## 3. 不可违反的硬约束

这些条目只是 `docs/development-rules.md` 中最容易出事的摘要，详细条款以该文件为准。

1. **先判断再执行**：检查需求的错误前提、逻辑跳跃和缺失信息；独立区分事实、计划、预测和外部待验收；不同意要说明依据、风险和替代方案。
2. **不重建规则**：先查仓库已有的规则、路线、能力矩阵和交接文档；文档缺失或冲突时指出并请用户裁决，不发明流程或产品语义。
3. **新后端先立完整路线**：覆盖到格式冻结的路线经用户确认并写入仓库后，才能写第一阶段代码。
4. **一次一个完整阶段**：不跨阶段、不只做局部修补；缺环境的能力记入对应格式的人工/外部清单，不阻塞、也不冒充已通过。
5. **工具随包供应**：默认打包必需的工具内嵌在后端 NuGet 包中并带固定哈希校验，不运行时下载，不引入付费必需服务。
6. **入口只是适配层**：MSBuild Task 与未来 CLI 调用同一 Core/后端公共 API；后端不依赖 MSBuild、`.csproj`、dotnet publish 或 .NET 应用模型。
7. **新功能必有新测试**：每项新增/修改功能要有可区分旧行为的自动化断言；本机可安全执行的真实测试当阶段跑完；只运行旧测试不算完成。
8. **秘密不入库**：签名私钥、PFX 密码、令牌不进入仓库、项目文件、普通命令行或可回显日志。
9. **文档各司其职**：泛用规则只在 `docs/development-rules.md`；某格式的规则、契约、路线、证据只放该格式的文档；
   历史记录只放 `docs/project-history.md`；当前事实只放 `PROJECT_CONTEXT.md`。
   文档用中文，一句一行。
10. **Git 纪律**：未经用户明确说"提交"不提交；未明确要求不推送；提交消息用 `type(scope): 中文描述`；保留用户已有的无关工作区改动；清理只限本轮明确创建且已核对归属的产物。
11. **诚实标注状态**：缺环境的验收项（UAC、生产证书、真实重启、干净宿主、ARM64 等）保留为"外部待验收"，不得写成已通过；单台开发机的结果不得推广成平台兼容声明。

## 4. 文档地图（一个文件只放它该放的东西）

| 文件 | 只放什么 | 不放什么 |
| --- | --- | --- |
| `AGENTS.md` | 接管入口与硬约束摘要 | 规则全文、状态详情 |
| `docs/development-rules.md` | 全部跨格式泛用规则 | 任何格式专属内容、历史记录 |
| `docs/roadmap.md` | 产品边界、格式顺序、未来计划 | 协作规则细则、格式实现细节 |
| `PROJECT_CONTEXT.md` | 当前事实：版本、阶段、最近验证、未决问题、下一步 | 规则、历史分轮记录、格式专属细节 |
| `docs/project-history.md` | 按时间排序的过往工作与当时证据 | 当前状态结论、规则 |
| `README.md` | 面向使用者的能力与用法 | 协作规则、阶段计划 |
| `docs/<format>-roadmap.md` | 该格式的设计决策、阶段目标与实施证据 | 泛用规则、其他格式内容 |
| `docs/<format>-capability-matrix.md` | 该格式逐项能力与验收状态 | 规则、路线叙述 |
| `docs/<format>-manual-testing.md` | 该格式人工验收用例 | 可自动化的内容、外部条件索引 |
| `docs/<format>-open-items.md` | 该格式外部输入/环境待办索引 | 可本机完成的事项、用例步骤 |
| `docs/<format>-upstream-*.md` 等 | 该格式上游参照与取舍 | 当前能力承诺 |
| `samples/<name>/*-sample.md` | 该示例的操作说明 | 后端规则、阶段证据 |
| `tests/<dir>/<format>-*.md` | 该测试入口的用法说明 | 测试结果记录（写进阶段证据） |
| `third_party/<tool>/*-provenance.md` | 第三方来源、许可、哈希核查 | 实现规则 |
| `docs/manual-testing-index.md` | 各格式人工文档入口 | 用例步骤、结论 |

找不到该放哪的内容时，先停下来想它属于"规则、计划、当前事实、历史、某格式、用户文档、第三方"中的哪一类，再放进对应文件；都不合适时向用户说明再定。

## 5. 常用命令

```powershell
dotnet build Bundler.slnx -c Release
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
```

各格式真实集成入口见 `docs/development-rules.md` 的命令节和各 `tests/*/` 说明；Windows MSI 的本机安装测试需要 `-ConfirmLocalInstall`。

## 6. 阶段结束

每个阶段完成后按 `docs/development-rules.md` 的阶段结束报告模板汇报，并在同一轮更新 `PROJECT_CONTEXT.md`、`docs/roadmap.md` 状态行、对应格式文档。
提交与推送分别需要用户明确要求；阶段完成不构成提交授权。

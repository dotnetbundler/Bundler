# CLI 产品化路线（CLI-C1）

> 状态：规划轮完成，待逐条裁决；实施尚未开始。
> 范围依据：`docs/roadmap.md` CLI-C1 节——固化共享配置 schema、`validate`/`plan`/`bundle` 共用 Core/后端、稳定退出码/机器可读输出/日志/帮助/版本/发布方式、覆盖全部已冻结格式、删除早期 alpha 参数不承诺兼容。
> 上游参照：`tauri-cli` 审计（`tauri-apps/tauri` DeepWiki）——命令面分层、`--bundles` 参数优先于配置文件 `bundle.targets`、`--config` 层叠、环境变量 verbosity 等模式已复核取舍；Tauri 面向"开发工具链"（dev/build/init/migrate/mobile），本 CLI 只做打包，范围更窄。

## 1. 决策清单（待裁决，推荐项已标出）

| # | 决策点 | 候选 | 推荐与理由 |
| --- | --- | --- | --- |
| 1 | 命令面 | bundle 单命令 / `validate`+`plan`+`bundle` 三命令 / 加 `info`/`formats` | **`validate`+`plan`+`bundle` 三命令**——roadmap 已固化此范围；`validate` 校验配置与输入目录不产出，`plan` 打印解析后计划（格式×rid→产物路径）不构建，`bundle` 产出。`info`/`formats` 登记拒绝（`plan --json` 已可给出矩阵信息，独立命令无增量价值） |
| 2 | 参数解析 | `System.CommandLine` / Spectre.Console.Cli / 手写 | **手写最小解析器**——仓库基调是零外部依赖托管实现；三命令参数面小，引入 CLI 框架依赖（含 beta 版位）得不偿失 |
| 3 | 配置载体 | 仅命令行 / 仅 `bundler.json` / 文件+参数层叠 | **`bundler.json` + 命令行层叠**（参数覆盖文件值，仿 Tauri `tauri.conf.json`+`--config` 模式）；schema 与 MSBuild `Bundler*` 属性一一对应（§2 映射表），一份知识两处消费 |
| 4 | 机器可读输出 | 无 / `--json` 全局开关 | **`--json`**：`plan`/`validate`/`bundle` 均支持；stdout 只放机器可读 JSON，人类日志走 stderr——管道组合友好，CI 可直接消费产物清单 |
| 5 | 退出码 | 0/1 两值 / 分级枚举 | **三级**：`0` 成功；`1` 打包/后端执行失败；`2` 用法或配置校验失败（`validate` 的"配置非法"也归 2）。文档固化，向后兼容承诺自此建立 |
| 6 | 日志 | verbose 计数 / quiet/normal/verbose 三档 | **三档**：`--quiet`/`--verbose` 二选一开关，缺省 normal；内部走既有 `IBundleLogger` 接口，CLI 适配器写 stderr |
| 7 | 格式选择 | `--formats` 参数 / 配置文件 / 二者层叠 | **`--formats zip,targz` 优先，其次 `bundler.json` 的 `formats`，再次目标 OS 缺省集**——与 Tauri `bundle.targets` 优先级语义一致 |
| 8 | 输入契约 | CLI 跑 dotnet publish / 只吃已发布目录 | **只吃已发布目录**：`--input-dir`（必填）+ `--rid` + `--formats` + 包名/版本；`dotnet publish` 由用户或 MSBuild 路径负责——后端契约本来就拿 InputDirectory，CLI 不做二次构建编排 |
| 9 | 分发方式 | dotnet tool / 自包含单文件 / 两者 | **`dotnet tool`（`DotNetCliTool` nupkg，`bundler` 命令名）**为唯一首发通道；自包含/AOT 单二进制登记 OI 后置评估（体积与 RID 扇出成本需实测） |
| 10 | 旧原型处置 | 保留兼容 / 原地重写 / 删除重建 | **删除重建**——`src/Bundler.Cli`（164 行 NSIS-only 原型，`IsPackable=false`）整体删除，按新决策面重实现；用户已明确快速开发期不承诺兼容 |
| 11 | 环境变量 | 无 / `DOTNET_BUNDLER_*` 族 | **仅 `DOTNET_BUNDLER_VERBOSE`**（CI 场景）——其余一律显式参数，避免隐式行为面膨胀 |
| 12 | hooks/脚本化扩展 | beforeBundleCommand 族 / 拒绝 | **明确拒绝**——与干净宿主立场一致，不新增外部进程编排面；需要编排由用户脚本在 CLI 外做 |
| 13 | shell completions | 提供 / 拒绝 | **明确拒绝**——手写解析器下补全脚本维护成本大于价值；`--help` 足够 |
| 14 | 遥测/联网 | 无 / 匿名遥测 | **明确拒绝**——打包工具不应联网 |
| 15 | 阶段骨架 | 两段 / 三段 | **三段**：`CLI-1` 骨架（新 Cli 项目+三命令+退出码+`--json`+三档日志+旧原型删除）→ `CLI-2` 配置 schema 固化与全格式旋钮透传 → `CLI-3` dotnet tool 打包分发 + 文档定稿 + 冻结 |

## 2. MSBuild ↔ CLI 配置映射表（schema 固化对象）

| MSBuild 属性/项 | `bundler.json` 字段 | CLI 参数 |
| --- | --- | --- |
| `BundlerFormats` | `formats: string[]` | `--formats` |
| `BundlerPackageName`/`BundlerVersion` | `packageName`/`version` | `--package-name`/`--package-version` |
| `BundlerRuntimeIdentifier` | `rid` | `--rid` |
| `PublishDir` → `InputDirectory` | `inputDir` | `--input-dir` |
| `BundlerOutputDirectory` | `outputDir` | `--output-dir` |
| `Bundler<Format>*` 各格式旋钮 | `<format>.*` 子对象 | `--<format>.<knob>=<value>` 通用键值透传 |
| `@(Bundler<Format>File)` | `<format>.files: [{source,destination}]` | 仅配置文件承载（命令行不映射文件列表） |

> 原则：CLI 参数覆盖高频项；低频/结构化项（文件映射、关系字段列表）仅经配置文件承载——避免命令行参数面无限膨胀。

## 3. 阶段表

| 阶段 | 范围 | 退出条件 |
| --- | --- | --- |
| `CLI-1` | 删 `src/Bundler.Cli` 原型；新建 `src/Bundler.Cli`（`bundler` exe，`DotNet.Bundler.Cli`）：手写解析器 + `validate`/`plan`/`bundle` + `--input-dir`/`--rid`/`--formats`/`--json`/`--quiet`/`--verbose` + 三级退出码 + 单元测试 + `tests/Cli.Integration/Verify.sh` | 三命令在八种冻结格式上跑通最小链路；`--json` 输出结构断言；退出码分级断言；`Bundler.Tests` 全绿 |
| `CLI-2` | `bundler.json` 配置 schema 固化 + `--config` 层叠 + 全格式旋钮/文件映射透传 + 非法配置拒绝路径 | schema 文档定稿；每格式至少一组 config 驱动断言；逃逸/非法值拒绝断言全绿 |
| `CLI-3` | `DotNetCliTool` nupkg 打包 + `dotnet tool install` 实测 + README/示例文档 + 能力矩阵定稿 + OI/MT 收口 + 冻结基线写入 | 本地 `dotnet tool install --global` 实装跑通；冻结文档与清单完备 |

## 4. 阶段实施证据

（实施阶段回填。）

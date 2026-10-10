# CLI 产品化路线（CLI-C1）

> 状态：**已冻结**于 `0.1.0-alpha.62`（2026-09-27，分支 `cli-development`）。CLI-1/2/3 + CLI-AOT 全部完成，证据见 §4。
> 范围依据：`docs/roadmap.md` CLI-C1 节——固化共享配置 schema、`validate`/`plan`/`bundle` 共用 Core/后端、稳定退出码/机器可读输出/日志/帮助/版本/发布方式、覆盖全部已冻结格式、删除早期 alpha 参数不承诺兼容。
> 上游参照：`tauri-cli` 审计（`tauri-apps/tauri` DeepWiki）——命令面分层、`--bundles` 参数优先于配置文件 `bundle.targets`、`--config` 层叠、环境变量 verbosity 等模式已复核取舍；Tauri 面向"开发工具链"（dev/build/init/migrate/mobile），本 CLI 只做打包，范围更窄。

## 1. 决策清单（已裁决——按推荐项放行）

| # | 决策点 | 候选 | 推荐与理由 |
| --- | --- | --- | --- |
| 1 | 命令面 | bundle 单命令 / `validate`+`plan`+`bundle` 三命令 / 加 `info`/`formats` | **`validate`+`plan`+`bundle` 三命令**——roadmap 已固化此范围；`validate` 校验配置与输入目录不产出，`plan` 打印解析后计划（格式×target→产物路径）不构建，`bundle` 产出。`info`/`formats` 登记拒绝（`plan --json` 已可给出矩阵信息，独立命令无增量价值） |
| 2 | 参数解析 | `System.CommandLine` / Spectre.Console.Cli / 手写 | **手写最小解析器**——仓库基调是零外部依赖托管实现；三命令参数面小，引入 CLI 框架依赖（含 beta 版位）得不偿失 |
| 3 | 配置载体 | 仅命令行 / 仅 `bundler.json` / 文件+参数层叠 | **`bundler.json` + 命令行层叠**（参数覆盖文件值，仿 Tauri `tauri.conf.json`+`--config` 模式）；schema 与 MSBuild `Bundler*` 属性一一对应（§2 映射表），一份知识两处消费 |
| 4 | 机器可读输出 | 无 / `--json` 全局开关 | **`--json`**：`plan`/`validate`/`bundle` 均支持；stdout 只放机器可读 JSON，人类日志走 stderr——管道组合友好，CI 可直接消费产物清单 |
| 5 | 退出码 | 0/1 两值 / 分级枚举 | **三级**：`0` 成功；`1` 打包/后端执行失败；`2` 用法或配置校验失败（`validate` 的"配置非法"也归 2）。文档固化，向后兼容承诺自此建立 |
| 6 | 日志 | verbose 计数 / quiet/normal/verbose 三档 | **三档**：`--quiet`/`--verbose` 二选一开关，缺省 normal；内部走既有 `IBundleLogger` 接口，CLI 适配器写 stderr |
| 7 | 格式选择 | `--formats` 参数 / 配置文件 / 二者层叠 | **`--formats zip,targz` 优先，其次 `bundler.json` 的 `formats`，再次目标 OS 缺省集**——与 Tauri `bundle.targets` 优先级语义一致 |
| 8 | 输入契约 | CLI 跑 dotnet publish / 只吃已发布目录 | **只吃已发布目录**：`--input-dir`（必填）+ `--target` + `--formats` + 包名/版本；`dotnet publish` 由用户或 MSBuild 路径负责——后端契约本来就拿 InputDirectory，CLI 不做二次构建编排 |
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
| `BundlerTarget` | `rid` | `--target` |
| `PublishDir` → `InputDirectory` | `inputDir` | `--input-dir` |
| `BundlerOutputDirectory` | `outputDir` | `--output-dir` |
| `Bundler<Format>*` 各格式旋钮 | `<format>.*` 子对象 | `--<format>.<knob>=<value>` 通用键值透传 |
| `@(Bundler<Format>File)` | `<format>.files: [{source,destination}]` | 仅配置文件承载（命令行不映射文件列表） |

> 原则：CLI 参数覆盖高频项；低频/结构化项（文件映射、关系字段列表）仅经配置文件承载——避免命令行参数面无限膨胀。

## 2.1 配置字段统一迁移表（破坏性重构，无兼容别名）

alpha.88 之后各后端配置字段统一为同一套命名契约：JSON 键（camelCase）、CLI `--<fmt>.<knob>` 键与 MSBuild `Bundler*` 属性同步改名，旧键一律按未知键拒绝（rc=2）。

| 后端 | 旧键 → 新键 |
| --- | --- |
| 全部 | 附加文件统一 `files: [{source,destination}]`；安装域统一 `installScope`；路径后缀仅 `*File`/`*Directory` |
| deb | `revision`→`release`、`maintainer`→`vendor`、`conffiles`→`configLocations`、`preinstFile`/`postinstFile`/`prermFile`/`postrmFile`→`preInstallFile`/`postInstallFile`/`preUninstallFile`/`postUninstallFile`、`compression` 删除（恒 gzip）、`categories` 改字符串数组 |
| rpm | `requires`→`depends`、`compression` 删除、`categories` 改字符串数组、`signingKeyFile`/`signingKeyPassphrase`→`signing.keyFile`/`signing.passphrase` |
| alpineapk | `preInstallScript`/`postInstallScript`/`preDeinstallScript`/`postDeinstallScript`/`preUpgradeScript`/`postUpgradeScript`→`*File` 同名单、`signing*`→`signing.*` |
| appimage | `categories` 改字符串数组、`signing*`→`signing.*` |
| app | `bundleName`→`packageName`、`contents`→`files`（条目 `targetPath`→`destination`）、`frameworks`→`frameworkDirectories`、签名 `temporaryCertificatePath`→`temporaryCertificateFile`、`apiKeyPath`→`apiKeyFile` |
| dmg | `skipWindowLayout`/`windowX`/`windowY`/`windowWidth`/`windowHeight`/`appIconX`/`appIconY`/`applicationsIconX`/`applicationsIconY`/`iconSize`→`layout` 子对象（`layout=null` 即跳过）、签名 `temporaryCertificatePath`→`temporaryCertificateFile` |
| pkg | `identifier`→`packageName`、`installLocation`→`installRoot`、`payloadItems`→`files`、`domain`→`installScope`、签名 `temporaryCertificatePath`→`temporaryCertificateFile`、`apiKeyPath`→`apiKeyFile` |
| msi | `msiVersion`→`version`、`bannerBitmap`→`bannerFile`、`dialogBitmap`→`dialogFile`、`expertTemplate`→`expertTemplateFile`、`extensionFragments`→`extensionFragmentFiles`、`expertMergeModules`→`expertMergeModuleFiles` |
| nsis | `installMode`→`installScope`、`installerIcon`→`installerIconFile`、`uninstallerIcon`→`uninstallerIconFile`、`headerImage`→`headerFile`、`sidebarImage`→`sidebarFile`、`uninstallerHeaderImage`→`uninstallerHeaderFile`、`installerHooks`→`installerHooksFile` |

MSBuild 属性同步：`Bundler<Format><Old>` 全部按上表改名（如 `BundlerDebRevision`→`BundlerDebRelease`、`BundlerMacDmgWindowX`→`BundlerMacDmgLayoutWindowX`、`BundlerMacPkgIdentifier`→`BundlerMacPkgPackageName`）；mac 公证 API key 三元组统一 `Bundler<App|Pkg>NotaryApiKeyFile`/`NotaryApiKeyId`/`NotaryApiKeyIssuer`（config 侧对应 `ApiKeyFile`/`ApiKeyId`/`ApiKeyIssuer`）；约定：载荷相对路径（`shortcuts.icon`、`configLocations` 等）永不带 File/Path/Directory/Files/Directories 后缀；宿主路径旋钮由 `BundlerJsonContext` 元数据按复数后缀自动导出（PathKnobs 不再手维护）；`BundlerDebCompression`/`BundlerRpmCompression` 删除；`BundlerMacFramework` 项组改名 `BundlerMacAppFrameworkDirectory`；`BundlerMacAppFile`/`BundlerMacPkgFile`/`BundlerResource` 等项的元数据 `TargetPath`→`Destination`。

## 3. 阶段表

| 阶段 | 范围 | 退出条件 |
| --- | --- | --- |
| `CLI-1` | 删 `src/Bundler.Cli` 原型；新建 `src/Bundler.Cli`（`bundler` exe，`DotNet.Bundler.Cli`）：手写解析器 + `validate`/`plan`/`bundle` + `--input-dir`/`--target`/`--formats`/`--json`/`--quiet`/`--verbose` + 三级退出码 + 单元测试 + `tests/Cli.Integration/Verify.sh` | 三命令在八种冻结格式上跑通最小链路；`--json` 输出结构断言；退出码分级断言；`Bundler.Tests` 全绿 |
| `CLI-2` | `bundler.json` 配置 schema 固化 + `--config` 层叠 + 全格式旋钮/文件映射透传 + 非法配置拒绝路径 | schema 文档定稿；每格式至少一组 config 驱动断言；逃逸/非法值拒绝断言全绿 |
| `CLI-3` | `DotNetCliTool` nupkg 打包 + `dotnet tool install` 实测 + README/示例文档 + 能力矩阵定稿 + OI/MT 收口 + 冻结基线写入 | 本地 `dotnet tool install --global` 实装跑通；冻结文档与清单完备 |

## 4. 阶段实施证据

### CLI-1（`0.1.0-alpha.60`，分支 `cli-development`）

- 删除旧 `src/Bundler.Cli` 原型（164 行 NSIS-only、`IsPackable=false`），按决策面重建。
- `src/Bundler.Cli`（`bundler` exe）：手写参数解析器（`--name value`/`--name=value`/旗标/拒未知项与重复项）；`validate`/`plan`/`bundle` 三命令；`--input-dir`/`--target`/`--formats`/`--product-name`/`--identifier`/`--package-version`/`--output-dir`/`--main-executable`/`--publisher`/`--description`/`--homepage`/`--copyright`/`--license-file`/`--icons` 参数面。
- 路径解析基准：`bundler.json` 内的相对路径按配置文件所在目录解析；命令行覆盖值（`--input-dir`/`--output-dir`/`--license-file`/`--icons`/`--<格式>.<旋钮>`）按当前工作目录解析——两套基准刻意不同（命令行直觉 vs 配置文件自洽），`--help` 有明示。
- 三级退出码：0 成功 / 1 后端或 IO 失败 / 2 用法或配置校验失败；`BundleValidationException` 归 2。
- `--json`：`validate`/`plan`/`bundle` stdout 只出 JSON（`valid`+`issues` / `items[]` / `artifacts[]`），日志走 stderr。
- 日志三档：默认 Information 起；`--quiet` 只留 Error；`--verbose` 加 Trace；`DOTNET_BUNDLER_VERBOSE` 环境变量同效。
- `--formats all` 按 target 展开矩阵支持集（linux-x86_64 → deb/rpm/appimage/zip/targz）；`tar.gz` 作为 `targz` 别名接受。
- `FormatDispatcher`：格式→门面映射，逐格式单格式配置分发（与 MSBuild 任务同一契约）。
- 单元测试：`Bundler.Tests` 新增 10 项（用法/退出码/json 形态/`all` 展开/真实 zip 产物/矩阵拒绝），合计 194/194 全绿。
- `tests/Cli.Integration/Verify.sh` 全绿：真实 publish 目录上五格式 bundle（deb/rpm/appimage/zip/targz 产物逐件断言 + sha256 侧车）、`--json` 形状断言、退出码分级（含后端失败=1 用 chmod 000 文件触发）、quiet/verbose 行为。
- 本机不产格式（nsis/msi/app/dmg/pkg）：CLI 面可达、经 validate/plan 覆盖；真实打包装上 Windows/macOS 宿主，登记 MT-01/02。

### CLI-2（`0.1.0-alpha.61`，分支 `cli-development`）

- `bundler.json` schema 固化：顶层字段与 `BundleConfiguration` 一一对应（`productName`/`identifier`/`version`/`publisher`/`description`/`homepage`/`copyright`/`licenseFile`/`outputDirectory`/`icons`/`resources`/`fileAssociations`/`urlProtocols`/`targets`），`targets[]` 项为 `target`/`inputDirectory`/`mainExecutable`/`signingFiles`/`formats`；格式段 `nsis|msi|app|dmg|pkg|deb|rpm|appimage|archive` 直接反序列化进各 `XxxBundleConfiguration`。
- 层叠：`--config` 载入文件 → CLI 共享参数覆盖顶层字段 → 目标参数（`--target`/`--input-dir`/`--main-executable`/`--formats`）写 `targets[0]` → `--<fmt>.<knob>=<v>` 并入格式段；值自动判型（true/false/整数/`[`/`{` 字面量/逗号列表/字符串）。
- 严格 schema：未知顶层键、`targets[]` 键、格式段旋钮一律拒绝并列出合法键名（退出码 2）；`--config` 缺失/非对象同 2。
- 相对路径按配置文件目录解析（`licenseFile`/`outputDirectory`/`icons`/`resources[].source`/`targets[].inputDirectory`/`signingFiles` + 格式段 `*File` 标量与 `files` 数组（各后端统一）/`app.frameworkDirectories` 的 `source`）。
- 单元测试：`Bundler.Tests` 新增 4 项（config 驱动 bundle、CLI 覆盖文件值、未知键拒绝、点号旋钮并入），合计 198/198 全绿。
- `tests/Cli.Integration/Verify.sh` 扩展全绿：config 驱动 zip+文件映射断言、`--formats` 覆盖、`--archive.archive-name` 点号覆盖、未知顶层键/未知点号旋钮/缺失文件三拒绝路径。
### CLI-3（`0.1.0-alpha.61`，分支 `cli-development`）

- `src/Bundler.Cli` 转为 `PackAsTool`：`PackageId=DotNet.Bundler.Cli`、`ToolCommandName=bundler`、`net10.0` any 布局、`Version=$(BundlerPackageVersion)`。
- `dotnet pack` 产出 `DotNet.Bundler.Cli.0.1.0-alpha.61.nupkg`；本机 `dotnet tool install --tool-path` 实装成功，`bundler --version`/`--help`/`bundle`（zip+deb 真实产出 + sha256 侧车）实测通过。
- 如实注记：本机 `dotnet` 位于 `~/.dotnet` 非默认搜索路径，shim 需 `DOTNET_ROOT` 指向安装根才能启动——属宿主布局事项而非 CLI 缺陷，`.NET` 常规安装位置不受影响。
- 冻结基线：CLI 契约（命令面/退出码/JSON 形状/`bundler.json` schema/格式旋钮面）冻结于 `0.1.0-alpha.62`；冻结测试向量 = `Bundler.Tests` 198/198 + `tests/Cli.Integration/Verify.sh` 全绿（含 config 层叠与拒绝路径）。
- 冻结后仅接受带回归测试的缺陷修复；Windows/macOS 宿主打包、CI 管道消费、AOT 分发评估保留 CLI-OI/MT 清单。

### CLI-AOT（`0.1.0-alpha.62`，分支 `cli-development`）——用户追加需求

- `src/Bundler.Cli` 增 `PublishAot`/`IsAotCompatible`/`InvariantGlobalization`；`dotnet publish -r linux-x86_64` 产出原生 ELF（~49.5MB，stripped，无 dotnet 运行时依赖）。
- AOT 化改造：新增 `BundlerJsonContext`（source-gen，camelCase）承载 bundler.json 全部载荷类型；`CliConfig` 反序列化与 `EnforceSchema` 合法键枚举改走 `JsonTypeInfo`（零反射）；`CliProgram` 输出 payload 由匿名类型改为 `JsonObject` DOM 构建。
- 顺带修复：`JsonNode.Parse` 补 `JsonDocumentOptions`（注释/尾逗号），与 Core `BundleConfigurationLoader` 口径一致。
- `Verify.sh` 新增 AOT 段：原生二进制 `--version`/bundle zip 实测断言（并证明无 `DOTNET_ROOT` 下运行）。
- 实测：`bundler bundle --config bundler.json`（zip+targz+deb+文件映射+点号旋钮）原生二进制全绿。

### 2026-10-08 逐格式独立失败语义（三轮测试待裁项落地）

- 多格式扇出改逐格式独立失败：单格式构建期异常（含宿主门禁 `PlatformNotSupportedException`）WARN+跳过、其余格式照常产出，末位 stderr 汇总 `<n> format(s) failed: <fmt> (<原因>)` 并以 rc=1 收场——此前任一格式失败中止整批，可产格式也被饿死（mac 宿主批次因门禁 `msi` 中止连带 zip 零产出的实证根因）。
- `BundleValidationException`/`CliUsageException` 仍向上穿透保 rc=2 用法层语义（format↔target 矩阵拒绝、未知格式、非法旋钮不降级）。
- `bundler.json` 更新清单只在有产物时 emit；MSBuild 任务同语义（`BundleDesktopApplication` 逐格式 try/catch → `LogWarning`+汇总 `LogError` 返 false，校验拒绝仍走任务级 catch）。
- 断言：`CliTests.BundlePerFormatIndependentFailure`（门禁格式 WARN+zip 照常产出+rc=1+stderr 具名失败格式）。

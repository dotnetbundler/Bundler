# CLI（`bundler` 命令行）

本文件是 CLI 的唯一文档：命令面、配置 schema、分发形态与边界。
已冻结（基线 `0.1.0-alpha.62`）；后端语义见各格式文档。

## 1. 命令面与输出契约

- 三命令：**`validate`**（校验配置与输入目录不产出）、**`plan`**（打印格式×target→产物路径计划不构建）、**`bundle`**（产出）。
- 手写最小参数解析器（零外部依赖立场，不引 CLI 框架）。
- **退出码三级**：`0` 成功、`1` 打包/后端执行失败、`2` 用法或配置校验失败——向后兼容承诺自此建立。
- **`--json`**：stdout 只放机器可读 JSON，人类日志走 stderr（CI 可直接消费产物清单）。
- 日志三档 `--quiet`/`--verbose`（走 `IBundleLogger` 适配）；`--help`/`--version`。
- 环境变量仅 `DOTNET_BUNDLER_VERBOSE`（CI 场景），其余一律显式参数。
- 格式选择优先级：`--formats` > `bundler.json` 的 `formats` > 目标 OS 缺省集。
- **输入契约：只吃已发布目录**（`--input-dir` 必填+`--target`）；`dotnet publish` 由用户或 MSBuild 路径负责——CLI 不做二次构建编排。

## 2. 配置 schema（`bundler.json` + 参数层叠）

参数覆盖文件值；schema 与 MSBuild `Bundler*` 属性一一对应；未知键/未知旋钮拒绝；相对路径按配置文件目录解析。

| MSBuild 属性/项 | `bundler.json` 字段 | CLI 参数 |
| --- | --- | --- |
| `BundlerFormats` | `formats: string[]` | `--formats` |
| `BundlerPackageName`/`BundlerVersion` | `packageName`/`version` | `--package-name`/`--package-version` |
| `BundlerTarget` | `target` | `--target` |
| `BundlerInputDir` | `inputDir` | `--input-dir` |
| `BundlerOutputDirectory` | `outputDir` | `--output-dir` |
| `Bundler<Format>*` | `<format>.*` 子对象 | `--<format>.<knob>=<value>` 通用透传 |
| `@(Bundler<Format>File)` | `<format>.files: [{source,destination}]` | 仅配置文件承载 |

原则：CLI 参数覆盖高频项；低频/结构化项仅经配置文件承载，避免参数面无限膨胀。

## 3. 分发形态与宿主边界

- **`dotnet tool`**：`DotNet.Bundler.Cli` nupkg（PackAsTool，`bundler` 命令），需 `DOTNET_ROOT` 指向 SDK 安装。
- **原生 AOT 二进制**：`dotnet publish -r <rid>` 免运行时 ELF/PE。
- Windows/macOS/Linux 全格式经六宿主 CI 矩阵验证（各宿主跑其可产格式）。
- centos7 等旧 glibc 宿主**明确不支持**：CLI 为 net10.0 托管工具且 AOT 走现代 glibc；产物 `.deb`/`.rpm` 装向 centos7 无格式障碍（载荷运行时兼容属载荷侧）。MSBuild 入口（netstandard2.0 程序集）理论上可被 .NET 6 SDK 加载，灰色组合未验证不承诺。

## 4. 明确拒绝

- `info`/`formats` 独立命令（`plan --json` 已覆盖）。
- hooks/脚本化扩展（干净宿主立场）；shell completions；遥测/联网（打包工具不联网）。
- 旧 `src/Bundler.Cli` NSIS-only 原型已删除，不承诺兼容。

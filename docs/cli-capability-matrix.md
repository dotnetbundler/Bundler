# CLI 能力矩阵

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 外部待验收项的明细在 `docs/cli-open-items.md`。

| 能力 | 状态 | 阶段 | 备注 |
| --- | --- | --- | --- |
| `bundler validate` 命令 | 已实现 | CLI-1 | 校验配置与 `--input-dir` 不产出 |
| `bundler plan` 命令 | 已实现 | CLI-1 | 打印格式×rid→产物路径计划，不构建 |
| `bundler bundle` 命令 | 已实现 | CLI-1 | 五 Linux 格式本机实测；win/mac 格式经 validate/plan 覆盖、宿主打包记 MT |
| 手写最小参数解析器 | 已实现 | CLI-1 | 零外部依赖立场 |
| 三级退出码（0/1/2） | 已实现 | CLI-1 | 成功/执行失败/用法或校验失败 |
| `--json` 机器可读输出 | 已实现 | CLI-1 | stdout=JSON、stderr=人类日志 |
| `--quiet`/`--verbose` 三档日志 | 已实现 | CLI-1 | 走 `IBundleLogger` 适配 |
| `--help`/`--version` | 已实现 | CLI-1 | — |
| `bundler.json` 配置 + 参数层叠 | 已实现 | CLI-2 | schema 与 MSBuild `Bundler*` 一一对应；未知键拒绝；相对路径按配置文件目录解析 |
| 全格式旋钮透传（`--<format>.<knob>=`） | 已实现 | CLI-2 | 高频项参数化，文件映射仅配置承载；未知旋钮拒绝 |
| `dotnet tool` 分发（`bundler`） | 已实现 | CLI-3 | `DotNet.Bundler.Cli` nupkg（PackAsTool），本机 `--tool-path` 实装后 `bundler` bundle deb/zip 实测通过；需 `DOTNET_ROOT` 指向 SDK 安装 |
| 原生 AOT 二进制分发 | 已实现 | CLI-AOT | `dotnet publish -r <rid>` 产出免运行时 ELF；linux-x64 本机实测 |
| 自包含/AOT 单二进制分发 | 外部待验收 | — | OI-01：体积与 RID 扇出成本需实测 |
| 旧 `src/Bundler.Cli` 原型 | 已删除 | CLI-1 | 164 行 NSIS-only，不承诺兼容 |
| `info`/`formats` 独立命令 | 明确拒绝 | — | `plan --json` 已覆盖 |
| hooks/脚本化扩展 | 明确拒绝 | — | 干净宿主立场 |
| shell completions | 明确拒绝 | — | 手写解析器下维护成本不值 |
| 遥测/联网 | 明确拒绝 | — | 打包工具不联网 |
| Windows/macOS 宿主跑 CLI 全格式 | 外部待验收 | CLI-3 | MT-01/02 |
| centos7 等旧 glibc 宿主跑 CLI | 明确拒绝 | — | CLI 为 net10.0 托管工具 + AOT 二进制走现代 glibc，centos7（glibc 2.17）两头均不可运行；产物 `.deb`/`.rpm` 装向 centos7 无格式障碍（载荷自身运行时兼容属载荷侧） |
| centos7 等旧 glibc 宿主跑 MSBuild 入口 | 不支持 | — | task 为 netstandard2.0 程序集，理论上可由 .NET 6 SDK（centos7 可装的最高 SDK，已 EOL）宿主加载——灰色组合未验证不承诺；.NET 8+ SDK 本身装不上 centos7 |

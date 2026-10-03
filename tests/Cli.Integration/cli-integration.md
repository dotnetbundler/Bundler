# CLI 集成测试（tests/Cli.Integration）

测试体已收编进 `tests/Bundler.IntegrationTests`（`CliIntegrationTests`，xUnit v3）；
`Verify.sh` 只是薄入口，按 `--filter-class` 转发到对应测试类。覆盖 CLI-C1（CLI-1/2/3 + AOT）的退出条件：对真实 `dotnet publish` 目录跑 `validate`/`plan`/`bundle` 三命令，断言退出码分级、`--json` 机器可读输出、日志档位与格式扇出。

## 运行

```bash
bash tests/Cli.Integration/Verify.sh
# 直接入口
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class CliIntegrationTests
```

`artifacts/cli-integration/` 存在陈旧产物时测试工作区自动挪旁名（`.stale-*`）自愈合，不拒绝重跑。

## 前置条件

- dotnet SDK（构建 CLI 与发布 fixture）；
- `python3`（`--json` 输出结构断言）；
- Linux 宿主追加 `appimage` 格式腿（非 Linux 宿主自动只跑 `deb`/`rpm`/`zip`/`targz`）；
- AOT 段在本机实发 `dotnet publish -r <宿主 Linux RID>`（musl 上为 `linux-musl-x64`，需 Linux 宿主与原生工具链）。

## 断言面

- 构建：Release 构建 CLI、`Fixture` 自包含 publish 产出可执行文件。
- 命令面：`--version`/`--help`、无参/未知命令/未知格式/未知选项/缺选项值均退出码 `2`。
- `validate`：有效配置退出 `0` 且 `--json` 输出 `valid=true`/`issues=[]`；无效输入（不存在目录等）退出非零且 issues 非空。
- `plan`：计划 JSON 逐格式列出产物路径与摘要。
- `bundle`：真实产出各格式产物并落 `out/<rid>/<format>/`，`.sha256` 侧车核对。
- `bundler.json`：`--config` 配置文件层叠、点号旋钮覆盖、未知键/缺失文件拒绝断言。
- AOT：原生二进制免运行时 `bundle` 冒烟 + 宿主裁剪后端断言。

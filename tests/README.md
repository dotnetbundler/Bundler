# Bundler 测试入口

分层判据、覆盖清单与 CI 拓扑见 `docs/design/testing.md`；本文件是给跑测试的人的用法说明。

## 结构

| 目录 | 工程 | 层职责 |
| --- | --- | --- |
| `Unit/` | `Bundler.UnitTests` | 单元层：纯进程内、零真实 spawn（机器可核查：`Process.Start` 与 `ToolRunner` 三 spawn 入口 `RunAsync`/`TryRunAsync`/`RunSync` 实调用零命中——`ToolRunnerTests` 自身经桩调用与 CliTests/MacAppTests 收割注释提及除外，spawn 一律经 `ToolRunner.Handler` 桩缝） |
| `Api/` | `Bundler.ApiTests` | 公共 API functional 层（无 IVT——编译期强制只碰公开面，真跑内嵌工具产真物） |
| `Integration/` | `Bundler.IntegrationTests` | 真环境层：fixture 发布链、真装真卸、docker 矩阵、更新链、外部工具 |
| `Package/` | `Bundler.PackageTests` | 本地 nupkg 消费契约层（不经项目引用） |
| `Shared/` | — | 四工程链接共享的基建源码（进程驱动/工作区/docker/UIA/临时目录） |
| `Fixtures/` | — | 被测 fixture 工程与载荷（各测试工程共用，见下方地图） |
| `Manual/` | — | 特殊验收脚本（CI 装不下的验收项；登记见 `docs/project/special-acceptance.md`） |

## 入口

```powershell
dotnet build Bundler.slnx -c Release   # 前置：测试 dll 须先构建
dotnet test tests/Unit/Bundler.UnitTests.csproj -c Release
dotnet test tests/Api/Bundler.ApiTests.csproj -c Release
dotnet test tests/Integration/Bundler.IntegrationTests.csproj -c Release
dotnet test tests/Package/Bundler.PackageTests.csproj -c Release
```

CI 与成批跑法唯一驱动是 `scripts/ci/run-tests.{sh,ps1}`（suite=unit|api|integration|package|all）：
测试程序集自宿主（MTP+xUnit3）——集成腿逐类直跑 dll 并落 `artifacts/ci/test-progress.log` 指认在途类，`-longRunning 300` 打印挂起用例名。

### 筛选

`dotnet test` 经 MTP 集成透传 `--` 后参数；直跑 dll 则参数直接给程序集：

```powershell
dotnet test tests/Integration/Bundler.IntegrationTests.csproj -c Release -- --filter-class "*NsisIntegrationTests*"
dotnet tests/Integration/bin/Release/net10.0/Bundler.IntegrationTests.dll -class NsisIntegrationTests
dotnet tests/Integration/bin/Release/net10.0/Bundler.IntegrationTests.dll -list classes
```

### Package 前置

`Package` 层消费本地 nupkg：跑前必须先打仓根包。
`run-tests.*` 与测试自身 `RepositoryPackages` 共享 `artifacts/packages`：同一进程内只打一次，
跨进程经 `.pack.lock` 串行共用该目录（每次运行都会重打，由 MSBuild 增量兜底）；
手动则逐 src 工程 pack：`dotnet pack src/<Name>/<Name>.csproj -c Release -o artifacts/packages`。

## 门禁

跳过一律经 `Assert.SkipWhen/SkipUnless` 报原因；每层门禁语义：

| 门禁 | 机制 | 含义 |
| --- | --- | --- |
| 宿主 | `TestPlatform.IsWindows/IsLinux/IsMacOS/IsX64`、`OperatingSystem.IsX`、`Architecture.*`、`DockerRunner.CanRunArm64Containers` | 格式后端只在对应宿主可产/可装；错宿主跳过 |
| 同意闸 | `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` | NSIS/MSI/包消费真装腿写真实用户配置与注册表；CI runner 可丢弃默认置 1，本机须显式同意 |
| 外部工具 | `ExternalTools.Require/Has` | `rpm`/`rpm2cpio`/`cpio`、`desktop-file-validate`、`unsquashfs`、`gpg`、`lintian`/`rpmlint`、`docker`、`clang` 等缺席即跳（NSIS/WiX 工具链随包分发，不经此门禁） |
| docker | `DockerRunner.IsAvailable` | 发行版矩阵腿需 docker daemon；镜像拉取失败按映射回落备用 registry |
| fixture | fixture 工程文件存在性 | 载荷缺失即跳（不静默产出假断言） |

交互式 NSIS 向导腿额外要求交互桌面会话（`WindowsDesktop` UIA/Win32 驱动），无桌面即跳。

## Fixture 地图

| `tests/Fixtures/` | 载荷 | 喂给 |
| --- | --- | --- |
| `Nsis/Fixture` | Windows NSIS 安装器（英/简中双语言、自定义 Shortcut 页） | Integration NSIS 腿、produce.ps1 |
| `Nsis/LegacyMsi*` | 旧 MSI 迁移 fixture | Integration 迁移腿 |
| `Deb`/`Rpm`/`AlpineApk`/`AppImage` | Linux 各格式 fixture（desktop/metainfo/systemd/config 变体经 MSBuild 属性） | Integration + produce/consume 联合交换 |
| `MacApp`/`MacDmg`/`MacPkg` | macOS 三格式 fixture（含 universal 产件入口） | Integration + verify universal 腿 |
| `Archive` | zip/tar.gz 归档 fixture | Integration + 各宿主 consume 腿 |
| `Cli` | CLI 端到端 fixture | Integration CLI 腿 |
| `MsiPackage/{Fixture,Standalone}` | nupkg 消费 fixture（`Bundler.LocalPackages.props` 钉本地源） | Package MSI 契约腿、produce.ps1 MSI 产件 |
| `UpdateCompat/` | v1 冻件更新兼容载荷（字节固定，`-text` 不经 eol 改写） | Integration 更新兼容腿 |

## Manual 层

`tests/Manual/{win,mac,linux}/` 是脚本化特殊验收：凭证/真机/VM/公网/破坏注入/真人交互类。
每条验收登记运行条件于 `docs/project/special-acceptance.md`；证据契约见 `tests/Manual/shared/evidence-format.md`，
落 `tests/Manual/evidence/`（gitignore，不回库）。CI 可跑的子集由 `verify` workflow 的 `manual-auto` 腿执行。

# 测试指南

本目录只放自动化测试工程、共享测试件与集成 fixture。
`Special/` 存放不能收编成 dotnet test 的特殊验收脚本（真机/VM/凭证/交互/公网）；
只能人工/外部条件完成的验收项统一登记在 `docs/project/special-acceptance.md`。

## 工程

| 工程 | 内容 | 运行 |
| --- | --- | --- |
| `Bundler.Tests` | 纯托管单元/契约测试（xUnit v3 + Microsoft.Testing.Platform） | `dotnet test tests/Bundler.Tests/Bundler.Tests.csproj -c Release` |
| `Bundler.ApiTests` | 直接调后端公共 API 产包并断言内容（宿主无关） | `dotnet test tests/Bundler.ApiTests/Bundler.ApiTests.csproj -c Release` |
| `Bundler.IntegrationTests` | 端到端集成：发布 fixture、产包、真装真卸、docker 矩阵 | `dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release` |
| `Bundler.LocalPackagesTests` | 本地包消费契约：fixture 经 `Bundler.LocalPackages.props` 消费仓根本地 nupkg，验证发货包在真实消费方构建中可用（当前为 MSI 腿，仅 Windows） | `dotnet test tests/Bundler.LocalPackagesTests/Bundler.LocalPackagesTests.csproj -c Release` |

本机定位：未做特殊说明时 Windows 宿主**只跑基础面**（Release build + `Bundler.Tests`/`ApiTests` + 宿主可达的集成腿），**完整多宿主矩阵在 GitHub CI 跑**——`test-legs.yml` `workflow_dispatch`（suite×runners），真装/交互/提权腿在 CI 一次性环境由 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 自动启闸，本机默认不启。
dispatch/盯 run 需要 GitHub PAT（期望位置 `secret:personal:Github_Linlccc_PAT`，走 `curl -H "Authorization: token $Github_Linlccc_PAT"`——`gh` wrapper 会忽略 `GH_TOKEN`）；本机找不到 PAT 时提醒用户提供，不要跳过 CI 腿冒充已验。

先 `dotnet build Bundler.slnx -c Release` 再跑测试；集成测试的打包接线经 `Bundler.ProjectReference.targets` 走项目引用，不依赖已发布的 nupkg。
`Bundler.LocalPackagesTests` 例外：它有意经 `Bundler.LocalPackages.props` 消费本地构建的 nupkg，运行前需要 `dotnet pack Bundler.slnx -c Release -o artifacts/packages`。

## 按格式/类过滤

测试类即格式：`NsisIntegrationTests`/`MacAppIntegrationTests`/`MacDmgIntegrationTests`/`MacPkgIntegrationTests`/`DebIntegrationTests`/`RpmIntegrationTests`/`AppImageIntegrationTests`/`ArchiveIntegrationTests`/`AlpineApkIntegrationTests`/`CliIntegrationTests`；本地包消费腿为 `MsiLocalPackagesTests`（在 `Bundler.LocalPackagesTests` 工程）。

```powershell
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class "*DebIntegrationTests*"
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-method "*Smoke*"
```

`--` 之后为传给测试宿主（Microsoft Testing Platform）的过滤选项，不是 `dotnet test` 自身参数；每次只接受一个名字，`--filter-class`/`--filter-method` 不支持 `A|B` 或逻辑写法。
直接运行测试程序集（如 CI 脚本逐类驱动）时用 xUnit v3 原生单横线选项 `-class`/`-method`/`-trait`，参考 `scripts/ci/verify-*.sh|ps1`。

## 资源门禁（trait 与环境变量）

`[Trait("Requires", ...)]` 标记宿主级资源要求（标在方法级；仅当全类都需同意时升类级），可按 `--filter-trait`/`--filter-not-trait` 精确选排：

| trait | 含义 | 另行同意条件 |
| --- | --- | --- |
| `docker` | 需要可用 docker 守护（deb/rpm/apk/AppImage 容器矩阵） | 守护缺席即 SKIP |
| `elevation` | 需要提权（POSIX `sudo -n` / Windows 管理员）写系统目录、装包 | 无提权即 SKIP |
| `localinstall` | 真装真卸会写真实用户配置/注册表/系统服务 | 另须 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1`，否则 SKIP |
| `interactive` | GUI 级验收：真实点击驱动安装向导/系统对话框 | Windows 需交互式桌面会话（UIA 可达）；macOS 需 TCC 辅助功能授权；缺席即 SKIP |

```powershell
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-trait "Requires=localinstall"
```

`localinstall` 双重门禁：trait 用于选择，`BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 是同意闸（对应原脚本 `-ConfirmLocalInstall`）。

## 宿主条件速查

- Windows 真跑：`NsisIntegrationTests`（NSIS 工具链内嵌，任意宿主可产包，真装仅 Windows）、`MsiLocalPackagesTests`（`tests/Bundler.LocalPackagesTests`，仅 Windows + `localinstall`）。
- Windows GUI 腿：`NsisIntegrationTests.InteractiveWizardInstallsAndUninstalls`（语言页→License→nsDialogs 快捷方式页→安装→卸载全链真实点击）、`MsiLocalPackagesTests.InteractiveWizardInstallsAndRemoves`（Welcome→License→VerifyReady→Exit + 维护流 Remove）；两腿经 `Shared/Tooling/WindowsDesktop.cs` 裸 UIA3 驱动，nsDialogs 自绘页免疫 `Invoke` 时自动升级物理鼠标注入。
- Linux 真跑：`Deb`/`Rpm`/`AppImage`/`Archive` 全部宿主腿 + docker 矩阵 + FUSE 挂载腿。
- macOS 真跑：`MacApp`/`MacDmg`/`MacPkg` 全部腿（per-user 安装、relocate、LSDB 断言）；GUI 腿 `MacDmgIntegrationTests.FinderSlaAcceptanceMountsVolume` 经 osascript 点 Agree 挂载带 SLA 卷。
- `AlpineApk`：x86_64 `alpine` docker 直跑；arm64 宿主走 qemu binfmt；musl 宿主原生。
- `Cli`：CLI 产物的端到端命令面（宿主 AOT 产物）。

## 目录

- `Bundler.IntegrationTests/Fixtures/<格式>/`：各格式被发布成包的 fixture 工程与载荷 `Assets/`；`Nsis/` 下另有 `LegacyMsiFixture*/`（旧 MSI 升级场景）。
- `Bundler.LocalPackagesTests/Fixtures/Msi/`：本地包消费契约腿的 `Fixture`/`Standalone` 工程。
- `Shared/TestPlatform.cs`：四工程链接共享的宿主探测门面（OS/架构/musl/root/docker）；`Shared/Tooling/`：链接共享的集成基建（进程/提权/工作区/包定位/`MsiSupport`/`WindowsDesktop` UIA3 向导驱动）。
- "build→pack"链在 build 前一律关闭构建服务器：测试腿走 `Dotnet.ShutdownBuildServers()`（`Dotnet.BuildAndPack`/`MsiSupport`/`RepositoryPackages` 已统一收口），脚本腿用 `dotnet build-server shutdown`——MSB3027 是 Windows 文件锁语义（常驻 MSBuild 节点持任务程序集锁），POSIX 腿不需要。

## 断言与集成组织

- 断言用惯用形式：异常断言用 `Assert.ThrowsAny*`/`ThrowsAnyAsync*`（catch 匹配子类，语义与 `Assert.Throws*` 的精确类型要求不同），接返回值断言字段或消息；相等断言用 `Assert.Equal` 而非 `Assert.True(a==b)`；不用 try/catch 再断言。
  需要核对异常消息内容时可用带消息核对的断言包装（如 `ExpectAsync`，核对类型+消息子串）；仅断言类型的包装（如 `AssertThrows`，其 message 参数只是失败标签）亦可用——包装内部可 try/catch 或 `Record.Exception*` 捕获后断言，不得宽松成全类型通吃。
- 单格式 API fixture 收编为 `Bundler.ApiTests` 的 `<Format>ApiTests` 测试类，系统集成测试体收编为 `Bundler.IntegrationTests` 的 `<Format>IntegrationTests` 测试类，宿主门控经 `Assert.Skip`；
  新后端参考现有目录、命名、fixture、包源、测试入口、断言、日志和清理——统一测试风格和组织，不统一各格式用例清单；有实际格式原因可采用专用方式并在格式测试说明写明，不要模拟另一格式不存在的行为。
- API fixture 以项目引用直引对应后端项目；`Bundler.Tests` 同样使用项目引用；MSBuild fixture 经 `Bundler.ProjectReference.targets` 接入 `src/Bundler.MSBuild`；
  仓外独立包消费 fixture（`Bundler.LocalPackagesTests/Fixtures/Msi/`）保留 `PackageReference`+`Bundler.LocalPackages.props` 垫片，测试代码传本轮 `BundlerPackageSource`/`BundlerPackageVersion` 与独立缓存并用 `MsiSupport.AssertLocalBundlerRestore` 核验实际还原的包 ID、版本、源与缓存——复制项目连同 props 复制，项目自身声明必要 SDK 属性，不靠根 props、旧 `obj` 或隐藏 `--source` 才工作。
- 集成测试按 Pack、隔离还原、生成产物、执行系统行为、断言最终状态组织；每轮使用独立输出和产品身份，预检无碰撞；NuGet 可列出 SDK/VS fallback 文件夹，不能要求 `packageFolders` 只有缓存一项。
  记录命令、宿主/架构、包与应用版本、哈希、退出码、日志、清理及未验证范围；集成失败即使复跑通过也记录首次失败、调查和复跑，不能仅放宽断言。

## 人工/外部验证

- `tests/Special/`：不能进 dotnet test 的特殊验收脚本——真机/虚拟机/凭证/交互/公网类，按宿主分目录，索引见其 README（用法与各脚本内文文档）。
- `tests/Special/win/nsis-reboot/Verify.ps1`：NSIS 真实重启链路的两阶段人工验证，只在可抛弃 Windows VM 运行（用法见其 `nsis-reboot.md`）。

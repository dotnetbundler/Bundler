# 测试指南

本目录只放自动化测试工程、共享测试件与集成 fixture。
人工验证步骤见各格式 `docs/<format>-manual-testing.md`，外部待验收项见 `docs/<format>-open-items.md`。

## 工程

| 工程 | 内容 | 运行 |
| --- | --- | --- |
| `Bundler.Tests` | 纯托管单元/契约测试（xUnit v3 + Microsoft.Testing.Platform） | `dotnet test tests/Bundler.Tests/Bundler.Tests.csproj -c Release` |
| `Bundler.ApiTests` | 直接调后端公共 API 产包并断言内容（宿主无关） | `dotnet test tests/Bundler.ApiTests/Bundler.ApiTests.csproj -c Release` |
| `Bundler.IntegrationTests` | 端到端集成：发布 fixture、产包、真装真卸、docker 矩阵 | `dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release` |
| `Bundler.LocalPackagesTests` | 本地包消费契约：fixture 经 `Bundler.LocalPackages.props` 消费仓根本地 nupkg，验证发货包在真实消费方构建中可用（当前为 MSI 腿，仅 Windows） | `dotnet test tests/Bundler.LocalPackagesTests/Bundler.LocalPackagesTests.csproj -c Release` |

先 `dotnet build Bundler.slnx -c Release` 再跑测试；集成测试的打包接线经 `Bundler.ProjectReference.targets` 走项目引用，不依赖已发布的 nupkg。
`Bundler.LocalPackagesTests` 例外：它有意经 `Bundler.LocalPackages.props` 消费本地构建的 nupkg，运行前需要 `dotnet pack Bundler.slnx -c Release -o artifacts/packages`。

## 按格式/类过滤

测试类即格式：`NsisIntegrationTests`/`MacAppIntegrationTests`/`MacDmgIntegrationTests`/`MacPkgIntegrationTests`/`DebIntegrationTests`/`RpmIntegrationTests`/`AppImageIntegrationTests`/`ArchiveIntegrationTests`/`AlpineApkIntegrationTests`/`CliIntegrationTests`；本地包消费腿为 `MsiLocalPackagesTests`（在 `Bundler.LocalPackagesTests` 工程）。

```powershell
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release --filter-class "*DebIntegrationTests*"
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release --filter-method "*Smoke*"
```

xUnit v3/MTP 过滤器每次只接受一个名字，`--filter-class`/`--filter-method` 不支持 `A|B` 或逻辑写法。

## 资源门禁（trait 与环境变量）

`[Trait("Requires", ...)]` 标记宿主级资源要求，可按 `--filter-trait` 精确选排：

| trait | 含义 | 另行同意条件 |
| --- | --- | --- |
| `docker` | 需要可用 docker 守护（deb/rpm/apk/AppImage 容器矩阵） | 守护缺席即 SKIP |
| `elevation` | 需要提权（POSIX `sudo -n` / Windows 管理员）写系统目录、装包 | 无提权即 SKIP |
| `localinstall` | 真装真卸会写真实用户配置/注册表/系统服务 | 另须 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1`，否则 SKIP |

```powershell
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release --filter-trait "Requires=localinstall"
```

`localinstall` 双重门禁：trait 用于选择，`BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 是同意闸（对应原脚本 `-ConfirmLocalInstall`）。

## 宿主条件速查

- Windows 真跑：`NsisIntegrationTests`（NSIS 工具链内嵌，任意宿主可产包，真装仅 Windows）、`MsiLocalPackagesTests`（`tests/Bundler.LocalPackagesTests`，仅 Windows + `localinstall`）。
- Linux 真跑：`Deb`/`Rpm`/`AppImage`/`Archive` 全部宿主腿 + docker 矩阵 + FUSE 挂载腿。
- macOS 真跑：`MacApp`/`MacDmg`/`MacPkg` 全部腿（per-user 安装、relocate、LSDB 断言）。
- `AlpineApk`：x86_64 `alpine` docker 直跑；arm64 宿主走 qemu binfmt；musl 宿主原生。
- `Cli`：CLI 产物的端到端命令面（宿主 AOT 产物）。

## 目录

- `Bundler.IntegrationTests/Fixtures/<格式>/`：各格式被发布成包的 fixture 工程与载荷 `Assets/`；`Nsis/` 下另有 `LegacyMsiFixture*/`（旧 MSI 升级场景）。
- `Bundler.LocalPackagesTests/Fixtures/Msi/`：本地包消费契约腿的 `Fixture`/`Standalone` 工程。
- `Shared/TestPlatform.cs`：四工程链接共享的宿主探测门面（OS/架构/musl/root/docker）；`Shared/Tooling/`：链接共享的集成基建（进程/提权/工作区/包定位/`MsiSupport`）。

## 人工/外部验证

- `tools/Windows.Nsis.Reboot/Verify.ps1`：NSIS 真实重启链路的两阶段人工验证，只在可抛弃 Windows VM 运行（用法见其 `nsis-reboot.md`）。

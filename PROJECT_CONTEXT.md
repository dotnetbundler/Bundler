# DotNet.Bundler 项目上下文

> 最后更新：2026-10-13
> 当前分支：`main`
> 当前包版本：`0.1.0-alpha.102`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全部 11 个格式、CLI 与 UPDATE 均已冻结，无进行中的格式阶段；目标模型已定型——`TargetOperatingSystem{Windows,MacOS,Linux}` + `LibC` 旁维度 + `{os}[-{libc}]-{arch}` canonical 词（别称仅输入归一，产物名用各生态词），契约与全产物名目录见 `docs/targets.md`**
> 实施证据以 git 提交与 PR 记录为准，各格式 `docs/formats/` 单文档只留设计决策、语义契约与能力状态
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 规则在 `docs/development-rules.md`；产品顺序在 `docs/roadmap.md`；各格式细节在 `docs/formats/` 下对应文档。
> 本文内容须与 Git、代码和测试一致；不一致时以后三者为准并先修正本文。

## 1. 项目结构（现况）

| 项目 | 角色 | 目标框架 |
| --- | --- | --- |
| `src/Bundler.Abstractions` | 跨包稳定契约 | `netstandard2.0` |
| `src/Bundler.Core` | 格式无关的校验、规划、编排、工作目录生命周期、内容寻址工具缓存 | `netstandard2.0;net10.0` |
| `src/Bundler.Nsis` | Windows + NSIS 后端（内嵌 NsisToolset 3.12-r1 多宿主工具与 win-x86 Native AOT 插件） | `netstandard2.0` |
| `src/Bundler.Wix` | Windows + WiX 3.14.1/MSI 后端（内嵌固定 WiX 工具子集） | `netstandard2.0` |
| `src/Bundler.Signing.Windows` | Windows Authenticode 签名 API（NSIS/MSI 共用） | `netstandard2.0` |
| `src/Bundler.MacApp` | macOS `.app` 后端（骨架/Info.plist/`.icns`/Contents 载荷映射） | `netstandard2.0` |
| `src/Bundler.MacDmg` | macOS `.dmg` 后端（`hdiutil` 全链、拖放卷、`Ulmo`/`Udzo`/`Udbz`） | `netstandard2.0` |
| `src/Bundler.MacPkg` | macOS `.pkg` 后端（`pkgbuild`/`productbuild` 组件与分发包、签名/公证/专家脚本） | `netstandard2.0` |
| `src/Bundler.Deb` | Debian `.deb` 后端（纯托管 ar/tar/gzip 写入器，无原生工具依赖，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.Rpm` | RPM `.rpm` 后端（纯托管 lead/header/cpio/gzip 写入器，无原生工具依赖，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.AppImage` | Linux `.AppImage` 后端（内嵌 appimagetool+type2 runtime，仅 Linux 宿主构建） | `netstandard2.0` |
| `src/Bundler.Archive` | `.zip`/`.tar.gz` 归档后端（纯托管写入器，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.AlpineApk` | Alpine `.apk` 后端（纯托管三段 gzip 写入器，任意构建宿主；可选 RSA 签名经 BouncyCastle） | `netstandard2.0` |
| `src/Bundler.Update` | UPDATE 打包侧：清单发射器 + ECDSA P-256 `.sig`/`.blockmap` 旁车生成 | `netstandard2.0` |
| `src/Bundler.Updater` | UPDATE 应用内库：`UpdateClient` 查/下/验/换四动词 + block-map 差分（`BUNDLER_UPDATER_LINK` 单源双栖 Core 协议层） | `net10.0;netstandard2.0` |
| `src/Bundler.Updater.Bootstrap` | UPDATE 引导件：Native AOT per-target `bundler-updater` + `tools/posix/bundler-updater.sh` 降级件（经 Bundler.Core 内嵌资源供应） | `net10.0` |
| `src/Bundler.MSBuild` | MSBuild Task 适配层（`buildTransitive` 导入） | `netstandard2.0` |
| `src/Bundler.Cli` | CLI 适配层（dotnet tool nupkg + `PublishAot` 原生二进制双分发） | `net10.0` |
| `src/Bundler.Package` | 便利元包 `DotNet.Bundler`（聚合后端与 MSBuild 支持） | `netstandard2.0` |
| `tests/Bundler.Tests` | 唯一快速测试入口（xUnit v3，`dotnet test`；用例全注册、宿主门控经 `Assert.Skip` 跳过，以每次实跑输出为准） | `net10.0` |
| `tests/Bundler.ApiTests` | API 消费测试入口（xUnit v3，`dotnet test`；十格式 `<Format>ApiTests` 测试类直调后端 API 产真实产物再断言，宿主门控经 `Assert.Skip`，集成脚本经 `-- --filter-class` + `*_API_FIXTURE_OUTPUT` 环境变量复用） | `net10.0` |
| `tests/Bundler.IntegrationTests` | 系统集成测试体（xUnit v3，`dotnet test -- --filter-class/--filter-method`；每格式一个 `<Format>IntegrationTests` 测试类，全部脚本测试点逐腿收编；共享基建在 `tests/Shared/Tooling/`；Windows 真装腿用 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 门禁） | `net10.0` |
| `tests/Bundler.LocalPackagesTests/` | 本地包消费契约工程：`MsiLocalPackagesTests` + `Fixtures/Msi/`（经 `Bundler.LocalPackages.props` 消费本地 nupkg 的 `Fixture`+`Standalone`，仅 Windows 真跑） | `net10.0` |
| `tests/Bundler.IntegrationTests/Fixtures/` | 集成 fixture 集中地：各格式被发布成包的 fixture 工程与载荷 `Assets/`（`Nsis/` 含 `LegacyMsiFixture*/`） | `net10.0` |
| `tests/Shared/` | 四测试工程链接共享：`TestPlatform.cs` 宿主探测门面 + `Tooling/` 集成基建（`ProcessRunner`/`DockerRunner`/`ElevatedRunner`/`IntegrationWorkspace`/`MsiSupport`/`WindowsDesktop` UIA3 向导驱动/`ShellLink` 等） | `net10.0` |
| `tests/README.md` | 测试入口、trait 门禁与 fixture 地图 | — |
| `tests/Special/` | 特殊验收脚本矩阵（进不了 dotnet test 的验收项按 win/mac/linux 分宿主脚本 + 证据契约 `shared/evidence-format.md`，产出 `evidence/` 已 gitignore） | sh/ps1 |
| `tests/Special/win/nsis-reboot` | 可抛弃 VM 重启验证入口（`Verify.ps1` 两阶段：锁定文件卸载 `3010` → 重启 → pending rename/目录/注册表/journal 清理断言） | PowerShell |
| `samples/HelloBundlerApp` | 全后端统一公开示例（应用版本 `1.0.0`，主工程公共旋钮 + `formats/*.props` 各后端专属旋钮，100% 旋钮覆盖） | `net10.0` |
| `tools/Bundler.Nsis.Plugin` | NSIS 原生插件源码（有意在 slnx 之外，重建需 .NET 10 + Windows 原生链） | `net10.0` |
| `third_party/` | 第三方归档、许可证、逐文件 SHA-256 与 provenance 文档 | — |

根 `Directory.Build.props` 的默认 `TargetFramework` 为 `net10.0`；
MSBuild 任务链保留 `netstandard2.0`（双宿主加载契约，见 `docs/development-rules.md` §3）。
src/ 各工程共享元数据统一在 `src/Directory.Build.props`（链式导入根 props），Cli/Core 按项目名条件豁免；
外部依赖版本集中在 `Directory.Packages.props`（中央包管理），`PackageReference` 不带 `Version=`，自仓 `DotNet.Bundler*` 包引用用 `VersionOverride="$(BundlerPackageVersion)"`。

## 2. 各格式当前状态

| 格式 | 冻结基线 | 文档 |
| --- | --- | --- |
| NSIS | `0.1.0-alpha.31`（alpha.32/33 journal 加固） | `docs/formats/win-nsis.md` |
| MSI | `0.1.0-alpha.43` | `docs/formats/win-msi.md` |
| `.app` | `0.1.0-alpha.45` | `docs/formats/mac-app.md` |
| `.dmg` | `0.1.0-alpha.45` | `docs/formats/mac-dmg.md` |
| `.pkg` | `0.1.0-alpha.47` | `docs/formats/mac-pkg.md` |
| `.deb` | `0.1.0-alpha.51` | `docs/formats/linux-deb.md` |
| `.rpm` | `0.1.0-alpha.55` | `docs/formats/linux-rpm.md` |
| `.AppImage` | `0.1.0-alpha.58` | `docs/formats/linux-appimage.md` |
| `.zip`/`.tar.gz` | `0.1.0-alpha.59` | `docs/formats/archive.md` |
| CLI | `0.1.0-alpha.62` | `docs/cli.md` |
| Alpine `.apk` | `0.1.0-alpha.63` | `docs/formats/linux-alpine-apk.md` |
| UPDATE | `0.1.0-alpha.74` | `docs/update.md` |

签名能力（SIGN 已收官）：rpm/AppImage 可选 OpenPGP/GPG、apk 可选 RSA、NSIS/MSI 托管 Authenticode、app/dmg codesign、pkg productsign——总览见 `docs/signing.md`。
各格式契约与能力状态详见对应文档；外部事项统一登记在 `docs/special-acceptance.md`。

## 3. 最近验证

当前基线：`main@e0b6848`（`0.1.0-alpha.102`）——CI 测试腿接线、测试矩阵收口与文档一格式一件/目录化重构已随 PR #71–#75 合入。
最近验证：本机 Windows x64 `build` 0 警告、`Bundler.Tests` 全量 491 测 0 失败；release-verify @ `a970992` 7/9 腿绿、`macos-26-intel` 撞 `hdiutil` 瞬抖、`windows-11-arm` 未落终态（以运行记录为准），本文件不逐条留档。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- 各格式外部事项等待对应环境输入，统一登记在 `docs/special-acceptance.md`。
- WiX v3 维护风险已裁决封存：v3 维持现状不再复议（2026-10-05，见 `docs/formats/win-msi.md` §1 与 `third_party/wix/msi-wix-provenance.md`），非未决项。
- 仓外独立包消费 fixture（`tests/Bundler.LocalPackagesTests/Fixtures/Msi/` 的 `Fixture` 与 `Standalone/`）在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源（PR #41）；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。

## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，功能面无进行中阶段。
剩余工作：

- 外部待验收项（统一登记在 `docs/special-acceptance.md`，见 §5）；
- 发布链资产实际推送（NuGet/Release 为另一步）；
- 零星已登记增强按 `docs/roadmap.md` §5.2 候选标准排队。

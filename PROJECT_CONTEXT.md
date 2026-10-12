# DotNet.Bundler 项目上下文

> 最后更新：2026-10-12
> 当前分支：`main`（最新已实测基线见 §3 最新一轮）
> 当前包版本：`0.1.0-alpha.102`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全部 11 个格式、CLI 与 UPDATE 均已冻结，无进行中的格式阶段；基础目标模型重构已完成并入 `main`（alpha.90–93）——`TargetOperatingSystem{Windows,MacOS,Linux}` + `LibC` 旁维度 + `CpuArchitecture` 族序重排 + `BundlerTarget` `{os}[-{libc}]-{arch}` canonical GNU 词（别称仅输入归一，产物文件名层用各生态词），契约与全产物名目录见 `docs/target-matrix.md`**
> 各格式冻结基线：NSIS `alpha.31`（后续 alpha.32/33 journal 加固）；MSI `alpha.43`；`.app`/`.dmg` `alpha.45`；`.pkg` `alpha.47`；`.deb` `alpha.51`；`.rpm` `alpha.55`；`.AppImage` `alpha.58`；`.zip`/`.tar.gz` `alpha.59`；CLI `alpha.62`；`.apk` `alpha.63`；UPDATE `alpha.74`
> 签名能力（SIGN 已收官）：rpm/AppImage 可选 OpenPGP/GPG 签名、apk 可选 RSA 签名、NSIS/MSI 托管 Authenticode、app/dmg codesign、pkg productsign——逐格式证据见各 `<format>-roadmap.md` 与 `docs/signing-roadmap.md`
> 实施证据以 git 提交与 PR 记录为准，各格式 `<format>-roadmap.md` 只留设计决策与语义契约
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 规则在 `docs/development-rules.md`；产品顺序在 `docs/roadmap.md`；各格式细节在各 `docs/<format>-*.md`。
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
| `tests/Bundler.ApiTests` | API 消费测试入口（xUnit v3，`dotnet test`；十格式 `<Format>ApiTests` 测试类直调后端 API 产真实产物再断言，宿主门控经 `Assert.Skip`，集成脚本经 `--filter-class` + `*_API_FIXTURE_OUTPUT` 环境变量复用） | `net10.0` |
| `tests/Bundler.IntegrationTests` | 系统集成测试体（xUnit v3，`dotnet test --filter-class/-method`；每格式一个 `<Format>IntegrationTests` 测试类，全部脚本测试点逐腿收编；共享基建在 `tests/Shared/Tooling/`；Windows 真装腿用 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 门禁） | `net10.0` |
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
MSBuild 任务链（`Bundler.MSBuild` 及其加载的 Abstractions/Core/Nsis/Wix/Signing.Windows）保留 `netstandard2.0`，因为任务程序集须同时被 .NET Framework `MSBuild.exe` 和 `dotnet msbuild` 双宿主加载（见 `docs/development-rules.md` 第 3 节）。
src/ 各工程共享元数据（`Authors`/`PackageVersion`/`PackageReadmeFile`/`netstandard2.0` TFM/README 与 THIRD-PARTY-NOTICES 打包项）统一在 `src/Directory.Build.props`（链式导入根 props），Cli/Core 按项目名条件豁免；
外部依赖版本集中在 `Directory.Packages.props`（中央包管理），`PackageReference` 不带 `Version=`，自仓 `DotNet.Bundler*` 包引用用 `VersionOverride="$(BundlerPackageVersion)"`。

## 2. 各格式当前状态

### Windows + NSIS（已冻结，`71a5c90`，基线 `0.1.0-alpha.31`，alpha.32/33 journal 加固）

实现契约（自动化协议、退出码、事务 journal、前向恢复、快捷方式规则、签名管线、工具供应、原生插件）见 [`docs/nsis-roadmap.md`](docs/nsis-roadmap.md)；
能力矩阵、人工清单、外部待办、上游取舍、专项决策分别在对应 `nsis-*.md`。

### Windows + WiX/MSI（已冻结，`0.1.0-alpha.43`）

WIN-MSI-1..9 全部完成：current-user/all-users 安装、x64+x86、38 语言独立产物、自定义 UI 序列、显式版本映射与升级/降级策略、旧 MSI 迁移与自动检测、受控 WiX 扩展与专家模式、可选 Authenticode 签名。
设计决策、阶段目标与验证证据见 [`docs/msi-roadmap.md`](docs/msi-roadmap.md)；能力状态见 [`docs/msi-capability-matrix.md`](docs/msi-capability-matrix.md)。

### macOS（`.app`/`.dmg`/`.pkg` 均已冻结，基线 `alpha.45`/`alpha.45`/`alpha.47`）

- `.app`：骨架/Info.plist 生成（Bundle 键、文件关联 `CFBundleDocumentTypes`+UTI 导出、URL scheme、ATS 例外域、调用方 plist 文件或内联合并）、`.icns` 图标（PNG 合成、`.car` 直通、`.icon` 经 `actool` 编译）、托管 Mach-O fat/thin 校验；签名管线 inside-out `codesign`（ad-hoc/identity/p12 临时钥匙串三模式）+ hardened runtime + entitlements + 显式公证（ditto→notarytool→stapler，凭证显式配置或 `APPLE_*` 环境回退）。
- `.dmg`：`hdiutil` 全链（UDRW→attach→拖放布局→convert），压缩 `Ulmo`/`Udzo`/`Udbz`，Finder 窗口布局全旋钮 + 无 GUI 会话自动降级，背景图/卷图标/`udifrez` SLA 注入，`codesign` 本体签名。
- `.pkg`：`pkgbuild` 组件包 + `productbuild` 分发包（标题/欢迎/许可/结语页、`system`/`current-user-home` 域、`BundlerPkgPayload` 任意载荷、`--scripts` 专家脚本），`pkgbuild --sign`/`productsign` 签名与 notarytool 公证管线；per-user 域免提权真实安装已验。
- 已登记分歧：macOS 打包工具不可再分发，采用"宿主工具检测+版本下限"策略（见各 `mac-*-roadmap.md`）；缺 codesigning 身份、Rosetta 等记入 `docs/mac-*-open-items.md`。
- `osx` 通用 RID：MSBuild `BundlerUniversalRuntimeIdentifiers` 复数 RID 内层发布 + 纯托管 fat 合并器（Mach-O 并片/其余逐字节一致/单边薄拒产），CLI `--input-dir` 可重复多目录合并（PR #14）。

### 其余格式

- `.deb`（已冻结，`alpha.51`）：纯托管 ar/tar/gzip 写入器；control/`md5sums`、SemVer→deb 版本映射、关系字段、Section/Priority、maintainer 脚本、systemd unit、conffiles、`.desktop`/hicolor/metainfo/changelog/copyright、`BundlerDebFile` 映射；压缩仅 gzip（xz/zstd 登记拒绝）。
- `.rpm`（已冻结，`alpha.55`）：纯托管 lead/header/cpio/gzip 写入器；六族关系字段、License/Group/Url、scriptlet/systemd unit/`%config(noreplace)`、gzip-only；可选 GPG 签名（`RPMSIGTAG_RSA`+`RPMSIGTAG_PGP`，`rpm -K`/zypper/dnf 可验）。
- `.AppImage`（已冻结，`alpha.58`）：内嵌 appimagetool×2 + type2 runtime×2（SHA-256 provenance），AppDir 共享 freedesktop 件 + 脚本 AppRun，压缩固定 zstd；可选 GPG 签名（`.sha256_sig` 段，`gpgv` 已验）。
- `.zip`/`.tar.gz`（已冻结，`alpha.59`）：纯托管写入器，单顶层目录布局、执行位与符号链接保留、`.sha256` 侧车、确定性构建；zip/tar 均流式写出：zip 经 Zip64 动态升级无尺寸上限，tar 单条目至 ustar ~8GiB 顶。
- `.apk`（已冻结，`alpha.63`）：纯托管三段 gzip 写入器，`.PKGINFO`+六脚本+`BundlerAlpineApkFile` 映射+pax SHA1 校验和，可选 RSA 签名段；`linux-musl-x86_64/arm64` 目标。
- CLI（已冻结，`alpha.62`）：`validate`/`plan`/`bundle` 三命令共用 Core/后端、`bundler.json` 层叠、`--bundles`/`--input-dir`、稳定退出码与 `--json`；dotnet tool nupkg + `PublishAot` 原生二进制双分发。
- UPDATE（已冻结，`alpha.74`，PR #27）：静态 JSON 清单 feed + ECDSA P-256 分离签名（`.sig`+`feed.sig` 防降级）+ `bundler-update.json` 身份旁车 + 外置 AOT 引导件（shell 降级）；nsis/msi 重跑安装器、zip/targz/app/appimage 三段式换包（备份/崩溃恢复/`--rollback`）；block-map 64KiB 差分默认开（实测 83% 复用，失败回落全量）；本地/UNC feed 离线可更新；deb/rpm/apk/dmg/pkg 按权威实践排除；外部待验收见 `docs/update-open-items.md`。

各格式契约、证据与外部事项详见 `docs/<format>-roadmap.md` / `<format>-capability-matrix.md` / `<format>-open-items.md`。

## 3. 最近验证

当前基线：`main@17624bf`（`0.1.0-alpha.102`）。
最近验证：`build` 0 警告；CliTests 42/42 通过；AppImage/Rpm 关联腿 47/47 通过——逐 PR 验证与轮测/CI 证据以 git 提交与 PR 记录为准，本文件不再逐条留档。

## 4. 外部验收边界（未执行，不视为完成）

以下项目在本机无法自动化，转入人工/外部清单，**不得宣称已完成**：

- 真实交互 UI 流转、勾选启动实际行为、`InvalidDirDlg`、位图显示、缩放/辅助功能；
- junction/重解析点作为安装目标的安装时行为（原生 MSI 条件检测受限，记录为能力边界）；
- per-machine UAC 真实提权安装/卸载；
- 干净 Windows 10/11 宿主、x86/ARM64 宿主执行；
- 生产 Authenticode 证书与 RFC 3161 时间戳；
- 真实重启与锁定文件场景、本地化内容评审；
- 启用 Windows FIPS 策略宿主的 `candle -fips` 真实行为与非拉丁语言交互安装审校（MSI-OI-13 与 `docs/special-acceptance.md` 母语审校项）。

逐项清单见各格式 `docs/<format>-open-items.md`；只能人工/外部付费完成的验收统一登记在 `docs/special-acceptance.md`。

## 5. 未决问题（等待用户或外部输入）

- WiX v3 已退出免费社区服务；大范围公开分发前须重新评估维护风险（见 `third_party/wix/msi-wix-provenance.md`）。
- 各格式外部事项按 OI 清单等待对应环境输入：`docs/<format>-open-items.md` 全套（生产证书/公证凭证、UAC 提权、真实重启、干净宿主矩阵、ARM64 真机、语言审校等；MSI 签名已裁决不补集成腿——`Bundler.Tests/WixTests` 三断言已覆盖，外部仅余 MSI-OI-06 生产证书/时间戳）。
- 仓外独立包消费 fixture（`tests/Bundler.LocalPackagesTests/Fixtures/Msi/` 的 `Fixture` 与 `Standalone/`）在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`,
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源（PR #41，公网同版共存不会选错件）；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。

## 6. 默认下一步

全部格式与 CLI 均已冻结并入 `main`，四宿主完整测试全绿（§3）。
集成测试收编（PR #17）、dotnet/skills 审计整改（PR #18）、API 收窄与覆盖率收口（PR #19）、仓库结构清理与本地包消费独立工程（PR #20）均已并入 `main`（§3），本轮工作在途项清零。
UPDATE 自更新模块已实现并冻结（`alpha.74` 起，PR #27..#59 持续加固与重构至 `alpha.94`），新一轮三轮四宿主测试全绿、三条待裁项与权威对标补齐全部落地，跨版本兼容冻件腿与三宿主真小卷 ENOSPC 腿在网，`tests/Special/` 特殊验收脚本矩阵就位（§3 最新三轮）。
目标模型重构（PR #55..#59）已并入 main 并经三轮四宿主轮测（终轮全新会话整轮零缺陷）+ release-verify 同树 3 连绿收口。
剩余工作：外部待验收项（各格式 OI 清单，见 §5）、发布链资产实际推送（nuget+release 为另一步）、以及零星已登记增强（按各 `<format>-open-items.md` 评估）。release-verify 六宿主全量验证已落地并入 main，main 上 dispatch 3 连绿。


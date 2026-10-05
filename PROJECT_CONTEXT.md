# DotNet.Bundler 项目上下文

> 最后更新：2026-10-05
> 当前分支：`main`（HEAD 以 git 为准；最新已实测基线见 §3 最新一轮）
> 当前包版本：`0.1.0-alpha.69`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全部 11 个格式（nsis/msi/app/dmg/pkg/deb/rpm/appimage/zip/targz/alpineapk）与 CLI 均已冻结并入 `main`；无进行中的格式阶段**
> 各格式冻结基线：NSIS `alpha.31`（后续 alpha.32/33 journal 加固）；MSI `alpha.43`；`.app`/`.dmg` `alpha.45`；`.pkg` `alpha.47`；`.deb` `alpha.51`；`.rpm` `alpha.55`；`.AppImage` `alpha.58`；`.zip`/`.tar.gz` `alpha.59`；CLI `alpha.62`；`.apk` `alpha.63`
> 签名能力（SIGN 已收官）：rpm/AppImage 可选 OpenPGP/GPG 签名、apk 可选 RSA 签名、NSIS/MSI 托管 Authenticode、app/dmg codesign、pkg productsign——逐格式证据见各 `<format>-roadmap.md` 与 `docs/signing-roadmap.md`
> 逐阶段实施证据以带日期条目归档在各格式 `<format>-roadmap.md`，本文不重复记录
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
| `tools/Windows.Nsis.Reboot` | 可抛弃 VM 重启验证入口（`Verify.ps1` 两阶段：锁定文件卸载 `3010` → 重启 → pending rename/目录/注册表/journal 清理断言） | PowerShell |
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
- `.zip`/`.tar.gz`（已冻结，`alpha.59`）：纯托管写入器，单顶层目录布局、执行位与符号链接保留、`.sha256` 侧车、确定性构建；zip 无 Zip64（>4GB 拒绝），tar 单条目 ~2GiB（内存模型）。
- `.apk`（已冻结，`alpha.63`）：纯托管三段 gzip 写入器，`.PKGINFO`+六脚本+`BundlerAlpineApkFile` 映射+pax SHA1 校验和，可选 RSA 签名段；`linux-musl-x64/arm64` 目标。
- CLI（已冻结，`alpha.62`）：`validate`/`plan`/`bundle` 三命令共用 Core/后端、`bundler.json` 层叠、`--bundles`/`--input-dir`、稳定退出码与 `--json`；dotnet tool nupkg + `PublishAot` 原生二进制双分发。

各格式契约、证据与外部事项详见 `docs/<format>-roadmap.md` / `<format>-capability-matrix.md` / `<format>-open-items.md`。

## 3. 最近验证

### 2026-10-05 MIT 许可 + 流式 tar + GUI 级验收（分支 `devin/1791190775-mit-tar-gui`，PR 待建）

- MIT 许可证落定：`LICENSE`（Copyright (c) 2026 Bundler contributors）+ `src/Directory.Build.props` 补 `PackageLicenseExpression=MIT` 与 Pack 项 + nuspec `<license type="expression">` 与打包文件项；README 许可节同步。
- `ARCHIVE-OI-06` 消解：`TarEntry` 增 `OpenContent` 流式载荷（`Func<Stream>`），`TarWriter.WriteEntry` 逐条流式写出——2.2 GiB 稀疏文件用例实测通过；约 8 GiB ustar 上限为 `WriteOctal` 确定性 `ArgumentException`；deb/apk 内部整 tar 暂存为已知残余（文档已登记）。
- GUI 级验收新增三腿（trait `Requires=interactive`）：
  `NsisIntegrationTests.InteractiveWizardInstallsAndUninstalls`（win 宿主 PASS·3m17s）、
  `MsiLocalPackagesTests.InteractiveWizardInstallsAndRemoves`（win 宿主 PASS·2m18s）、
  `MacDmgIntegrationTests.FinderSlaAcceptanceMountsVolume`（mac 宿主 PASS·69s）。
- Windows 驱动基建：`tests/Shared/Tooling/WindowsDesktop.cs`——`Interop.UIAutomationClient`（netstandard2.0 纯 COM interop，弃用 FlaUI：4.0.0 拖入带漏洞 System.Drawing.Common 触发 NU1904、5.0.0 仅 net48 触发 NU1701/NETSDK1136）；
  宿主迭代六轮实测坐实的驱动规则：候选按钮按实体类名过滤（标题栏伪按钮类名空）、同指纹连击 ≥3 升级物理鼠标注入（nsDialogs 自绘页与 Finish 免疫 `Invoke`）、终态件含 `Finish`/`Close`、Finish 前一律清复选框。
- mac SLA 应答经 osascript：`DiskImages UI Agent` 为 background-only 进程，whose 子句须显式含其名；前置 TCC 探针失败即 Skip。
- 验证：本机 build 0W/0E、三腿在对应宿主实测 PASS、宿主零残留；四宿主完整回归待跑。

### 2026-10-05 十样品合一（PR #21 已并入 `main`，squash `695ec2b`）

- `samples/` 收敛为单一 `samples/HelloBundlerApp/`：公共旋钮（身份/元数据/许可/签名共享/归档版本）在主 csproj，
  每后端全套公开旋钮在 `formats/{Nsis,Msi,MacApp,MacDmg,MacPkg,Deb,Rpm,AppImage,Archive,AlpineApk}.props` 经 `<Import>` 导入，
  `HelloBundler<Format>*` 传透覆盖；`Assets/<format>/` 归位格式专属素材；10 份样品文档合为 `hello-bundler-app-sample.md`。
- `BundlerFormats` 默认按 `$(RuntimeIdentifier)`×宿主 OS 推导：win→nsis[;msi 仅 Windows 宿主];zip;targz、osx→app;dmg;pkg 按宿主/显式分化、
  linux→deb;rpm[;appimage 仅 Linux];zip;targz、musl→alpineapk;zip;targz；`-p:BundlerFormats=` 任意子集可覆盖。
- buildTransitive 全部 224 个公开 `Bundler*` 旋钮逐一比对——0 缺口；旧样品遗漏的 18 个旋钮已补齐
  （`BundlerWixUpgradeCode`、WiX 专家扩展 5 件、`BundlerNsisLegacyMsiAutoDetect`、rpm Program 脚本、apk 五元数据等）。
- 合并暴露的五连环样品缺陷逐条修复复验：无 RID 时 formats 坍缩（测试腿补 `-r`）、msi 许可须 RTF（含 msi 自动切换，`!app` 卫句）、
  mac `BundlerIcon` 缺 RID 门禁污染 win 图标集、CJK 字符进不了 MSI 各语言 DB codepage（含 msi 时中文文案/文件名自动回退 ASCII）、
  NSIS 快捷方式工作目录随 CJK 回退改指 `docs`；三条产品契约约束已写入样品文档。
- 耦合更新：`MsiLocalPackagesTests` 三变体改跑 `HelloBundlerMsi*`（补 `-r win-x64`+`BundlerFormats=msi`）；
  `ProgramTests` 断言十 props 导入与前缀覆盖；当前用法文档同步（dated 验收记录保留原文）。
- 四宿主回归 `a10ff79` 全绿：win 默认发布四格式全产 + LocalPackages 8/8 真装；linux `deb;rpm;appimage;zip;targz` + 跨宿主 `nsis;zip;targz`；
  mac `dmg;pkg;zip;targz` 图标照常；alpine `alpineapk;zip;targz`。Devin Review 两轮 6 条全属实已修，复审零发现。
- 版本：`BundlerPackageVersion` 维持 `alpha.69`。

### 2026-10-04 仓库结构清理（PR #20 已并入 `main`，squash `33f37b9`）

- tests/ 收敛：12 个 `tests/<格式>.Integration/` 散目录的 fixture 与载荷全部并入 `tests/Bundler.IntegrationTests/Fixtures/<格式>/`（`Nsis/` 含 `LegacyMsiFixture*/`）；
  定位经 `RepositoryLayout.FixturesDirectory`（工程自身目录自定位）+ fixture 自身 `GetPathOfFileAbove` 导入，深度无关。
- 本地包消费独立成工程：`tests/Bundler.LocalPackagesTests/`（`MsiLocalPackagesTests` 类 + `Fixtures/Msi/` 的 `Fixture`/`Standalone`）——仓外经 `Bundler.LocalPackages.props` 消费本地 nupkg 的契约腿与其他测试工程分离；
  `MsiIntegrationTests`→`MsiLocalPackagesTests`、`MsiFixture`→`MsiLocalPackagesFixture`；集成基建下沉 `tests/Shared/Tooling/`（两测试工程链接共享），`MsiSupport` 同时被 NSIS 连续性腿使用故留在共享位。
- 资源归位：`templates/nsis` → `src/Bundler.Nsis/templates/`（EmbeddedResource LogicalName 不变）、`buildTransitive/*` → `src/Bundler.MSBuild/buildTransitive/`（nupkg 内 `buildTransitive/` 布局不变）、`tests/Windows.Nsis.Reboot` → `tools/Windows.Nsis.Reboot`。
- 脚本清零：17 个 `Verify.ps1`/`Verify.sh` 薄入口删除，统一为 `dotnet test --filter-class/-method`；保留 `tools/Bundler.Nsis.Plugin/Build.ps1`（真实构建脚本）。
- 文档瘦身：`docs/project-history.md`、3 个 `*-tauri-capability-audit.md`、12 个 `tests/*-integration.md` 删除；
  审计/历史指针全部去链接化或固化进路线与能力矩阵；dated 验收记录保留原文；新建 `tests/README.md` 统揽入口/门禁/fixture 地图。
- 权威标准件：新增 `.gitattributes`（eol 规范化 + `bad-crlf.sh` 载荷豁免）与 `.editorconfig`；本地包消费验收腿保留（权威项目对标：MSBuild-targets/工具包必须验证 nupkg 契约）。
- Bundler.IntegrationTests.csproj 与 Bundler.LocalPackagesTests.csproj 均排除各自 `Fixtures/**` 编译 glob；测试代码 `templates/`、`buildTransitive/` 源树路径改指新位置。
- 四宿主回归 `af07c19`（win/linux/mac/alpine）：build 全 0W/0E、pack 16@alpha.69；`LocalPackagesTests` win 8P/0F 真装真卸（其余宿主 8S 门禁）；`IntegrationTests` 174（182−8 MSI 腿迁出）win 41P/133S、linux 111P/63S、mac 51P/123S、alpine 97P/77S 全 0F——win 上 41+8=49 真装腿与拆分前等效；
  `Bundler.Tests` win 265P/16S、linux|mac 242P/39S、alpine 237P/44S；`ApiTests` win|linux 7P/3S、mac 8P/2S、alpine 6P/4S。Devin Review 零发现。

### 2026-10-04 API 面收窄 + 死代码清除 + 覆盖率盲点测试（PR #19 已并入 `main`，merge `ad36261`）

- `BundleConfigurationLoader`（Core，net10 TFM 专有）`public`→`internal`：反射式 STJ 公共面是 AOT 裁剪雷区，全仓仅 `ProgramTests` 一处调用（IVT 已覆盖）。
- 删两处双宿主覆盖率证实的零调用死代码：`BundleOrchestrator`（`BundlePipeline` 的无人使用 facade）、`AppImageProcessRunner.CaptureAsync`。
- Bundler.Tests +11 用例全部无宿主门禁：`ApkIdentity`/`ApkSigner`（非 RSA 键拒绝）、`DebName`/`DebVersion`（epoch 守卫）、`AppImageIdentity`（RID 架构映射）、MacApp 纯托管 helper（`InfoPlist` 全类型回环+非法形状、`MachO` thin/fat、`MacAppAssetsCar.IconImageName`）。
- 覆盖率（linux cobertura）：AppImage 81.0→87.0、MacApp 67.4→70.6、Core 86.9→87.5、AlpineApk 92.8→94.1、Deb 92.1→92.5；mac 宿主采集（coverlet 静态插桩，dotnet-coverage 在 macOS MTP 不可用）：Core 87.9/MacDmg 83.5/MacPkg 83.3/MacApp 80.6，残余缺口=签名/公证腿（宿主 0 签名身份，外部项）。
- 验证：linux 本机 281/242P/0F/39S + ApiTests 7P/3S + AppImage 集成 16/17；win 281/265P/0F/16S；mac 281/242P/0F/39S；alpine(musl) 281/237P/0F/44S——四宿主全绿。
- Devin Review 1 条 bug 属实已修（包内容变更未推进版本→`alpha.69`）。
- 版本：`BundlerPackageVersion` 推进 `alpha.69`。

### 2026-10-03 dotnet/skills 规范整改（PR #18 已并入 `main`，merge `258fad6`）

- 依据 `dotnet/skills` 官方仓 5 族审计（msbuild 9 条 / csharp-refactoring / pinvoke / aot-compat / 测试质量 6 条）出的修复项全部落位：
  `src/Directory.Build.props` 新建收编 15 个 csproj 重复元数据（`Authors`/`PackageVersion`/`PackageReadmeFile`/`netstandard2.0` TFM/README+THIRD-PARTY-NOTICES，链式导入根 props，Cli/Core 按名豁免）；
  `Directory.Packages.props` 新建中央包管理（5 个外部版本集中），`PackageReference` 去 `Version=`，自仓包引用改 `VersionOverride`；`DotNet.Bundler.nuspec` 补 THIRD-PARTY-NOTICES 文件项。
- 小问题：Archive fixture `ln -sf` 加 OS 门禁；`Bundler.ProjectReference.targets` 路径分隔符统一 `/`；`NsisToolResolver` 的 `chmod` 补 `CharSet.Ansi`；`ProgramTests` 平台判断改走 `TestPlatform`。
- 测试侧：`Bundler.Tests/TestPlatform.cs` 旧薄拷贝删除改链 `tests/Shared/TestPlatform.cs` 并补 musl 门禁；catch-and-assert 全部转 `Assert.ThrowsAny*`（catch 匹配子类，不等价 `Assert.Throws*` 的精确类型），`when` 过滤的 catch 转 ThrowsAny+显式断言；`Assert.True(a==b)` 转 `Assert.Equal`；
  IntegrationTests 按资源打 `[Trait("Requires", docker/elevation/localinstall)]`（docker 9 处/elevation 3 处/localinstall 方法级 7 处 + NSIS 类级；MSI `PublicSampleMsiTableContract` 走 `EnsurePackages` 免同意闸故豁免），`--filter-trait` 实测可用。
- 覆盖率采集一轮（dotnet-coverage cobertura，本机 linux）：宿主无关后端 81–95%（Deb 92.0/AlpineApk 92.6/Rpm 94.6/Nsis 89.9/Archive 89.3/Core 87.7），低分项均为宿主门控（Wix 7.2、Signing.Windows 26.9 全 win 门禁，MacApp 67.6 mac 门禁），`AppImageProcessRunner` 0% 由 IntegrationTests 腿覆盖；`BundleConfigurationLoader` 57.1% 记契约项。
- 验证（四宿主 @ `4022dc7` 全绿零失败）：win 254P/16S+49P/133S+9 薄入口、linux 231P/39S+111P/71S+6 薄入口、mac 231P/39S+51P/131S+6 薄入口、alpine 226P/44S+97P/85S+17P/1S（Bundler.Tests/IntegrationTests 计数）；
  pack 四宿主逐包核验内容不变（netstandard2.0 lib+README+THIRD-PARTY-NOTICES+Authors+alpha.68）；alpine 的 5 个 makensis 边界败随 T1 转 SKIP——四宿主首次全 0F。
- Devin Review 1 条 flag 属实已修（MSI `localinstall` 类级→方法级，`b19ca95`）；0 bug/0 security/0 code_quality。

### 2026-10-03 集成测试体全量收编 `Bundler.IntegrationTests`（PR #17 已并入 `main`）

- 改造内容：全部集成脚本的测试点逐腿收编进 `tests/Bundler.IntegrationTests`（xUnit v3，--filter-class/-method 选择），
  覆盖 Archive/AlpineApk/Deb/Rpm/AppImage/Cli/MacApp/MacDmg/MacPkg 九脚本 + Windows.Nsis.Integration/Verify.ps1（~30 变体、事务故障注入、旧 MSI 迁移、junction/篡改拒绝、签名链）
  + Windows.Msi.Integration 八脚本（DTF 表级断言 + msiexec 真装卸）。
- 脚本归宿：`Verify.sh`/`Verify.ps1` 全部降为薄入口转发 `dotnet test`；`tests/AssertLocalRestore.ps1`、`MsiTestSupport.ps1` 删除（能力并入 Tooling）；`Windows.Nsis.Reboot/Verify.ps1` 保留（真实重启属外部待验收）。
- 同意闸统一为 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1`（薄入口按其原开关语义置位）；NSIS 原脚本无开关，跑脚本即同意。
- 验证（同 HEAD `7a7fcc8` 四宿主 `dotnet test` 全量，0 败）：win 182/49P/133S（Nsis 27+Msi 8+Cli 13 真装真卸）、linux 182/111P/71S（docker 矩阵真装+FUSE 真挂载）、
  mac 182/51P/131S（MacApp/Dmg/Pkg 含 per-user 真装+relocate 语义）、alpine(musl 容器) 182/97P/85S（含 `linux-musl-x64` 真 AOT 实跑）；本机 linux 计数与宿主逐位吻合。
- 迭代修复链（全部测试侧）：夹具门禁惰性化（ctor 内 SkipWhen 记 fail）与陈旧工作区自愈、dotnet 子命令进程内串行（并发还原撞 `project.assets.json`）、
  msiexec 1618 整机单例闸+重试、pkg 装腿 relocate 诱饵改隐藏-恢复、进程退出 EOF 等待限时（`build-server shutdown` 挂死根因）、musl 边界腿改真跑/门禁（AOT 宿主 RID、root/dpkg 架构 SKIP）、
  `build-server shutdown` 限 Windows（musl `VBCSCompiler -shutdown` 间歇挂死）。
- 残留审计与再加固：四宿主清扫后新发现两类测试基建缺陷并修复——并发 `dotnet test` 会整删对方活工作区致 docker 缺失挂载源被 daemon 建成 root 空目录级联 EACCES
  （`IntegrationWorkspace` 每 slug 进程级文件锁 + `DockerRunner` 挂载源存在性断言，linux 并发复现实证按设计诚实失败）；
  pkg 装腿 relocate 竞态（`.app.hidden` 仍是合法 bundle 可被 lsd 再注册——隐藏双层化，内部 `Contents` 一并摘帽，窗口归零，mac 连跑 2×12/12 实证）。
- Devin Review 四项修复并入：NSIS 清理不再递归删 `transactions/` 父目录（跨产品共享，仅空时修剪）；`~/Applications/support` 按 `pkgutil --files` 收据外科删除；
  pkg 装腿 `installer` 移入 `try`（失败也恢复隐藏 `.app`）；签名口令改 env 通道 + gpg `--passphrase-file`（不再进 argv/失败日志，RPM 同型）。
- 最终验证（HEAD `facf0b4`）：linux 182/111P/0F/71S、alpine 182/97P/0F/85S 全量基线不变；win NSIS 27/27 与 MSI 8/8、mac MacPkg 连跑 2×12/12、各宿主锁路径实证；四宿主零残留复核通过。
- 版本：`BundlerPackageVersion` 推进 `alpha.68`。

### 2026-10-03 四宿主完整测试（测试正规化验证，PR #16 并入 `main`）

- 改造内容：`tests/Bundler.Tests` 迁 xUnit v3（270 用例全注册，`dotnet test` + MTP，宿主门控 `Assert.Skip`，`--external-sign-fixture` 自调用保留）；
  断言语义分级惯用化（368 处）；10 个 `*.Api.PackageFixture` 收编 `tests/Bundler.ApiTests`（产出契约改 `<FORMAT>_API_FIXTURE_OUTPUT` 环境变量，
  集成脚本 `dotnet test --filter-class` 复用）；MSI 共享 `Program.cs` 移入 `Standalone/`（仓外包消费腿不变）；
  `Pack-MsiTestPackages` 前导 `dotnet build-server shutdown` 消除四轮复现的 nodeReuse 任务程序集文件锁。
- 全量腿四宿主全绿：构建 0W/0E + 16 nupkg（`alpha.67`）；`Bundler.Tests` 254/231/231/226（win/linux/mac/alpine，0 败；alpine 5 败=makensis glibc 边界）；
  `Bundler.ApiTests` 门禁符合预期（win 7P/3S、linux 7P/3S、mac 8P/2S、alpine 6P/4S）；集成腿全过（win：NSIS 28 bundles+MSI 9 脚本本真安装；
  linux：6 `Verify.sh` 含 FUSE 挂载；mac：App/Dmg/Pkg/Cli；alpine：Apk 含 binfmt 实装）。
- 联合测试：四产方 99 件产出 sha256 核对 130/130 行全吻合；99 格装卸全过（msi-x86 缺 x86 运行时、msi-arm64 拒装两个预期边界）。
- 修复闭环：`KeepsPackageConsumerVersionsAligned` 断言改指 ApiTests 全后端项目引用；`IsMusl` 改探测 `ld-musl-*` 加载器覆盖非 Alpine musl（Devin Review 发现）。

### 2026-10-02 四宿主完整测试（项目引用改造验证，PR #15 并入 `main`）

- 改造内容：仓内 29 个示例/fixture 由 `PackageReference` 改项目引用，接线在 `Bundler.ProjectReference.targets`；
  MSI 仓外复制腿保留包引用作为包契约验收（`Bundler.LocalPackages.props` 收窄为仓外垫片，待决见 §5）。
- 全量腿四宿主全绿：干净构建 0W/0E + 16 nupkg；`Bundler.Tests` 268(win)/236(linux)/237(mac)/231-236(alpine，5 项 makensis glibc 边界)；
  各集成腿全过（win：NSIS 复验 29 bundles + MSI 8 脚本本真安装；linux：6 `Verify.sh` 含 AppImage 双腿；mac：App/Dmg/Pkg 全过；alpine：Apk 全过含 binfmt 实装）。
- 联合测试：四产方 99 件产出 sha256 核对 130/130 行全吻合；99 格装卸全过（仅 msi-x86 缺 x86 运行时、msi-arm64 拒装两个预期边界）。
- 修复闭环：NSIS `Verify.ps1` 位置参数残留 10 处、nodeReuse 文件锁（`BuildReference=false` 根因消除）均已修复并在 win-x64 复验。

### 2026-10-01 四宿主完整测试两轮（全量验收+联合测试，`main` @ `ade67d5`）

- 验收腿全绿：Windows Server 2022 x64（`Bundler.Tests` 268、NSIS 全量+MSI、**Authenticode 一次性证书签名三断言实过**）；Ubuntu 22.04 x86_64（236、五 `Verify.sh` 全绿、**rpm `rpm -K` digests signatures OK**、**AppImage `gpgv` Good signature**）；macOS 26.5.2 arm64（237、codesign ad-hoc 验签、pkg 签名缺真证书 SKIP 属 OI）；alpine-docker（236、apk 未签拒装+放行、**自签包公钥入 `/etc/apk/keys/` 免开关实装**+rc=99 双向断言）。
- 联合测试：四产方全格式×RID 共 138 件 → sha256 清单逐行核对 138/138 全吻合 → 各装方真实装卸/运行全过；osx 通用件走 `BundlerUniversalRuntimeIdentifiers` 正路（PublishSingleFile/AOT 配方），产出自验 fat+可运行。
- 无产品缺陷；qemu 仿真路本轮未开（apk/musl 腿由 alpine-docker 容器承担）。

### 2026-09-28 三平台全量验收（`main` @ `83fd134`）

- Windows Server 2022 x64：**268/268 全绿**（`Bundler.Tests` 235 + NSIS `Verify.ps1` 全量 + MSI 8 脚本全 exit 0）；验收发现 8 项缺陷已修复并经 PR #3 并入。
- Ubuntu 22.04 x86_64：**244/244 全绿、零缺陷**；五个 `Verify.sh` 全绿（docker 发行版矩阵、lintian/rpmlint 门控、GPG 签名段、Zip64 >4GB 确定性拒绝），另实测 systemd `enable --now`、apt/dnf 仓库工作流、KDE 桌面观感；CLI `linux-x64` AOT 51.9MB ELF 实跑通过。
- macOS 26.5.2 arm64：**243/244**；唯一未过项为 `Cli.Integration/Verify.sh` 缺 Linux `uname` 门禁在 macOS 上误报 appimage 段（测试基建缺口非产品缺陷，登记 CLI-OI-05）；验收发现 4 项缺陷已修复并经 PR #2/#4 并入；GUI 实做 DMG SLA 面板、Installer.app 页面、`sudo installer -pkg -target /` system 域安装、quarantine 首启阻断。
- ARM64（qemu 仿真）：验收完成（partial）——仿真环境已验，真机 ARM64 保留为外部待验收。
- 剩余外部待验收项（UAC 交互、真实重启、Apple Developer 凭证/公证、Intel/Rosetta、Windows ARM64、生产签名、干净宿主矩阵、22 语言审校等）按各格式 OI 清单如实保留。

### 2026-09-29/30 四平台全量验收 ×4 + 跨宿主联合测试 ×5（`main` @ `ffd1025`）

- 验收腿（每轮逐格一致零漂移）：Windows Server 2022 x64 13 腿（构建 0 警告、`Bundler.Tests` 244、NSIS 全量+MSI 8 脚本+CLI 冒烟、win-x86 三格式产出回归）；Ubuntu 22.04 x86_64 9 腿（212 单测、五 `Verify.sh` 全绿）；macOS 26.5.2 arm64 9 腿（213 单测、.app/.dmg/.pkg `Verify.sh` 全绿、Archive 门禁 not_run）；arm64 qemu 7 腿（宿主构建、arm64 容器真实装卸运行、`AI\x02` binfmt、AOT 交叉）。
- 联合测试：每轮产方全格式×7 RID 扇出 → sha256 清单分发 → 各装方核 sha 后真实装卸/运行；装腿 win 39 / linux 32 / mac 28 / qemu 18，四轮全过。
- 发现并修复（PR #12 并入）：Windows 产 osx 产物丢 unix 执行位（`ArchiveTree` 探针补 8 个 Mach-O 魔数）；win-x86 被 `DesktopTargetMatrix` 硬规则误关（nsis/zip/targz 开放 + `TargetArchitectureName` 补 `X86=>"x86"`）；MSBuild 校验失败吞明细（逐条透传 `issue.Path: issue.Message`）。
- 确定性结论：托管写入器（deb/rpm/zip/targz）同条件逐字节确定（同宿主跨轮 + linux↔qemu 同型跨宿主 16/16 对一致）；nsis/appimage 差异归因外部工具内嵌时间戳（上游性质，非缺陷）；跨 OS archive 差异归因 publish 载荷元数据与宿主 mode 表达。
- 非缺陷登记：pkg 重定位为宿主预存同 CFBundleIdentifier 副本触发的系统语义；x86 载荷运行需宿主有 32 位 .NET 运行时（`0x800700C1` 为环境前置）。

### 2026-09-26 Windows 11 Pro build 26200 x64 跨格式收尾回归

- `dotnet build Bundler.slnx -c Release`：0 警告/0 错误。
- `tests/Bundler.Tests` Release：78/78 PASS（新增自动检测渲染/默认关闭、1602 分支、MSBuild 映射断言）。
- NSIS `Verify.ps1` **全绿一次通过**（`alpha.44` 本地包）：既有全部用例 + 新增旧 MSI 自动检测命中、
  名称/发布者不匹配负例、同 UpgradeCode 多版本取最高并全部清理、NSIS→MSI 目录延续/优先级/范围外回落。
  此前两次复现的"committed journal 未清理"断言本轮未出现（`SafeDeleteTree` 瞬态占用重试修复）。
- MSI 集成**全套** PASS：`Verify.ps1`、`VerifyLifecycle.ps1`、`VerifyMaintenance.ps1`、`VerifyWinMsi5..8.ps1`、
  `VerifyPublicSample.ps1`（`alpha.44`，`WixProductDocument` 新增 NSIS 目录搜索+条件 CA 后全量复跑）。
- 环境检查：无遗留安装、注册表项、事务目录或探测目录残留。
- 原生插件重建：`DotNetBundlerNsis.dll` SHA-256 见 `third_party/nsis-plugin/nsis-plugin-provenance.md`，
  与 `Bundler.Tests` 断言一致。

历史验证（2026-09-26，WIN-MSI-9 冻结回归，`alpha.43`）：MSI 全套集成与 78/78 测试通过；
NSIS 回归首轮遇既知事务清理竞态 flake、复跑全绿（本轮已修复）；包供应与 Tauri 快照漂移复核完成。

## 4. 外部验收边界（未执行，不视为完成）

以下项目在本机无法自动化，转入人工/外部清单，**不得宣称已完成**：

- 真实交互 UI 流转、勾选启动实际行为、`InvalidDirDlg`、位图显示、缩放/辅助功能；
- junction/重解析点作为安装目标的安装时行为（原生 MSI 条件检测受限，记录为能力边界）；
- per-machine UAC 真实提权安装/卸载；
- 干净 Windows 10/11 宿主、x86/ARM64 宿主执行；
- 生产 Authenticode 证书与 RFC 3161 时间戳；
- 真实重启与锁定文件场景、本地化内容评审；
- 启用 Windows FIPS 策略宿主的 `candle -fips` 真实行为与非拉丁语言交互安装审校（MSI-OI-13/MSI-MT-12）。

逐项清单见 `docs/msi-open-items.md`（`MSI-OI-*`）与 `docs/msi-manual-testing.md`（`MSI-MT-*`），NSIS 侧见 `docs/nsis-open-items.md`/`docs/nsis-manual-testing.md`；其余格式同类项经 `docs/manual-testing-index.md` 索引到各自 `*-open-items.md`/`*-manual-testing.md`。

## 5. 未决问题（等待用户或外部输入）

- WiX v3 已退出免费社区服务；大范围公开分发前须重新评估维护风险（见 `third_party/wix/msi-wix-provenance.md`）。
- 各格式外部事项按 OI 清单等待对应环境输入：`docs/<format>-open-items.md` 全套（生产证书/公证凭证、UAC 提权、真实重启、干净宿主矩阵、ARM64 真机、语言审校等；MSI 签名已裁决不补集成腿——`Bundler.Tests/WixTests` 三断言已覆盖，外部仅余 MSI-OI-06 生产证书/时间戳）。
- 仓外独立包消费 fixture（`tests/Bundler.LocalPackagesTests/Fixtures/Msi/` 的 `Fixture` 与 `Standalone/`）在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`,
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。

## 6. 默认下一步

全部格式与 CLI 均已冻结并入 `main`，四宿主完整测试全绿（§3）。
集成测试收编（PR #17）、dotnet/skills 审计整改（PR #18）、API 收窄与覆盖率收口（PR #19）、仓库结构清理与本地包消费独立工程（PR #20）均已并入 `main`（§3），本轮工作在途项清零。
无未启动的后端立项项；新立项按 `docs/roadmap.md` §7.2 准入与新后端完整路线规则。
剩余工作：外部待验收项（各格式 OI 清单，见 §5）、以及零星已登记增强（Zip64 等按 `docs/archive-open-items.md` 评估）。


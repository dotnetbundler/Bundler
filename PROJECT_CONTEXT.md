# DotNet.Bundler 项目上下文

> 最后更新：2026-10-08
> 当前分支：`main`（HEAD 以 git 为准；最新已实测基线见 §3 最新一轮）
> 当前包版本：`0.1.0-alpha.83`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全部 11 个格式（nsis/msi/app/dmg/pkg/deb/rpm/appimage/zip/targz/alpineapk）、CLI 与 UPDATE 自更新模块均已冻结并入 `main`；无进行中的格式阶段**
> 各格式冻结基线：NSIS `alpha.31`（后续 alpha.32/33 journal 加固）；MSI `alpha.43`；`.app`/`.dmg` `alpha.45`；`.pkg` `alpha.47`；`.deb` `alpha.51`；`.rpm` `alpha.55`；`.AppImage` `alpha.58`；`.zip`/`.tar.gz` `alpha.59`；CLI `alpha.62`；`.apk` `alpha.63`；UPDATE `alpha.74`
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
| `src/Bundler.Update` | UPDATE 打包侧：清单发射器 + ECDSA P-256 `.sig`/`.blockmap` 旁车生成 | `netstandard2.0` |
| `src/Bundler.Updater` | UPDATE 应用内库：`UpdateClient` 查/下/验/换四动词 + block-map 差分（`BUNDLER_UPDATER_LINK` 单源双栖 Core 协议层） | `net10.0;netstandard2.0` |
| `src/Bundler.Updater.Bootstrap` | UPDATE 引导件：Native AOT per-RID `bundler-updater` + `tools/posix/bundler-updater.sh` 降级件（经 Bundler.Core 内嵌资源供应） | `net10.0` |
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
- `.zip`/`.tar.gz`（已冻结，`alpha.59`）：纯托管写入器，单顶层目录布局、执行位与符号链接保留、`.sha256` 侧车、确定性构建；zip/tar 均流式写出：zip 经 Zip64 动态升级无尺寸上限，tar 单条目至 ustar ~8GiB 顶。
- `.apk`（已冻结，`alpha.63`）：纯托管三段 gzip 写入器，`.PKGINFO`+六脚本+`BundlerAlpineApkFile` 映射+pax SHA1 校验和，可选 RSA 签名段；`linux-musl-x64/arm64` 目标。
- CLI（已冻结，`alpha.62`）：`validate`/`plan`/`bundle` 三命令共用 Core/后端、`bundler.json` 层叠、`--bundles`/`--input-dir`、稳定退出码与 `--json`；dotnet tool nupkg + `PublishAot` 原生二进制双分发。
- UPDATE（已冻结，`alpha.74`，PR #27）：静态 JSON 清单 feed + ECDSA P-256 分离签名（`.sig`+`feed.sig` 防降级）+ `bundler-update.json` 身份旁车 + 外置 AOT 引导件（shell 降级）；nsis/msi 重跑安装器、zip/targz/app/appimage 三段式换包（备份/崩溃恢复/`--rollback`）；block-map 64KiB 差分默认开（实测 83% 复用，失败回落全量）；本地/UNC feed 离线可更新；deb/rpm/apk/dmg/pkg 按权威实践排除；外部待验收见 `docs/update-open-items.md`。

各格式契约、证据与外部事项详见 `docs/<format>-roadmap.md` / `<format>-capability-matrix.md` / `<format>-open-items.md`。

## 3. 最近验证

### 2026-10-08 PR #38 压缩合并（squash `888a127`，main `888a127`，版本推进 `alpha.83`）

- 三轮测试 §5 三条待裁项按用户裁决（全采推荐项）落地：**sh 重启 cwd 对齐 AOT**（`_restart_nohup` 分目录级 `installDir`/文件级父目录，与 AOT `Restart` 双侧一致）；**junction/符号链接叶链穿透**写入 `update-capability-matrix.md` 契约行（双侧实证一致的解析穿透语义固化为设计承诺）；**多格式扇出改逐格式独立失败**——`RunBundle`/`BundleDesktopApplication` 逐格式 try/catch，单格式失败 WARN+计入失败表继续、末位汇总具名收场（CLI stderr→rc=1、MSBuild `LogError`→task false），校验拒绝（`BundleValidationException`/`CliUsageException`）仍穿透保 rc=2/任务级 catch 语义。
- 新断言 ×2（cwd 双侧平价 `RestartApp_CwdMatchesAotContract`、独立失败 `BundlePerFormatIndependentFailure`）；Bundler.Tests 全量 **366 件 0F**；复审 2 条发现回帖裁为设计语义（feed 覆盖写如实描述产出、格式旋钮无效=该格式不可产），无代码改动。

### 2026-10-08 PR #37 压缩合并（squash `9007056`，main `9007056`，版本推进 `alpha.82`）

- 新一轮三轮四宿主测试（R1/R2 复用会话、R3 全新会话）期间腿抓缺陷批量收口，三轮全绿：**CopyTree 保真**——`EnumerateDirectories(AllDirectories)` 把目录软链实体化且 managed 复制丢目录级 xattr（mac 签名 .app 洗白），POSIX 一律走宿主工具（macOS `ditto`、其余 `cp -a src/. dst/`，对齐 sh 语义），工具缺席 WARN 回退 managed，managed 重写为逐条 `EnumerateFileSystemEntries`+`LinkTarget` 判链重建（顺带治 Windows junction 同类实体化）。
- **移动操作两段式双侧对齐**：sh `mv` 跨卷与 AOT `File.Move`/`MoveTree` 半途留半成品污染备份槽恢复判据——sh 新增 `move_node`（mv→失败则 cp -a 到 `.partial-$$` 再原子换名）换七处调用点；AOT `MoveFile`/`MoveTree` 同构两段式换五处——输出槽"未开始/全本"两态恒成立。
- **挂载点/NT 目标家族**（win 腿+复审三轮拦截）：卷挂载点 reparse target（NT 对象名/裸 `Volume{GUID}\` 名）被当普通链接跟跳拼假路径——目录级静默写错卷 rc=0；别名同址漏判（`C:\mnt\app`×`D:\app` 物理同址拼写不同）——比较层经 `GetFinalPathNameByHandle(VOLUME_NAME_NT)` 归一到 `\Device\…` 对象层全拒，跨卷判改 `GetVolumePathName`；`LinkTarget` 对 `\\?\` 目标剥前缀返回裸名——判别改走 reparse Flags bit0：绝对 flag 的 `Volume{GUID}[\sub]` 经 `GetVolumePathNamesForVolumeName` 换 DOS 挂载名解真，相对 flag 照旧拼父级，不识设备形 rc=2。
- **腿断言收编入测试**：新增 `Bundler.Tests/UpdaterBootstrapParityTests` 13 件——POSIX 同一断言矩阵跑 AOT 进程内+真 `sh` 子进程双侧（win 跑 AOT 单侧），覆盖生命周期/回滚/同址互嵌六向拒绝/链接族/崩溃恢复 marker/良性叶链/仓内件冒烟；Bundler.Tests 全量 364 件 0F。
- 四腿复验全绿（最终 head `38ffe92`）：win mountvol 族+reparse-flag 判别矩阵、junction/链接面回归；mac ditto 保真+签名 .app `codesign --deep --strict`；linux tmpfs ENOSPC 无残渣；alpine busybox+musl 双侧；五 RID 引导件按 `38d857b` 同宿主重产回填（sha 逐件核验）；复审最终轮 0 发现。
- 三轮测试总账：R1/R2 干净（`edaab74`），R3 重跑零缺陷直通（`38ffe92`）——缺陷台账与三级证据见会话总报告。
- 待办：待裁项三条见 §5（`--app` 子进程 cwd 双实现分歧、junction/install 叶链穿透契约文档化、门禁格式中止整批语义）。

### 2026-10-07 PR #36 压缩合并（squash `71f0142`，main `71f0142`，版本推进 `alpha.81`）

- R3 重跑 mac 真机腿抓出的 2 缺陷 + 复审/腿复验拦下的 1 个数据丢失级缺陷收口：**D1** `ArchiveExtractor.RemoveAppleDoubleTree` 只挂 managed 回退——Darwin ditto/bsdtar 优选路径成功后载荷自带嵌套 `__MACOSX` 落进安装位（mac 腿 UpdateTests 1F 根因）——改任一提取成功后统一清树；**D2** 输出槽（备份槽/marker 槽/retain 目标）预置指向保护区外的良性叶链——`Directory.Exists` 顺链探测跳过预删 → `Directory.Move` 撞 already exists rc=4 且 `.bundler-swap` marker 残留楔形（后续每次 apply 误判崩溃恢复）——修 AOT 三处：`DeleteNodeIfPresent`/`DeleteLinkNodeIfPresent` lstat 语义删链节点不触目标、marker 槽叶链先删防 `Exists` 假触发与 `WriteAllText` 写穿、POSIX 侧补 `[ -L "$MARKER" ] && rm -f` 对齐。
- Devin Review BUG 发现（跨卷 `MoveTree` 半途失败留下完整备份却 `backupTaken=false`→catch 清 marker→下次 apply 删孤备致安装文件永失）：`MoveTree` 改两段式——`CopyTree` 到 `<dest>.partial-<rand>` 临时名再原子 rename 就位，备份槽只呈"未开始/全本"两态、恢复判据改 `Directory.Exists`；alpine 腿 ENOSPC 复验另抓到首版两段式未落地（编辑取消），`8926bf9` 修正后 sh/AOT 双侧实证。
- 腿复验全绿（最终代码 head）：mac 真机 D1 单测转绿+E2E 零残留、D2 三腿 rc=0 良性、hdiutil 小卷 ENOSPC install 逐字节完好；linux 349 测试 0F + sh/AOT 各 140 断言 + docker tmpfs ENOSPC 无残渣；alpine busybox 48/48 + musl 48/48 + 三断言 3/3；win 349 0F + 17/17 + UNC 跨盘符两段式；五 RID 引导件按 8926bf9 源码同宿主重产回填（sha 逐件核验）；复审最终轮 0 发现。
- 待办照旧：R3 整轮于本基线重跑（四腿全新会话，第三轮须零缺陷直通）；D3 遗留已知限制见 PR #34 条目。

### 2026-10-07 PR #35 压缩合并（squash `4906e05`，main `4906e05`，版本推进 `alpha.80`）

- R3 宿主腿实跑抓出的四缺陷集中收口：**D1** `dmg;pkg;zip` 多格式发布时 `.app` 构建两次写同一运输件，`UpdateManifestEmitter` 各记一条 feed 条目（同 url/file/size、不同 sha/sig），`SelectArtifact` 首条命中 stale → `sha256 mismatch — payload refused`，osx-arm64 更新链对真实发布件 100% 失败——修按产物 url+rid+format 键去重、后写条目原位替换；**D2** `SetFile -a E` 把 `com.apple.FinderInfo` xattr 写进 dmg 内层 .app 根，拖放安装随应用到用户机 → `codesign --deep --strict` 判 detritus——修法为整体移除该调用；**D4** `SingleTopDirectory` 单目录不判根级文件，"目录内容直压"手工件只装子目录（alpine 实证 196 文件静默丢失）——修仅 `dirs==1 && files==0` 才解包 wrapper；**D5** `RemoveAppleDoubleTree` 只清顶层，`__MACOSX` 嵌套残留落进安装目录——改全树递归清 + `Directory.Exists` 守卫（防父删子链枚举炸）+ targz managed 路同步。
- Devin Review 2 发现全实证修复（Exists 守卫 + 去重键加 rid+format）；宿主腿复验：mac feed 单条目 sha 对磁盘、codesign strict PASS、48 定向+344 全测绿；alpine 容器 31/31、195 文件全在、嵌套 `__MACOSX` zip+targz 双侧清零；UpdateTests 32/32 + MacDmgTests 18/18。
- 待办照旧：R3 整轮于本基线重跑（四腿全新会话，第三轮须零缺陷直通）；D3 遗留已知限制见 PR #34 条目。

### 2026-10-07 PR #34 压缩合并（squash `d455190`，main `d455190`，版本推进 `alpha.79`）

- D3 立项的 macOS 归档保真一轮批：macOS 宿主且载荷树带 xattr 时产出侧 zip 走 `ditto -c -k --sequesterRsrc`、targz 走系统 bsdtar、`.app` 运输 zip 走 `ditto --keepParent`（Sparkle/electron-updater 同款，AppleDouble 入 `__MACOSX/`）；解包侧新 `ArchiveExtractor`——Darwin `ditto -x -k`/`tar -x`，其余宿主 managed 提取+`__MACOSX/` 清树+S_IFLNK 符号链接还原（补齐 Linux zip 更新断链潜伏洞）；工具缺席/失败回退 managed+告警。
- mac 腿实证 D3 闭合：ad-hoc 签名 `.app` 经 zip 更新链换包后 `codesign --verify --deep --strict` 通过、180 条 `__MACOSX/`、xattr 全存活；期间另抓 D5（ditto 半提取残渣污染 managed 回退→`ClearDirectory`）与 D6（目录/软链自身 xattr 丢——`ArchiveTree` 给 Directory/Symlink 条目补 `SourcePath`）均已修。
- Devin Review 五轮 6 发现全处置，第 4、5 轮连续 0 发现；四腿复验全绿：linux zip S_IFLNK 4/4、alpine busybox 5/5+musl 10/10、win zip 链 195 文件逐字节一致。
- 已知限制已挂 update-roadmap：`ditto -x` 不还原软链自身 xattr（Apple 工具语义上限，Sparkle 同款）——zip 通道链级 xattr 落地即丢，targz 通道三态全保真；`.app` 签名不含链级 xattr 主场景无碍。

### 2026-10-07 PR #33 压缩合并（squash `923ead4`，main `923ead4`，版本推进 `alpha.78`）

- R3 宿主腿抓出的链接解析缺陷族一轮批：环链载荷/装位被放行（`File.Exists`/`Directory.Exists` 对环链报存在+`ResolveLinkTarget` ELOOP 被吞）——`ResolveLinkChain` 改纯 `LinkTarget` 跳走+`resolving`/`visited` 双守卫判环，环链与超 40 跳一律 rc=2；链目标字面拼写逃逸互嵌判（`/var` vs `/private/var`）——每跳经 `CanonicalParentPath` 物理化；补 `payload⊂retain` 第六向守卫。
- POSIX `bundler-updater.sh` 全语义对齐：`resolve_link`+`norm_seg` 逐段解算替代 `cd+pwd -P` 单段规范化，访问集经位置参数 `$@` 跨 `resolve_link`↔`norm_seg` 互递归帧下传（修跨帧嵌套环栈耗尽、`|` 字符误判），`norm_parent` 环链父级报 rc=2 不再字面回退；41+ 链由软断点改硬限判环。
- Devin Review 六轮共 8 发现全实证修复；宿主腿复验：linux-x64 aot/bash/dash 39/39、macOS 真机 AOT=sh 114/114、alpine busybox 18/18+36/36、win junction/mklink 环链全 rc=2+34 腿回归；四 RID 引导件按同 RID 宿主重产回填（sha256 逐件核验）。
- 开放项：`.app` 经 zip 传输丢 `com.apple.cs.*` xattr/符号链接（D3）已立项另行处理。

### 2026-10-07 PR #32 压缩合并（squash `2eed745`，main `2eed745`，版本推进 `alpha.77`）

- R3 新腿 linux 实跑抓出的高危缺陷及连环硬化一轮批：`bundler-updater apply` 在 install↔payload 同址/互嵌下静默抹数据（备份移走→空载荷覆盖→备份消失）——修 install↔payload↔backup↔retain **四向同址/互嵌拒绝**，全部先于 marker 恢复与任何文件操作（rc=2）；POSIX `bundler-updater.sh` 同协议双侧对齐。
- Devin Review 五轮共 9 发现全实证修复：符号链接面系统硬化——**双拼写原则**（关系判一律叶链解析到底的物理名、文件操作一律父物理化+叶字面）：载荷经文件级/目录级符号链接逃逸互嵌判（mv 后自指死链）、悬挂 install 链接 marker 恢复失配、备份/retain 叶链越界删链外目标、环链死循环（解析限 40 跳）、`--backup-dir` 缺省位预置叶链逃逸同址判；附随修侧车挪位（`bundler-update.json` 移入 .app `Contents/Resources` 保 codesign）、`--app`/`--log` 相对路径就地解析、Restart 失败降级 WARN 非 rc=4、Restart Manager console-ctrl 广播误杀测试宿主（`CreateNoWindow`）。第五轮 0 发现。
- 宿主腿实证：busybox sh 20 腿全绿 + musl 容器真产件 3/3、win-x64 ~Update 49（48P/1S）、本机 49/49；五 RID 引导件全部按 head 重产回填（win/osx-arm64/osx-x64/linux-musl-x64/linux-x64，sha256 逐件核验）。

### 2026-10-06 PR #30 压缩合并（squash `45842ff`，main `45842ff`，版本推进 `alpha.76`）

- 四项整改合一批：CLI 删 `apk` 别名（`alpineapk` 唯一合法名，`apk` 留 Android）；`BundlerMacAppShortVersion` 默认继承 `$(BundlerVersion)`（样品输出不变）；`ApplyOptions.KeepRollbackBackup` 默认 false——换包期瞬备 `.bundler-backup` 必建（原子性+marker 崩溃恢复不可关），成功后默认删，开启才迁保留位当回滚点；保留位分层数据区 `DotNet.Bundler/backups/<名>-<路径哈希>`（per-user→LOCALAPPDATA/~/Library/XDG，per-machine→ProgramData//Library//var/lib）；POSIX `bundler-updater.sh` 同协议。
- Devin Review 四轮共 6 发现全实证修复：retain==install 等值逃逸（`SameOrInside`）、迁移失败阻断（`TryRetainOrRemoveBackup` 降级留瞬备）、`..` 字面误拒（`norm_path` 物理规范化+前移入参）、缺席父目录 `..` 逃逸（`norm_lexical` 词法折叠）、`--backup-dir` 无嵌套判抹安装位（补双向嵌套拒）、跨 RID 入包件过期（五 RID 全重产回填）。第四轮 0 发现。
- 宿主腿实证：win-x64（Server 2022）Update 47/47 + 自产件六腿全绿；macOS arm64 真机（26.5.2）等值拒/降级/.app/sh 全绿 + osx-x64 交叉产件；alpine busybox sh 嵌套拒/降级/回归 3/3 + musl 件容器实跑 3/3；本机全量 336/297P/0F/39S。
- 产件来源记录：win-x64/osx-arm64/osx-x64 由对应宿主子会话产件回传；linux-musl-x64 由 alpine 容器 `dotnet10-sdk`+musl 工具链真产（glibc 宿主交叉产出系 PT_INTERP/NEEDED 混血残件，不可用——musl 件只能在 musl 工具链宿主产）；linux-x64 本机产。
- 已知边界：POSIX 不存在路径的 `..` 为词法折叠近似（兜底降级不抹数据）；osx-x64 件未在 Intel Mac 实跑（挂 `docs/special-acceptance.md` 借机组既有项）。

### 2026-10-06 main `2bec10e` R3 四宿主回归 + PR #29 压缩合并（squash `be58d94`）

- 四段全项复跑：全量（四宿主 build 0W/0E + pack 19 nupkg + Tests/ApiTests/LocalPackages/IT 全 0F：linux/alpine 333(294P/39S)+10+9S+176、win 333(317P/16S)+7+9P 真装+176、mac 294P/39S+8+9S+176）+ 样品（全格式 + UPDATE 侧车齐）+ 更新（引导件 apply/rollback rc=0、签验负例、file-swap E2E、差分 mac 81.7%/linux 缓存腿 100% 复用、nsis `/UPDATE`+msi major upgrade）+ 联合互产互装全实证（deb/rpm 三产方×debian/fedora 解析安装 6/6、apk 三产方 alpine 实装 7/7、nsis 两产方 win `/S` 2/2、osx-arm64 .app mac 实机直跑 rc=0；musl/osx apphost 跨产方字节一致）。
- 真缺陷二修（PR #29）：Range 拒供差分不落全量（`TryDownloadDeltaAsync` 逐段 `catch(UpdateException)`→删残件→`return false` 回落，违反 update-roadmap.md:104 契约）+ 本地 feed 转义件名 `%20` 字面拼接 Abort 134（`ResolveArtifactLocation` 本地分支 `Uri.UnescapeDataString`）；各配回归测试；mac 实机无绕行 E2E 复验全通，linux/mac 双侧单测 335(296P/39S)+IT 0F。
- 版本推进 `alpha.75`（`Bundler.Updater` 库改动需新包号触达——Devin Review 发现成立后修）。
- 已裁决不做：`.bundler-backup` 挪位+孤儿清理（备份固定一份不累积、清理无法完备）；CLI `apk` 别名已删统名 `alpineapk`（alpha.76 落地，`apk` 名保留给未来 Android）。

### 2026-10-06 main `53303ae` 第二轮四宿主回归（全绿零缺陷）

- 四段全项复跑：全量（四宿主 build 0W/0E + pack 19 nupkg + Tests/ApiTests/LocalPackages/IT 基线持平 0F：linux 333/176、alpine 333/176、mac 333/176、win 333/176+LocalPackages 9P 真装真卸）+ 样品（全格式 + UPDATE 旋钮组侧车齐）+ 更新（引导件 apply/rollback rc=0、nsis `/UPDATE` 静默语义 + msi major upgrade 实跑）+ 联合互产互装交叉矩阵全过。
- PR #28 依赖声明端到端实证：deb `Depends`/rpm `Requires`/apk `depend` 逐位吻合；三路产方 deb/rpm 经 `apt-get`/`dnf` 解析安装自动拉入 libicu 链+探针 rc=0；三路产方 musl apk 干净 alpine 实装依赖自动解析+钩子+卸载全 rc=0。
- 跨宿主确定性升级实证：**osx-arm64 apphost 四宿主四方字节一致**（sha256 64af4bd5）、musl apphost 三产方同 BuildID；win/linux/mac 产 nsis 在 win `/S` 真装真卸；跨产 mac 件 mac 真机直跑 rc=0。差异仅剩已归因项（dll/pdb MVID、文本 CRLF、fileVersion 占位）。
- 基建事故处置：win 会话 VM 两次 dead_box 报废重开（替补 6f4d2053 完成腿）。
- 非缺陷观察：linux 产 manifest 自含自身一行；win 联合 zip 混入 `.bundler-work/` 残件；mac 产 osx 件带 update 侧车（产方启用旋钮，覆盖正向）；均为产方打包卫生项非产品缺陷。

### 2026-10-06 main 回归轮 + PR #28 压缩合并（squash `8b1e02a`）

- main `dbbbf91` 四宿主子会话回归：全量（build 0W/0E + pack 19 nupkg + Tests/ApiTests/IT 全绿 0F）+ 样品（全格式产出 + UPDATE 旁车齐）+ 更新（签验负例/引导件换包回滚/差分/installer-replay/file:// feed）+ 联合交叉矩阵（互产互装）全过。
- 联合实证面：linux 产 nsis 在 win `/S` 真装真卸；linux/win 产 musl apk 在 alpine 实装跑通；win 产 deb/rpm 在 debian/fedora 装跑；跨 RID 载荷按 PT_INTERP 边界结构断言；同宿主重产 byte-identical 实证（跨宿主差异全归因 SDK 构建非确定面/ECDSA 随机 k/时间戳，非写入器缺陷）。
- 唯一真缺陷修复（PR #28）：样品 deb/rpm/apk 未声明 .NET 运行时依赖（裸容器装后探针 rc=134 缺 libicu）→ `formats/*.props` 默认声明 Depends/Requires（deb 含 `libicu76|74|72|70|67|66` 候选子句、rpm glibc 组、apk 补 `icu-libs`），实证 apt/dnf 自动解析后探针 rc=0；Review 一轮发现（libicu66/67 未覆盖）已修。
- 版本不变：`alpha.74`（样品 props 不进 nupkg，无包内容变化）。

### 2026-10-06 PR #27 压缩合并并入 main（squash `4345670`）

- UPDATE 自更新模块全量落地：UPDATE-1..6 全部实现并四宿主实证，冻结基线 `0.1.0-alpha.74`。
- 完整测试轮收口：三族缺陷修复后四宿主全绿——NU5026 TFM 压制、`DataContractJsonSerializer` AOT 不兼容（迁 `UpdateJson` STJ 源生成）、Windows `%(RecursiveDir)` 反斜杠致内嵌资源名与构建机相关（`LogicalName` 归一 `'\'→'/'`）。
- 最终态：pack 19 nupkg 全 rc=0、IntegrationTests 四宿主 0 败、Bundler.Tests 全绿、Devin Review `f144a36` 零发现；5 件 per-RID AOT 引导件与 BootstrapPlan 最终态一致。

### 2026-10-06 UPDATE 完整测试轮修复（分支 `devin/1791228441-update-module`，PR #27）

- 四宿主全量测试暴露两族缺陷均已修：Bundler.Updater 双目标被 src 级 props 单数 TFM 压制（补空复位行 → pack NU5026 清零，原 98 腿级联）；UPDATE 序列化迁 STJ 源生成（新 `UpdateJson`，netstandard2.0 仍 DCS → CLI AOT IL2026/IL3050 清零）。
- usage 补 `--rollback`；载荷根级引导件/身份旁车定为设计契约。
- 证据：pack rc=0、CLI AOT publish rc=0；本机 333/294P/0F/39S + IntegrationTests 176/111P/0F/65S；四宿主子会话增量复测在途。
- 待办：宿主复测回报 + win/osx 引导件重建入库后复审。

### 2026-10-06 UPDATE 复审修复第三轮（分支 `devin/1791228441-update-module`，PR #27）

- 复审 5 发现：2 属实已修（`SignatureUrl`——feed 带 query/fragment 时 `.sig` 插路径段；`Apply` 文件级侧车移到派生引导件前防半态），3 旧案重报回线（url 转义/Apply 门禁/提取根隔离均已在前轮修复）。
- 证据：+1 用例（SignatureUrl 四断言）；update 腿 44/44；win/osx/linux/musl 五件 AOT 引导件与 BootstrapPlan 最终态一致（宿主子会话第三轮重建入库）。
- 待办：复审收敛后等合并授权。

### 2026-10-06 UPDATE 复审修复第二轮（分支 `devin/1791228441-update-module`，PR #27）

- Devin Review 二轮 9 发现：7 属实已修（清单 url 转义、feed 本体 `.sig` 验签堵降级、文件级身份旁车、差分缓存改 Verify 后刷新、Apply 门禁未验件、引导件提取根按 uid 隔离 0700、POSIX 重启直 exec 去 `sh -c`），2 误报回线（.app 清单条目、+build 剥离均已实现）。
- 证据：+6 用例全绿；本机 Bundler.Tests 332/293P/0F/39S；build 0W/0E；linux-x64/linux-musl-x64 AOT 件重建入库。
- 待办：win-x64/osx-x64/osx-arm64 AOT 件宿主子会话第三轮重建；复审。

### 2026-10-06 UPDATE 复审修复轮（分支 `devin/1791228441-update-module`，PR #27）

- Devin Review 8 bug+5 flag 全部属实处置：`.app` 目录件改产 `.app.zip` 运输件进清单；清单 `url` 改相对清单目录路径；引导件工具内嵌 Bundler.Core 资源（删 tasks/updater 与 CLI 复制通道）；win 禁降级 .sh；nsis/msi 免引导件；崩溃恢复先于 install 存在性检查；新增文件级换包（AppImage 单件，sh 同构）；`TryRead` 增 `Contents/` 探测；semver 数值段比较+`+` 整串剥离；`.part` 重复下载先删再移；归档同名条目去重；删根 `tools/` 陈旧脚本。
- 证据：`UpdateTests`+`UpdaterClientTests` 新增 11 用例全绿（含 linux 真 AOT 件文件级换包+回滚）；build 0W/0E；linux-x64/linux-musl-x64 AOT 件重建入库。
- 待办：win-x64/osx-x64/osx-arm64 AOT 件待宿主子会话重建（UPDATE-3 同法）。

### 2026-10-05 UPDATE-5/6 差分+模块冻结（分支 `devin/1791228441-update-module`，版本 `0.1.0-alpha.74`）

- UPDATE-5 block-map 差分落地：`UpdateBlockMap` v1（64KiB 固定块+有序 sha256 b64）单源双栖；发射器每制品产 `.blockmap` 并写清单 `blockMap` 字段；`EnableDelta` 默认开——命中块本地复制+缺失块合并连续区间（http 硬要求 206、本地定位读）→产物 size+sha256 全验→任一环节失败回退全量；`<install>.bundler-cache/artifact.bin` 为差分源件缓存并随成功下载刷新。
- 双路实证全绿：本地差分命中腿（日志断言 `delta applied`/`B reused`）、脏缓存回退腿、**回环 HTTP 真腿**（微型服务器，feed/块表/Range-206 全走 http）；本机 Bundler.Tests 315/276P/0F/39S。
- 整模块真实 E2E：MSBuild 旋钮全链（keygen→publish→侧车/引导件入 zip→v1→v2 全量换包→回滚逐字节还原→v3 差分 83% 复用→逐文件一致）；顺手修三处真缺陷（MSBuild 旁 updater/ 缺失、Assembly.Location 探测、相对安装目录绝对化）。
- UPDATE-6 文档族收口：`update-capability-matrix.md`+`update-open-items.md`（OI-01..06）+`update-manual-testing.md`（MT-01..06）+`manual-testing-index.md` 登记+README"应用自更新"节+roadmap §7.2 改已实现；UPDATE 模块冻结基线 `0.1.0-alpha.74`。
- 外部待验收：arm64 AOT 引导件（qemu ilc 边界）、Apple 凭证同身份腿、真实发布链狗食、per-machine UAC、公网 CDN Range（UPDATE-OI-01..06）。

### 2026-10-05 UPDATE-3/4 宿主实证+应用内库（分支 `devin/1791228441-update-module`，版本 `0.1.0-alpha.74`）

- UPDATE-3 四宿主真机实证全绿：win（file-swap/断电恢复/NSIS `/UPDATE` 链/MSI major upgrade）、linux（kill -9 跨卷中段恢复 701M、软链+exec 位、posix bash+dash、签名负例）、mac（.app 门禁未签互换/quarantine 剥离/bundle-id 拒 rc4/`open -n` 重启/posix darwin 分支）、musl（static-pie 静态件+sh 三场景）；per-RID 引导件 win-x64/osx-arm64/osx-x64/linux-musl-x64 入库（远端 `2ca2e62`）。
- UPDATE-4 `src/Bundler.Updater` 应用内库落地：UpdateClient 四动词门面+`.part` 续传+file:// 离线腿+sha256/ECDSA 双验拒放+installer-replay/file-swap 分派+`--rollback` 引导模式；共享源链接+`Protocol` 命名空间隔离；feed 语义定稿为"清单文件地址+裸文件名相对件"。
- 修复：sh `--keep-payload` mv/copy 语义分叉、跨卷 CopyTree 软链解引用+exec 位丢失、`BootstrapperPath` .sh 误抢二进制分支、无匹配件改返回 null。
- 证据：UpdateTests 15+UpdaterClientTests 7=22/22（含真 AOT 引导件端到端换包+回滚）；全量 Bundler.Tests 310/271P/0F/39S；build 0W/0E。
- 遗留：mac 真实签名身份腿需 Apple Developer ID（外部待验收）；linux-arm64 AOT 因 qemu ilc SIGABRT 不可产（环境边界）。

### 2026-10-05 UPDATE-1 打包侧实现（分支 `devin/1791228441-update-module`，版本 `0.1.0-alpha.73`）

- 新工程 `src/Bundler.Update`（netstandard2.0）：`EcdsaSigner`（ECDSA P-256/SHA-256 分离签名，`.sig` 旁车统一 64B P1363 归一、验签 DER/P1363 双试）、`UpdateManifestEmitter`（`.sig` 旁车+`bundler-update-feed.{channel}.json` 清单：version/channel/publishedAt/notes/artifacts[rid,format,url,file,size,sha256,sig]；仅六适配格式进清单）。
- `src/Bundler.Core/Update/`：`UpdateKeyMaterial`（ec-p256 JSON 密钥材料+`FromPublicPoint` 还原；`ECDsa.Create()` 默认曲线 P-521 已实测须显式 nistP256）、`UpdateIdentitySidecar`（`bundler-update.json`：format/rid/channel/feedUrl/publicKey，公钥由私钥文件派生或显式给定）、`UpdatePayloadStaging`（更新开启时把旁车注入 staging 拷贝，用户发布目录零触碰）。
- 五后端注入：nsis（InspectDirectoryTree 后，签名 staging 复制带旁车入载荷）、wix（CollectFiles 校验后）、macapp（`Contents/` 根随 bundle 一起 codesign）、appimage（`usr/lib/<pkg>/` 载荷根）、archive（`UnderStem` 条目表追加 stem 级文件条目）。
- 旋钮：MSBuild `BundlerUpdateEnabled/FeedUrl/Channel/SigningKeyFile/PublicKey/Notes`（任务参数+buildTransitive 透传+样品 `formats/Update.props` 六旋钮全覆盖）；CLI `bundler.json` `update` 分节 + `--update.<knob>` 覆盖（CliArguments/CliConfig 节表登记，顺带修复 `--alpineapk.*` 解析期被拒的既有缺口）+ `bundler update-keygen --key-file <path>` 生成密钥并打印公钥。
- 验证：`UpdateTests` 10/10（密钥往返/64B P1363/DER 双验/错钥改件拒绝/清单 schema/emit 强要私钥/旁车含公钥/staging 注入不碰源目录/ArchiveBundler 真产 zip 内含旁车）；CLI 冒烟 keygen→zip 打包→`.sig`+feed+zip 内旁车三方闭环；全量 Bundler.Tests 298（259P/0F/39S）、ApiTests 7/0/3S、build 0W/0E。

### 2026-10-05 UPDATE 自更新路线立项（PR #26 已并入 `main`，squash `7617ff5`）

- 全面调研七家权威实现（Tauri/Sparkle/Velopack/electron-updater/Omaha/NetSparkle-Onova/AppImageUpdate）后定稿 `docs/update-roadmap.md`：13 项决策经用户裁决——六格式覆盖（nsis/msi/app/appimage/zip/targz；deb/rpm/apk 包管边界排除、dmg/pkg 非载体排除）、静态 JSON 清单/通道（支持 `file://`/UNC 离线 feed）、ECDSA P-256 `.sig` 分离签名（.NET 10 BCL 无公开 Ed25519 实测）、`bundler-update.json` 安装身份旁车、两式应用语义（安装器重跑/Sparkle 三段式换包）、同格式约束、外置 Native AOT 引导+shell 降级、block-map 差分（UPDATE-5，定宽 64KiB 块+CDC 备选登记）、备份回滚、mac 签名构建期归属+验身份剥 quarantine。
- 阶段表 `UPDATE-1..6` 至全功能：打包侧 → 引导程序 → file-swap+installer-replay 宿主实证 → `Bundler.Updater` 应用内库 → 差分 → 文档族冻结。
- Review 四轮清零（差分阶段表述统一/blockmap 协议定死/复用范围限定/离线验收腿入 UPDATE-4）。

### 2026-10-05 Zip64 动态升级（已压缩合并 `ef448b3`，PR #25）

- `ZipWriter` 按 BCL `ZipArchive` 式动态 Zip64：尺寸/压缩长/本地偏移顶 0xFFFFFFFF 或条目数顶 0xFFFF 的字段写哨兵+真值进 Zip64 extra/EOCD64+locator，version needed 升 45；未顶限归档字节与经典布局完全一致；流式条目按声明长 2MiB 安全带预升级（覆盖 deflate 最坏膨胀）。
- 原"Zip64 确定性拒绝"语义废止；`RejectsZip64` 用例替换为 EOCD64/头形态/4.3GiB 端到端三断言。
- 验证：本机 `ArchiveTests` 21/21（含 4.3GiB 稀疏实写+ZipArchive 全量读回 CRC、65536 条目 EOCD64）；CLI 实产 4.3GiB zip64 包经 `unzip -t` 全量解压 CRC、Python `zipfile`、win 宿主 `Expand-Archive`/`tar.exe` 三方读端实证。
- 版本推进 `alpha.72`；归档线尺寸天花板完全解除（zip 无上限、tar ~8GiB ustar 顶）。

### 2026-10-05 zip 载荷流式写出（已压缩合并 `03a070b`，PR #24）

- `ZipEntry` 新增 `Func<Stream>? OpenContent`（优先于 `Content`，契约与 `TarEntry` 一致）：
  流式条目恒 Deflate——本地头先写零值占位、增量 CRC-32 随写边算、末尾回填三字段，输出字节与预知长度写法同构，不用 data descriptor。
- `ArchiveTree.ToZipEntry` 文件来源改 `FileStream` 工厂；stored 优化仅留缓冲路径。
- 单条目上限由 ~2GiB 内存顶升至经典 zip 4GiB 格式顶；Review 修复：顶到 Zip64 哨兵值（0xFFFFFFFF/0xFFFF）的字段按 `>=` 统一确定性拒绝（尺寸/压缩长/本地偏移/中央目录三项/条目数），新注释全中文。
- 验证：本机 build 0W/0E、`Bundler.Tests` 288/249P/0F/39S（+4 新用例，含 2.2GiB 稀疏文件实产 zip 经 ZipArchive 全量读回验回填 CRC）、`ArchiveIntegrationTests` 14/14、pack 16 包 `alpha.71`。

### 2026-10-05 外部待验收实证清收（已压缩合并 `108041a`，PR #23）

- win-x64 r31 实机证据（main `90128b1` 工作区零改动）：
  - **junction 安装目标**（MSI-OI-12 子项消解）：MSI 不规范化重解析点——INSTALLFOLDER/Dir target/ARPINSTALLLOCATION 记 junction 路径原样；20 文件透写真实目标双侧可见；/x 清目标+ARP 注销但保留 reparse point；ARP 落 HKLM（currentUser 亦然）；孤立情形（junction+目标删而注册残留）下同 ProductCode /i 变 no-op resume、/x 仅清注册。
  - **FIPS**（MSI-OI-13 消解）：策略 Enabled=1 读回确认；Security 4688 审计捕获 candle.exe 实含 `-fips`（light 无此开关）；`=false` 对照在策略下 candle exit 308+CNDL0308 拒构（证实旋钮必要性非摆设）；FIPS 产物 /i→/x 零残留。
- alpine-docker 实证（同 HEAD）：
  - **qemu-aarch64 binfmt**（APK-OI-02 补强）：`tonistiigi/binfmt --install arm64` 注册后 `DockerArm64InstallRunRemoveUnderBinfmt` 真跑通过 1/1/0·76s（原 SKIP 腿激活）。
  - **musl AppImage FUSE**（LINUX-APPIMAGE-OI-04 补证）：特权容器（`--device /dev/fuse --cap-add SYS_ADMIN`+apk fuse）内 `./app.AppImage hi` 真挂载 `/tmp/.mount_*` 执行 rc=0、extract-and-run rc=0——musl 消费侧 FUSE 路径实测可用。
- 22 语言机械审校（本机）：22 `.nsh` 各 26 条 LangString 键集逐位一致、`LANG_*` 常量与目录映射全对（Persian→FARSI 设计内）、占位符集合跨语言一致、零条译文与英文原文相同——机械层无可消项，剩余仅母语审校/RTL 视觉/DPI 截断。
- 文档回填：`msi-open-items`（OI-12 子项/OI-13 消解）、`msi-capability-matrix`（junction 定性/FIPS 策略宿主验证）、`msi-manual-testing`（MT-11/12 子项标注）、`alpine-apk-open-items`（APK-OI-02）、`linux-appimage-open-items`（OI-04 musl 补证）、`nsis-open-items`（语言机械层复审）。
- 剩余真外部项不变：真实重启+锁定文件、per-machine UAC（EnableLUA=0）、干净宿主/真机矩阵、生产签名/公证凭证、RTL 母语审校、真机 ARM64。

### 2026-10-05 MIT 许可 + 流式 tar + GUI 级验收（PR #22 已并入 `main`，squash `243313b`）

- MIT 许可证落定：`LICENSE`（Copyright (c) 2026 Bundler contributors）+ `src/Directory.Build.props` 补 `PackageLicenseExpression=MIT` 与 Pack 项 + nuspec `<license type="expression">` 与打包文件项；README 许可节同步。
- `ARCHIVE-OI-06` 消解：`TarEntry` 增 `OpenContent` 流式载荷（`Func<Stream>`），`TarWriter.WriteEntry` 逐条流式写出——2.2 GiB 稀疏文件用例实测通过；约 8 GiB ustar 上限为 `WriteOctal` 确定性 `ArgumentException`；deb/apk 内部整 tar 暂存为已知残余（文档已登记）。
- GUI 级验收新增三腿（trait `Requires=interactive`）：
  `NsisIntegrationTests.InteractiveWizardInstallsAndUninstalls`（win 宿主 PASS·3m17s）、
  `MsiLocalPackagesTests.InteractiveWizardInstallsAndRemoves`（win 宿主 PASS·2m18s）、
  `MacDmgIntegrationTests.FinderSlaAcceptanceMountsVolume`（mac 宿主 PASS·69s）。
- Windows 驱动基建：`tests/Shared/Tooling/WindowsDesktop.cs`——`Interop.UIAutomationClient`（netstandard2.0 纯 COM interop，弃用 FlaUI：4.0.0 拖入带漏洞 System.Drawing.Common 触发 NU1904、5.0.0 仅 net48 触发 NU1701/NETSDK1136）；
  宿主迭代六轮实测坐实的驱动规则：候选按钮按实体类名过滤（标题栏伪按钮类名空）、同指纹连击 ≥3 升级物理鼠标注入（nsDialogs 自绘页与 Finish 免疫 `Invoke`）、终态件含 `Finish`/`Close`、Finish 前一律清复选框。
- mac SLA 应答经 osascript：`DiskImages UI Agent` 为 background-only 进程，whose 子句须显式含其名；前置 TCC 探针失败即 Skip。
- 验证：本机 build 0W/0E、四宿主完整回归全绿（win 交互双腿/mac SLA 腿实测 PASS，SLA 挂载竞态已修复验）；Devin Review 版本推进裁决→`alpha.70`。

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
UPDATE 自更新模块已实现并冻结（`alpha.74` 起，PR #27..#38 持续加固至 `alpha.83`），新一轮三轮四宿主测试全绿、三条待裁项全部落地（§3 最新两轮）。
剩余工作：CI 发布链测试（产出→安装→可用，触发方式与更新链是否纳入待用户确认）、格式旋钮统一预检（复审挂后续项——把各后端旋钮校验抽到扇出前校验层）、ENOSPC 断言收编（需 MoveTree/CopyTree 注入点，待裁决）、外部待验收项（各格式 OI 清单，见 §5）、以及零星已登记增强（按各 `<format>-open-items.md` 评估）。


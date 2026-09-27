# DotNet.Bundler 项目上下文

> 最后更新：2026-09-27
> 当前分支：`cli-development`（自 `main` `6592a0f` 拉出；八种格式全部冻结入 main）
> 当前包版本：`0.1.0-alpha.62`（根 `Directory.Build.props` 的 `BundlerPackageVersion`；`.deb` 冻结基线 `alpha.51`，`.rpm` 冻结基线 `alpha.55`，`.pkg` 冻结基线 `alpha.47`，`.app`/`.dmg` 冻结基线 `alpha.45`）
> 当前阶段：WIN-MSI-1..9 全部完成（MSI 冻结于 `alpha.43`）；macOS `.app`/`.dmg`/`.pkg` 均已冻结；
> `LINUX-DEB-1..5` 全部完成，`.deb` 冻结并已入 `main`：冻结基线 `0.1.0-alpha.51`，测试向量 141/141 + `Verify.sh` 全绿
> `.rpm` 已冻结于 `0.1.0-alpha.55`（`linux-rpm-development`，`LINUX-RPM-1..5` 完成）：`DotNet.Bundler.Rpm` 纯托管写入器 + `deb;rpm` 扇出 + 六族关系字段 + License/Group/Url 覆盖 + 共享 freedesktop 件 + `BundlerRpmFile` 映射 + 四 scriptlet/systemd unit/%config(noreplace)/gzip-only 压缩；fedora/rockylinux/opensuse 三容器真实装卸与升级语义 + rpmlint 豁免门控 + arm64 结构断言全绿；决策与证据见 `docs/linux-rpm-roadmap.md`
> `LINUX-APPIMAGE-1..4` 已完成，`.AppImage` 冻结于 `linux-appimage-development`（`0.1.0-alpha.58`）：`DotNet.Bundler.AppImage` 内嵌 appimagetool×2 + type2 runtime×2（SHA-256 provenance，始终 `--runtime-file` 外供不联网），AppDir 共享 freedesktop 件 + 脚本 AppRun + 根 desktop 链接/.DirIcon/默认 PNG 回落，压缩固定 zstd（pinned mksquashfs 约束）；九旋钮接线 + `deb;rpm;appimage` 扇出；`--appimage-extract` 结构断言 + 解出真实运行 + 三容器冒烟全绿
> `ARCHIVE-1` 已完成（分支 `archive-development`，`0.1.0-alpha.59`）：`DotNet.Bundler.Archive` 纯托管 zip/tar.gz 写入器（zip 自实现 unix mode/symlink）、单顶层目录布局、执行位与符号链接双保留、`.sha256` 侧车、`BundlerArchivePackageName`/`Version`/`ArchiveName` + `@(BundlerArchiveFile)` 接线、`zip;targz` 与五格式混排扇出；`Bundler.Tests` 181/181 + `tests/Archive.Integration/Verify.sh` 真实解包断言全绿；证据见 `docs/archive-roadmap.md` §4
> `ARCHIVE-2` 已完成（同分支，同版本）：旋钮面对照决策复核无缺口；收口断言补强（确定性构建 sha256 一致、目录源拒绝、Zip64 拒绝）；`Bundler.Tests` 184/184 + `Verify.sh` 全绿
> `ARCHIVE-3` 已完成（同分支，同版本）：宿主矩阵收口（python3 zipfile/tarfile 跨实现互读断言接入 `Verify.sh`）、干净宿主审计（零进程出口）、能力矩阵定稿、OI/MT 收口——**`.zip`/`.tar.gz` 归档冻结基线 `0.1.0-alpha.59`**
> `CLI-C1` 规划轮已完成（分支 `cli-development`）：决策清单 15 项 + `CLI-1..3` 三段骨架 + MSBuild↔CLI 映射表入档 `docs/cli-roadmap.md`，配套 cli-capability-matrix / cli-manual-testing / cli-open-items；决策已按推荐项放行
> `CLI-C1` 已完成（分支 `cli-development`，`0.1.0-alpha.62`）：CLI 契约冻结——三命令/退出码/`--json`/`bundler.json` schema/全格式旋钮面/dotnet tool nupkg + 原生 AOT 二进制双分发；`Bundler.Tests` 198/198 + `tests/Cli.Integration/Verify.sh`（含 AOT 段）全绿。待指令合并 `main`。
> 当前：`CLI-1` 完成，默认下一阶段 `CLI-2`（bundler.json schema 固化 + 层叠 + 全格式旋钮透传）
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 规则在 `docs/development-rules.md`；产品顺序在 `docs/roadmap.md`；历史记录在 `docs/project-history.md`；各格式细节在各 `docs/<format>-*.md`。
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
| `src/Bundler.MSBuild` | MSBuild Task 适配层（`buildTransitive` 导入） | `netstandard2.0` |
| `src/Bundler.Cli` | 开发原型（`IsPackable=false`，不发布） | `net10.0` |
| `src/Bundler.Package` | 便利元包 `DotNet.Bundler`（聚合后端与 MSBuild 支持） | `netstandard2.0` |
| `tests/Bundler.Tests` | 唯一快速测试入口（当前 163 项，Linux 宿主口径全绿；macOS 宿主口径多 MacPkg 公证等宿主用例） | `net10.0` |
| `tests/Msi.Api.PackageFixture` / `tests/Nsis.Api.PackageFixture` / `tests/MacApp.Api.PackageFixture` / `tests/Deb.Api.PackageFixture` / `tests/Rpm.Api.PackageFixture` | 仅引用 NuGet 后端的 API 消费 fixture | `net10.0` |
| `tests/Windows.Nsis.Integration` / `tests/Windows.Msi.Integration` | 真实 Windows 集成入口；`Fixture/` 为 MSBuild 消费 fixture；NSIS 侧含 `LegacyMsiFixture`（旧 MSI 迁移源） | PowerShell / `net10.0` |
| `tests/MacOS.App.Integration` | 真实 macOS `.app` 集成入口（`Verify.sh`，bash）+ `Fixture/` MSBuild 消费 fixture | bash / `net10.0` |
| `tests/MacOS.Dmg.Integration` | 真实 macOS `.dmg` 集成入口（`Verify.sh`，bash）+ `Fixture/` MSBuild 消费 fixture | bash / `net10.0` |
| `tests/Linux.Deb.Integration` | 真实 `.deb` 集成入口（`Verify.sh`，bash，含免密 `sudo dpkg -i/-r` 烟雾）+ `Fixture/` MSBuild 消费 fixture | bash / `net10.0` |
| `tests/Linux.Rpm.Integration` | 真实 `.rpm` 集成入口（`Verify.sh`，bash，含 docker `fedora:latest` 容器 `rpm -i/-e` 烟雾）+ `Fixture/` MSBuild 消费 fixture | bash / `net10.0` |
| `tests/Windows.Nsis.Reboot` | 需可抛弃 VM 的重启测试占位 | — |
| `samples/HelloNsisApp` / `samples/HelloMsiApp` / `samples/HelloMacApp` / `samples/HelloMacDmg` / `samples/HelloMacPkg` / `samples/HelloDebApp` / `samples/HelloRpmApp` | 公开可运行示例（应用版本 `1.0.0`） | `net10.0` |
| `tools/Bundler.Nsis.Plugin` | NSIS 原生插件源码（有意在 slnx 之外，重建需 .NET 10 + Windows 原生链） | `net10.0` |
| `third_party/` | 第三方归档、许可证、逐文件 SHA-256 与 provenance 文档 | — |

根 `Directory.Build.props` 的默认 `TargetFramework` 为 `net10.0`；
MSBuild 任务链（`Bundler.MSBuild` 及其加载的 Abstractions/Core/Nsis/Wix/Signing.Windows）保留 `netstandard2.0`，因为任务程序集须同时被 .NET Framework `MSBuild.exe` 和 `dotnet msbuild` 双宿主加载（见 `docs/development-rules.md` 第 3 节）。

## 2. 各格式当前状态

### Windows + NSIS（已冻结，`71a5c90`）

基线于 `0.1.0-alpha.31` 冻结，alpha.32/33 后续加固 journal 恢复；此后仅文档与共享层随 MSI 工作演进，语义不变。
实现契约（自动化协议、退出码、事务 journal、前向恢复、快捷方式规则、签名管线、工具供应、原生插件）见 [`docs/nsis-roadmap.md`](docs/nsis-roadmap.md)；
能力矩阵、人工清单、外部待办、上游取舍、专项决策分别在对应 `nsis-*.md`。
NSIS 集成 `tests/Windows.Nsis.Integration/Verify.ps1` 在 2026-09-27 的 alpha.42 回归中保持全绿（`IBundleBackend` 多产物契约变更未改变 NSIS 行为）。

### Windows + WiX/MSI（进行中）

WIN-MSI-1..8 实现与本机自动化验证完成：

- `WIN-MSI-1` 基础 MSI（current-user、x64、candle/light `-wx` 严格编译、UUIDv5 身份）
- `WIN-MSI-2` 结构与测试整理
- `WIN-MSI-3` 本地化界面、RTF 许可、签名管线
- `WIN-MSI-4` 发布级工具链（WiX 3.14.1 随包、警告即错误、ICE91 唯一豁免）
- `WIN-MSI-5` x86、显式 MSI 版本映射、可选降级
- `WIN-MSI-6` 安装目录选择（范围校验）、自定义 UI 序列、品牌位图、可选 Feature（快捷方式/PATH/卸载入口）、仅交互启动、ARP 元数据
- `WIN-MSI-7` 38 语言独立产物（一次构建每语言一个 MSI）、调用方 `.wxl` 键覆盖、快捷方式图标、`candle -fips` 透传
- `WIN-MSI-8` 受控 WiX 扩展（白名单 fragment + 调用方 ID 前缀 + 显式引用）与显式专家模式（整份模板/merge module + `candle -d` 身份变量 + 构建后数据库身份回读）

设计决策、阶段目标、实施记录与验证证据见 [`docs/msi-roadmap.md`](docs/msi-roadmap.md)；
能力状态见 [`docs/msi-capability-matrix.md`](docs/msi-capability-matrix.md)。

### macOS（`MAC-APP-1`/`MAC-APP-2`/`MAC-APP-3` 已实现，未冻结）

`MAC-APP`：上游审计、PKG 格式决策、`MAC-APP-1..5` 阶段分解已写入 `docs/mac-*.md` 一套文档，全部决策已确认。
`MAC-APP-1` 完成（2026-09-26 云 macOS VM 验证）：`src/Bundler.MacApp`（netstandard2.0）交付 `.app` 骨架生成、Info.plist 核心键、`.icns` 透传/合成、Contents 载荷映射语义；`BundlerFormats=app` MSBuild 映射与 `MacAppBundler` 直接 API；`tests/Bundler.Tests` 新增 22 条单测、`tests/MacApp.Api.PackageFixture` NuGet 消费 fixture、`tests/MacOS.App.Integration/Verify.sh` 真实生成→`plutil`→启动→删除链路、`samples/HelloMacApp`。
`MAC-APP-2` 完成（2026-09-26 云 macOS VM 验证）：文件关联（`CFBundleDocumentTypes`+`UTExportedTypeDeclarations`+UTI 推断表）、URL scheme（`CFBundleURLTypes`）、ATS 例外域、调用方 Info.plist 合并（文件/内联二选一，身份键回读拒绝）、`Assets.car` 管线（`.car` 直接采用优先，`.icon` 经 `actool`≥26 编译、缺失降级，`assetutil` 回读 `CFBundleIconName`）、托管 Mach-O fat/thin 架构校验（等价 `lipo -info`，Verify.sh 用真 `lipo` 交叉断言）。
MSBuild 映射：`BundlerMacDocumentType`/`BundlerMacUrlType` 项组与 `BundlerMacAppExceptionDomain`/`BundlerMacAppInfoPlistFile`/`BundlerMacAppInfoPlistXml` 属性。
Verify.sh 真实通过 `lsregister` 注册、`open <文件>`/`open <scheme>://` 唤起、`~/Applications` 拷入拷出。
`MAC-APP-3` 完成（2026-09-26 云 macOS VM 验证）：`MacAppBundleConfiguration.Signing` + `MacAppSigning`——ad-hoc/identity/临时钥匙串三模式、`xattr -crs`、inside-out 嵌套签名（`MacOS`/`Frameworks`/`Plugins`/`Helpers`/`XPCServices`/`Libraries` 内全部常规文件，含非 Mach-O 托管 dll）、`codesign --verify --deep --strict` 硬断言、非 ad-hoc `spctl` 评估、hardened runtime、entitlements；显式公证管线（ditto→`notarytool submit`--wait→`stapler staple`，凭证显式配置优先、`APPLE_*` env 回退，`skipStapling`/`NotaryWait` 开关）；临时钥匙串创建/导入/销毁与失败清理；MSBuild 新增 15 个 `BundlerMacApp*` 签名/公证属性。
新增 15 条单测（MAC-APP-2 九条 + MAC-APP-3 六条），macOS 宿主全套件 81 项全绿。
关键已登记分歧：macOS 打包工具不可再分发，采用“宿主工具检测+版本下限”策略替代字面“工具随包供应”（路线第 2 节）；默认产物不签名、公证默认关闭。
本机（云 macOS VM 26.5.2 arm64 + Xcode 26.6 + .NET 10.0.401）已实测全部所需宿主工具在位；缺 codesigning 身份、Rosetta、`pwsh`，记入 `docs/mac-app-open-items.md`。

### 未开始的格式

`.pkg` 已冻结（`MAC-PKG-1..5` 全部完成）。
`LINUX-DEB`：**1..5 全部完成，格式冻结并入 `main`**（2026-09-27，冻结基线 `0.1.0-alpha.51`）——`src/Bundler.Deb` 托管 ar/tar/gzip 写入器；DEB-1 最小 control+`md5sums`、`usr/lib`+`usr/bin` 布局、SemVer→deb 映射与八旋钮；DEB-2 关系字段透传、Section/Priority、`.desktop` 生成与 `DesktopFile` 覆盖、hicolor 图标（PNG 探测/`@2x`）、metainfo、`changelog.gz`/`copyright`、`BundlerDebFile` 绝对路径映射；DEB-3 维护者脚本四旋钮（0755/shebang+LF）、`SystemdServiceFile` 托管 unit + postinst `daemon-reload` 合成、conffiles（`/etc` DebFile 自动登记）、压缩仅 gzip（xz/zstd 登记拒绝）、升级/conffile 保留语义实测；DEB-4 lintian 硬断言基线（豁免清单入档）、`changelog.Debian.gz` 自动补发与扩展描述默认行、`linux-arm64` 结构断言、docker `debian:stable`/`ubuntu:latest` 装卸矩阵；141 项 Bundler.Tests + `Verify.sh`（真实 `sudo dpkg -i/-r/-P`、`desktop-file-validate`、`dpkg -L` 回读、lintian 信息级审计）全绿；`samples/HelloDebApp` 演示全旋钮；
`CLI-C1`：全部完成并冻结（`0.1.0-alpha.62`），细节见 `docs/cli-roadmap.md` §4。

## 3. 最近验证（2026-10-03，Windows 11 Pro build 26200 x64，跨格式收尾回归）

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

历史验证（2026-10-02，WIN-MSI-9 冻结回归，`alpha.43`）：MSI 全套集成与 78/78 测试通过；
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

逐项清单见 `docs/msi-open-items.md`（`MSI-OI-*`）与 `docs/msi-manual-testing.md`（`MSI-MT-*`），NSIS 侧见 `docs/nsis-open-items.md`/`docs/nsis-manual-testing.md`。

## 5. 未决问题（等待用户或外部输入）

- 本仓库开源许可证尚未确定。
- WiX v3 已退出免费社区服务；大范围公开分发前须重新评估维护风险（见 `third_party/wix/msi-wix-provenance.md`）。
- `MSI-OI-12`/`MSI-OI-13` 等外部事项待有对应环境时验收。
- MAC 规划轮已确认项（2026-09-26）：PKG 纳入 `PackageFormat`（`MAC-DMG` 后、Linux 前）；宿主工具检测策略替代内嵌供应（原则：构建工具尽量覆盖更多宿主设备；Xcode 专属工具只服务可选能力且须可降级；产出物设备兼容范围由应用开发者决定）；后端按格式分包 `Bundler.MacApp`/`Bundler.MacDmg`/`Bundler.MacPkg` + 共享 `Bundler.Signing.Mac`；universal 走 `osx-x64`/`osx-arm64` 双产物不加枚举。
- MAC 规划轮决策全部确认（2026-09-26）：`CFBundleVersion` 默认=版本号可覆盖；`LSMinimumSystemVersion` 调用方显式配置、未配置不写入；构建宿主=未签名 `.app` 任意宿主可构建（附权限位警告），需 Apple 工具的步骤限 macOS；宿主下限对齐 Tauri 同等标准（公证走 `notarytool`，Xcode 13+/macOS 11.3+），`actool`/`SetFile` 为可降级可选增强。
- `MAC-APP-OI-*` 外部事项（Developer ID 证书/公证凭证、Rosetta/Intel 宿主、干净宿主矩阵等）待有对应环境时验收；MAC-APP-4 已将 quarantine 拦截、LSMinimumSystemVersion 超限拒绝、v1→v2 原地升级、osx-x64 产物结构转自动化。
- `.app` 格式已冻结（2026-09-26，MAC-APP-5）：冻结基线 `mac-app-development` @ `BundlerPackageVersion=0.1.0-alpha.45`，行为契约=`docs/mac-app-capability-matrix.md` 定稿表+路线各节验收记录；冻结后仅缺陷修复附回归测试。
- `mac-app-development` 已合入 `main`（2026-09-26，快进合并，`b59e610`）；MAC-DMG 在 `mac-dmg-development` 分支推进。
- `MAC-DMG-1` 完成（2026-09-26 云 macOS VM 验证）：`src/Bundler.MacDmg`（netstandard2.0）交付 `hdiutil` 全链（create UDRW→attach→`/Applications` 链接+`SetFile -a E`→detach 退避→`convert`，默认 `Ulmo` 可配 `Udzo`/`Udbz`）；`BundlerFormats=dmg` MSBuild 映射与 `MacDmgBundler` 直接 API；`Bundler.Tests` 新增 9 条、`tests/MacOS.Dmg.Integration/Verify.sh` 真实 attach/断言/detach/verify、`samples/HelloMacDmg`。
- `MAC-DMG-2` 完成（2026-09-26 云 macOS VM 验证）：Finder 布局与品牌——`osascript` 窗口布局全可配（窗口 200,120+660×400、app=180,170、Applications=480,170、icon 128 默认对齐上游）、背景图拷入 `.background/`、卷图标 `.VolumeIcon.icns`+`SetFile -a C`（缺失降级）、无 GUI 会话降级警告+`BundlerMacDmgSkipWindowLayout` 显式开关；`Bundler.Tests` 新增 5 条（95 项全绿）、Verify.sh 品牌落卷断言+`.DS_Store` 条件断言全绿；修正 `hdiutil resize` 为 `-limits` 扇区数绝对值增长、`attach` 在布局运行时去掉 `-nobrowse`。
- `MAC-DMG-3` 完成（2026-09-26 云 macOS VM 验证）：DMG 本体 `codesign`（`MacDmgSigningConfiguration`：identity `-`=ad-hoc 与 `TemporaryCertificatePath`+Password 互斥，复用 `.app` 临时钥匙串）+ `LicenseFile`→`udifrez` SLA 注入（`LPic`/`STR#`/`TEXT`|`RTF `，字节格式按上游 dmg-license 源码订正）；`Bundler.Tests` 新增 4 条（99 项全绿）、Verify.sh EULA+签名变体 `udifderez` 回读+`codesign --verify`/`Signature=adhoc` 全绿；踩坑：`udifrez` 需 `-image` 旗标、只认转换后 UDIF（UDRW 报 78）。示例补 `HelloMacDmgLicenseFile`/`HelloMacDmgDmgSign*` 旋钮。
- `MAC-DMG-4` 完成（2026-09-26 云 macOS VM 验证）：原生 E2E 与支持矩阵——Verify.sh 全绿扩展（SLA 真实挂载门控 stdin 取消/`Y` 挂载、quarantine 传播到拷出副本、osx-x64 产物挂载+`file` x86_64 断言、非法压缩值失败无产物无残留）；干净宿主复核：必需工具仅 `hdiutil`/`osascript`（`SetFile`/`codesign`/`security` 按能力可选），无 Xcode/CLT 必需依赖；x64 运行态、GUI 观感、干净宿主首启登记外部待验收（OI-01/03/04）。
- `MAC-DMG-5` 完成（2026-09-26）：`.dmg` 格式冻结——上游复核无漂移（dev 头仍 `447fa9f`，`bundle_dmg`/`dmg/mod.rs` 字节一致），矩阵定稿，供应链复核确认无第三方内嵌工具；冻结基线 `0.1.0-alpha.45`，冻结测试向量 = `Bundler.Tests` 99/99 + Verify.sh 全绿；roadmap 推进 `MAC-PKG`。
- `mac-dmg-development` 已合入 `main`（2026-09-26 快进合并，`8cf5de5`），包版本推进 `0.1.0-alpha.47`；`MAC-PKG` 规划轮收官：11 项决策全部确认（`docs/mac-pkg-roadmap.md`）。
- `MAC-PKG-1` 完成（2026-09-26，macOS 26.5.2 arm64）：`Bundler.MacPkg` 后端落地（`pkgbuild` 全链、identifier/version/install-location 默认与覆盖、`BundlerPkgPayload` 任意载荷、宿主门控、失败清理）；MSBuild `BundlerMacPkg*` 接线；`Bundler.Tests` 109 项全绿 + `tests/MacOS.Pkg.Integration/Verify.sh` 全绿；示例 `samples/HelloMacPkg`。
- `MAC-PKG-2` 完成（2026-09-27）：分发包与页面——配置任一分发特性自动升级 `productbuild`，`distribution.xml` 生成（title 默认产品名、welcome/conclusion/license 页、许可页复用 `LicenseFile`、域名 `system`/`current-user-home`）；`Bundler.Tests` 114 项全绿 + `Verify.sh` 全绿含 **per-user 域免提权真实安装**（`~/Applications` 落位、`pkgutil --volume ~` 收据断言、启动验证）。踩坑：分发文档须声明 `<options hostArchitectures>` 否则 arm64 宿主误报 Rosetta；`<relocate>` bundle 重定位会抢占 install-location；per-user 收据在 `~/Library/Receipts`。
- `MAC-PKG-3` 完成（2026-09-27）：签名与公证+专家脚本——`MacPkgSigningConfiguration`（identity/临时证书/公证三模式凭证）、组件包 `pkgbuild --sign --timestamp`、分发包 `productsign --sign`（productbuild 出未签名档再签）、`.pkg` 公证 `notarytool submit`（直接收 pkg）+`stapler`、`ScriptsDirectory` 专家旋钮（`pkgbuild --scripts`）；`Bundler.Tests` 122 项全绿 + `Verify.sh` 全绿（postinstall 真实执行标记断言、无效身份失败路径）。真实签名/公证凭证属 OI-02/03。
- `MAC-PKG-4` 完成（2026-09-27）：原生 E2E 与支持矩阵——覆盖安装升级实测（v1→v2 收据版本更新）、osx-x64 产物结构+x86_64 payload 断言（运行态 OI-04）、干净宿主复核（纯系统工具链，公证路径需 Xcode）；`Bundler.Tests` 122 项全绿 + `Verify.sh` 全绿。
- `MAC-PKG-5` 完成（2026-09-27）：`.pkg` 冻结——能力矩阵定稿、OI-01..05 收口、冻结基线 `0.1.0-alpha.47`（冻结测试向量：122/122 + Verify.sh 全绿）；roadmap 推进 `LINUX`。默认下一阶段：`LINUX` 规划轮。
- MAC-DMG 规划轮已确认（2026-09-26）：C# 原生编排 `hdiutil`/`osascript`/`SetFile`/`sips` 不内嵌 create-dmg fork；DMG 本体可 `codesign`（`-` 跳过）不做公证；无 GUI 会话跳过布局+警告，`BundlerDmgSkipWindowLayout` 开关；EULA 经 `hdiutil udifrez` 注入 SLA；压缩格式可配置枚举 Udzo/Ulmo/Udbz、默认 `Ulmo`（挂载侧需 macOS 10.12+）；产物 `OutputDirectory/<rid>/dmg/<产品名>.dmg`；窗口布局全可配默认对齐上游。

## 6. 默认下一步

`.pkg` 已冻结于 `0.1.0-alpha.47`（`MAC-PKG-1..5` 全部完成）。macOS 线三种格式（`.app`/`.dmg`/`.pkg`）全部冻结。
`LINUX-DEB` 规划轮已完成（2026-09-27），`LINUX-DEB-1` 已于同日完成并验证。
默认下一步：`LINUX-RPM-1`——规划轮文档已入档（`docs/linux-rpm-roadmap.md` 决策清单草案 + `LINUX-RPM-1..5` 阶段表 + 能力矩阵/MT/OI 骨架）；等用户逐条确认决策后实施。

## 7. 历史记录

按时间排序的过往工作、当时证据、已知历史问题与任务衔接标识见 [`docs/project-history.md`](docs/project-history.md)。
该文件是档案，不代表当前状态。

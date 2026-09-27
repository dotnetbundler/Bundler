# Linux `.deb` 后端实施路线（LINUX-DEB）

> 状态：**LINUX-DEB-1 已完成并验证**（2026-09-27，分支 `linux-deb-development`，`BundlerPackageVersion` `0.1.0-alpha.48`）。
> 上游审计见 [`docs/linux-tauri-capability-audit.md`](linux-tauri-capability-audit.md)（固定快照 `tauri-apps/tauri@447fa9f`）。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/linux-deb-capability-matrix.md`](linux-deb-capability-matrix.md)；外部条件见 [`docs/linux-deb-open-items.md`](linux-deb-open-items.md)；人工步骤见 [`docs/linux-deb-manual-testing.md`](linux-deb-manual-testing.md)。

## 1. 已确认决策（2026-09-27，用户逐条确认）

1. **格式范围**：LINUX 线只含 `Deb`/`Rpm`/`AppImage`（公共枚举已有）。
   flatpak/snap 是商店与沙盒运行时生态而非单文件安装格式、pacman 属 Arch 专属，均记"明确拒绝/另立路线"，不进本线。
2. **工具供应：纯托管写入器，零原生工具**。
   `.deb` = `ar` 归档（`debian-binary`+`control.tar.gz`+`data.tar.gz`），由 `Bundler.Deb` 后端用 C# 直接写出，不调用 `dpkg-deb`、不内嵌二进制、不运行时下载。
   上游 Tauri 同款策略（`ar`+`tar`+`flate2` 纯 Rust）；`.rpm` 同策略（托管 rpm 写入器）。
   收益：任意构建宿主（含 Windows/macOS CI）可产 Linux 包，无 GPL 再分发义务，产出确定可复现。
   代价：需自行实现 ar/tar/gzip 容器与 deb 语义细节，正确性由 `dpkg-deb -I`、`dpkg -i`、`lintian` 真实断言背书。
   AppImage 例外：格式需要 ELF runtime+squashfs，不可纯托管，采用内嵌固定版本 `appimagetool`（SHA-256 provenance，构建限 Linux 宿主），详见审计对应行。
3. **包拆分**：`DotNet.Bundler.Deb`/`DotNet.Bundler.Rpm`/`DotNet.Bundler.AppImage` 每格式一包（沿用 `Bundler.MacApp`/`MacDmg`/`MacPkg` 先例）。
   freedesktop 数据树生成（`.desktop`、hicolor 图标、`usr/` 布局、MIME 映射）是三格式真实共享需求：首个落地于 `Bundler.Deb` 内部实现，`Bundler.Rpm`/`Bundler.AppImage` 落地时提取到 `Bundler.Core` 内部件并以 `InternalsVisibleTo` 供后端复用，不提前造公共 NuGet 包。
4. **payload 布局**：默认 `usr/lib/<package-name>/` 装载荷目录，`usr/bin/<command>` 为指向 `../lib/<package-name>/<main>` 的符号链接。
   参照上游 `/usr/bin`+`/usr/lib/<name>` 惯例并按目录载荷修正（.NET 发布物是多文件目录，不能整目录塞进 `usr/bin`）；
   `BundlerDebInstallRoot` 可改为 `/opt/<name>/` 等绝对路径，`/usr/bin` 链接可用 `BundlerDebBinLink` 关闭或改名。
5. **桌面集成**：生成 `<package-name>.desktop` 到 `usr/share/applications/`（`Type=Application`、`Terminal=false`、`Name`=ProductName、`Exec`/`Icon`=bin 链接名、`Comment`=Description、`Categories`=`BundlerDebCategories` 分号列表、`MimeType`=公共 `FileAssociations.MimeType` 并集 + `UrlProtocols.Schemes` 映射的 `x-scheme-handler/<scheme>`）。
   图标：PNG 按像素尺寸落 `usr/share/icons/hicolor/<w>x<h>/apps/`、`@2x` 文件名落 `<w>x<h>@2/apps/`（上游惯例）；非 PNG 首版不支持。
   AppStream metainfo 可选旋钮 `BundlerDebMetainfoFile`（调用方给整文件，落 `usr/share/metainfo/`）。
   `BundlerDebDesktopFile` 整文件覆盖生成结果（专家旋钮，责任边界标注）。
6. **依赖声明**：control `Depends:`/`Recommends:`/`Provides:`/`Conflicts:`/`Replaces:` 只透传调用方显式声明（`BundlerDeb*` 属性+直接 API 字段），不做运行时探测、不自动补 `shlibs:`（守 `development-rules.md` §2 边界）。
7. **维护者脚本**：专家旋钮 `BundlerDebPreInst`/`PostInst`/`PreRm`/`PostRm` 整文件注入 control.tar（权限 0755），责任边界与 MSI 专家模式同口径标注。
   托管 `systemd` unit 为可选旋钮 `BundlerDebSystemdServiceFile`（安装到 `usr/lib/systemd/system/`，postinst 仅 `daemon-reload`，不自动 enable/start；启用由调用方脚本决定）。
   `BundlerDebConffile` 项把载荷外文件声明为 conffiles（`dpkg -r` 保留、`-P` 清除）。
8. **版本映射**：`BundlerVersion`(SemVer)→deb `Version:` 默认映射 `上游版本~预发布-修订号`（`1.0.0-alpha.1`→`1.0.0~alpha.1-1`；`+build` 元数据保留 `+`）；`BundlerDebVersion`（完整含 revision）与 `BundlerDebRevision`/`BundlerDebEpoch` 显式覆盖。
   上游原样透传 `version_string` 不映射（`1.0.0-alpha` 会被 dpkg 读成比 `1.0.0` 新），本仓库显式收紧。
9. **签名**：`.deb` 包签名不做（deb 生态签名在 apt 仓库侧 `Release`/`InRelease`，`dpkg-sig` 包级签名覆盖率极低）；每个产物附带 `<file>.sha256` 侧车校验和。
   rpm 的 GPG 签名是真实生态惯例，排 LINUX-RPM 后续阶段；AppImage `--sign` 与 updateinfo 同理登记。
10. **命名与输出契约**：文件名用 deb 原生约定 `<package>_<deb-version>_<arch>.deb`（arch：`amd64`/`arm64`）；目录契约不变 `OutputDirectory/<rid>/deb/`。
11. **MSBuild 多格式**：放开适配层单格式限制，`BundlerFormats=deb;rpm` 同次 publish 由管线扇出（管线本身支持多后端，仅 Task 分发改动）；LINUX-DEB-1 先落地 `deb` 单格式，多格式扇出随 LINUX-RPM-1 一并验证。
12. **分支**：`linux-deb-development`（逐格式分支，沿用 mac 先例），rpm/appimage 各自 `linux-rpm-development`/`linux-appimage-development`。
13. **阶段骨架**：每格式"最小可用→元数据与桌面集成→脚本/systemd/压缩→原生 E2E 矩阵→审计冻结"五段；对应文档集在各自规划轮补齐。
14. **命名**：示例 `samples/HelloDebApp`；fixture `tests/Deb.Api.PackageFixture`；集成 `tests/Linux.Deb.Integration/Verify.sh`；文档 ID 前缀 `LINUX-DEB-OI-xx`/`LINUX-DEB-MT-xx`。

## 2. 打包工具下限（三层口径）

- **打包工具（能力）下限**：无原生工具——`Bundler.Deb` 以托管代码写出 ar/tar/gzip，能力下限即后端可运行的宿主；
  打包工具不绑定宿主 OS，任意能跑 .NET 的宿主可产 `.deb`。
- **后端下限**：`netstandard2.0` 库，同其他后端口径。
- **入口下限**：MSBuild=.NET SDK 支持的全部宿主；CLI 待定（CLI-C1）。
- **安装宿主边界**：`.deb` 产物面向 dpkg 系发行版（Debian/Ubuntu 及其衍生）；deb 版本字段、zst 压缩等特性按各发行版 dpkg 版本滚动核对，EOL 发行版只记"可运行"不作支持承诺。
- **验证工具分层**：`lintian`/`desktop-file-validate`/`dpkg`/`apt` 只用于本仓库验收链，不属产物依赖；缺失时集成脚本降级跳过对应断言并记录（验证工具 ≠ 打包工具）。
- **可选特性降级**：图标尺寸探测、systemd unit、metainfo 等均为可选配置，缺失不抬高必需链路。

## 3. 语义契约

- **身份**：deb `Package:` 名（`ProductName` 规范化 kebab 小写，`BundlerDebPackageName` 覆盖）+ `Version:`（决策 8 映射）+ `Architecture:`（`amd64`/`arm64`）。
  同一 `Package` 名的版本序列构成升级线；改 `Package` 名=新产品线（`Conflicts`/`Replaces`/`Provides` 由调用方声明迁移关系）。
- **输入契约**：`BundleTargetConfiguration.InputDirectory` 整体载荷 → `usr/lib/<package-name>/`；`MainExecutable` 须有可执行位语义（tar 0755）。
  `Resources`/`BundlerDebFile` 映射冲突、路径逃逸、链接外指在打包前报错。
- **安装语义**：`dpkg -i` 全量替换；升级=安装更高 `Version`；降级需 `--force-downgrade` 或 apt 允许（文档写实，不做防护承诺）；`dpkg -r` 保留 conffiles，`dpkg -P` 全清。
- **所有权**：全部 `data.tar` 载荷归包所有；卸载按 dpkg 清单删除；conffiles 语义见决策 7。
- **失败语义**：构建任一步失败→清理工作目录与半成品 `.deb`，不留伪产物（沿用 `BundlePipeline` 契约）。
- **输出**：`OutputDirectory/<rid>/deb/<package>_<version>_<arch>.deb` + `<file>.sha256` 侧车。
- **诚实边界**：deb 不承诺"双击 GUI 安装观感"、"apt 仓库工作流"（那是仓库职责）；这些进能力矩阵"明确拒绝/不适用"行而非夸大。

## 4. 阶段分解

### LINUX-DEB-1：最小可用 `.deb`

- **前置**：本路线确认入档（本阶段）。
- **目标/交付**：`src/Bundler.Deb`（netstandard2.0，`DotNet.Bundler.Deb` 包）——托管 ar 写入器、tar 写入器（含符号链接条目、uid/gid 0、确定性排序）、gzip（`System.IO.Compression`）、`debian-binary`、control 核心字段（`Package`/`Version`/`Architecture`/`Installed-Size`/`Maintainer`/`Priority=optional`/`Homepage`/`Description`）、`md5sums`、载荷→`usr/lib/<name>/`+`usr/bin` 符号链接、SemVer→deb 版本映射与覆盖旋钮、`.sha256` 侧车；
  `DebBundler` 直接 API + `BundlerFormats=deb` MSBuild 接线（`BundlerDeb*` 属性映射）；
  `Bundler.Tests` 新用例 + `tests/Deb.Api.PackageFixture` + `tests/Linux.Deb.Integration/Verify.sh`（真实 `dpkg-deb -I/-c` 结构断言 + **sudo `dpkg -i`/`dpkg -r` 真实装卸烟雾** + `dpkg --contents` 清单核对）；
  示例 `samples/HelloDebApp` 骨架（默认命令即产出可装 `.deb`）。
- **不做**：Depends 等关系字段、.desktop/图标、维护者脚本、systemd、conffiles、压缩选项。
- **退出**：`dpkg-deb` 识别产物、`dpkg -i/-r` 真实装卸零残留断言、失败路径无伪 `.deb`。
- **状态**：**已完成**（2026-09-27）。
  交付按规划落地：`src/Bundler.Deb` 纯托管 ar/tar/gzip 写入器、`DebBundler`/`DebBundleConfiguration` 公共面、`BundlerFormats=deb` MSBuild 接线与 `BundlerDeb*` 八个旋钮；
  验证：`tests/Bundler.Tests` 新增 12 项 DebTests（结构/映射/覆盖/失败/确定性/sha256/MSBuild 接线断言）全绿（合计 133 项）；
  `tests/Linux.Deb.Integration/Verify.sh` 全绿——`ar t` 三成员、`dpkg-deb -I/-c` 元数据与清单、解包运行、`md5sum -c`、`sha256sum -c`、覆盖/SemVer/失败变体、`Deb.Api.PackageFixture` NuGet 冒烟、免密 `sudo dpkg -i`/`dpkg -r` 真实装卸零残留；
  `samples/HelloDebApp` 默认 publish 产出 `hello-deb-app_1.0.0-1_amd64.deb` 并被 `dpkg-deb` 识别；
  `Bundler.LocalPackages.props` 修正 `RestoreSources` 丢失 nuget.org 的既有缺陷（干净宿主还原必需，属顺带修复）。

### LINUX-DEB-2：元数据与桌面集成

- **前置**：LINUX-DEB-1 通过。
- **目标/交付**：`Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces`/`Section`/`Priority`/`Maintainer` 覆盖；`.desktop` 生成（决策 5 全字段，`MimeType` 并集含 `x-scheme-handler/`）；hicolor 图标（PNG 尺寸探测、`@2x`）；`ChangelogFile`→changelog.gz、`LicenseFile`→copyright；`BundlerDebFile` 包内绝对路径映射；`DesktopFile` 整文件覆盖旋钮；`BundlerDebInstallRoot`/`BundlerDebBinLink`。
- **退出**：`desktop-file-validate` 通过断言；图标/桌面文件落入正确 hicolor/applications 路径断言；装后 `dpkg -L` 与 .desktop 内容回读。

### LINUX-DEB-3：维护者脚本、systemd 与压缩

- **前置**：LINUX-DEB-2 通过。
- **目标/交付**：`preinst`/`postinst`/`prerm`/`postrm` 专家旋钮（0755 归档）；`SystemdServiceFile` 托管 unit（含 postinst `daemon-reload` 片段的自动合成与调用方脚本冲突校验）；`Conffiles`；压缩枚举（默认 gzip；xz/zstd 在该阶段按宿主 dpkg 支持矩阵裁决或登记拒绝）；升级/降级语义实测（同包 `dpkg -i` 新版升级、`dpkg -r` conffiles 保留与 `-P` 清除断言）。
- **退出**：脚本在真实 `dpkg -i` 中执行断言（标记文件）；unit 安装路径断言；重复安装/升级/卸载回归全绿。

### LINUX-DEB-4：原生 E2E 与支持矩阵

- **前置**：LINUX-DEB-1..3 完成。
- **目标/交付**：`lintian` 接入 Verify.sh（基线断言+豁免清单显式登记）；`linux-arm64` 产物结构断言（arm64 运行态装测属外部）；docker `debian:stable`/`ubuntu:latest` 容器真实装卸矩阵；干净宿主复核（构建侧零系统依赖复核）；示例全旋钮收口；矩阵/文档/未验证格如实限缩。
- **退出**：矩阵实测格子有证据；未测格子进 OI/MT 清单。

### LINUX-DEB-5：审计与格式冻结

- **前置**：LINUX-DEB-4 完成。
- **目标/交付**：上游复核（快照漂移重核审计行）；能力矩阵定稿；OI/MT 收口；冻结基线写入本路线与 `PROJECT_CONTEXT.md`；`docs/roadmap.md` 推进 `LINUX-RPM`。
- **退出**：`.deb` 冻结基线写入，`LINUX-RPM` 规划轮待启动。

## 5. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | `dpkg-deb -I/-c` 结构、`sudo dpkg -i`/`dpkg -r`/`dpkg -P` 真实装卸、`dpkg -L` 清单、`lintian`、`desktop-file-validate`、systemd 单元落位、脚本执行标记、容器内装卸（docker debian/ubuntu） |
| 人工/外部 | GUI 桌面观感（菜单项/图标/文件关联双击）、linux-arm64 真实宿主、更多发行版矩阵（Debian oldstable、非 systemd 发行版）、apt 仓库工作流、生产签名流程（若后续阶段引入） |

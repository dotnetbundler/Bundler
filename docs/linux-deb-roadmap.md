# Linux `.deb` 后端实施路线（LINUX-DEB）

> 状态：**LINUX-DEB-1..5 全部完成，`.deb` 冻结并已并入 `main`**（2026-09-27，`412d3bb`）。
> 冻结基线：`DebBundleConfiguration` 配置面与行为契约冻结于 `0.1.0-alpha.51`；冻结测试向量 = `Bundler.Tests` 141/141 + `DebIntegrationTests` 全绿；冻结后仅缺陷修复附回归测试。
> 上游审计已完成（Tauri 固定快照 `447fa9f`）。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/linux-deb-capability-matrix.md`](linux-deb-capability-matrix.md)；外部条件与人工/特殊验收统一登记在 [`docs/special-acceptance.md`](special-acceptance.md)。

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
10. **命名与输出契约**：文件名用 deb 原生约定 `<package>_<deb-version>_<arch>.deb`（arch：`amd64`/`arm64`）；目录契约不变 `OutputDirectory/deb/（byFormat 布局；flat 为 OutputDirectory/）`。
11. **MSBuild 多格式**：放开适配层单格式限制，`BundlerFormats=deb;rpm` 同次 publish 由管线扇出（管线本身支持多后端，仅 Task 分发改动）；LINUX-DEB-1 先落地 `deb` 单格式，多格式扇出随 LINUX-RPM-1 一并验证。
12. **分支**：`linux-deb-development`（逐格式分支，沿用 mac 先例），rpm/appimage 各自 `linux-rpm-development`/`linux-appimage-development`。
13. **阶段骨架**：每格式"最小可用→元数据与桌面集成→脚本/systemd/压缩→原生 E2E 矩阵→审计冻结"五段；对应文档集在各自规划轮补齐。
14. **命名**：示例 `samples/HelloDebApp`；fixture `tests/Bundler.ApiTests`（`DebApiTests`）；集成 `tests/Bundler.IntegrationTests` 的 `DebIntegrationTests`（fixture `Fixtures/Deb/`）；文档 ID 前缀 `LINUX-DEB-OI-xx`/`LINUX-DEB-MT-xx`。

## 2. 打包工具下限（三层口径）

- **打包工具（能力）下限**：无原生工具——`Bundler.Deb` 以托管代码写出 ar/tar/gzip，能力下限即后端可运行的宿主；
  打包工具不绑定宿主 OS，任意能跑 .NET 的宿主可产 `.deb`。
- **后端下限**：`netstandard2.0` 库，同其他后端口径。
- **入口下限**：MSBuild=.NET SDK 支持的全部宿主；CLI=.NET 10 运行时或 NativeAOT 支持的宿主（dotnet tool 与 AOT 二进制双形态）。
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
- **输出**：`OutputDirectory/deb/（byFormat 布局；flat 为 OutputDirectory/）<package>_<version>_<arch>.deb` + `<file>.sha256` 侧车。
- **诚实边界**：deb 不承诺"双击 GUI 安装观感"、"apt 仓库工作流"（那是仓库职责）；这些进能力矩阵"明确拒绝/不适用"行而非夸大。

## 4. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | `dpkg-deb -I/-c` 结构、`sudo dpkg -i`/`dpkg -r`/`dpkg -P` 真实装卸、`dpkg -L` 清单、`lintian`、`desktop-file-validate`、systemd 单元落位、脚本执行标记、容器内装卸（docker debian/ubuntu） |
| 人工/外部 | GUI 桌面观感（菜单项/图标/文件关联双击）、linux-aarch64 真实宿主、更多发行版矩阵（Debian oldstable、非 systemd 发行版）、apt 仓库工作流、生产签名流程（若后续阶段引入） |

# Tauri 通用 Linux 能力审计（`.deb`、`.rpm`、AppImage，2026-09-27 快照）

本文件是 LINUX 系列格式的上游参照审计。
本仓库尚无 Linux 后端实现；“当前 Bundler 事实”列描述的是公共模型与规划器已存在的部分。
审计方法、状态口径与通用规则见 [`docs/development-rules.md`](development-rules.md) 第 2、6 节；
`.deb` 阶段分解见 [`docs/linux-deb-roadmap.md`](linux-deb-roadmap.md)，`.rpm` 与 AppImage 的路线在各自规划轮定稿前以本审计的“选择与阶段”列为预期。
三种格式在上游共用同一套 freedesktop 数据树生成，故共用一份审计。
只比较用户可观察能力，不复制上游字段、模板、脚本或内部机制。

## 上游快照与方法

固定快照：`tauri-apps/tauri` `dev` 分支提交 `447fa9f3f993fe77724189e355078b38ce20baea`。
该提交与 `docs/mac-tauri-capability-audit.md` 的 MAC-APP-5/MAC-DMG-5 复核基线相同；
2026-09-27 克隆核对 `dev` 头仍指向 `447fa9f`，自 MAC 冻结复核以来上游无新提交，本审计无漂移问题。
2026-09-27 LINUX-DEB-5 复核：`git ls-remote` 确认 `dev` HEAD 仍为 `447fa9f`（提交时间 2026-09-26），审计行基线保持有效。

固定比较点（均按快照 SHA 链接）：

- [`crates/tauri-bundler/src/bundle/linux/debian.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/debian.rs)：`.deb` 生成（纯 Rust：`ar`+`tar`+`flate2`，不调用 `dpkg-deb`）。
- [`crates/tauri-bundler/src/bundle/linux/rpm.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/rpm.rs)：`.rpm` 生成（纯 Rust `rpm` crate，含 OpenPGP 签名路径）。
- [`crates/tauri-bundler/src/bundle/linux/freedesktop/mod.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/freedesktop/mod.rs) 与 [`main.desktop`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/freedesktop/main.desktop)：`.desktop` 生成与 hicolor 图标布局（三格式共用）。
- [`crates/tauri-bundler/src/bundle/linux/appimage/mod.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/appimage/mod.rs)、[`linuxdeploy.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/linux/appimage/linuxdeploy.rs) 与同目录两个 linuxdeploy 插件脚本：AppImage 生成（`linuxdeploy` 运行时下载模式）。
- [`crates/tauri-utils/src/config.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-utils/src/config.rs)：`DebConfig`、`RpmConfig`、`AppImageConfig`、`LinuxConfig`、`RpmCompression`。
- [`crates/tauri-bundler/src/bundle/settings.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/settings.rs)：`PackageType` 与公共取值入口。
- [`crates/tauri-bundler/src/bundle/category.rs`](https://github.com/tauri-apps/tauri/blob/447fa9f3f993fe77724189e355078b38ce20baea/crates/tauri-bundler/src/bundle/category.rs)：`AppCategory`→freedesktop `Categories` 映射表。

不在本审计范围：`kmp/`（KMP 属其他生态）、Tauri updater 协议（绑定其运行时，如需应用更新另立跨格式路线）、上游 CLI 构建编排细节、webkit2gtk/gstreamer 等 Tauri 自身运行时依赖的打包逻辑（属 `development-rules.md` §2 明确不做的运行时依赖编排）。
上游 `dev` 会继续漂移，进入对应阶段前按快照路径重新核对差异。

## `.deb` 能力逐项审计（LINUX-DEB 范围）

| Tauri 用户能力或配置 | 当前 Bundler 事实 | 选择与阶段 |
| --- | --- | --- |
| 纯 Rust 生成 `.deb`：`ar` 归档内 `debian-binary`(`2.0`)+`control.tar.gz`+`data.tar.gz`，全程不调用 `dpkg-deb` | 无后端；`PackageFormat.Deb` 已在枚举 | **采用同策略的托管写入器**（C# 写 ar/tar/gzip）：LINUX-DEB-1；构建宿主不受发行版限制，任意 OS 可产 `.deb` |
| 包名 `heck::kebab` 化产品名，产物 `<name>_<version>_<arch>.deb`（`x86_64→amd64`、`aarch64→arm64`） | `linux-x64`/`linux-arm64` RID 已解析 | LINUX-DEB-1；`Package:` 名默认由 `ProductName` 规范化，`BundlerDebPackageName` 显式覆盖；文件名用 deb 原生约定 |
| 载荷布局：主程序→`usr/bin/<bin>`，资源→`usr/lib/<产品名>/` | `InputDirectory` 是任意目录（含多文件 .NET 发布物） | LINUX-DEB-1；**按目录载荷修正上游布局**：载荷根默认 `usr/lib/<name>/`，`usr/bin/<name>` 为指向主程序的符号链接（上游单文件二进制直放 `usr/bin` 的写法不适用于目录载荷） |
| control 字段：`Package`/`Version`/`Architecture`/`Installed-Size`(KiB)/`Maintainer`(authors→publisher→identifier 段回退)/`Section`/`Priority`(默认 `optional`)/`Homepage`/`Description`（短描述+长描述逐行） | `Publisher`/`Description`/`Homepage`/`Version` 已有公共模型 | LINUX-DEB-1 核心字段；LINUX-DEB-2 全量（`Section`/`Priority`/`Maintainer` 覆盖旋钮） |
| 依赖与关系字段：`depends`/`recommends`/`provides`/`conflicts`/`replaces` 显式字符串列表 | 无对应配置 | LINUX-DEB-2 显式透传，不做依赖探测（守 `development-rules.md` §2 边界） |
| maintainer 脚本：`preinst`/`postinst`/`prerm`/`postrm` 四件，权限 0755 | 无 | LINUX-DEB-3 专家旋钮（整文件注入，责任边界标注）；上游默认面也是脚本文件直传，无更安全的托管抽象可参考 |
| `md5sums` 文件（逐文件 MD5） | 无 | LINUX-DEB-1（lintian 合规项） |
| 自定义文件映射 `deb.files`（包内绝对路径→宿主文件） | 公共模型 `Resources` 是安装目录内相对映射 | LINUX-DEB-2 `BundlerDebFile` 项支持包内绝对路径映射（如 `/etc/<name>/`），逃逸校验 |
| `changelog`→`usr/share/doc/<name>/changelog.gz` | `LicenseFile` 已有 | LINUX-DEB-2：`ChangelogFile` 旋钮；`LicenseFile`→`usr/share/doc/<name>/copyright`（Debian 版权惯例） |
| `desktop_template`（handlebars 整模板覆盖） | 无 | LINUX-DEB-2 提供整份 `.desktop` 文件覆盖旋钮（不引入模板引擎，字段级旋钮优先） |
| `version_string` 原样写入 `Version:`（无 SemVer→deb 规范化） | `Version` 已校验 SemVer | **有意收紧**：LINUX-DEB-1 定义显式映射（预发布 `-`→`~`、附加 `-<revision>` 默认 1），`BundlerDebVersion`/`BundlerDebRevision` 可覆盖；上游原样透传会让 `1.0.0-alpha` 被 dpkg 读成新版 |
| 压缩：`control.tar.gz`+`data.tar.gz` 固定 gzip | 无 | LINUX-DEB-1 默认 gzip；xz/zstd 登记后续（.NET 无内置 xz 编码器；`data.tar.zst` 需 dpkg≥1.21.18，老宿主不认） |
| `.deb` 包签名 | 无 | 明确拒绝首个版本：deb 生态签名在仓库侧（`Release`/`InRelease`）而非包本体，`dpkg-sig` 覆盖率极低；提供 `.sha256` 侧车文件代替 |
| 构建宿主：deb 生成纯 Rust 无 OS 门控 | 无 | LINUX-DEB-1：任意宿主可构建（含 Windows/macOS CI），验收链用真实 `dpkg`/`lintian` |

## `.rpm` 能力逐项审计（LINUX-RPM 范围，规划轮前为预期）

| Tauri 用户能力或配置 | 当前 Bundler 事实 | 选择与预期阶段 |
| --- | --- | --- |
| 纯 Rust `rpm` crate 生成 `.rpm`（无 `rpmbuild` 进程） | `PackageFormat.Rpm` 已在枚举 | **采用同策略的托管写入器**：LINUX-RPM；任意宿主可构建 |
| 产物 `<name>-<version>-<release>.<arch>.rpm`（`x86_64`/`aarch64`），`release` 默认 `1`、`epoch` 可配 | `linux-x64`/`linux-arm64` 已有 | LINUX-RPM；`BundlerRpmRelease`/`BundlerRpmEpoch` 覆盖旋钮；rpm `Version` 禁含 `-`，SemVer 预发布段移入 Release 的映射契约（`1.0.0-alpha`→`Version=1.0.0 Release=0.alpha.1` 式）显式定义 |
| 关系字段：`depends`→`Requires`、`recommends`、`provides`、`conflicts`、`obsoletes`（`Dependency::any` 直传） | 无 | LINUX-RPM 显式透传；额外 `supplements`/`suggests` 等弱依赖按需评估 |
| 压缩 `compression`：Gzip(6) 默认 / Zstd / Xz / Bzip2 / None 可配 | 无 | LINUX-RPM 托管写入器内实现 gzip；xz/zstd 依赖可用编码器另行裁决 |
| 四脚本 `pre/post-install/remove`（rpm `%pre`/`%post`/`%preun`/`%postun`） | 无 | LINUX-RPM 专家旋钮（脚本内容注入 header tag） |
| GPG 签名：`TAURI_SIGNING_RPM_KEY`/`…_PASSPHRASE` 环境变量，`build_and_sign` 写签名 header | 秘密管理规则已有 | LINUX-RPM 后续阶段：显式密钥配置（引用凭据，不入库）；rpm 生态包签名是真实惯例，与 deb 侧"仓库签名为主"不同 |
| `License:` 标签取 `settings.license`（SPDX 字符串） | `LicenseFile` 是文件路径 | LINUX-RPM 提供 `BundlerRpmLicense`（SPDX 标识符）字段；`LicenseFile` 仍落 `/usr/share/doc` |
| 载荷/桌面集成与 deb 完全同构（二进制 `usr/bin`、资源 `usr/lib/<name>`、同一 `.desktop` 生成、同一图标布局、`files` 映射） | 同 deb 行 | LINUX-RPM；共享 freedesktop 数据树生成器（落地方式见 `linux-deb-roadmap.md` §1 决策 4） |

## AppImage 能力逐项审计（LINUX-APPIMAGE 范围，规划轮前为预期）

| Tauri 用户能力或配置 | 当前 Bundler 事实 | 选择与预期阶段 |
| --- | --- | --- |
| **打包时联网下载工具**：`linuxdeploy-<commit>-<arch>.AppImage`、`AppRun-<arch>`（tauri binary-releases 仓库）与 `linuxdeploy-plugin-appimage`（continuous release） | 本仓库硬规则"工具随包供应、不运行时下载" | **拒绝下载模式**：内嵌固定版本 `appimagetool`（x86_64/aarch64 Linux 宿主机型）+ SHA-256 provenance 进 `third_party/`；构建限 Linux 宿主 |
| `linuxdeploy` 的依赖打包职责（拷贝共享库、`--plugin gtk`/`gstreamer`、webkit2gtk 进程探测复制） | 产品边界 §2：不编排应用运行时依赖 | **明确拒绝**：不引入 linuxdeploy；AppDir 只装调用方准备好的自包含载荷，文档写清"依赖须打进载荷"契约 |
| AppDir 结构：`usr/`（与 deb data 树同构，复用 `debian::generate_data`）+ 根级 `AppRun`、`.DirIcon` 符号链接、`<name>.desktop` 符号链接、`<name>.png` 根图标 | 无 | LINUX-APPIMAGE；复用 freedesktop 数据树生成；`AppRun` 用受控脚本（`exec $APPDIR/usr/bin/<main>`+显式 env 前缀），不用预编译二进制 |
| 工具在无 FUSE 环境以 `--appimage-extract-and-run`/`APPIMAGE_EXTRACT_AND_RUN=1` 运行 | 无 | LINUX-APPIMAGE；内嵌 `appimagetool` 同样必须支持 extract-and-run 路径（容器/CI 常无 FUSE） |
| 产物 `<产品名>_<version>_<amd64|aarch64>.AppImage` | 输出契约 `OutputDirectory/<rid>/<format>/` | LINUX-APPIMAGE；沿用上游文件命名 |
| `bundle_media_framework`（gstreamer 打包） | 无 | 不适用：Tauri webkit/媒体栈专属 |
| 无内嵌更新元数据（上游 updater 走独立机制，不产 `.zsync`） | 无 | 明确拒绝首个版本做 updateinfo/zsync；需要时另立跨格式更新路线 |
| AppImage 本体签名（`--sign` GPG，上游未实现） | 无 | 计划实现评估：`appimagetool` 支持 `--sign`；排 LINUX-APPIMAGE 后期阶段或外部待办 |

## 跨格式公共观察

- 上游 Linux 三格式共享同一 freedesktop 数据树生成（`.desktop`+hicolor 图标+`usr/` 布局），`.desktop` 模板字段固定 `Categories/Comment/Exec/StartupWMClass/Icon/Name/Terminal=false/Type=Application/MimeType`；文件关联取 `mime_type`，深链 scheme 映射为 `x-scheme-handler/<scheme>`。
- 上游 `Icon=<主程序名>`、`Exec=<主程序名>`（依赖 `usr/bin` 就位）；本仓库目录载荷经 `/usr/bin` 符号链接后可保持同名约定。
- 上游 `AppCategory`→freedesktop `Categories` 映射表可直接参照（如 `DeveloperTool→Development;`），本仓库以显式 `Categories` 字段接受 freedesktop 注册类目名，不引入 mac 式 `public.app-category` 枚举的二次映射。
- 上游对 `files`/`desktop_template` 等采用"调用方给整文件"模式，与本仓库 MSI 专家模式/`ScriptsDirectory` 的责任边界惯例一致。
- 上游无任何 deb/rpm 真实安装验证产物记录；本仓库按既有规则要求最小阶段即做 `dpkg -i`/`rpm -i` 真实烟雾测试，验证强度高于上游。

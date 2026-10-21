# Linux `.deb` 包（dpkg 系）

本文件是 `.deb` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project/project-rules.md`。

## 1. 状态与决策

- `.deb` 已冻结（基线 `0.1.0-alpha.51`），仅缺陷修复附回归测试。
- **纯托管写入器，零原生工具**：`.deb` = `ar` 归档（`debian-binary`+`control.tar.gz`+`data.tar.gz`）由 `Bundler.Deb` 用 C# 直写，不调 `dpkg-deb`、不内嵌二进制、不运行时下载——任意构建宿主（含 Windows/macOS CI）可产，无 GPL 再分发义务，产出确定可复现；正确性由 `dpkg-deb -I`、`dpkg -i`、`lintian` 真实断言背书。
- flatpak/snap（商店/沙盒生态而非单文件格式）、pacman（Arch 专属）明确拒绝。
- freedesktop 数据树生成（`.desktop`、hicolor 图标、MIME 映射）落地于 `Bundler.Deb` 内部，`Bundler.Rpm`/`Bundler.AppImage` 经 `Bundler.Core` 内部件 `InternalsVisibleTo` 复用，不另造公共包。
- 载荷布局：`usr/lib/<package-name>/` 装目录载荷，`usr/bin/<command>` 符号链接指 `../lib/<package>/<main>`；`BundlerDebInstallRoot` 可改 `/opt/<name>` 等绝对路径，`BundlerDebBinLink` 改名或 `none` 关闭。
- 依赖声明只透传调用方显式声明（`Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces`），不做运行时探测、不自动补 `shlibs:`。
- **`.deb` 包级签名不做**——deb 生态签名在 apt 仓库侧（`Release`/`InRelease`），`dpkg-sig` 覆盖率极低；apt 仓库生成亦属分发管线非打包器职责。每产物附 `.sha256` 侧车。
- 压缩仅 `gzip`；xz/zstd 拒绝（netstandard2.0 无托管编码器；zstd 另需 dpkg≥1.21.18）。

## 2. 宿主门槛

- 打包工具无原生依赖，任意能跑 .NET 的宿主可产 `.deb`；后端 `netstandard2.0`；MSBuild=.NET SDK 全宿主；CLI=.NET 10 运行时或 NativeAOT 宿主。
- 产物面向 dpkg 系发行版（Debian/Ubuntu 及衍生）；EOL 发行版只记"可运行"不承诺支持。
- 验证工具（`lintian`/`desktop-file-validate`/`dpkg`/`apt`）只属本仓库验收链不属产物依赖，缺失时集成断言降级跳过——验证工具 ≠ 打包工具。

## 3. 语义契约

- **身份**：`Package:`（ProductName kebab 化，`BundlerDebPackageName` 覆盖）+ `Version:` + `Architecture:`（`amd64`/`arm64`，其他经 `BundlerDebArchitecture` 覆盖）；同 `Package` 名版本序列构成升级线，改名=新产品线（迁移关系由调用方 `Conflicts`/`Replaces`/`Provides` 声明）。
- **版本映射**：SemVer→`上游版本~预发布-修订号`（`1.0.0-alpha.1`→`1.0.0~alpha.1-1`，`+build` 保留 `+`）——上游原样透传会被 dpkg 读歪，本仓库显式收紧；`BundlerDebVersion`/`BundlerDebRelease`/`BundlerDebEpoch` 显式覆盖。
- **输入**：`InputDirectory` → `usr/lib/<package-name>/`；`MainExecutable` 须带可执行位（tar 0755）；映射冲突、路径逃逸打包前报错。
  链接契约与 rpm/apk/AppImage 统一（`PayloadSource`/`PayloadPathBook`）：输入树内符号链接保留为包内链接条目（目标重写为相对链接目录的形式，0777），外指或悬空链接打包前拒绝，目录链接不穿透递归；链接目标另须在包内可达——`includeDirectories=false` 的资源腿指向空目录等"主机存活、包内悬空"情形同口径拒绝。
- **安装**：`dpkg -i` 全量替换；升级=装更高 `Version`；降级原生放行（仅 warning、不需 `--force-downgrade`——`syspkg-verify` D3 相位实证）；`dpkg -r` 保留 conffiles、`-P` 全清。
- **所有权**：全部 `data.tar` 载荷归包所有，卸载按 dpkg 清单删除。
- **失败**：构建任一步失败清理半成品 `.deb` 与侧车。
- **确定性构建**：tar 条目排序、uid/gid 0、mtime 固定 1980-01-01 UTC——同输入字节级一致（1980 取值规避 lintian ancient-file）。
- **诚实边界**：不承诺"双击 GUI 安装观感"与 apt 仓库工作流。

## 4. 能力状态

已实现并验证：

- 产物与结构：`dpkg-deb -I`/`ar t`/`tar` 断言；`md5sums` 清单经 `md5sum -c` 真实核对；失败清理断言。
- control 字段全覆盖（`Package`/`Version`/`Architecture`/`Installed-Size`/`Maintainer`（默认 Publisher→Identifier 回退）/`Priority`（五值校验默认 optional）/`Section`/`Homepage`/`Description`）。
- 桌面集成：`.desktop` 生成落 `usr/share/applications/`（全字段+`MimeType`=`FileAssociations`∪`x-scheme-handler/<scheme>`），`desktop-file-validate` 双重断言；`BundlerDebDesktopFile` 整文件专家覆盖；hicolor 图标 PNG 尺寸探测+`@2x` 目录（非 PNG 报错）。
- AppStream metainfo：`BundlerDebMetainfoFile`→`usr/share/metainfo/<包名>.metainfo.xml`；落盘名=包名是固定契约（`validate-tree` 要求文件名==组件 id，rDNS id 与非 rDNS 包名只能满足其一——已记入 lintian 豁免，内容本体经 `appstreamcli validate --pedantic` 真实门禁）。
- `ChangelogFile`→`changelog.gz`、`LicenseFile`→`copyright`（`usr/share/doc/<包名>/`）；自动补发单行 `changelog.Debian.gz` 打包条目保字节确定性。
- `BundlerDebFile` 绝对路径自定义映射（拒相对/`..`/空段）。
- maintainer 脚本（`BundlerDeb{PreInstall,PostInstall,PreUninstall,PostUninstall}File` 整文件注入 0755，shebang+LF 校验，真实 `dpkg -i/-r` 标记断言）。
- systemd unit（`BundlerDebSystemdServiceFile`→`usr/lib/systemd/system/`，postinst 自动合成 `daemon-reload` 与调用方脚本合并，不自动 enable/start）；真实宿主 `daemon-reload`/`enable --now` 已验。
- conffiles：`BundlerDebConfigLocations` + `/etc` 下 `BundlerDebFile` 自动登记；真实 `-r` 保留/`-P` 清除/升级 `--force-confold` 断言。
- lintian 基线闸：`lintian-exemptions.txt` 硬断言新 tag 即失败；豁免 6 项——embedded-library、undeclared-elf-prerequisites（自包含载荷固有）、no-manual-page、changelog-not-compressed-with-max-compression（netstandard2.0 GZipStream 无最大档）、initial-upload-closes-no-bugs（非官方包固有）、appstream-metadata-validation-failed（文件名/id 契约边界）。
- 生命周期：`dpkg -i`/`-r`/`-P` 真实装卸、`dpkg -s`/`-L`、链接启动断言；同包 1.0.0→1.0.1 升级 + conffile 保留断言。
- 架构：`linux-aarch64` 产物结构与 `ubuntu-26.04-arm` 真机 `dpkg -i`/PATH/`-r`（`arm64-real-hw.sh`）实证。
- 多发行版矩阵：debian:stable/bookworm/bullseye + ubuntu:latest 容器真实装卸跑；debian:trixie lintian 闸腿；更广矩阵属 CI 扩版候选。
- 自更新走 installer-replay（UPDATE 系统包扩展，已实现）：客户端 ECDSA 验签 + `dpkg -s` 账本归属双检预检，经三级提权链拉起 `bundler-updater pkg-apply`，由引导件按 `apt-get install -y` → `dpkg -i`（`DEBIAN_FRONTEND=noninteractive --force-confdef --force-confold`）退化链装本地包、降权重启——发射/`pkg` 身份/双实现动词/客户端 replay 编排全落地，debian 容器真 root 全链经 `syspkg-verify` 实证。
  契约见 `docs/design/update.md` §2。

外部待验收（详见 `docs/project/special-acceptance.md`）：

- GUI 桌面观感收窄项：文件/协议关联**双击唤起**（菜单项/图标/点击启动已于 KDE 真桌面验证消解）。
- systemd unit 的 `status`/`journal` 观察与卸载后 unit 清理。
- UPDATE 图形提权入口链（pkexec→zenity/kdialog→`sudo -S` 桌面交互）、repack 拒绝与依赖变更场景。

## 5. 明确拒绝

- `.deb` 包级签名（dpkg-sig/debsigs）与 apt 仓库生成——见 §1。
- xz/zstd 压缩——见 §1。

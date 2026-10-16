# Linux `.rpm` 包（RPM 系）

本文件是 `.rpm` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project/project-rules.md`。

## 1. 状态与决策

- `.rpm` 已冻结（基线 `0.1.0-alpha.55`），仅缺陷修复附回归测试。
- **纯托管写入器，零原生工具**（延续 `.deb` 同策略）：`.rpm` = lead（96B）+ 签名 header + 主 header + cpio(newc) 载荷，由 `Bundler.Rpm` 用 C# 直写——任意宿主可产、无 GPL 义务、确定可复现；正确性由 `rpm -qip`/`-K`/`-i`、`rpmlint` 与自写 `RpmPackageReader` 双向互证背书。
- 安装面为 dnf/yum/zypper 系发行版；SUSE 专属宏与 `pattern`/`product` 等扩展不做，按通用 RPM 4 规范产出。
- 载荷布局与 deb 同构：`usr/lib/<package-name>/` + `usr/bin/<command>` 相对符号链接；`BundlerRpmInstallRoot`/`BundlerRpmBinLink` 覆盖。差异：rpm 要求**每个目录显式登记为包条目**（dir mode 0755）——按规范直写，不用上游的空文件 hack。
- 依赖透传：`Requires`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests`（`name [op evr]` 解析 `< <= = >= >`，拒 `!=`）；`AutoReqProv` 显式置 0；`rpmlib(...)` 自依赖按实际特性登记。
- 压缩恒 gzip 无旋钮（netstandard2.0 无托管编码器）；`PAYLOADCOMPRESSOR`/`PAYLOADFLAGS` 如实填写。
- 确定性：`BUILDTIME`/`FILEMTIMES` 固定 1980-01-01 UTC + 条目排序，同输入字节级一致。

## 2. 宿主门槛

- 任意能跑 .NET 的宿主可产 `.rpm`；后端 `netstandard2.0`；MSBuild/CLI 同其他后端口径。
- 验证工具（`rpm`/`rpmlint`/容器内 `dnf`）仅验收链使用，缺失降级 SKIP。
- 产物面向 RPM 系发行版；发行版宏差异写实记录，不作全版本承诺。

## 3. 语义契约

- **身份**：`NAME`/`VERSION`/`RELEASE`/`EPOCH` 等 header tag；`License`(SPDX)/`Vendor`/`Group`/`URL`/`Summary`/`Description` 透传或覆盖（置空省略 tag）。
- **版本映射**：rpm `Version` 禁含 `-`；SemVer→`Version=x.y.z Release=1`（正式）/`Release=0.1.<prerelease>`（预发布，Fedora 惯例保 `<1` 排序）；`+build` 进 Release 后缀；`BundlerRpmVersion`/`Release`/`Epoch` 显式覆盖。
- **文件摘要**：`FILEDIGESTS` sha256（`FILEDIGESTALGO=8`），容器 `rpm -V` 实测。
- **conffile**：`BundlerRpmConfigFile` 与 `/etc` 下 `RpmFile` 自动标 `%config(noreplace)`（`FILEFLAGS` 17）；`rpm -e` 已修改者存 `.rpmsave`，`rpm -U` 原地保留无 `.rpmnew`——与 deb `-r` 语义对应。
- **scriptlet**：`BundlerRpm{PreInstall,PostInstall,PreRemove,PostRemove}File`→`PREIN`/`POSTIN`/`PREUN`/`POSTUN`+`*PROG` 解释器（LF 强制、`#!` 剥离为 PROG、缺省 `/bin/sh`）。
- **systemd**：`BundlerRpmSystemdServiceFile`→`usr/lib/systemd/system/` 0644 装而不启；`%post`/`%postun` 自动合成 `daemon-reload || true` 并入调用方 scriptlet。
- **桌面集成**：完全复用 `Bundler.Core` 共享 `FreedesktopFiles` 生成器（`.desktop` 全字段/`BundlerRpmDesktopFile` 覆盖/hicolor `@2x`/metainfo/`usr/share/doc`+`changelog.gz` 标 %doc/`BundlerLicenseFile`→`/usr/share/licenses/<pkg>/` 标 %license）；包内绝对路径映射经 `@BundlerRpmFile Destination`（逃逸校验；新父目录占有、`/etc` 等共享根不占有）。
- **命名**：`<name>-<version>-<release>.<arch>.rpm`（`x86_64`/`aarch64` 等）；`.sha256` 侧车每产物一份。
- **多格式扇出**：`BundlerFormats=deb;rpm` 同次 publish 双产物；MSBuild `-p:` 分号须 `%3B` 转义。

## 4. 能力状态

已实现并验证：

- 托管写入器全链（`rpm -qip` 识别 + Reader 互证 + 容器 `rpm -i/-e`）。
- 版本映射（`2.0.0-beta.1`→`2.0.0-0.1.beta.1` 实测）；确定性构建；`.sha256` 侧车；`FILEDIGESTS`。
- 元数据/桌面集成全项；scriptlet 四旋钮容器标记断言；systemd 落位+合成；conffile `.rpmsave`/升级保留断言。
- `rpm -U` Release 1→2 升级实测。
- **包级 GPG 签名已实现**（`BundlerRpmSigningKeyFile`/`Passphrase`，BouncyCastle OpenPGP v3 纯托管，嵌 `RPMSIGTAG_RSA`(268)+`RPMSIGTAG_PGP`(1002) 双标签同 rpmsign 形态；不供密钥则产物与未签名构建逐字节一致；隔离 rpmdb `rpm --import`+`rpm -K` 实测 `digests signatures OK`；zypper 认 RSA 标签）。注意：签名断言类整体 Linux 门控——托管签名实现宿主无关，win/mac 宿主产出未单独成腿（风险低，按需再设）。
- `rpmlint` 基线：`rpmlint-exemptions.txt` 7 项豁免硬断言，新 tag 即失败。
- 容器矩阵：fedora:latest、rockylinux:9、opensuse/leap:latest、almalinux:8、rockylinux:8 五镜像真实 `rpm -i/-q/-ql`/运行/`-V`/`-e` 零残留。
- `linux-aarch64`：`ubuntu-26.04-arm` 真机腿 `arm64-real-hw.sh --rpm`（`rpm -i`/`-ql`/`-e`）实证。
- `deb;rpm` 同次 publish 扇出断言。
- 自更新走 installer-replay（UPDATE 系统包扩展，已实现）：客户端 ECDSA 验签 + `rpm -q` 账本归属双检预检，经三级提权链拉起 `bundler-updater pkg-apply`，由引导件按 `dnf install -y` → `zypper install` → `rpm -U` 退化链装本地包、降权重启。
  回滚重放加 `--oldpackage`——发射/`pkg` 身份/双实现动词/客户端 replay 编排全落地，fedora 容器真 root 全链（含 dnf5 `install` 原地降级）经 `syspkg-verify` 实证。
  契约见 `docs/design/update.md` §2。

外部待验收（详见 `docs/project/special-acceptance.md`）：

- GUI 桌面观感收窄项：文件/协议关联双击唤起。
- 更广发行版宿主矩阵（RHEL8 旧版等）属 CI 扩版候选；生产 GPG 密钥分发/吊销属发布者流程（`gpg-production.sh` 临时密钥全链已收编入 verify ubuntu 腿）。
- UPDATE 图形提权入口链（pkexec→zenity/kdialog→`sudo -S` 桌面交互）、`zypper install` 非交互降级形态、repack 拒绝与依赖变更场景。

## 5. 明确拒绝

- dnf/zypper 仓库生成——分发管线非打包器职责。
- xz/zstd 载荷压缩；SUSE 专属宏。

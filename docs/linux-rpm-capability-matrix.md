# Linux `.rpm` 能力矩阵（LINUX-RPM）

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 每行记录完成条件与边界；本矩阵随 `LINUX-RPM-1..5` 推进逐行更新，已于 LINUX-RPM-5 定稿（冻结基线 `0.1.0-alpha.55`）。

## 产物与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 托管 RPM 写入器（lead+签名 header+主 header+cpio+gzip） | 已实现 | LINUX-RPM-1 | 纯托管、任意宿主可产；`rpm -qip` 识别 + `RpmPackageReader` 双向互证 + fedora 容器 `rpm -i/-e` 全绿 |
| `usr/lib/<pkg>/` + `usr/bin` 符号链接布局 | 已实现 | LINUX-RPM-1 | 目录显式登记为包条目（dir mode 0755）；`BundlerRpmInstallRoot`/`BinLink` 覆盖，`none` 关闭；非 `/usr` 根下链接转绝对路径 |
| SemVer→rpm 版本映射（预发布段→Release `0.x`） | 已实现 | LINUX-RPM-1 | Fedora 惯例映射 + `BundlerRpmVersion`/`Release`/`Epoch` 显式覆盖；`2.0.0-beta.1`→`2.0.0-0.1.beta.1` 实测断言 |
| 确定性构建 | 已实现 | LINUX-RPM-1 | 固定 BUILDTIME/FILEMTIMES=1980-01-01 UTC/条目排序，字节级一致性断言 |
| `.sha256` 侧车 | 已实现 | LINUX-RPM-1 | 每产物一份，`sha256sum -c` 断言 |
| 文件 sha256 摘要（`FILEDIGESTS`） | 已实现 | LINUX-RPM-1 | `FILEDIGESTALGO=8`；fedora 容器 `rpm -V` 通过 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `Requires`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests` 透传 | 已实现 | LINUX-RPM-2 | `BundlerRpm*` 分号列表 → `name [op evr]` 解析（`< <= = >= >`，拒 `!=`）写入三件套 tag；`rpmlib(...)` 自依赖保留；不探测运行时依赖 |
| `License`(SPDX)/`Vendor`/`Group`/`URL`/`Summary`/`Description` | 已实现 | LINUX-RPM-2 | `BundlerRpmLicense`/`Group`/`Url` 覆盖（`Group`/`Url` 置空省略 tag）；包级 `BundlerLicenseFile` 按 rpm 原生惯例落 `/usr/share/licenses/<pkg>/` 并标 %license（128） |
| `.desktop` 生成与 `usr/share/applications/` 落位 | 已实现 | LINUX-RPM-2 | `Bundler.Core` 共享 `FreedesktopFiles` 生成器（deb 行为不变）；`BundlerRpmDesktopFile` 整文件覆盖；`desktop-file-validate` 断言 |
| hicolor 图标 / AppStream metainfo / `usr/share/doc` | 已实现 | LINUX-RPM-2 | PNG 尺寸探测 + `@2x`→`48x48@2` 目录；metainfo 落 `/usr/share/metainfo/`；`BundlerRpmChangelogFile`→`changelog.gz` 标 %doc；hicolor 子树不占有（发行版惯例） |
| 包内绝对路径自定义映射 | 已实现 | LINUX-RPM-2 | `@BundlerRpmFile` `Destination` 元数据；逃逸校验（拒相对/`..`/空段）；新非共享父目录占有、`/etc` 等共享根不占有 |

## 脚本、服务与压缩

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| scriptlet（PREIN/POSTIN/PREUN/POSTUN） | 已实现 | LINUX-RPM-3 | `BundlerRpm*InstallFile`/`*UninstallFile` 四旋钮 + `*Program` 解释器覆盖；LF 强制、`#!` 剥离为 PROG、缺省 `/bin/sh`；容器 `rpm -i/-e` 标记断言全绿 |
| systemd unit | 已实现 | LINUX-RPM-3 | `usr/lib/systemd/system/<pkg>.service` 落位 + `%post`/`%postun` `daemon-reload` 自动合成（并入调用方 scriptlet），装而不启 |
| conffile（`%config(noreplace)`） | 已实现 | LINUX-RPM-3 | `/etc` 下 `RpmFile` 目标自动 FILEFLAGS 17 + `BundlerRpmConfigFiles` 显式列表（须命中载荷文件）；`rpm -e` `.rpmsave` + `rpm -U` 原地保留断言 |
| 压缩选项 | 已实现（gzip）/登记拒绝（xz/zstd） | LINUX-RPM-3 | `BundlerRpmCompression` 仅收 `gzip`，其余拒绝；同 deb 立场：netstandard2.0 无托管编码器 |
| 升级/卸载语义实测 | 已实现 | LINUX-RPM-3 | 容器内 `rpm -U` Release 1→2 升级 + 修改后 %config 原地保留（无 .rpmnew）+ `rpm -e` `.rpmsave` 断言全绿 |

## 签名与分发

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 包级 GPG 签名 | 已实现 | `SIGN-1`（`signing-development`，`0.1.0-alpha.62+`） | 可选能力：供 `BundlerRpmSigningKeyFile`/`Passphrase` 即嵌双标签 `RPMSIGTAG_RSA`(268)+`RPMSIGTAG_PGP`(1002)（BouncyCastle OpenPGP v3 包，纯托管，同 rpmsign 形态）；不供则产物与未签名构建逐字节一致；隔离 rpmdb `rpm --import`+`rpm -K` 实测 `digests signatures OK`；zypper 认 RSA 标签（PGP-only 曾被报 unsigned） |
| dnf/zypper 仓库生成 | 明确拒绝 | — | 仓库管理属分发管线而非打包器 |

## 生命周期与宿主矩阵

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 容器真实装卸（fedora/rockylinux/opensuse） | 已实现 | LINUX-RPM-1/4 | `fedora:latest`+`rockylinux:9`+`opensuse/leap:latest` 三容器真实 `rpm -i`/`rpm -q`/`rpm -ql`/运行/`rpm -V`/`rpm -e` 零残留全绿；镜像不可拉取记 SKIP |
| `rpmlint` 基线 | 已实现 | LINUX-RPM-4 | `rpmlint-exemptions.txt` 豁免清单硬断言；7 项豁免入档，新 tag 即失败 |
| `linux-arm64` 产物 | 已实现（结构+运行） | LINUX-RPM-4 | `*.aarch64.rpm`+`ARCH=aarch64`+载荷结构断言全绿；2026-10 `ubuntu-24.04-arm` CI 真机装卸跑绿（`arm64-real-hw.sh`，OI-01 剩观感人工） |
| 多格式扇出（`deb;rpm` 同次 publish） | 已实现 | LINUX-RPM-1 | `BundlerFormats=deb;rpm` 同次 publish 双产物断言；MSBuild `-p:` 分号须 `%3B` 转义 |

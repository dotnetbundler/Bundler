# Linux `.rpm` 能力矩阵（LINUX-RPM）

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 每行记录完成条件与边界；本矩阵随 `LINUX-RPM-1..5` 推进逐行更新，冻结时定稿。

## 产物与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 托管 RPM 写入器（lead+签名 header+主 header+cpio+gzip） | 计划实现 | LINUX-RPM-1 | 纯托管、任意宿主可产；`rpm -qip`/`rpm -K` 识别 + `RpmPackageReader` 双向互证 |
| `usr/lib/<pkg>/` + `usr/bin` 符号链接布局 | 计划实现 | LINUX-RPM-1 | 目录显式登记为包条目（dir mode 0755）；`BundlerRpmInstallRoot`/`BinLink` 覆盖 |
| SemVer→rpm 版本映射（预发布段→Release `0.x`） | 计划实现 | LINUX-RPM-1 | Fedora 惯例映射 + `BundlerRpmVersion`/`Release`/`Epoch` 显式覆盖 |
| 确定性构建 | 计划实现 | LINUX-RPM-1 | 固定 BUILDTIME/FILEMTIMES/条目排序，同输入字节级一致 |
| `.sha256` 侧车 | 计划实现 | LINUX-RPM-1 | 每产物一份 |
| 文件 sha256 摘要（`FILEDIGESTS`） | 计划实现 | LINUX-RPM-1 | `FILEDIGESTALGO=8`；`rpm -V` 可校验 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `Requires`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests` 透传 | 计划实现 | LINUX-RPM-2 | 显式声明透传，`rpmlib(...)` 自依赖按特性集如实写；不探测运行时依赖 |
| `License`(SPDX)/`Vendor`/`Group`/`URL`/`Summary`/`Description` | 计划实现 | LINUX-RPM-2 | `BundlerRpmLicense` 字符串旋钮；`LicenseFile` 仍落 doc 目录 |
| `.desktop` 生成与 `usr/share/applications/` 落位 | 计划实现 | LINUX-RPM-2 | 与 deb 同一生成器（共享 freedesktop 件），`desktop-file-validate` 断言 |
| hicolor 图标 / AppStream metainfo / `usr/share/doc` | 计划实现 | LINUX-RPM-2 | 同 deb 行 |
| 包内绝对路径自定义映射 | 计划实现 | LINUX-RPM-2 | `@BundlerRpmFile` `Destination` 元数据 |

## 脚本、服务与压缩

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| scriptlet（PREIN/POSTIN/PREUN/POSTUN） | 计划实现 | LINUX-RPM-3 | 专家旋钮整文件注入 + `…PROG` 解释器；容器内真实执行断言 |
| systemd unit | 计划实现 | LINUX-RPM-3 | `usr/lib/systemd/system/` 落位 + post/postun `daemon-reload` 合成，装而不启 |
| conffile（`%config(noreplace)`） | 计划实现 | LINUX-RPM-3 | `/etc` 自动标记 + 显式列表；`rpm -e` `.rpmsave` 语义断言 |
| 压缩选项 | 计划实现（gzip）/登记拒绝（xz/zstd） | LINUX-RPM-3 | 同 deb 立场：netstandard2.0 无托管编码器 |
| 升级/卸载语义实测 | 计划实现 | LINUX-RPM-3 | `rpm -U` 同包升级、`rpm -e` conffile 保留断言 |

## 签名与分发

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 包级 GPG 签名 | 计划实现（后置评估） | LINUX-RPM-3 后独立阶段 | rpm 生态真实惯例；签名 header 结构与密钥引用另行裁决，默认仅 `sha256` 侧车 |
| dnf/zypper 仓库生成 | 明确拒绝 | — | 仓库管理属分发管线而非打包器 |

## 生命周期与宿主矩阵

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 容器真实装卸（fedora/rockylinux/opensuse） | 计划实现 | LINUX-RPM-1/4 | docker 容器内 `rpm -i/-e/-U` 断言 |
| `rpmlint` 基线 | 计划实现 | LINUX-RPM-4 | 豁免清单硬断言（同 deb 机制） |
| `linux-arm64` 产物 | 计划实现（结构）/外部待验收（运行） | LINUX-RPM-4 | 结构断言本机可做；arm64 真实安装属 OI |
| 多格式扇出（`deb;rpm` 同次 publish） | 计划实现 | LINUX-RPM-1 | deb 决策 11 遗留项随本阶段落地 |

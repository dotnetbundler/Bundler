# ARCHIVE 格式路线（`.zip` / `.tar.gz`）

> 状态：规划轮完成，待逐条裁决；实施尚未开始。
> 范围依据：`docs/roadmap.md` ARCHIVE 节——压缩包后端（zip / tar.gz 等归档分发形式），跨 Windows/macOS/Linux 通用。
> 上游参照：Tauri 不产生归档格式（审计已覆盖 deb/rpm/appimage/dmg），本格式无上游可直接对照，决策按本仓库既有立场推导。

## 1. 决策清单（待裁决，推荐项已标出）

| # | 决策点 | 候选 | 推荐与理由 |
| --- | --- | --- | --- |
| 1 | 归档类型范围 | zip only / zip+tar.gz / zip+tar.gz+tar.zst | **zip + tar.gz**——覆盖 Windows（zip 惯例）与 Unix（tar.gz 惯例）两个主流分发形态；tar.xz/tar.zst/7z 登记明确拒绝（编码器依赖/普及度），需要时另起决策 |
| 2 | 工具供应 | 系统 `zip`/`tar` / 托管写入器 | **纯托管写入器**，同 deb/rpm 立场——任意宿主可产、零外部进程；`tar` 复用 Bundler.Deb 已验证的 tar/gzip 写法（提取共享件到 Bundler.Core），`zip` 自实现 local header + central directory（`System.IO.Compression` 的 ZipArchive 不支持 unix mode/符号链接写出，不够用） |
| 3 | 归档内布局 | 载荷平铺根目录 / 单顶层目录 / usr 树 | **单顶层目录** `<pkg>-<ver>-<rid>/` 内含完整 publish 载荷——归档分发惯例（解压不成"散弹"）；不引入 usr/lib 语义（归档不走路径约定） |
| 4 | 可执行位保留 | 不保留 / tar mode+zip unix attrs | **保留**——tar 条目写 mode；zip 写 `version made by=Unix` + external attrs（Info-ZIP 惯例，`unzip`/`zipinfo` 可还原）；Windows 载荷无 exec 位需求但写入无害 |
| 5 | 符号链接 | 展平复制 / tar 保留、zip 展平 / 双保留 | **双保留**——tar 原生 symlink 条目；zip 按 Info-ZIP 惯例（mode 0100777 + 数据为 link target）。publish 载荷常见 `lib*.so` 链接，展平会膨胀且改语义 |
| 6 | 校验和 | 无 / .sha256 侧车 | **`.sha256` 侧车**——与 deb/rpm/appimage 一致 |
| 7 | 签名 | 无 / 内嵌签名 | **明确拒绝**——zip 无通行签名惯例；归档定位是"搬运分发"，签名诉求应在具体平台格式层解决 |
| 8 | 旋钮面 | 最小（名称/版本）/ 对齐 freedesktop 族 | **最小集**：`PackageName`/`Version`/`ArchiveName` 模板、`@(BundlerArchiveFile)` 归档相对路径映射（与 `BundlerAppImageFile` 同口径校验）；freedesktop/desktop 件不属于归档职责，不引 |
| 9 | 矩阵归属 | 每 OS 各开 / 全 OS 通用 | **全 OS 通用**——`PackageFormat.Zip`/`TarGz` 对 Windows/macOS/Linux 目标均可用（DesktopTargetMatrix 放开）；典型用法 win→zip、linux/mac→tar.gz（用户自选） |
| 10 | 压缩级别 | 固定 / 可选 | **固定**（tar.gz 用 gzip level 6 同 deb 口径；zip 用 deflate level 6）——压缩级别旋钮无用户价值，登记拒绝 |
| 11 | 真实验证 | unzip/tar 断言 / 仅结构 | **真实解包**——`unzip`/`zipinfo`/`tar -xzf` 逐路径断言 + 解出载荷真实运行 + mode/symlink 还原断言（`zipinfo -l`、`tar -tvf`、`test -x`）；win-x64 归档在 Windows 宿主无本机断言环境 → 记 MT |
| 12 | 阶段骨架 | — | `ARCHIVE-1` 双写入器+最小可用+扇出 → `ARCHIVE-2` 旋钮面收口（文件映射等） → `ARCHIVE-3` 矩阵/审计/冻结 |

## 2. 语义对应表（供实现期对照）

| deb/rpm/appimage 概念 | 归档对应 | 说明 |
| --- | --- | --- |
| `PackageFormat.Deb` 等 | `PackageFormat.Zip`/`TarGz` | 枚举新增两值 |
| AppDir | 归档顶层目录 `<pkg>-<ver>-<rid>/` | 单一根，不进 usr 语义 |
| `BundlerAppImageFile` | `@(BundlerArchiveFile)` | 归档相对 POSIX 目标，校验口径一致 |
| scriptlet/conffile/关系字段 | 不适用 | 无包管理器 |
| `.sha256` 侧车 | 同名 `.zip.sha256`/`.tar.gz.sha256` | 一致 |
| e_machine 断言 | 不适用 | 归档无 ELF 头，载荷断言照旧 |

## 3. 阶段表（同既有格式节奏）

| 阶段 | 范围 | 退出条件 |
| --- | --- | --- |
| `ARCHIVE-1` | `src/Bundler.Archive` 托管 zip/tar.gz 写入器 + `ArchiveBundleConfiguration` + MSBuild `BundlerFormats=zip;targz` 接线 + 单元测试 + `tests/Archive.Integration/Verify.sh` + `samples/HelloArchiveApp` | zipinfo/tar 真实解包逐路径断言、解出载荷运行、mode/symlink 还原断言、`deb;rpm;appimage;zip;targz` 扇出 |
| `ARCHIVE-2` | `@(BundlerArchiveFile)` 映射与拒绝路径、旋钮收口 | 映射落位/碰撞/逃逸拒绝断言全绿 |
| `ARCHIVE-3` | 宿主矩阵收口（多 OS 归档互读：Linux 产 zip 在 Windows 解等，可到面则记 MT）、干净宿主审计、能力矩阵定稿、冻结基线写入 | 冻结文档与 OI/MT 清单完备 |

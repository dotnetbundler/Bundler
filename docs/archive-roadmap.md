# ARCHIVE 格式路线（`.zip` / `.tar.gz`）

> 状态：**`.zip`/`.tar.gz` 归档格式已冻结于 `0.1.0-alpha.59`**——ARCHIVE-1..3 全部完成；冻结基线与收口明细见 §4 ARCHIVE-3 节。
> 范围依据：`docs/roadmap.md` ARCHIVE 节——压缩包后端（zip / tar.gz 等归档分发形式），跨 Windows/macOS/Linux 通用。
> 上游参照：Tauri 不产生归档格式（审计已覆盖 deb/rpm/appimage/dmg），本格式无上游可直接对照，决策按本仓库既有立场推导。

## 1. 决策清单（已裁决——按推荐项放行）

| # | 决策点 | 候选 | 推荐与理由 |
| --- | --- | --- | --- |
| 1 | 归档类型范围 | zip only / zip+tar.gz / zip+tar.gz+tar.zst | **zip + tar.gz**——覆盖 Windows（zip 惯例）与 Unix（tar.gz 惯例）两个主流分发形态；tar.xz/tar.zst/7z 登记明确拒绝（编码器依赖/普及度），需要时另起决策 |
| 2 | 工具供应 | 系统 `zip`/`tar` / 托管写入器 | **纯托管写入器**，同 deb/rpm 立场——任意宿主可产、零外部进程；`tar` 复用 Bundler.Deb 已验证的 tar/gzip 写法（提取共享件到 Bundler.Core），`zip` 自实现 local header + central directory（`System.IO.Compression` 的 ZipArchive 不支持 unix mode/符号链接写出，不够用） |
| 3 | 归档内布局 | 载荷平铺根目录 / 单顶层目录 / usr 树 | **单顶层目录** `<pkg>-<ver>-<target>/` 内含完整 publish 载荷——归档分发惯例（解压不成"散弹"）；不引入 usr/lib 语义（归档不走路径约定） |
| 4 | 可执行位保留 | 不保留 / tar mode+zip unix attrs | **保留**——tar 条目写 mode；zip 写 `version made by=Unix` + external attrs（Info-ZIP 惯例，`unzip`/`zipinfo` 可还原）；Windows 载荷无 exec 位需求但写入无害 |
| 5 | 符号链接 | 展平复制 / tar 保留、zip 展平 / 双保留 | **双保留**——tar 原生 symlink 条目；zip 按 Info-ZIP 惯例（mode 0100777 + 数据为 link target）。publish 载荷常见 `lib*.so` 链接，展平会膨胀且改语义 |
| 6 | 校验和 | 无 / .sha256 侧车 | **`.sha256` 侧车**——与 deb/rpm/appimage 一致 |
| 7 | 签名 | 无 / 内嵌签名 | **明确拒绝**——zip 无通行签名惯例；归档定位是"搬运分发"，签名诉求应在具体平台格式层解决 |
| 8 | 旋钮面 | 最小（名称/版本）/ 对齐 freedesktop 族 | **最小集**：`PackageName`/`Version`/`ArchiveName` 模板、`@(BundlerArchiveFile)` 归档相对路径映射（与 `BundlerAppImageFile` 同口径校验）；freedesktop/desktop 件不属于归档职责，不引 |
| 9 | 矩阵归属 | 每 OS 各开 / 全 OS 通用 | **全 OS 通用**——`PackageFormat.Zip`/`TarGz` 对 Windows/macOS/Linux 目标均可用（DesktopTargetMatrix 放开）；典型用法 win→zip、linux/mac→tar.gz（用户自选） |
| 10 | 压缩级别 | 固定 / 可选 | **固定**（tar.gz 用 gzip level 6 同 deb 口径；zip 用 deflate level 6）——压缩级别旋钮无用户价值，登记拒绝 |
| 11 | 真实验证 | unzip/tar 断言 / 仅结构 | **真实解包**——`unzip`/`zipinfo`/`tar -xzf` 逐路径断言 + 解出载荷真实运行 + mode/symlink 还原断言（`zipinfo -l`、`tar -tvf`、`test -x`）；windows-x86_64 归档在 Windows 宿主无本机断言环境 → 记 MT |
| 12 | 阶段骨架 | — | `ARCHIVE-1` 双写入器+最小可用+扇出 → `ARCHIVE-2` 旋钮面收口（文件映射等） → `ARCHIVE-3` 矩阵/审计/冻结 |

## 2. 语义对应表（供实现期对照）

| deb/rpm/appimage 概念 | 归档对应 | 说明 |
| --- | --- | --- |
| `PackageFormat.Deb` 等 | `PackageFormat.Zip`/`TarGz` | 枚举新增两值 |
| AppDir | 归档顶层目录 `<pkg>-<ver>-<target>/` | 单一根，不进 usr 语义 |
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

## 4. 阶段实施证据

### ARCHIVE-1（`0.1.0-alpha.59`，分支 `archive-development`）

- `src/Bundler.Archive`：`ArchiveBundler` 门面（3 OS × zip/targz 六个后端实例）+ `ArchiveBundleBackend` + `ArchiveTree` 载荷收集 + `ArchiveIdentity` 命名/包名校验。
- zip 写入器自实现（`System.IO.Compression` 无法写 unix mode/symlink）：local+central directory、`version made by=Unix`、external attrs 高字 unix mode（S_IFREG|S_IFDIR|S_IFLNK）、deflate 带 stored 回落、CRC32 自实现；Zip64 显式拒绝。
- tar.gz 复用 `Bundler.Deb` tar 写法提取到 `Bundler.Core` 的 `Archives/TarWriter`（ustar、固定 mtime 1980、`./` 前缀惯例、长名 prefix 拆分）；`Bundler.Deb` 行为不变由既有 141+ 断言背书。
- mode 语义：Linux/macOS 用 libc `access(X_OK)` 判真实执行位，其他宿主回落 ELF/shebang 魔数探测；符号链接经 `ReparsePoint`+libc `readlink` 保留目标。
- MSBuild：`BundlerFormats=zip;targz`、`BundlerArchivePackageName`/`Version`/`ArchiveName` 三旋钮、`@(BundlerArchiveFile)` 归档相对映射（拒绝对路径/`..`/`.`/空段/反斜杠/碰撞）；MSBuild 包装载 `DotNet.Bundler.Archive.dll`。
- `PackageFormat` 增 `Zip`/`TarGz`；`DesktopTargetMatrix` 三 OS 全放开；`BundlePlanner` 输出 `zip/`、`targz/` 目录。
- 单元测试：`Bundler.Tests` 新增 8 项（结构/mode/symlink/映射/拒绝/扇出/MSBuild 接线），合计 181/181 全绿。
- `tests/Archive.Integration/Verify.sh` 全绿：`unzip -l`/`zipinfo -l`/`tar -tvf` 清单与 mode 断言、真实解包逐路径+载荷运行+执行位+符号链接还原断言、覆盖变体、映射与非法目标失败变体（不留半成品）、`deb;rpm;appimage;zip;targz` 单 publish 扇出、`windows-x86_64`/`macos-arm64` 交叉目标 zip。
- `samples/HelloArchiveApp`：publish 产出 zip+tar.gz 并解出运行实测通过。

### ARCHIVE-2（`0.1.0-alpha.59`，分支 `archive-development`）

- 旋钮面对照决策 8 复核：`PackageName`/`Version`/`ArchiveName` + `@(BundlerArchiveFile)` 最小集齐备，无缺口；
  freedesktop/desktop 件确认不引（归档无安装语义）。
- `BundlerArchiveFile` 校验口径复核：与 `BundlerAppImageFile` 对齐（尾部斜杠归一化、反斜杠/绝对路径/空段/`.`/`..` 段/载荷碰撞拒绝，目录源拒绝），段校验更严属超集。
- 收口断言补强：确定性构建（两次构建 sha256 逐字节一致）、目录源拒绝、Zip64 拒绝（>65535 条目触发显式 throw）；`Bundler.Tests` 合计 184/184 全绿。
- `tests/Archive.Integration/Verify.sh` 复跑全绿（映射落位/逃逸拒绝/失败不留半成品断言在 ARCHIVE-1 已就位）。

### ARCHIVE-3（`0.1.0-alpha.59`，分支 `archive-development`）

- 宿主矩阵收口：本机 Linux 宿主实测到面；跨 OS 互读（Linux 产归档在 Windows/macOS 解出）本机无宿主，登记 ARCHIVE-MT-01..04/ARCHIVE-OI-01..04，未冒充。
- 跨实现互读补强：`Verify.sh` 新增 python3 `zipfile`/`tarfile` 段——第三方实现（非 .NET/Info-ZIP/GNU tar）读 zip（条目存在、external attrs 执行位、symlink 内容）与 tar.gz（条目、symlink 类型与目标）断言全绿。
- 干净宿主审计：`grep` 全量复核 `src/Bundler.Archive`——零 `System.Diagnostics.Process` 出口，外部调用面仅 `UnixLinks.cs` 两处 libc P/Invoke（`readlink`/`access`）；与决策 2"纯托管写入器、零外部进程"一致。

- 能力矩阵定稿：十二行定稿——十行已实现/明确拒绝，"跨 OS 互读实测"维持外部待验收（对应 OI-01..04），新增"第三方实现互读"已实现行；矩阵内无悬空"计划实现"行。
- OI/MT 收口：ARCHIVE-OI-01..05 逐条复核维持登记；ARCHIVE-MT-01..05 人工验收清单全量保留。
- 冻结基线：`ArchiveBundleConfiguration`/`ArchiveBundler` 公共配置面与行为契约冻结于 `0.1.0-alpha.59`；冻结测试向量 = `Bundler.Tests` 184/184 全绿 + `tests/Archive.Integration/Verify.sh` 全绿（unzip/zipinfo/tar 解包断言、载荷运行、mode/symlink 还原、python3 互读、映射与失败变体、五格式扇出、win/mac 交叉 zip）。冻结后仅接受带回归测试的缺陷修复。

### 2026-10-05 zip 流式写出 + Zip64 动态升级（`0.1.0-alpha.72`，分支 `devin/*-zip-streaming`/`devin/*-zip64`）

- 流式 zip：`ZipEntry.OpenContent`（`Func<Stream>`，契约同 `TarEntry`）——占位本地头、Deflate 分块写、增量 CRC-32、写后回填三字段（可寻址输出流前提），产物字节与预知长度写法同构；stored 回落仅留缓冲路径；`ToZipEntry` 文件来源改 `FileStream` 工厂。
- Zip64：按 BCL `ZipArchive` 式动态升级——单条尺寸/压缩长/本地偏移顶 0xFFFFFFFF 或条目数顶 0xFFFF 的字段写哨兵、真值进 Zip64 extra/EOCD64+locator，version needed 升 45；未顶限归档字节与经典布局完全一致；流式条目按声明长 2MiB 安全带预升级兜底（覆盖 deflate 最坏膨胀）。
- 验证：`ArchiveTests` +4 用例——4.3GiB 稀疏文件实写+`ZipArchive` 全量读回 CRC、流式与内联逐字节一致、安全带预升级头形态、65536 条目 EOCD64；CLI 实产 4.3GiB zip64 包经 Info-ZIP `unzip -t` 全量解压 CRC 与 Python `zipfile` 通过，win 宿主 `Expand-Archive`/`tar.exe` 实证见会话记录。

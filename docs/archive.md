# 归档包（`.zip` / `.tar.gz`）

本文件是归档后端的唯一文档：决策、语义契约、能力状态。
归档定位是"搬运分发"：无包管理器、无安装事务、无平台语义；跨 Windows/macOS/Linux 全目标通用（`PackageFormat.Zip`/`TarGz`，典型 win→zip、linux/mac→tar.gz）。

## 1. 决策

- 范围只 **zip + tar.gz**；tar.xz/tar.zst/7z 明确拒绝（编码器依赖/普及度，需要时另起决策）。
- **纯托管写入器**：`tar` 复用 Bundler.Deb 验证过的写法（共享件在 `Bundler.Core`）；`zip` 自实现 local header+central directory（`ZipArchive` 不支持 unix mode/符号链接写出）。
- 布局：**单顶层目录** `<pkg>-<ver>-<target>/` 含完整 publish 载荷（解压不成"散弹"）；不引入 usr 语义。
- **可执行位保留**：tar 写 mode；zip 写 `version made by=Unix`+external attrs（Info-ZIP 惯例）；文件头魔数探针判定（ELF/shebang/Mach-O→0755，其余 0644）。
- **符号链接保留**：tar 原生 symlink 条目；zip 按 Info-ZIP 惯例（mode 0100777+数据为链接目标）——publish 载荷常见 `lib*.so` 链接，展平会改语义。
- `.sha256` 侧车与其他格式一致；压缩级别固定（gzip/deflate level 6）不暴露旋钮。
- 旋钮最小集：`PackageName`/`Version`/`ArchiveName` 模板、`@(BundlerArchiveFile)` 归档相对路径映射（逃逸/碰撞校验同 AppDir 口径）；freedesktop/desktop 件不属归档职责。
- **签名明确拒绝**：zip 无通行签名惯例，签名诉求在平台格式层解决。

## 2. 能力状态

已实现并验证：

- 双写入器 + 单顶层目录 + exec 位 + symlink + `.sha256` 侧车全链。
- 全目标覆盖：windows-i686/x86_64/aarch64、macos-universal/x86_64/arm64、linux-x86_64/aarch64、linux-musl-x86_64/aarch64（无架构门禁）；`macos-universal` 通用载荷契约见 `docs/mac-app.md`。
- 多格式扇出（`BundlerFormats=zip;targz` 及与其他格式混排）。
- 确定性：同输入同条件逐字节一致（跨轮 + linux↔qemu 跨宿主 16/16 对）；跨 OS 差异归因载荷 publish 元数据与宿主 mode 表达，非写入器缺陷。
- 跨 OS 互读：win/linux/mac/qemu 四宿主互产互解（解压、exec 位/symlink 还原、载荷运行）；第三方互读（python `zipfile`/`tarfile` 断言）。
- zip 流式载荷（`ZipEntry.OpenContent`：占位本地头+回填+增量 CRC-32；单条目超 ~2GiB 内存顶；流式恒 deflate）。
- Zip64 动态升级（>4GiB 条目/≥65535 条目/超 4GiB 偏移；BCL 式哨兵+EOCD64/locator；未顶限字节不变；`unzip -t`/`ZipArchive`/`Expand-Archive` 实证）。

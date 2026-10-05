# ARCHIVE 外部待办清单（ARCHIVE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `ARCHIVE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| ARCHIVE-OI-01 | ARCHIVE-3 | ~~Windows 宿主~~ 已验证 | **已消解**：2026-09-28 Windows Server 2022 x64——Linux 产 `.zip` 在 `Expand-Archive` 解开、载荷可运行 |
| ARCHIVE-OI-02 | ARCHIVE-3 | ~~macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64——`ditto -xk`/`tar -xzf` 解开 `.tar.gz`，exec 位与 symlink 还原断言通过 |
| ARCHIVE-OI-03 | ARCHIVE-3 | ~~老发行版宿主~~ 已验证 | **已消解**：2026-09-28 centos7 容器（yum vault 修复后装 UnZip 6.00）——老 unzip 解 zip、mode/symlink 还原记录 |
| ARCHIVE-OI-04 | ARCHIVE-3 | ~~交叉宿主对~~ 已验证 | **已消解**：2026-09-29/30 四宿主联合测试——win/linux/mac/qemu 互产互装归档，解压+exec 位/symlink 还原+载荷运行全过（覆盖 MT-04 场景） |
| ARCHIVE-OI-05 | ARCHIVE-3 | ~~大载荷场景~~ 已消解 | **已消解**：2026-09-28 Linux 验收会话实测 >4GB 载荷走 Zip64 确定性拒绝路径（非失败即拒绝，按设计）。
2026-10-05 语义更新：Zip64 按 BCL 式动态升级实现（哨兵字段+Zip64 extra+EOCD64/locator，仅顶限字段升级、未顶限归档字节不变），>4GiB 单条目/≥65535 条目数/>4GiB 本地偏移与中央目录均正常写出，原拒绝路径废止；`ArchiveTests` 新增 4.3GiB 稀疏文件写+ZipArchive 全量读回、65536 条目 EOCD64、安全带预升级头形态三断言，win 宿主 Expand-Archive/tar.exe 与本机 unzip -t 全量解压 CRC 验证通过。 |
| ARCHIVE-OI-06 | 待定 | ~~大载荷评估~~ 已消解 | **已消解**：2026-10-05——`TarEntry` 新增 `Func<Stream>? OpenContent` 流式载荷（写出端按 `Stream.Length` 填头、分块写、早 EOF 抛 `EndOfStreamException`），`ArchiveTree`/`DebPackageWriter`/`ApkPackageWriter` 文件来源条目全部改走 FileStream 工厂不再整读内存；`ArchiveTests.StreamsFilePayloadsAboveTwoGiB` 实测 2.2GiB 稀疏文件产 tar.gz（头 Size 字段与 sha256 旁车核验）、`StreamsEntryContentIdenticalToInline` 断言流式与内联字节一致（确定性不变）、`StreamedEntryEndingEarlyFails` 断言截断拒绝。单条目上限回到 ustar 八进制尺寸字段 ~8GiB 格式顶，超限由 `WriteOctal` 确定性拒绝（与 zip Zip64 拒绝同语义）。
2026-10-05 同轮 zip 侧拉平：`ZipEntry` 新增同款 `OpenContent`（占位本地头 + Deflate 流式 + 增量 CRC32 + 写后回填三字段，要求可寻址输出流；流式条目恒 deflate，stored 优化仅留缓冲路径），`ArchiveTree.ToZipEntry` 文件来源改 FileStream 工厂；`StreamsZipFilePayloadsAboveTwoGiB` 实测 2.2GiB 产 zip 并经 ZipArchive 全量读回验证回填 CRC、流式与内联字节一致、早 EOF 拒绝、单条目 >4GiB 按经典 zip 边界确定性拒绝——单条目上限由 ~2GiB 内存顶升至 4GiB 格式顶。
2026-10-05 再拉平：Zip64 动态升级落地后单条目上限完全解除（字段顶哨兵即升级），流式路径不再有尺寸天花板。 |

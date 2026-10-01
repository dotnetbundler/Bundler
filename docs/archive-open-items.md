# ARCHIVE 外部待办清单（ARCHIVE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `ARCHIVE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| ARCHIVE-OI-01 | ARCHIVE-3 | ~~Windows 宿主~~ 已验证 | **已消解**：2026-09-28 Windows Server 2022 x64——Linux 产 `.zip` 在 `Expand-Archive` 解开、载荷可运行 |
| ARCHIVE-OI-02 | ARCHIVE-3 | ~~macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64——`ditto -xk`/`tar -xzf` 解开 `.tar.gz`，exec 位与 symlink 还原断言通过 |
| ARCHIVE-OI-03 | ARCHIVE-3 | ~~老发行版宿主~~ 已验证 | **已消解**：2026-09-28 centos7 容器（yum vault 修复后装 UnZip 6.00）——老 unzip 解 zip、mode/symlink 还原记录 |
| ARCHIVE-OI-04 | ARCHIVE-3 | ~~交叉宿主对~~ 已验证 | **已消解**：2026-09-29/30 四宿主联合测试——win/linux/mac/qemu 互产互装归档，解压+exec 位/symlink 还原+载荷运行全过（覆盖 MT-04 场景） |
| ARCHIVE-OI-05 | ARCHIVE-3 | ~~大载荷场景~~ 已消解 | **已消解**：2026-09-28 Linux 验收会话实测 >4GB 载荷走 Zip64 确定性拒绝路径（非失败即拒绝，按设计） |
| ARCHIVE-OI-06 | 待定 | 大载荷评估 | tar.gz 单条目 ~2GiB 上限：`TarEntry.Content` 为内存 `byte[]`（.NET 数组硬顶），早于 tar 八进制尺寸字段 ~8GiB 格式顶触发；>2GiB 单文件当前无解。待评估：是否做流式 tar 条目（写出端不整读内存）。 |

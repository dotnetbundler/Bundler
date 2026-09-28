# ARCHIVE 外部待办清单（ARCHIVE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `ARCHIVE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| ARCHIVE-OI-01 | ARCHIVE-3 | ~~Windows 宿主~~ 已验证 | **已消解**：2026-09-28 Windows Server 2022 x64——Linux 产 `.zip` 在 `Expand-Archive` 解开、载荷可运行 |
| ARCHIVE-OI-02 | ARCHIVE-3 | ~~macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64——`ditto -xk`/`tar -xzf` 解开 `.tar.gz`，exec 位与 symlink 还原断言通过 |
| ARCHIVE-OI-03 | ARCHIVE-3 | ~~老发行版宿主~~ 已验证 | **已消解**：2026-09-28 centos7 容器（yum vault 修复后装 UnZip 6.00）——老 unzip 解 zip、mode/symlink 还原记录 |
| ARCHIVE-OI-04 | ARCHIVE-3 | 交叉宿主对 | MT-04 用例执行与记录 |
| ARCHIVE-OI-05 | ARCHIVE-3 | ~~大载荷场景~~ 已消解 | **已消解**：2026-09-28 Linux 验收会话实测 >4GB 载荷走 Zip64 确定性拒绝路径（非失败即拒绝，按设计） |

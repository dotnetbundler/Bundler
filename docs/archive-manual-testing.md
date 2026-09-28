# ARCHIVE 人工验收用例（archive-manual-testing）

> 只登记本机无法自动化、需外部宿主的用例；每项对应 OI 条目。完成时回填证据。

| ID | 用例 | 所需外部条件 | 验证记录 |
| --- | --- | --- | --- |
| ARCHIVE-MT-01 | Windows 宿主 `Expand-Archive`/资源管理器解开 Linux 产 `.zip`，载荷可运行、`.exe` 无损坏 | Windows 宿主 | **已验证**（2026-09-28，Windows Server 2022 x64）：`Expand-Archive` 解开、载荷运行 |
| ARCHIVE-MT-02 | macOS `ditto -xk`/`tar -xzf` 解开 `.tar.gz`，exec 位与 symlink 还原 | macOS 宿主 | **已验证**（2026-09-28，macOS 26.5.2 arm64）：`ditto`/`tar` 解包、exec 位与 symlink 断言通过 |
| ARCHIVE-MT-03 | 古老 `unzip`（如 RHEL7 自带）解 zip，mode/symlink 还原行为记录 | 老发行版宿主 | **已验证**（2026-09-28，centos7 容器 UnZip 6.00）：解包与还原行为记录 |
| ARCHIVE-MT-04 | Windows 产 `.zip` 在 Linux `unzip` 下解包运行 | 交叉宿主对 | |
| ARCHIVE-MT-05 | 大载荷（>4GB）归档：zip64 路径行为记录（若有该场景） | 按需 | **已消解**（2026-09-28，Linux）：>4GB 载荷 Zip64 确定性拒绝实测 |

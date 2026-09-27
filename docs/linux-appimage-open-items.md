# Linux `.AppImage` 外部待办清单（LINUX-APPIMAGE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵/路线图中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `LINUX-APPIMAGE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| LINUX-APPIMAGE-OI-01 | LINUX-APPIMAGE-3 | GUI 桌面环境宿主（本机无桌面） | 双击运行观感、桌面集成工具识别记录（对应 MT-01） |
| LINUX-APPIMAGE-OI-02 | LINUX-APPIMAGE-3 | ARM64 Linux 宿主（本机 x86_64） | aarch64 产物真机运行记录（对应 MT-02）；决策 4 已实测可交叉，宿主仅做 ELF+squashfs 载荷断言 |
| LINUX-APPIMAGE-OI-03 | LINUX-APPIMAGE-3 | 旧 glibc 发行版宿主 | runtime 兼容性边界记录（对应 MT-03） |
| LINUX-APPIMAGE-OI-04 | LINUX-APPIMAGE-1 | 无 FUSE 环境之外的真 FUSE 宿主 | 原生 FUSE 挂载运行 vs extract-and-run 行为差异记录 |
| LINUX-APPIMAGE-OI-05 | 后置 | GPG 密钥与 appimagetool `--sign` 验证流程 | 若引入签名：签名产物验证记录（对应 MT-05） |
| ~~LINUX-APPIMAGE-OI-06~~ | LINUX-APPIMAGE-1 | ~~`appimagetool` aarch64 runtime 供应方式确认~~ | **已消解**：实测 pinned appimagetool 不内嵌 runtime、缺省联网下载 → 双 runtime 内嵌 `third_party/` 并始终 `--runtime-file` 外供；x86_64 宿主交叉产 aarch64 已验证（决策 4 回填）。 |

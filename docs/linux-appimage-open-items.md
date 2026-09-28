# Linux `.AppImage` 外部待办清单（LINUX-APPIMAGE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵/路线图中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `LINUX-APPIMAGE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| LINUX-APPIMAGE-OI-01 | LINUX-APPIMAGE-3 | GUI 桌面环境宿主 | **部分已验证**（2026-09-28，KDE）：双击启动=FUSE 同代码路径运行已验；appimaged/gear lever 类桌面集成工具的识别观感段仍待验（对应 MT-01 后半） |
| LINUX-APPIMAGE-OI-02 | LINUX-APPIMAGE-3 | ARM64 Linux 宿主（本机 x86_64） | aarch64 产物真机运行记录（对应 MT-02）；决策 4 已实测可交叉，宿主仅做 ELF+squashfs 载荷断言 |
| LINUX-APPIMAGE-OI-03 | LINUX-APPIMAGE-3 | ~~旧 glibc 发行版宿主~~ 已验证 | **已消解**：2026-09-28 centos7/ubuntu18.04 容器实测——runtime 在老 glibc 可挂载；补记边界发现：.NET 10 自包含载荷需 `GLIBCXX_3.4.20+`，centos7 的 libstdc++ 4.8.5 不满足属载荷 ABI 下限非缺陷（对应 MT-03） |
| LINUX-APPIMAGE-OI-04 | LINUX-APPIMAGE-1 | ~~真 FUSE 宿主~~ 已验证 | **已消解**：2026-09-28 真 FUSE 容器内原生挂载运行与 extract-and-run 行为差异记录 |
| LINUX-APPIMAGE-OI-07 | 测试基建 | ~~任意宿主~~ 已查明 | **已消解**：真因非 gpg/appimagetool 竞态——测试把 `GNUPGHOME` 建在打包 input 内，`/run/user/<uid>/gnupg` socketdir 不可用时活跃 gpg-agent 在该目录落下 `S.gpg-agent*` unix 套接字，`AppDirBuilder.CopyTree` 对套接字执行 `File.Copy` 必报 ENXIO；keyring 已移出 input，各后端 staging 统一经 stat 跳过非普通文件（本 PR 修复） |
| ~~LINUX-APPIMAGE-OI-05~~ | ~~后置~~ | ~~GPG 密钥与 appimagetool `--sign` 验证流程~~ | **已消解**：`SIGN-2` 落地可选签名——隔离 `GNUPGHOME` 导入私钥 + `APPIMAGETOOL_SIGN_PASSPHRASE` 注入 + `gpgv` 断言（Verify.sh 签名段），签名产物验证已自动化；生产密钥流程仍归分发侧人工事项 |
| ~~LINUX-APPIMAGE-OI-06~~ | LINUX-APPIMAGE-1 | ~~`appimagetool` aarch64 runtime 供应方式确认~~ | **已消解**：实测 pinned appimagetool 不内嵌 runtime、缺省联网下载 → 双 runtime 内嵌 `third_party/` 并始终 `--runtime-file` 外供；x86_64 宿主交叉产 aarch64 已验证（决策 4 回填）。 |

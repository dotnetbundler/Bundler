# UPDATE 人工验收用例（update-manual-testing）

> 只登记本机无法自动化、需外部宿主的用例；每项对应 OI 条目。完成时回填证据。

| ID | 用例 | 所需外部条件 | 验证记录 |
| --- | --- | --- | --- |
| UPDATE-MT-01 | ~~arm64 AOT 引导件在 macos-arm64/linux-aarch64/windows-arm64 宿主演 `apply`/`--rollback` 全流程~~ | ~~arm64 宿主+工具链~~ | **已收编**（2026-10-09）：macos-arm64 件换包腿此前已实证；linux-aarch64 件 docker tmpfs ENOSPC 腿、windows-arm64 件 VHD ENOSPC 腿随件入库进 CI（`release-verify` 六宿主矩阵）；三件产出均带宿主 `apply` 冒烟记录 |
| UPDATE-MT-02 | 生产签名 `.app` 的更新链：新版同身份验过放行、异身份拒绝 | Apple 凭证（UPDATE-OI-02） | 未执行——无生产证书 |
| UPDATE-MT-03 | 真实 macOS 桌面会话下 `open -n` 重启语义观察（Dock 图标/聚焦/路径） | 桌面会话宿主（UPDATE-OI-03） | 未执行——子会话为无头环境，断言止于进程存活 |
| UPDATE-MT-04 | 真实发布链狗食：公网静态托管 feed，HelloBundlerApp 装 v1→检出 v2→重启后新版在位 | 发布管线（UPDATE-OI-04） | 未执行 |
| UPDATE-MT-05 | per-machine 安装目录下的提权更新（`Program Files` 写入需要 UAC） | 交互式 Windows 宿主（UPDATE-OI-06） | 未执行——win 实证为 per-user 目录，提权语义未覆盖 |
| UPDATE-MT-06 | 公网/CDN 上 Range 差分行为记录（含 CDN 不回 206 时的全量回退表现） | 公网托管（UPDATE-OI-05） | 未执行——回环 206 已实证 |

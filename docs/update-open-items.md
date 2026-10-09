# UPDATE 外部待办清单（UPDATE-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `UPDATE-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| UPDATE-OI-01 | UPDATE-2 | win-x86 AOT 工具链 | ~~arm64 构建环境~~ **arm64 已全部就位**（2026-10-09）：osx-arm64 由 mac 宿主产出；linux-arm64/linux-musl-arm64 由 GitHub `ubuntu-26.04-arm` runner 真宿主产出（musl 件 alpine 容器内 NativeAOT）、win-arm64 由 `windows-11-arm` runner 产出，均冒烟 `apply` 实证入库；arm64 换包腿已收编进 CI（linux-arm64 走 docker tmpfs、win-arm64 走 VHD 小卷）。**剩余**：win-x86 引导件（x86 NativeAOT 需 VS x86 工具链，`windows-2025` runner 可产——暂未排期，win-x86 RID 列表内但无 x86 宿主可验）。 |
| UPDATE-OI-02 | UPDATE-3 | Apple Developer ID 凭证 | mac 换包前同签名身份比对腿实证：现实现要求新旧 `.app` codesign 身份一致，无生产证书只能跑未签名腿；凭证就位后验"签名应用更新链"。 |
| UPDATE-OI-03 | UPDATE-3 | 带真实桌面会话的 macOS 宿主 | `open -n` 重启后的真实 GUI 拉起观察（Dock/聚焦/对副本来路径的判定）；当前断言止于 open 返回码与进程存活。 |
| UPDATE-OI-04 | UPDATE-4 | 真实发布管线（私钥保管方+静态托管） | 端到端狗食：真实通道 `BundlerUpdateFeed` 指向公网静态托管，HelloBundlerApp 走 pack→安装→Check/Download/Apply 全链；私钥按"秘密不入库"纪律走秘密库。 |
| UPDATE-OI-05 | UPDATE-5 | 真实公网/CDN 托管 | 差分在真实 HTTP 栈上的 Range 行为记录（部分 CDN 对 Range 有缓存语义差异）；回环 206 腿已实证，公网腿属环境补证非逻辑补证。 |
| UPDATE-OI-06 | UPDATE-3 | per-machine/UAC 提权场景 | 引导件在提权安装目录（`Program Files`）下的写权限与 UAC 交互策略实证；当前 win 实证为 per-user 目录。 |

# 签名能力外部待办清单（SIGN-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `SIGN-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| SIGN-OI-01 | SIGN-1 | ~~真实发行版宿主~~ 已验证 | **已消解**：2026-09-28 fedora/opensuse 容器导入公钥后已签 rpm 真实安装（dnf+裸 `zypper install` 均过）。修正史：Leap 15.6/16.0 曾对仅 `RPMSIGTAG_PGP` 签名报 `Package header is not signed`（libzypp 只认 v4 header 签名 `RPMSIGTAG_RSA`=268）；双标签输出后 zypper 不再要求 `--allow-unsigned-rpm`，Leap 15.6/16.0 实测完整安装 |
| SIGN-OI-02 | SIGN-1/2 | 生产密钥 | 真实开发者 GPG 密钥签名 + 公钥分发/吊销流程演练 |
| SIGN-OI-03 | SIGN-2 | ~~FUSE 宿主~~ 已验证 | **已消解**：2026-09-28 真 FUSE 容器内已签 AppImage 免 extract-and-run 直跑 + `gpgv` 验签通过 |
| SIGN-OI-04 | SIGN-1 | Windows/macOS 宿主 | 非 Linux 宿主产已签 rpm 实测 |

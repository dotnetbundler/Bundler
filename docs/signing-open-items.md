# 签名能力外部待办清单（SIGN-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `SIGN-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| SIGN-OI-01 | SIGN-1 | 真实发行版宿主 | 已签 rpm 真实安装链路（`dnf`/`zypper`，导入公钥） |
| SIGN-OI-02 | SIGN-1/2 | 生产密钥 | 真实开发者 GPG 密钥签名 + 公钥分发/吊销流程演练 |
| SIGN-OI-03 | SIGN-2 | FUSE 宿主 | 已签 AppImage 免 extract-and-run 直跑 |
| SIGN-OI-04 | SIGN-1 | Windows/macOS 宿主 | 非 Linux 宿主产已签 rpm 实测 |

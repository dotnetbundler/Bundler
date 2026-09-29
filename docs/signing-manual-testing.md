# 签名能力人工验收用例（signing-manual-testing）

> 只登记本机无法自动化、需外部宿主的用例；每项对应 OI 条目。完成时回填证据。

| ID | 用例 | 所需外部条件 | 验证记录 |
| --- | --- | --- | --- |
| SIGN-MT-01 | 已签 rpm 在真实 Fedora/openSUSE 宿主 `dnf install`/`zypper in`（导入公钥后） | 真实发行版宿主 | **dnf 腿已验证**（2026-09-28 fedora 容器：导钥后已签 rpm 真实安装）。**zypper 腿复验中**：此前记录有误——Leap 15.6/16.0 对仅含 `RPMSIGTAG_PGP` 的包报 `Package header is not signed`（libzypp 仅认 v4 header 签名标签 `RPMSIGTAG_RSA`=268），需 `--allow-unsigned-rpm`。已改为双标签（RSA+PGP）输出，opensuse 待复验 |
| SIGN-MT-02 | 生产 GPG 密钥对签名 + 公钥分发流程演练 | 开发者真实密钥与分发渠道 | |
| SIGN-MT-03 | 已签 AppImage 在带 FUSE 环境直接运行 + 验签 | FUSE 宿主 | **已验证**（2026-09-28，真 FUSE 容器）：直跑 + `gpgv` 验签 |
| SIGN-MT-04 | Windows/macOS 宿主上签名 rpm（验证任意宿主可产已签 rpm） | 对应宿主 | |

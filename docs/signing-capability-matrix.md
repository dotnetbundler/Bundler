# 签名能力矩阵（SIGN）

> `已实现`、`计划实现`、`外部待验收`、`明确拒绝` 状态口径见 `docs/development-rules.md`。
> 行级证据见 `docs/signing-roadmap.md` §4；外部验收项见 `docs/signing-open-items.md`。

## rpm 签名

| 能力 | 状态 | 落地阶段 | 证据/备注 |
| --- | --- | --- | --- |
| `BundlerRpmSigningKeyFile`/`Passphrase` 旋钮 | 计划实现 | SIGN-1 | 供密钥才签；半配置拒绝 |
| OpenPGP 签名写 rpm signature header（RPMSIGTAG_RSA） | 计划实现 | SIGN-1 | 纯托管 BouncyCastle；无外部进程 |
| 容器内 `rpm --import` + `rpm -K` 验签 | 计划实现 | SIGN-1 | Verify.sh docker 段 |
| 无密钥产物与现状一致 | 计划实现 | SIGN-1 | sha256 对比断言 |

## AppImage 签名

| 能力 | 状态 | 落地阶段 | 证据/备注 |
| --- | --- | --- | --- |
| `BundlerAppImageSigningKeyFile`/`Passphrase` 旋钮 | 已实现 | SIGN-2 | 同 rpm 口径；MSBuild/CLI 自动透传 |
| appimagetool `--sign`（隔离 GNUPGHOME） | 已实现 | SIGN-2 | 唯一外部进程出口，仅供密钥时触发；口令经 `APPIMAGETOOL_SIGN_PASSPHRASE` 注入不上命令行 |
| `gpgv`/`--validate` 验签断言 | 已实现 | SIGN-2 | Verify.sh：两段置零→sha256 裸 hex→`gpgv` Good signature |
| 无密钥产物与现状一致 | 已实现 | SIGN-2 | 未签构建 `.sha256_sig` 保持全零断言 |

## 明确不做

| 能力 | 状态 | 理由 |
| --- | --- | --- |
| `.deb` 包级签名 | 明确拒绝 | deb 签名惯例在 apt 仓库侧；`dpkg -i` 不验包内签名 |
| 密钥分发/吊销管理 | 明确拒绝 | 属开发者职责，非打包器边界 |
| 签名默认开启 | 明确拒绝 | 可选能力；无密钥产物必须与现状一致 |

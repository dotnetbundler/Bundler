# 签名能力（跨格式）

本文件是签名横切面（SIGN）的唯一文档：各格式签名的实现方式、密钥契约与边界。
单格式细节见对应文档（`docs/design/formats/win-nsis.md` §4.8、`docs/design/formats/linux-rpm.md`、`docs/design/formats/linux-appimage.md`、`docs/design/formats/linux-alpine-apk.md`、`docs/design/formats/mac-app.md` §3 第 5-7 条、`docs/design/formats/mac-pkg.md`、`docs/design/formats/mac-dmg.md`）；平台×签名总览见 `docs/design/support-matrix.md`。

## 1. 通用契约

- **可选能力**：供密钥才签；不供密钥产物与未签名构建**逐字节一致**（可选能力不改变既有产物）。
- **半配置报错**：只给 Passphrase 而未给 KeyFile 属半配置错误，不静默忽略（三入口一致）；
  未加密私钥可不提供 Passphrase；
  加密私钥须提供可正确解密的口令。
- **打包内联**：签名是产物属性，配了就签，不做后置独立命令。
- **密钥形态**：私钥文件路径 + passphrase 旋钮（不依赖宿主 keyring；路径可走参数，passphrase 建议 secret 注入文件/环境）；密钥字面量绝不进 csproj/命令行/日志。
  入口层只收 env 间址：MSBuild 的 `*Passphrase`/`*Password` 旋钮为 `*EnvironmentVariable` 形式（属性值=环境变量名）；bundler.json 的秘密字段可写 `<名>EnvironmentVariable` 间址键。
- **签而必验**：签名产物必须有真实校验断言，拒绝"只签不验"。
- **测试密钥现生成**（`gpg --batch --gen-key` 一次性断言即弃），不入库。
- **信任模型边界**：只产签名，不管密钥分发与吊销——密钥生命周期属开发者职责。

## 2. 各格式实现

| 格式 | 实现 | 密钥/提供器 | 验签证据 |
| --- | --- | --- | --- |
| `nsis`/`msi` | Authenticode：内置 `mssign32` P/Invoke（仅 Windows 宿主）或 `WindowsExternalCommandSigner` 外部命令（任意宿主，HSM/云/远程 provider；有界等待 `BUNDLER_TOOL_TIMEOUT_SECONDS` 默认 15min，超时杀进程）；SHA-256+RFC 3161 | PFX/P12（密码走环境变量名；`EphemeralKeySet` 私钥不落用户密钥存储区）或 `My` 证书指纹；外部 provider 独立参数数组+占位符 | payload/插件/卸载器/安装器 staged 链路自动化；生产链见 SA-P-01 |
| `app` | 系统 `codesign` inside-out（identity/ad-hoc `-`/临时钥匙串）；`notarytool`/`stapler` 公证 | identity 字符串/钥匙串导入；公证凭证三模式+`APPLE_*` 回退 | ad-hoc 实跑全绿；Developer ID/公证挂 SA-P-02/08 |
| `dmg` | `codesign` 本体签名（identity / `-` ad-hoc / 临时钥匙串；`-` 签后 `codesign --verify`，未配置才跳过）；本体不公证 | DMG 自有 `Signing` 段，钥匙串实现复用 `MacAppSigning` | ad-hoc 验签实测 |
| `pkg` | `pkgbuild --sign`/`productsign --sign`（Developer ID Installer，**无 ad-hoc**）；`.pkg` 本体公证 | 同 `.app` 钥匙串复用 | 无效身份真实失败已验；真实证书挂外部 |
| `rpm` | 纯托管 BouncyCastle OpenPGP v3 双标签：`RPMSIGTAG_RSA`(268，签 main header——zypper 所验）+`RPMSIGTAG_PGP`(1002，签 header+payload——`rpm -K` 所验），与 `rpmsign --addsign` 同形态 | `BundlerRpmSigningKeyFile`/`Passphrase`（armor 私钥文件+口令） | 隔离 rpmdb `rpm --import`+`rpm -K` `digests signatures OK`；fedora `dnf install`（gpgcheck=1）过/未导公钥拒 |
| `appimage` | `appimagetool --sign`（唯一外部进程出口，仅供密钥时触发）；隔离 `GNUPGHOME` 导钥，口令经 `APPIMAGETOOL_SIGN_PASSPHRASE` 注入不上命令行 | 私钥文件+口令（同 rpm 口径） | `gpgv` Good signature 断言；未签构建 `.sha256_sig` 全零断言 |
| `alpineapk` | 纯托管 BouncyCastle RSA PKCS#1 v1.5/SHA1：`.SIGN.RSA.<密钥>.rsa.pub` 前置段签控制段 | `BundlerAlpineApkSigningKeyFile`（PEM/PKCS8/加密）+`Passphrase` | 公钥入 `/etc/apk/keys/` 免 `--allow-untrusted` 实装；`openssl dgst -sha1 -verify` 旁证 |
| `deb`/`zip`/`targz` | **不签** | — | deb 签名在 apt 仓库侧惯例；归档无通行签名惯例 |

`msi` 不另补真装集成腿（裁决）：`Bundler.ApiTests`（`WixApiTests`）断言已覆盖内嵌签名链，RFC 3161 时间戳子项已真实嵌入消解；外部仅余 SA-P-01 生产 CA 信任链。

## 3. 明确不做

- `.deb` 包级签名（apt 仓库侧惯例，`dpkg -i` 不验包内签名）。
- 密钥分发/吊销管理（开发者职责）。
- 签名默认开启。

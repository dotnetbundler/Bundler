# 签名能力路线（SIGN）

> 状态：规划轮文档组，决策清单待用户逐条裁决或直接放行（2026-09-27，分支 `signing-development`）。
> 范围依据：用户裁决——`deb` 不提供签名（维持拒绝）；`rpm` 与 `AppImage` 提供**可选**签名能力（开发者供密钥才签，不供不签）。
> 上游参照：`docs/linux-tauri-capability-audit.md` + 定点复核——rpm 侧 Tauri `build_and_sign` 走 `rpm` crate `pgp::Signer`：`RPMSIGTAG_PGP`(1002) 写 OpenPGP 签名包（覆盖 header+payload，SHA256 系），密钥为 armor 私钥 env + passphrase env（`TAURI_SIGNING_RPM_KEY`/`…_PASSPHRASE`）；AppImage 侧上游未实现 `--sign`。

## 1. 决策清单

| # | 决策点 | 候选 | 推荐 | 取舍 |
| --- | --- | --- | --- | --- |
| 1 | rpm 签名实现 | 纯托管 OpenPGP（BouncyCastle）/ 受控调 `rpmsign`+`gpg` | **纯托管 BouncyCastle** | 守住"零外部进程/任意宿主可产"立场；rpmsign 依赖宿主 rpm 工具链且只在 Linux 上签，偏离我们写入器定位 |
| 2 | rpm 签名形态 | RPMSIGTAG_RSA / PGP 签名包 / 分离 sig 文件 | **RPMSIGTAG_PGP（tag 1002，OpenPGP 签名包嵌 signature header，覆盖 main header+payload）** | 对齐 rpm crate `pgp::Signer` 实际行为（`rpm -K`/`--checksig` 原生路径） |
| 3 | rpm 密钥形态 | GPG 私钥文件（armor/binary）/ keyid 引用宿主 keyring | **私钥文件路径 + passphrase 旋钮**（`BundlerRpmSigningKeyFile`/`…Passphrase`） | 不依赖宿主 keyring 状态；密钥本身可经 secret 注入环境再落临时文件；绝不允许密钥字面量进 csproj/命令行/日志 |
| 4 | AppImage 签名实现 | appimagetool `--sign` / 自实现嵌入签名 | **appimagetool `--sign`**（宿主 gpg） | 签名位在 AppImage 尾部属 appimagetool 自有格式段，自实现需复刻其内部布局；宿主 gpg 是唯一外部依赖，且仅当开发者主动供密钥时才触发 |
| 5 | AppImage 密钥形态 | 同 rpm 文件式 / 宿主 keyring keyid | **私钥文件 + passphrase**（与 rpm 同口径），工具层经 `GNUPGHOME` 隔离 home 导入后 `--sign` | 与 rpm 一致的密钥供给契约；隔离 GNUPGHOME 不污染宿主 keyring |
| 6 | 签名时机 | 打包内联 / 后置独立命令 | **打包内联**（签名是产物属性，配了就签） | 与 Windows/macOS `SigningFiles` 口径一致 |
| 7 | 验证方式 | 构建内自验 / 外部工具验 | **构建即自验**：rpm 自写回读验签 + Verify.sh 容器内 `rpm --import`+`rpm -K`；AppImage `gpgv` 断言分离签名 | 无密钥不签不验；签名产物必须有真实校验断言，拒绝"只签不验" |
| 8 | 密钥缺失语义 | 报错 / 静默跳过 | **旋钮部分给出即报错**（给了 KeyFile 不给 Passphrase 等半配置 → 校验失败） | 半配置比静默跳过更安全 |
| 9 | 测试密钥 | 入库 / 测试时现生成 | **测试时现生成**（`gpg --batch --gen-key` 一次性密钥，断言即弃） | 测试密钥不入库；gitignore/清理路径确保不留档 |
| 10 | 默认关闭 | 有密钥签 / 总签 | **无密钥产未签名包且行为与现状逐字节一致** | 可选能力不能改变现有产物 |
| 11 | 命令行透传 | `--rpm.signing-*` / 仅配置文件 | **同既有约定**：MSBuild 属性 + bundler.json 段 + `--rpm.*`/`--appimage.*` 点号旋钮全通 | 密钥文件路径可走参数（路径非秘密），passphrase 建议 secret 注入文件或环境 |
| 12 | 阶段骨架 | 一段 / 两段 | **两段**：`SIGN-1` rpm 签名 → `SIGN-2` AppImage 签名（各含旋钮+实现+测试+验证+文档+冻结前移） | 沿用既有"一段一完整交付"惯例 |
| 13 | deb | 提供 / 不提供 | **不提供**（维持 `docs/linux-deb-roadmap.md` §34 拒绝裁决） | 用户已裁决 |
| 14 | 信任模型边界 | 我们验证签名有效性即可 / 兼管密钥分发 | **只产签名，不管密钥分发与吊销** | 密钥生命周期属开发者职责 |

## 2. 阶段表

| 阶段 | 范围 | 退出条件 |
| --- | --- | --- |
| `SIGN-1` | `RpmBundleConfiguration` 签名旋钮 + BouncyCastle OpenPGP 签名写 signature header + MSBuild/CLI 接线 + 单元测试 + Verify.sh 容器 `rpm -K` 断言 + 测试密钥现生成 + 无密钥回归 | 已签 rpm 容器内 `rpm --import`+`-K`/`--checksig` 真实验签断言全绿；未供密钥产物与现状一致；半配置拒绝断言；`Bundler.Tests` 全绿 |
| `SIGN-2` | `AppImageBundleConfiguration` 签名旋钮 + appimagetool `--sign`（隔离 GNUPGHOME）+ 同上接线/测试/验证 + 冻结基线前移 | 已签 AppImage `gpgv` 验签断言全绿；未供密钥产物与现状一致；文档矩阵/OI/MT 收口 |

## 3. 技术注记

- rpm signature header 已存在（`RPMSIGTAG_SIZE/MD5/PAYLOADSIZE/SHA1HEADER`），签名=在其上追加 OpenPGP v4 签名包条目（覆盖"main header+payload"的 RSA/SHA256 签名），`RpmHeaderWriter` 直接扩。
- BouncyCastle `Org.BouncyCastle.Bcpg.OpenPgp` 命名空间提供 PGP 签名包构造；密钥解析走 `PgpSecretKeyRingBundle`。
- 签名口径：RPM v4 惯例 `RPMSIGTAG_PGP`(tag 1002) 存对 main header+payload 字节的 OpenPGP 签名包（RSA key → RSA sig packet；rpm crate `pgp::Signer::load_from_asc`+`with_key_passphrase` 同口径，armor 私钥+passphrase）。
- AppImage `--sign` 产物形态：appimagetool 在镜像尾部附加签名段并产 `.sig`/digest 辅助文件；验证用 `gpgv`/`appimagetool --validate` 路径以实测为准。

## 4. 阶段证据

### SIGN-1 · RPM 包级 OpenPGP 签名（完成，2026-09-27）

- 实现：`src/Bundler.Rpm/RpmSigner.cs` 用 BouncyCastle 生成 v3
  binary-document 签名包（RSA/SHA-256），`RpmPackageWriter` 将其写入
  signature header 的 `RPMSIGTAG_PGP`（tag 1002）——覆盖字节为主
  header+载荷，与 `rpmsign`/rpm-rs 语义一致。
- 旋钮：`RpmBundleConfiguration.SigningKeyFile`/`SigningKeyPassphrase`；
  MSBuild `BundlerRpmSigningKeyFile`/`BundlerRpmSigningKeyPassphrase`；
  CLI `bundler.json` 的 `rpm.signingKeyFile`/`signingKeyPassphrase` 与
  `--rpm.*=` 透传自动到面。
- 半配置拒绝：只给口令或无密钥文件 → `ArgumentException`；口令错误 →
  `InvalidOperationException`；无密钥时产物与未配置构建逐字节一致。
- 密钥供给修正记录：决策 1 先写 `RPMSIGTAG_RSA`，Tauri 复核改为
  `RPMSIGTAG_PGP`（rpm-rs `pgp::Signer` 口径）。
- 装载修正：`DotNet.Bundler.MSBuild` 包增载
  `BouncyCastle.Cryptography.dll`（后端传递依赖不进 tasks 目录曾导致
  task 加载失败，已补并加包内容断言）。
- 测试：`Bundler.Tests` 201/201（新增签名验真、半配置拒绝、错误口令拒绝）。
- 真实验证：`tests/Linux.Rpm.Integration/Verify.sh` 新增签名段——
  `gpg --batch` 现生成 RSA-2048 测试密钥（隔离 `GNUPGHOME`，密钥不入库）→
  发布签名包 → 隔离 rpmdb `rpm --import` + `rpm -K` 实测
  `digests signatures OK`；未签名包断言不含 `signatures OK`；docker
  三容器装卸矩阵与 rpmlint 基线维持全绿。

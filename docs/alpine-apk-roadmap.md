# Alpine `.apk` 后端实施路线（APK）

> 状态：已确认（2026-09-30 用户逐项确认，D1 改判 `AlpineApk`，其余按推荐）
> 格式指 Alpine Linux 的 `.apk`（apk-tools v2），**非 Android `.apk`**；移动端支持若将来立项另立路线，届时命名须显式区分（如 `AlpineApk`/`AndroidApk`）。
> 本文档只放本格式的设计决策、阶段目标与实施证据；泛用规则在 `docs/development-rules.md`。

## 1. 已确认决策（2026-09-30 用户逐项确认）

- **APK-D1 命名与公共标识**：`PackageFormat.AlpineApk`、`DotNet.Bundler.AlpineApk` 程序集、`Bundler.AlpineApk` NuGet 包、CLI `--formats alpineapk`、MSBuild `BundlerAlpineApk*` 属性、bundler.json `alpineapk` 段。
  （用户改判：不取简短 `Apk`，与将来可能的 Android 支持显式区分。）
- **APK-D2 目标格式版本**：apk v2（apk-tools 2.x 现行主版本，Alpine 3.19–3.21+ 默认）——单文件 = 三段 gzip 流串联：签名段 tar（无末尾空块）+ 控制段 tar（无末尾空块）+ 数据 tar（有末尾空块）。
  apk v3（apk-tools 3.x，zstd/新索引）尚未发布稳定版，不预支。
- **APK-D3 实现方式**：纯托管写入器、零宿主工具依赖——与 `Bundler.Deb`/`Bundler.Rpm` 同模式，任意构建宿主可产。
  复用 `Bundler.Core` 的 `TarWriter`（需加"tar 段模式"：signature/control 段不写末尾两个空记录，data 段保留）+ `System.IO.Compression.GZipStream`（mtime=0、确定性字节）。
- **APK-D4 目标 RID**：仅 `linux-musl-x64`、`linux-musl-arm64`——apk 是 musl/Alpine 生态包管理器，向 glibc 目标产 apk 属语义不成立（门禁原则同 musl 拒绝 deb/rpm 的对称面）。
  架构名映射：`musl-x64`→`x86_64`、`musl-arm64`→`aarch64`（apk 架构命名，`BundlerAlpineApkArchitecture` 可显式覆盖，须为 apk 架构名）。
- **APK-D5 数据段布局**：载荷按文件系统路径落在数据 tar 内——默认 `/usr/lib/<pkgname>/` 承载全部载荷 + `/usr/bin/<binname>` 符号链接（链接名默认包名，`BundlerAlpineApkBinLink`，可关）；任意映射 `BundlerAlpineApkFile` 直接进数据段；符号链接/执行位如实写入。
  取舍：`/opt/` 是发行版自有包惯例的反例（Alpine 官方包用 `/usr/`），跟随 deb/rpm 的 `/usr/lib` 惯例。
- **APK-D6 控制段 `.PKGINFO` 字段**：写 `pkgname`、`pkgver`（`<版本>-r<release>`，release 默认 `0` 经 `BundlerAlpineApkRelease` 可调）、`pkgdesc`（默认产品名）、`url`、`arch`、`origin`（默认=pkgname）、`license`（`BundlerAlpineApkLicense`，默认不写入=未知）、`depend`/`provides`/`triggers`（各旋钮逐条写入）、`builddate`（固定纪元默认 0 保证确定性，`BundlerAlpineApkBuildDate` 可覆写）、`size`（写入器按载荷字节和自动算）、`datahash`（数据 gzip 流 sha256，apk 完整性校验字段，apktools 2.x 要求）。
- **APK-D7 安装脚本**：控制段可选脚本 `.pre-install`/`.post-install`/`.pre-deinstall`/`.post-deinstall`/`.pre-upgrade`/`.post-upgrade`，各对应 `BundlerAlpineApk*Script` 旋钮（脚本文件路径）；`triggers` 语义 = 监视目录列表，触发脚本随控制段携带。
- **APK-D8 签名**：可选 RSA——`.SIGN.RSA.<密钥文件名>.rsa.pub` 存 DER PKCS1v15(RSA+SHA1) 签名、签在控制段 gzip 流上；`BundlerAlpineApkSigningKeyFile`（PEM RSA 私钥）+ `BundlerAlpineApkSigningKeyPassphrase`；复用 SIGN 阶段的 BouncyCastle 依赖模式。
  未签名 apk 安装需 `apk add --allow-untrusted`，文档如实标注；签名公钥须已分发到目标 `/etc/apk/keys/` 才有验签意义。
- **APK-D9 不做的项（v1 明确排除）**：`replaces`/`provides_priority`/`install_if`/`datahashes` 之外的冷门 `.PKGINFO` 键（留 `BundlerAlpineApk*` 覆盖旋钮的 escape hatch）、apk v3、`apk` 仓库索引（`.apk.index` 属仓库运维非单包产出）、abuild 的 ELF `so:` 依赖自动扫描（依赖由调用方显式声明）。
- **APK-D10 产物命名与路径**：`OutputDirectory/<rid>/apk/<pkgname>-<pkgver>-r<rel>.apk`；同格式附带 `.sha256` 侧车（对齐 deb/rpm）。

## 2. 打包工具下限（三层口径）

1. **打包工具下限**：无外部工具——写入器纯托管；理论上任意支持 .NET 的构建宿主可产。
2. **后端下限**：`Bundler.AlpineApk` 目标 `netstandard2.0`，依赖 `Bundler.Core`（TarWriter/模型）与 `Bundler.Abstractions`；签名段依赖 BouncyCastle（同 rpm 签名路径）。
   （实施时按 APK-D1 定名 `Bundler.AlpineApk`/`AlpineApk*`，下文早期段名 `Apk*` 同义。）
3. **入口下限**：与现有 CLI/MSBuild 入口一致，无新增要求。
4. **验证宿主**：Alpine Linux（`apk` 实装验真）——`alpine:latest` 容器 x86_64 本机直跑；aarch64 经既有 qemu/binfmt 通道（`tonistiigi/binfmt`，与 arm64 集成腿同机制）。

## 3. 语义契约

- 产物是标准 apk v2：`abuild-gzsplit` 可拆三段、`tar -tzf` 可读、`apk add` 可装、`apk info`/`apk manifest` 可读元数据。
- 未签名产物声明 `.SIGN` 段可省（v2 允许，安装走 `--allow-untrusted`）；签名产物在密钥已分发宿主免开关可装。
- `datahash` 与逐文件 `APK-TOOLS.checksum.SHA1` pax 扩展头写实（数据段完整性字段不造假）。
- 确定性：相同输入与配置逐字节一致（gzip mtime 归 0、tar 时间戳同 deb/rpm 归一化口径）。
- 不冒充：`builddate` 默认 0 而非伪造当前时间；`size` 为写入器自算近似（apk 语义允许近似值）。

## 4. 阶段分解

### APK-1：最小可用 `.apk`

范围：`Bundler.AlpineApk` 程序集骨架（`AlpineApkBundleBackend : IBundleBackend`、`AlpineApkBundler` 直接 API、`AlpineApkBundleConfiguration`）、gzip×3 写入器（TarWriter 段模式）、`.PKGINFO` 核心字段（pkgname/pkgver/pkgdesc/url/arch/origin/size/datahash）、数据段载荷+binlink、pax `APK-TOOLS.checksum.SHA1`、矩阵 `LinuxMusl`+`AlpineApk`、流水线注册、CLI/MSBuild 接线。
验证：写出→自读回解析三段结构断言；`.PKGINFO` 字段断言；matrix 放通 musl+apk、其余目标拒绝断言；构建零警告。
出口：`Bundler.Tests` 新增断言全绿。
实施证据（2026-09-30，commit `074c203`，`0.1.0-alpha.63`）：`src/Bundler.AlpineApk` 落地，`Bundler.Tests` 225/225（新增 14 项），`dotnet build Bundler.slnx -c Release` 零警告；产物 `OutputDirectory/<rid>/apk/<pkgname>-<ver>-r<rel>.apk` + `.sha256`。

### APK-2：元数据、依赖与脚本

范围：`depend`/`provides`/`triggers`/`license`/`release`/`builddate` 旋钮、六个安装脚本、任意文件映射、.desktop/freedesktop 件（按需——musl 桌面语义弱，默认不做，存在争议则降级为 OI）。
验证：旋钮→字段/段内文件逐条断言；非法值拒绝（包名/版本/release 格式）。
实施证据（2026-09-30，commit `06e7550`）：六脚本+五元数据旋钮+`BundlerAlpineApkFile`/`AlpineApkFileEntry` 任意映射落地；.desktop/freedesktop 按 OI 决议不做（musl 桌面语义弱，能力矩阵登记明确拒绝）；`Bundler.Tests` 断言新增（包名/release/triggers 非法值拒绝、脚本非空+LF 校验）。
验证宿主发现（记入 D 决策修正）：真实 `musl` apk 解剖证实数据段每条目须带 pax `ctime=0`/`atime=0` 且 `APK-TOOLS.checksum.SHA1` 为**十六进制** sha1（符号链接按链接目标字符串哈希）——实现按此修正（见 APK-4 证据）。

### APK-3：可选 RSA 签名

范围：`BundlerAlpineApkSigningKeyFile`/`BundlerAlpineApkSigningKeyPassphrase`、`.SIGN.RSA.<name>.rsa.pub` 段生成、半配置拒绝、无密钥即未签名。
验证：生成签名 apk→openssl/BouncyCastle 回验签名字节；半配置断言；签名产物确定性。
实施证据（2026-09-30，commit `ed87ea4`）：`ApkSigner`（BouncyCastle `RsaDigestSigner`+`Sha1Digest`，PEM/PKCS8/加密私钥三态）+ 签名段前置；`Bundler.Tests` 新增 4 断言（`.SIGN.RSA` 成员与模式、`RSA.VerifyData` 回验、加密私钥、半配置 4 子例、签名确定性）。

### APK-4：原生 E2E 与支持矩阵

范围：`tests/Alpine.Apk.Integration/Verify.sh`——alpine 容器内 `apk add --allow-untrusted` 装未签名包、`apk add` 装签名包（自签公钥预置 keys）、装后真实运行载荷、`apk del` 卸载断言、python3 `zlib` 三段拆解结构断言（本机无 `abuild-gzsplit`，等价替身）、x86_64+aarch64（qemu）双腿、确定性三产逐字节。
出口：能力矩阵 `docs/alpine-apk-capability-matrix.md` 初稿 + E2E 全绿。
实施证据（2026-09-30，commit `63eabaf`）：`tests/Alpine.Apk.Integration/Verify.sh` 全绿——`alpine:latest` 容器 `apk add --allow-untrusted` 实装→脚本标记→运行→`apk del` 清理；未签名拒绝断言；自签包公钥入 `/etc/apk/keys/` 免 `--allow-untrusted` 实装；`openssl dgst -sha1 -verify` 独立验签；aarch64 经 qemu binfmt 实装+运行；确定性三产逐字节；`tests/AlpineApk.Api.PackageFixture` 直接 API 消费断言。同 commit 修正 pax 格式为真实 apk 实测口径（ctime/atime=0 全条目 + 十六进制 sha1 + 符号链接按目标字符串哈希——修正前被 `apk add` 以 missing embedded checksum 拒绝）。
取舍实录：自包含 musl 载荷的 `libstdc++.so.6`/`libgcc_s.so.1` 经 `depend` 显式声明（fixture 中 `libstdc++;libgcc`），与 APK-D9 的“依赖调用方显式声明”一致；样本与文档如实标注。

### APK-5：审计与格式冻结

范围：干净宿主审计、OI 清单（`alpine-apk-open-items.md`：真实官方签名密钥、`apk` 仓库索引集成等）、manual-testing 清单、能力矩阵定稿、冻结基线版本号。
实施证据（2026-09-30，本 commit）：能力矩阵/OI/MT 三文档定稿、`manual-testing-index` 收录、`PROJECT_CONTEXT`/`docs/roadmap.md`/`README` 状态同步、示例 `samples/HelloAlpineApkApp` 落地并经 `alpine:latest` 容器实装运行验证；**冻结基线未宣告**——本分支留 `devin/alpine-apk` 待用户终审合并，合并后方以 `0.1.0-alpha.63` 为准冻结。

## 5. 验证分层

| 层 | 断言什么 | 在哪跑 |
| --- | --- | --- |
| 写入器单测 | 三流结构、字段、pax 头、datahash、签名字节、确定性 | `Bundler.Tests`（任意宿主） |
| 读出器/拆包 | 自产 apk 回读字段；tar/gzsplit 等价拆解 | `Bundler.Tests` + alpine 容器 |
| 真实装卸 | `apk add`/`apk info`/`apk remove`、装后运行、签名免开关 | alpine docker（x86_64 + aarch64 qemu） |
| 门禁回归 | musl+apk 放通、glibc 目标拒绝、非 musl RID 拒绝 | `Bundler.Tests` 矩阵断言 |

## 6. 已知风险与留待裁决

- apk v3 若正式发布（zstd 流、新签名方案）需另起升级路线；v2 产物届时仍可装（apk-tools 兼容读）。
- `.PKGINFO` 无官方完整键清单（权威来源是 abuild 源码）——写字段集保守收敛在常见子集，escape hatch 覆盖遗漏。
- musl 目标的真实载荷（`dotnet publish -r linux-musl-*`）属调用方前提；验证腿用 self-contained fixture。
- 桌面集成（.desktop/图标）在 Alpine 桌面场景价值弱——默认不做，若真实需求出现再加阶段。

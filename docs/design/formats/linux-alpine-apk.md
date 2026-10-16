# Alpine `.apk` 包（apk-tools v2）

本文件是 `.apk` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
指 Alpine Linux 的 `.apk`（apk-tools v2），**非 Android `.apk`**；移动端若立项另立路线并显式区分命名（`AlpineApk`/`AndroidApk`）。

## 1. 状态与决策

- `.apk` 已冻结（基线 `0.1.0-alpha.63`），仅缺陷修复附回归测试。
- 公共标识：`PackageFormat.AlpineApk`、`Bundler.AlpineApk` 包、CLI `--formats alpineapk`、MSBuild `BundlerAlpineApk*`、bundler.json `alpineapk` 段；**`apk` 别名已移除**（留给未来 Android 格式）。
- **纯托管写入器，零原生工具**：apk v2 单文件 = 三段 gzip 流串联——签名段 tar（无末尾空块）+ 控制段 tar（无末尾空块）+ 数据 tar（有末尾空块）；复用 `Bundler.Core` `TarWriter` 段模式 + `GZipStream`（mtime=0 确定性）。apk v3 未发布稳定版不预支。
- **目标仅 `linux-musl-x86_64`/`linux-musl-aarch64`**——apk 是 musl/Alpine 生态包管理器，向 glibc 目标产 apk 语义不成立（与 musl 拒绝 deb/rpm 对称）；架构映射 `x86_64`/`aarch64`，`BundlerAlpineApkArchitecture` 可覆盖（须 apk 架构名）。
- 数据段布局：`/usr/lib/<pkgname>/` 载荷 + `/usr/bin/<binname>` 符号链接（`BundlerAlpineApkBinLink` 改名/关闭）；`BundlerAlpineApkFile` 任意绝对路径映射。Alpine 官方包用 `/usr/` 非 `/opt/`。

## 2. 语义契约

- `.PKGINFO`：`pkgname`/`pkgver`（`<版本>-r<release>`，release 默认 0）/`pkgdesc`（默认产品名）/`url`/`arch`/`origin`（默认=pkgname）/`license`（默认不写）/`depend`/`provides`/`triggers`（空格串联绝对路径）/`builddate`（默认 0 保确定性，`BundlerAlpineApkBuildDate` 可覆写）/`size`（写入器自算）/`datahash`（数据 gzip 流 sha256，v2 必需）。
- 版本映射：SemVer 预发布 `-`→`_` 并入上游段；`~`/`+` 按 apk 字符集收；`BundlerAlpineApkVersion`/`Release` 显式覆盖（release 默认 `0`，须非负整数）。
- 逐文件 `APK-TOOLS.checksum.SHA1` pax 扩展头（含符号链接按目标字符串哈希）——缺此被 `apk add` 拒。
- 六段脚本：`.pre-install`/`.post-install`/`.pre-deinstall`/`.post-deinstall`/`.pre-upgrade`/`.post-upgrade` 对应 `BundlerAlpineApk*File`（0755、非空+LF）；v2 upgrade 腿执行 `.pre-upgrade`/`.post-upgrade` 而 `.post-install` 不触发。
- 签名（可选 RSA）：`.SIGN.RSA.<密钥名>.rsa.pub` 段前置，DER PKCS#1 v1.5 RSA+SHA1 签在控制段 gzip 流上；`BundlerAlpineApkSigningKeyFile`（PEM，可加密）+ `Passphrase`；BouncyCastle 纯托管；确定性签名（同输入三产逐字节一致）；只给口令不给密钥文件=配置错误（三入口一致拒绝）。
- 未签名产物需 `apk add --allow-untrusted`；签名公钥须预置目标机 `/etc/apk/keys/` 才有验签意义（分发属供方运维）。
- 确定性：条目排序、uid/gid 0、mtime/atime/ctime 归一——同输入逐字节一致；`builddate` 默认 0 而非伪造时间。
- 产物 `<pkgname>-<pkgver>-r<rel>.apk` + `.sha256` 侧车。

## 3. 能力状态

已实现并验证：

- 三流结构/字段/pax 头/datahash/签名字节/确定性单测；`abuild-gzsplit` 拆段、`tar -tzf`、`apk info`/`manifest` 回读。
- `alpine:latest` 容器真实 `apk add`/运行/`apk del`；未签名拒绝断言；自签包公钥入 `/etc/apk/keys/` 免 `--allow-untrusted` 实装。
- `openssl dgst -sha1 -verify` 独立验签旁证。
- aarch64：qemu binfmt 仿真装跑 + `ubuntu-26.04-arm` 真机腿 `DockerArm64NativeInstallRunRemove` 原生实证。
- musl 放通 zip/targz/alpineapk/appimage、deb/rpm 拒绝、glibc 目标拒绝 alpineapk 的门禁矩阵断言。
- `alpineapk` 与其他格式混排扇出；CLI/bundler.json/API/NuGet 全接线；AOT 二进制内嵌产 apk（IL2026 干净）。

计划实现（UPDATE 系统包扩展阶段）：

- 自更新走 installer-replay：客户端 ECDSA 验签 + `apk info -e` 账本归属双检预检，经三级提权链拉起 `bundler-updater pkg-apply`，由引导件 `apk add ./x.apk` 装本地包（公钥未预置时 `--allow-untrusted`，TOFU 边界）、降权重启；契约见 `docs/design/update.md` §2。

外部待验收：签名公钥在目标机的分发（供方运维，非产物语义）；真机观感；UPDATE 链 `apk add` 降级语义（登记 `docs/project/special-acceptance.md`）。

## 4. 明确拒绝 / 不适用

- 桌面集成（.desktop/图标/metainfo）——musl/Alpine 非桌面场景。
- abuild/apk 仓库索引（`.apk.index` 属仓库运维）；abuild 的 ELF `so:` 依赖自动扫描（依赖显式声明）。
- 冷门 `.PKGINFO` 键（`replaces`/`provides_priority`/`install_if` 等——留 `BundlerAlpineApk*` escape hatch）；apk v3。

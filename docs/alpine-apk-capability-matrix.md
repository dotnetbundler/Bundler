# Alpine `.apk` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
外部待验收行绑定到 `docs/alpine-apk-open-items.md`、`docs/alpine-apk-manual-testing.md` 中的明确 ID。
决策与阶段见 `docs/alpine-apk-roadmap.md`。
本矩阵于 2026-09-30 随 `APK-1..5` 实施建立；全部行以 `AlpineApkIntegrationTests` 与 `tests/Bundler.Tests` `AlpineApkTests` 为证据（验证宿主为 Ubuntu x86_64 + docker `alpine:latest`，aarch64 经 qemu binfmt 仿真）。

## 产物与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.apk` 产物（托管三段 gzip 流串联写入器，零原生工具） | 已实现 | APK-1 | `OutputDirectory/<rid>/alpineapk/<package>-<version>-r<release>.apk` + `.sha256` 侧车；gzip 魔数/段数/零块规则逐一断言 |
| 任意构建宿主（含 Windows/macOS CI） | 已实现（Linux 宿主验证）/已由 CI 收编（release-verify alpine-docker + ubuntu-arm 腿） | APK-1 | 纯托管写出，无宿主 OS 门控；跨宿主构建矩阵属 APK-OI-01 |
| 目录载荷 → `/usr/lib/<package-name>/` | 已实现 | APK-1 | 固定安装根；数据段真实 `apk add` 后 `apk info -L` 逐路径断言 |
| `/usr/bin/<command>` 相对符号链接 | 已实现 | APK-1 | `usr/bin/<name>` → `../lib/<package>/<main>`；`BundlerAlpineApkBinLink` 可改名或 `none` 关闭 |
| pax 扩展头 `APK-TOOLS.checksum.SHA1`（十六进制 sha1） | 已实现 | APK-4 | 逐常规文件 + 逐符号链接（按链接目标字符串哈希）；对照真实 `musl` apk 解剖校验，缺此被 `apk add` 报 missing embedded checksum 拒绝 |
| 确定性构建（条目排序、uid/gid 0、mtime/atime/ctime 归一） | 已实现 | APK-1 | tar mtime 0 + 逐条目 pax `ctime=0`/`atime=0`；连产三次逐字节一致（`AlpineApkIntegrationTests` 断言） |
| 失败清理 | 已实现 | APK-1 | 构建失败删除半成品 `.apk` 与侧车；非法 release 变体断言无残留 |

## 元数据（`.PKGINFO`）

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 核心字段 `pkgname`/`pkgver`/`pkgdesc`/`url`/`arch`/`origin`/`size` | 已实现 | APK-1 | `pkgver=<version>-r<release>`（release 默认 0）；`origin` 默认=pkgname；`size` 自算解压数据载荷总和 |
| `datahash`（数据 gzip 流 SHA-256） | 已实现 | APK-1 | `sha256(member<N>.gz)` 逐字节核对断言 |
| `license`/`builddate`/`depend`/`provides`/`triggers` | 已实现 | APK-2 | `depend`/`provides` 分号列表逐行；`triggers` 空格串联绝对路径；`builddate` 默认 0（建议确定性构建别覆盖）；全部旋钮 .PKGINFO 行断言 |
| 版本映射 SemVer→apk（`~`/`+` 拒绝、`builder` 回退） | 已实现 | APK-1 | 预发布映射 `r<previous>.<label>`；显式 `BundlerAlpineApkVersion`/`Release` 规则校验（release 须非负整数） |
| 架构映射 `linux-musl-x64→x86_64`、`linux-musl-arm64→aarch64` | 已实现 | APK-1 | 其他 apk 架构名经 `BundlerAlpineApkArchitecture` 覆盖 |
| 任意绝对路径映射（`/etc` 等载荷外路径） | 已实现 | APK-2 | `@(BundlerAlpineApkFile)` `Destination` 元数据（绝对路径含文件名，拒相对/`..`/`.`/尾斜杠/重复目标）；API 面 `AlpineApkFileEntry` |

## 脚本与安装语义

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 六段安装脚本（pre/post-install、pre/post-deinstall、pre/post-upgrade） | 已实现 | APK-2 | `BundlerAlpineApk*Script` 整文件注入控制段（0755，须非空+LF）；真实 `apk add`/`apk del`/`apk add` 升级 标记断言（install/deinstall/upgrade 三段均实测；upgrade 腿验证 v2 的 `.pre-upgrade`/`.post-upgrade` 执行且 `.post-install` 不触发） |
| `apk add --allow-untrusted` 真实安装/运行/删除 | 已实现 | APK-4 | docker `alpine:latest` 容器内实装 → 脚本标记 → `/usr/bin` 运行 → `apk del` 清理断言 |
| 未签名包拒绝验证（无 `--allow-untrusted`） | 已实现 | APK-4 | `apk add` 报错拒绝断言 |
| 可信公钥安装（`/etc/apk/keys/`） | 已实现 | APK-4 | 自签包 + 公钥入 keys 后免 `--allow-untrusted` 实装通过 |
| aarch64 仿真安装/运行 | 已实现（qemu binfmt）/外部待验收（真机） | APK-4 | tonistiigi/binfmt 下 `apk add` + 运行断言；真机属 APK-OI-02 |
| 桌面集成（.desktop/图标/metainfo） | 明确拒绝 | — | musl/Alpine 是包管理器生态非桌面场景；与本格式不相关 |
| `abuild`/apk 仓库索引与发布工作流 | 不适用（边界外） | — | 工具只产出单包；仓库管理属外部工作流（APK-OI-04 为可选扩展评估） |

## 签名

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 可选 RSA 签名段（`.SIGN.RSA.<密钥文件名>.rsa.pub`） | 已实现 | APK-3 | PEM RSA 私钥（未加密或密码保护）→ PKCS#1 v1.5 RSA+SHA1 签控制 gzip 流，签名段前置于控制段；BouncyCastle 纯托管，无外部进程 |
| 半配置拒绝 | 已实现 | APK-3 | 只给口令不给密钥文件为配置错误（API/MSBuild/CLI 三入口一致） |
| `openssl dgst -sha1 -verify` 独立验签 | 已实现 | APK-4 | 外部工具旁证断言签名与公钥配对 |
| 签名确定性 | 已实现 | APK-3 | RSA PKCS#1 v1.5 确定性签名——同输入三产逐字节一致（含签名段） |
| 签名密钥在 `apk` 官方信任链中的分发 | 外部待验收 | — | 供方需自建渠道分发公钥至目标机 `/etc/apk/keys/`（属运维而非产物语义） |

## 接线面

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `BundlerFormats=alpineapk`（MSBuild） | 已实现 | APK-1 | `linux-musl-*` RID 上扇出；与其他格式混排（`zip;targz;alpineapk`）支持 |
| `bundler --formats alpineapk`（唯一合法名；`apk` 别名已移除——apk 名留给未来 Android 格式） | 已实现 | APK-1 | `Bundling for linux-musl-x64: alpineapk` 等实录可见；`--version` 内嵌真实包版本 |
| `bundler.json` `"alpineapk"` 段（唯一合法节名；`apk` 段名已移除） | 已实现 | APK-1 | `BundlerJsonContext` 覆盖全部格式旋钮 |
| `DotNet.Bundler.AlpineApk` NuGet 直接消费 | 已实现 | APK-4 | `AlpineApkBundler().BuildAsync(BundleConfiguration)` 契约测试 + `tests/Bundler.ApiTests`（`AlpineApkApiTests`，`alpineapk` API 用法） |
| `DotNet.Bundler` 便利元包传递 AlpineApk | 已实现 | APK-1 | `BundleDesktopApplication` Task 接线 + `buildTransitive` 传递 |
| AOT 发布干净性（`netstandard2.0` + `BouncyCastle`） | 已实现 | APK-1 | `Bundler.Cli` `PublishAot` 原生二进制内嵌生成 apk（IL2026 干净） |

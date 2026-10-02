# Linux `.AppImage` 能力矩阵（LINUX-APPIMAGE）

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 每行记录完成条件与边界；本矩阵随 `LINUX-APPIMAGE-1..4` 推进逐行更新，已于 LINUX-APPIMAGE-4 定稿（冻结基线 `0.1.0-alpha.58`）。

## 工具与产物

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 内嵌 `appimagetool`（x86_64/aarch64）+ 两个 type2 runtime + provenance | 已实现 | LINUX-APPIMAGE-1（`0.1.0-alpha.56`） | 四件入 `third_party/appimagetool/` + SHA-256 校验；pinned 构建不内嵌 runtime，始终经 `--runtime-file` 外供——不联网下载 |
| AppDir 组装（`usr/` 树 + `AppRun` + 根 `.desktop` + 图标） | 已实现 | LINUX-APPIMAGE-1 | 共享 freedesktop 生成器复用；脚本式 `AppRun` exec 经 usr/bin 链接；缺省图标用自带默认 PNG；根 `.desktop` 为指向 staged 件的符号链接 |
| 最小可用 `.AppImage` 产出与命名 | 已实现 | LINUX-APPIMAGE-1 | `<pkg>_<version>_<amd64|aarch64>.AppImage` + `.sha256` 侧车；Verify.sh 结构断言全绿 |
| FUSE-less 调用（extract-and-run） | 已实现 | LINUX-APPIMAGE-1 | `APPIMAGE_EXTRACT_AND_RUN=1` 调用工具；产物 `--appimage-extract-and-run` 在三容器实测通过 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| freedesktop 三件套（`.desktop`/hicolor 图标/metainfo） | 已实现 | LINUX-APPIMAGE-1 | 复用 `Bundler.Core` 共享生成器（`AlwaysEmitIcon`）；生成 `Categories` 缺省 `Utility`（appimagetool 硬要求）；`desktop-file-validate` 通过 |
| `BundlerAppImage*` 旋钮面（名称/版本/架构/install-root/bin-link/图标/桌面文件/categories/metainfo） | 已实现 | LINUX-APPIMAGE-1/2 | 九旋钮 + File 映射全接；压缩旋钮移除（决策 10：pinned mksquashfs 仅 zstd）；非法输入拒绝断言覆盖 |
| `BundlerAppImageFile` 任意 AppDir 路径映射 | 已实现 | LINUX-APPIMAGE-2 | AppDir 相对 POSIX 目标；拒绝对路径/`..`/空段/反斜杠/生成件碰撞/已存在路径 |

## 维护者能力

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| scriptlet / systemd unit / conffile | 不适用 | — | AppImage 无安装事务；无包管理器生命周期 |
| 关系字段（Requires 族） | 不适用 | — | 无依赖解析层；契约=自包含载荷 |
| 更新元数据（updateinfo/zsync） | 明确拒绝 | — | 首个版本不做；需要时另立跨格式更新路线 |
| 本体 GPG 签名（`appimagetool --sign`） | 已实现（可选能力） | `SIGN-2` | `BundlerAppImageSigningKeyFile`/`Passphrase` 供密钥才签；隔离 `GNUPGHOME` 导入 + `APPIMAGETOOL_SIGN_PASSPHRASE` 注入；`gpgv` 实测验签；无密钥产物不变 |

## 宿主与验证矩阵

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| x86_64 Linux 宿主构建 + 容器运行冒烟 | 已实现 | LINUX-APPIMAGE-1 | debian:stable/ubuntu:latest/fedora:latest 容器 `--appimage-extract-and-run` 实测输出断言 |
| `linux-musl-x64`/`linux-musl-arm64` 目标 | 已实现（musl-x64 产+跑实测、musl-arm64 产出实测） | 2026-10-01 门禁放行 | runtime 为静态 ELF（非 glibc 链接）——alpine 容器真产 `linux-musl-x64` AppImage，`APPIMAGE_EXTRACT_AND_RUN` 与 FUSE 挂载双路运行 musl 载荷通过；`musl-arm64` 产出实测（runtime-aarch64 嵌入、e_machine=AArch64）、运行腿待仿真 |
| `linux-arm64`（aarch64）产物 | 已实现（产出）/外部待验收（运行） | LINUX-APPIMAGE-1 | 可交叉（决策 4）：嵌入 runtime-aarch64 + `--runtime-file`；e_machine 结构断言；真机运行 OI-01 |
| `appimagelint` 基线 | 已实现（信息级） | LINUX-APPIMAGE-3 | 实跑：有效项全过，✖ 项为载荷 glibc 地板（xenial/trusty）与 `@2` scale 目录解析局限；无 tag 豁免体系 → 信息级接入 Verify.sh 非阻断段 |
| 多格式扇出（`deb;rpm;appimage`） | 已实现 | LINUX-APPIMAGE-1 | 同次 publish 三产物断言（Verify.sh fanout 变体） |
| 跨产方产物 sha256 差异 | 已归因（非缺陷） | 2026-09-29/30 联合测试 | 同输入跨产方 sha 不同——squashfs 元数据与内嵌 runtime 摘要 blob 随构建环境变化，解包树逐字节一致；上游 appimagetool 性质，不在确定性承诺范围 |

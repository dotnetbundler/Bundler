# Linux `.AppImage` 能力矩阵（LINUX-APPIMAGE）

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 每行记录完成条件与边界；本矩阵随 `LINUX-APPIMAGE-1..4` 推进逐行更新，冻结时定稿。

## 工具与产物

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 内嵌 `appimagetool`（x86_64）+ provenance | 计划实现 | LINUX-APPIMAGE-1 | `third_party/appimagetool/` 固定版本 + SHA-256 登记；不运行时下载 |
| AppDir 组装（`usr/` 树 + `AppRun` + 根 `.desktop` + 图标） | 计划实现 | LINUX-APPIMAGE-1 | 共享 freedesktop 生成器复用；脚本式 `AppRun`；缺省图标用自带默认 |
| 最小可用 `.AppImage` 产出与命名 | 计划实现 | LINUX-APPIMAGE-1 | `<产品名>_<version>_<arch>.AppImage` + `.sha256` 侧车；产物 `--appimage-extract` 结构断言 |
| FUSE-less 调用（extract-and-run） | 计划实现 | LINUX-APPIMAGE-1 | 容器内无 FUSE 下实测通过 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| freedesktop 三件套（`.desktop`/hicolor 图标/metainfo） | 计划实现 | LINUX-APPIMAGE-1/2 | 复用 `Bundler.Core` 共享生成器；根 `.desktop` 按 AppDir 约定落位 |
| `BundlerAppImage*` 旋钮面（名称/版本/架构/图标/桌面文件/压缩/文件映射） | 计划实现 | LINUX-APPIMAGE-2 | 与 deb/rpm 旋钮族对齐；非法输入拒绝断言 |
| `BundlerAppImageFile` 任意 AppDir 路径映射 | 计划实现 | LINUX-APPIMAGE-2 | 逃逸校验同 deb/rpm 口径 |

## 维护者能力

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| scriptlet / systemd unit / conffile | 不适用 | — | AppImage 无安装事务；无包管理器生命周期 |
| 关系字段（Requires 族） | 不适用 | — | 无依赖解析层；契约=自包含载荷 |
| 更新元数据（updateinfo/zsync） | 明确拒绝 | — | 首个版本不做；需要时另立跨格式更新路线 |
| 本体 GPG 签名（`appimagetool --sign`） | 计划实现（冻结外后置评估） | `LINUX-APPIMAGE-SIGN`（未排期） | 同 rpm 签名处置口径；冻结基线仅 `sha256` 侧车 |

## 宿主与验证矩阵

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| x86_64 Linux 宿主构建 + 容器运行冒烟 | 计划实现 | LINUX-APPIMAGE-1/3 | debian/ubuntu/fedora 容器 `--appimage-extract-and-run` 实测 |
| `linux-arm64`（aarch64）产物 | 计划实现（视决策 4 实测）/外部待验收（运行） | LINUX-APPIMAGE-1 裁决/3 | 可交叉则结构断言；不可交叉则限 aarch64 宿主 + OI 登记 |
| `appimagelint` 基线 | 计划实现 | LINUX-APPIMAGE-3 | 先评估误报情况再定信息级或硬断言 |
| 多格式扇出（`deb;rpm;appimage`） | 计划实现 | LINUX-APPIMAGE-1 | 同次 publish 三产物断言 |

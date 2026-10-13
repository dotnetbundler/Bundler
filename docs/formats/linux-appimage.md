# Linux `.AppImage`

本文件是 `.AppImage` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project-rules.md`。

## 1. 状态与决策

- `.AppImage` 已冻结（基线 `0.1.0-alpha.58`），仅缺陷修复附回归测试。
- **内嵌固定版本 `appimagetool`**（x86_64+aarch64 二进制 + 两个 type2 runtime 共四件入 `third_party/appimagetool/` + SHA-256 provenance）随 `DotNet.Bundler.AppImage` 包分发；pinned 构建不内嵌 runtime，**始终经 `--runtime-file` 外供**（缺省会联网下载），不联网。
- **不引入 linuxdeploy**（其职责是 gtk/gstreamer 等依赖拷贝编排，超产品边界）；契约：依赖须打进 publish 载荷。
- **构建宿主限 Linux**（appimagetool 是 Linux ELF，与 deb/rpm 任意宿主可产不同）；可交叉——x86_64 宿主可产 aarch64 产物（嵌入 runtime-aarch64，实测 e_machine=0xb7）。
- 一律以 `APPIMAGE_EXTRACT_AND_RUN=1` 调工具（容器/CI 常无 FUSE）。
- 压缩**仅 zstd**：pinned appimagetool 的 mksquashfs 只编译进 zstd（gzip/xz 实测报不支持），不暴露压缩旋钮。
- AppDir 结构：`usr/` 子树复用 `Bundler.Core` 共享 freedesktop 生成器（`.desktop`+hicolor+metainfo+`usr/lib/<pkg>/`+`usr/bin` 相对链接）；根级 `AppRun` 为**受控生成脚本** `exec "$APPDIR/usr/bin/<main>" "$@"`（可读可审，非预编译二进制）；根 `.desktop` 是指向 staged 件的符号链接，`Exec`/`Icon` 按 AppDir 约定修正。
- 图标：`BundlerAppImageIconFile` 可选，缺省用自带默认 PNG（appimagetool 对无图标硬失败）；`Categories` 缺省 `Utility`（硬要求）。
- 后端 `Bundler.AppImage`（netstandard2.0）；工具经受控 `ProcessRunner` 调用，参数不拼 shell。

## 2. 语义对应（deb/rpm → AppImage）

- 无包管理器：scriptlet/systemd/conffile/**关系字段**均不适用；无安装事务，契约=自包含载荷。
- `LicenseFile`/`ChangelogFile` 落 `usr/share/doc|licenses` 保留为 AppDir 内普通文件。
- freedesktop 三件套保留——桌面集成靠解包/辅助工具消费。
- 产物命名 `名称-版本-架构.AppImage` + `.sha256` 侧车；加入 `BundlerFormats` 扇出（`deb;rpm;appimage` 同次 publish 实测）。

## 3. 能力状态

已实现并验证：

- 最小产物全链：AppDir 组装、命名、结构断言；`--appimage-extract` 解包 + 载荷真实运行。
- 三容器（debian:stable/ubuntu:latest/fedora:latest）`--appimage-extract-and-run` 冒烟实测。
- `BundlerAppImage*` 九旋钮 + `BundlerAppImageFile` 任意 AppDir 路径映射（拒绝对/`..`/空段/反斜杠/生成件碰撞）。
- **本体 GPG 签名**（可选）：`BundlerAppImageSigningKeyFile`/`Passphrase` 供密钥才签，隔离 `GNUPGHOME` 导入 + `APPIMAGETOOL_SIGN_PASSPHRASE` 注入，`gpgv` 实测验签；无密钥产物不变。
- musl 目标：`linux-musl-x86_64` alpine 容器真产真跑（extract-and-run 与 FUSE 挂载双路）；`linux-musl-aarch64` 产出实测（运行腿待仿真）。
- `linux-aarch64`：`ubuntu-26.04-arm` 真机腿 `arm64-real-hw.sh --appimage` 直跑断言跑绿。
- `appimagelint` 信息级接入（无 tag 豁免体系，非阻断）：有效项全过，✖ 项为载荷 glibc 地板与 `@2` 目录解析局限。
- 已归因非缺陷：跨产方产物 sha256 差异（squashfs 元数据+内嵌 runtime 摘要随构建环境变化；解包树逐字节一致）——不在确定性承诺范围。

外部待验收（详见 `docs/special-acceptance.md`）：

- 真机观感项；`musl-arm64` 运行腿（仿真）。

## 4. 明确拒绝 / 不适用

- linuxdeploy（依赖编排超边界）、updateinfo/zsync（首版不做）、任意压缩旋钮（工具只支持 zstd）。
- 包管理器生命周期/依赖解析/AppStream 安装语义——无对应概念。

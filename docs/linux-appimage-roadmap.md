# Linux `.AppImage` 路线图（LINUX-APPIMAGE）

> 状态：**`.AppImage` 已冻结于 `0.1.0-alpha.58`**（`LINUX-APPIMAGE-1..4` 全部完成，`linux-appimage-development` 分支并入 `main`；冻结后仅缺陷修复附回归测试）。
> 本文件只放 AppImage 的设计决策与语义契约；泛用规则见 `docs/development-rules.md`。
> 上游依据：Tauri 审计 AppImage 段（快照 `447fa9f`）。

## 1. 决策清单（规划轮裁决对象）

| # | 决策点 | 推荐 | 取舍/依据 |
| --- | --- | --- | --- |
| 1 | 打包工具供应 | **内嵌固定版本 `appimagetool`**（x86_64 必备、aarch64 按需）入 `third_party/appimagetool/` + SHA-256 provenance，随 `DotNet.Bundler.AppImage` 包分发 | 上游 Tauri 用 linuxdeploy 打包时联网下载——违反"工具随包供应"硬规则；appimagetool 是 AppImage 官方规范工具，自包含（内嵌 runtime+mksquashfs） |
| 2 | 不引入 linuxdeploy | **明确拒绝**：linuxdeploy 的职责是依赖拷贝/插件编排（gtk/gstreamer/webkit2gtk），属应用运行时依赖编排，超出产品边界 §2 | AppDir 只装调用方准备好的自包含载荷；契约写进示例文档："依赖须打进 publish 载荷" |
| 3 | 构建宿主限制 | **Linux 宿主限定**（x86_64；aarch64 宿主取决决 4 结论） | appimagetool 是 Linux ELF；与 deb/rpm 不同，本格式不再是"任意宿主可产" |
| 4 | 架构覆盖 | **已裁决（APPIMAGE-1 实测）**：x86_64 宿主产 `amd64` 命名产物；**可交叉**——`runtime-x86_64`/`runtime-aarch64` 双双内嵌并始终经 `--runtime-file` 供应（实测：所钉 appimagetool 构建不内嵌 runtime，缺省会联网下载，必须始终外供）；x86_64 宿主产出的 aarch64 AppImage 为合法 ELF（e_machine=0xb7）；真机运行验收挂 `docs/special-acceptance.md` | `ARCH` env + `--runtime-file` 路径实测成立 |
| 5 | FUSE-less 运行 | 内嵌 appimagetool 一律以 `APPIMAGE_EXTRACT_AND_RUN=1`（或等价 `--appimage-extract-and-run`）方式调用 | 容器/CI 常无 FUSE；上游同样走此路径 |
| 6 | AppDir 结构 | `usr/` 子树复用 `Bundler.Core` 共享 freedesktop 生成器（同 deb/rpm 数据树：`.desktop`+hicolor 图标+metainfo+`usr/lib/<pkg>/`+`usr/bin` 相对链接）；根级 `AppRun` 用**受控生成脚本**（`exec "$APPDIR/usr/bin/<main>" "$@"`）而非预编译二进制 | 上游复用 deb 数据树生成的做法同构；脚本 AppRun 可读可审 |
| 7 | 图标策略 | `BundlerAppImageIconFile` 可选；缺省用 Bundler 自带默认 PNG 图标（`third_party` 登记来源） | appimagetool 对无图标硬失败；与上游"默认 tauri 图标"惯例一致 |
| 8 | `.desktop` 与根图标 | 根级 `<name>.desktop` + `.DirIcon`/根 PNG 按 AppImage 规范落位；根 `.desktop` 由共享生成器产出后按 AppDir 约定修正 `Exec`/`Icon` 字段 | appimagetool 校验根 desktop 与图标存在性 |
| 9 | 产物命名与位置 | `<ProductName>_<version>_<amd64|aarch64>.AppImage` 落 `OutputDirectory/<rid>/appimage/` + `.sha256` 侧车 | 沿用上游文件命名与本仓输出契约 |
| 10 | 压缩 | **已裁决（APPIMAGE-1 实测）**：**仅 zstd**——所钉 appimagetool continuous 构建的 mksquashfs 只编译进 zstd（`--comp gzip`/`xz` 均报 "Compressor not supported" 实测）；故不暴露 `BundlerAppImageCompression` 旋钮，固定走工具默认 zstd | 工具能力约束，非托管限制；上游未钉版本同样由此决定可用集 |
| 11 | 更新元数据 | **明确拒绝**首个版本做 updateinfo/zsync | 上游同样不产 `.zsync`；需要时另立跨格式更新路线 |
| 12 | 本体签名 | **冻结外后置评估**（`LINUX-APPIMAGE-SIGN`，未排期）：appimagetool `--sign` 走 gpg2 | 与 rpm 签名同处置口径；冻结基线仅 `sha256` 侧车 |
| 13 | 真实验证 | 产物 `--appimage-extract` 解包结构断言 + 解出载荷真实运行 + 容器（debian/ubuntu/fedora）内 `--appimage-extract-and-run` 冒烟；`appimagelint` 可用则作信息级，硬基线视其误报情况裁决 | AppImage 无包管理器安装语义，"真实验证"= 规范级运行形态断言 |
| 14 | 多格式扇出 | `appimage` 加入 `BundlerFormats` 扇出契约（`deb;rpm;appimage` 同次 publish） | 沿用 RPM-1 放开的扇出 |
| 15 | 后端形态 | `src/Bundler.AppImage`（netstandard2.0，`DotNet.Bundler.AppImage`），`internal AppImageBundleBackend`+公开 `AppImageBundler` 门面；工具调用为唯一允许的进程出口（封装 `ProcessRunner` 式受控调用，参数不拼 shell） | 全仓统一后端模式；托管写入器不适用（squashfs 由工具产出） |

## 2. 语义对应（deb/rpm → AppImage）

- 无包管理器：`PreInstall`/`PostInstall`/`PreUninstall`/`PostUninstall`/`%config`/systemd unit 均**不适用**（AppImage 无安装事务）。
- 关系字段（Requires 族）**不适用**：无依赖解析层。
- `LicenseFile`/`ChangelogFile` 落 `usr/share/doc|licenses` 语义保留为 AppDir 内普通文件。
- freedesktop 三件套（`.desktop`/hicolor/metainfo）保留——AppImage 的桌面集成靠解包/辅助工具消费这些文件。

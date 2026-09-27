# Linux `.AppImage` 路线图（LINUX-APPIMAGE）

> 状态：**规划轮待用户裁决**（2026-09-27，`linux-appimage-development` 分支；`.rpm` 已冻结于 `0.1.0-alpha.55`）。
> 本文件只放 AppImage 的设计决策、阶段分解与实施证据；泛用规则见 `docs/development-rules.md`。
> 上游依据：`docs/linux-tauri-capability-audit.md` AppImage 段（快照 `447fa9f`）。

## 1. 决策清单（规划轮裁决对象）

| # | 决策点 | 推荐 | 取舍/依据 |
| --- | --- | --- | --- |
| 1 | 打包工具供应 | **内嵌固定版本 `appimagetool`**（x86_64 必备、aarch64 按需）入 `third_party/appimagetool/` + SHA-256 provenance，随 `DotNet.Bundler.AppImage` 包分发 | 上游 Tauri 用 linuxdeploy 打包时联网下载——违反"工具随包供应"硬规则；appimagetool 是 AppImage 官方规范工具，自包含（内嵌 runtime+mksquashfs） |
| 2 | 不引入 linuxdeploy | **明确拒绝**：linuxdeploy 的职责是依赖拷贝/插件编排（gtk/gstreamer/webkit2gtk），属应用运行时依赖编排，超出产品边界 §2 | AppDir 只装调用方准备好的自包含载荷；契约写进示例文档："依赖须打进 publish 载荷" |
| 3 | 构建宿主限制 | **Linux 宿主限定**（x86_64；aarch64 宿主取决决 4 结论） | appimagetool 是 Linux ELF；与 deb/rpm 不同，本格式不再是"任意宿主可产" |
| 4 | 架构覆盖 | x86_64 宿主产 `amd64`（实为 x86_64 命名按上游惯例写 `<arch>` 段）；**aarch64 产物**待 APPIMAGE-1 实测 appimagetool 的 runtime 文件供应方式（`--runtime-file`/内嵌 arch 判定）后裁决：可交叉则嵌入 aarch64 runtime，不可交叉则 aarch64 产物限 aarch64 宿主并登记 OI | appimagetool 的 AppImage runtime 须与目标架构一致；不确定 flag 形态，规划期不臆断 |
| 5 | FUSE-less 运行 | 内嵌 appimagetool 一律以 `APPIMAGE_EXTRACT_AND_RUN=1`（或等价 `--appimage-extract-and-run`）方式调用 | 容器/CI 常无 FUSE；上游同样走此路径 |
| 6 | AppDir 结构 | `usr/` 子树复用 `Bundler.Core` 共享 freedesktop 生成器（同 deb/rpm 数据树：`.desktop`+hicolor 图标+metainfo+`usr/lib/<pkg>/`+`usr/bin` 相对链接）；根级 `AppRun` 用**受控生成脚本**（`exec "$APPDIR/usr/bin/<main>" "$@"`）而非预编译二进制 | 上游复用 deb 数据树生成的做法同构；脚本 AppRun 可读可审 |
| 7 | 图标策略 | `BundlerAppImageIconFile` 可选；缺省用 Bundler 自带默认 PNG 图标（`third_party` 登记来源） | appimagetool 对无图标硬失败；与上游"默认 tauri 图标"惯例一致 |
| 8 | `.desktop` 与根图标 | 根级 `<name>.desktop` + `.DirIcon`/根 PNG 按 AppImage 规范落位；根 `.desktop` 由共享生成器产出后按 AppDir 约定修正 `Exec`/`Icon` 字段 | appimagetool 校验根 desktop 与图标存在性 |
| 9 | 产物命名与位置 | `<ProductName>_<version>_<amd64|aarch64>.AppImage` 落 `OutputDirectory/<rid>/appimage/` + `.sha256` 侧车 | 沿用上游文件命名与本仓输出契约 |
| 10 | 压缩 | `BundlerAppImageCompression` 透传枚举 `{gzip,xz,zstd}`（默认 gzip），APPIMAGE-1 实测 appimagetool 接受值后定稿 | appimagetool 自己做 squashfs 压缩，无托管编码器限制 |
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

## 3. 阶段分解

### LINUX-APPIMAGE-1：内嵌工具集 + 最小可用 AppImage

- 下载并固定 `appimagetool`（x86_64，aarch64 视决策 4 实测结果）入 `third_party/appimagetool/` + provenance（来源/许可/SHA-256）。
- `AppImageBundler` 最小路径：AppDir 组装（`usr/` 树 + 受控 `AppRun` + 根 `.desktop` + 图标）→ extract-and-run 调 appimagetool → 命名/落位/`sha256` 侧车。
- `BundlerFormats=appimage` MSBuild 接线 + `DotNet.Bundler.MSBuild` 装载新 dll。
- 验证：`Bundler.Tests` 断言（AppDir 结构、AppRun 内容、命名）；`tests/Linux.AppImage.Integration/Verify.sh` 产物 `--appimage-extract` 结构断言 + 解出程序真实运行。
- 决策 4/10 的实测结论本阶段回填。
- 示例 `samples/HelloAppImageApp`。

### LINUX-APPIMAGE-2：旋钮面收口

- `BundlerAppImage*` 旋钮面：PackageName/Version/Architecture/IconFile/DesktopFile/Categories/Compression/File 映射等，与 deb/rpm 对齐的 freedesktop 旋钮集合。
- 非法输入拒绝路径与失败不留半成品断言。
- 示例演示全旋钮。

### LINUX-APPIMAGE-3：宿主矩阵与收口

- 容器运行矩阵（`debian:stable`/`ubuntu:latest`/`fedora:latest` 内 `--appimage-extract-and-run` 冒烟，缺镜像记 SKIP）。
- `appimagelint` 评估与基线处置；arm64 产物结构断言（可交叉前提下）或 OI 登记。
- 干净宿主复核：除内嵌 appimagetool 外无外部进程/下载。

### LINUX-APPIMAGE-4：审计复核与冻结

- 上游漂移复核；能力矩阵定稿；OI/MT 收口；冻结基线写入；`docs/roadmap.md` 推进 `ARCHIVE`。

## 4. 实施证据（阶段落地后逐行回填）

（规划轮占位——各阶段完成时按"交付/证据/边界"格式回填，同 deb/rpm-roadmap 惯例。）

# Linux `.AppImage` 路线图（LINUX-APPIMAGE）

> 状态：**`.AppImage` 已冻结于 `0.1.0-alpha.58`**（`LINUX-APPIMAGE-1..4` 全部完成，`linux-appimage-development` 分支并入 `main`；冻结后仅缺陷修复附回归测试）。
> 本文件只放 AppImage 的设计决策、阶段分解与实施证据；泛用规则见 `docs/development-rules.md`。
> 上游依据：Tauri 审计 AppImage 段（快照 `447fa9f`）。

## 1. 决策清单（规划轮裁决对象）

| # | 决策点 | 推荐 | 取舍/依据 |
| --- | --- | --- | --- |
| 1 | 打包工具供应 | **内嵌固定版本 `appimagetool`**（x86_64 必备、aarch64 按需）入 `third_party/appimagetool/` + SHA-256 provenance，随 `DotNet.Bundler.AppImage` 包分发 | 上游 Tauri 用 linuxdeploy 打包时联网下载——违反"工具随包供应"硬规则；appimagetool 是 AppImage 官方规范工具，自包含（内嵌 runtime+mksquashfs） |
| 2 | 不引入 linuxdeploy | **明确拒绝**：linuxdeploy 的职责是依赖拷贝/插件编排（gtk/gstreamer/webkit2gtk），属应用运行时依赖编排，超出产品边界 §2 | AppDir 只装调用方准备好的自包含载荷；契约写进示例文档："依赖须打进 publish 载荷" |
| 3 | 构建宿主限制 | **Linux 宿主限定**（x86_64；aarch64 宿主取决决 4 结论） | appimagetool 是 Linux ELF；与 deb/rpm 不同，本格式不再是"任意宿主可产" |
| 4 | 架构覆盖 | **已裁决（APPIMAGE-1 实测）**：x86_64 宿主产 `amd64` 命名产物；**可交叉**——`runtime-x86_64`/`runtime-aarch64` 双双内嵌并始终经 `--runtime-file` 供应（实测：所钉 appimagetool 构建不内嵌 runtime，缺省会联网下载，必须始终外供）；x86_64 宿主产出的 aarch64 AppImage 为合法 ELF（e_machine=0xb7）；运行验收仍归 OI-01 | `ARCH` env + `--runtime-file` 路径实测成立 |
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

### LINUX-APPIMAGE-1（已实现，`0.1.0-alpha.56`）

- 交付：`third_party/appimagetool/` 四件（tool x86_64/aarch64 + runtime x86_64/aarch64，SHA-256 provenance）；`src/Bundler.AppImage`（netstandard2.0）：`AppImageBundler`/`AppImageBundleBackend`/`AppDirBuilder`/`AppImageToolset`（嵌入资源→哈希校验→缓存目录落位）/`AppImageProcessRunner`/`AppImageIdentity`；`BundlerFormats=appimage` 接线 + MSBuild 包装载 dll；`FreedesktopFiles.Options.AlwaysEmitIcon`；示例 `samples/HelloAppImageApp`。
- 证据：`Bundler.Tests` 171/171（新增 8 项：AppDir 结构/AppRun 内容/命名/默认图标/非法拒绝/真实构建/交叉构建/接线断言）；`tests/Linux.AppImage.Integration/Verify.sh` 全绿：`--appimage-extract` 结构断言 + 解出 `AppRun` 与 `--appimage-extract-and-run` 真实运行 + 覆盖/桌面覆盖/失败变体 + aarch64 ELF 结构断言 + `deb;rpm;appimage` 扇出 + docker debian/ubuntu/fedora 容器冒烟 + 直 API nupkg 消费。
- 实测修正：决策 4 可交叉（runtime 内嵌方案）；决策 10 仅 zstd（pinned mksquashfs 约束）；pinned appimagetool 不带 `--runtime-file` 会联网下载 runtime——已改为始终内嵌供应；appimagetool 强制 `Categories=`——生成件缺省补 `Utility`。
- 边界：`BundlerAppImageFile` 任意映射归 APPIMAGE-2；appimagelint 归 APPIMAGE-3；aarch64 真机运行 OI-01。

### LINUX-APPIMAGE-2（已实现，`0.1.0-alpha.57`）

- 交付：`AppImageFileEntry` + `AppImageBundleConfiguration.Files`（AppDir 相对 POSIX 目标，拒绝对路径/`..`/`.`/空段/反斜杠）；落位晚于生成件，与 `AppRun`/`.DirIcon`/根 `<name>.desktop`/`<name>.png` 及已存在路径碰撞即拒绝；MSBuild `@(BundlerAppImageFile)`（`Destination` 元数据）接线。
- 证据：`Bundler.Tests` 173/173（新增 2 项：任意文件落位 + 8 类非法目标/碰撞/缺源拒绝）；`Verify.sh` 新增 files 变体（解出断言内容一致）与 bad-file 失败变体（绝对路径目标 publish 失败）；示例 `HelloAppImageApp` 演示 `HelloAppImageFiles=1`。
- 边界：压缩旋钮已在 APPIMAGE-1 按实测移除（决策 10）；freedesktop 旋钮族九件在 APPIMAGE-1 一次接齐，本阶段仅余 File 映射收口。
- 阶段范围修正：原 APPIMAGE-2 范围中除 File 映射外的旋钮在 APPIMAGE-1 已落地，故此阶段实质为 File 映射 + 拒绝路径收口。

### LINUX-APPIMAGE-3（已实现，`0.1.0-alpha.58`）

- 交付：aarch64 产物载荷深读断言（`hsqs` 魔数定位 squashfs 偏移，unsquashfs 直读断言入口二进制 EM_AARCH64——不执行 aarch64 运行时的前提下覆盖载荷层）；appimagelint 信息级段入 `Verify.sh`（检测到才跑、永不阻断）；干净宿主进程出口审计。
- appimagelint 裁决（信息级而非硬断言，理由入档）：其报告多为载荷 ABI 属性（宿主构建 glibc 地板，xenial/trusty 未达属预期）与工具自身局限——不能解析 freedesktop `@2` scale 目录（`48x48@2` 是规范内合法 scale 路径）且无 tag 豁免体系；glibc/libstdc++/图标/.desktop 有效项全为 ✔ 或预期性 ✖。
- 干净宿主审计：`src/Bundler.AppImage` 进程出口仅 appimagetool（固定 argv）与 `chmod`/`ln` 小工具调用，无 shell、无网络下载——与决策 1/5/15 一致。
- 证据：`Verify.sh` 全绿含新 arm64 载荷断言与 appimagelint 信息级实跑输出；容器矩阵（debian/ubuntu/fedora）维持全绿。
- 边界：aarch64 真机执行仍为 OI-01；appimagelint 保持信息级（硬基线需其支持 tag/豁免机制，登记留尾）。

### LINUX-APPIMAGE-4（已实现，`0.1.0-alpha.58`）

- 上游复核：`git ls-remote` 显示 `tauri dev` HEAD 由 `447fa9f` 漂移至 `d15cf9b`；对审计引用文件（`bundle/linux/appimage/{mod,linuxdeploy}.rs`、`freedesktop/mod.rs`、`config.rs`、`category.rs`）按两快照逐一取回 diff——**全部字节级一致**，AppImage 侧审计行基线保持有效。
- 收口：能力矩阵定稿无悬空"计划实现"行（GPG 签名按 rpm 口径标冻结外后置评估 `LINUX-APPIMAGE-SIGN`）；OI-01..05 维持登记，OI-06 消解；MT-01..05 全量保留。
- 冻结基线：`AppImageBundleConfiguration` 配置面与行为契约冻结于 `0.1.0-alpha.58`；冻结测试向量 = `Bundler.Tests` 173/173 + `Verify.sh` 全绿（extract/AppRun 真实运行、三容器矩阵、arm64 载荷深读、appimagelint 信息级段）。
- 冻结后仅接受带回归测试的缺陷修复。

**外部待验收实证回填（2026-10-05，`0.1.0-alpha.70`，alpine-docker 宿主）**：

- **musl 消费侧 FUSE 路径**（LINUX-APPIMAGE-OI-04 补证）：特权容器（`--device /dev/fuse --cap-add SYS_ADMIN`+`apk add fuse`）内 musl AppImage `./app.AppImage hi` 真挂载 `/tmp/.mount_*` 执行 rc=0、`--appimage-extract-and-run` rc=0——musl 下 FUSE 挂载运行实测可用。

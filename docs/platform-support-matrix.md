# 宿主与版本下限矩阵

本文汇总三类下限：各后端打包工具（能力）下限、MSBuild 入口下限（按后端分）、CLI 入口下限（含 AOT 二进制）。
口径分三级并如实标注：**契约/代码口径**（源码硬约束）、**官方支持口径**（上游支持矩阵，随支持期滚动）、**已实测口径**（本仓库实际跑通的宿主，单台结果不外推）。
三层下限定义见 `docs/development-rules.md` 第 3 节；安装侧版本下限（产物对目标系统的要求）单列，不与构建宿主混记。

## 1. 各后端打包工具（能力）下限

| 后端/格式 | 打包工具 | 构建宿主 OS（契约口径） | 构建宿主版本下限 | 产物安装侧下限 |
| --- | --- | --- | --- | --- |
| `.nsis` | 内嵌 makensis 3.12-r1（`hosts/{win-x86,linux-x64,linux-arm64,osx-x64,osx-arm64}` 五宿主二进） | Windows / Linux / macOS，x64 与 arm64 | Windows 宿主经 win-x86 工具全架构可跑（x64/arm64 经 x86 仿真）；Linux/macOS 宿主版本随工具构建基线——已实测 ubuntu 22.04（glibc 2.35）、macOS 26.5 arm64；musl 宿主未实测 | NSIS 3.x 安装器面向 Windows NT 系（XP 起理论可达，老版本未实测）；x86 stub 在 x64/arm64 Windows 经 WoW64/x86 仿真装卸实过（win-x64/win-arm64/win-x86 产物均在真实 Windows 装卸通过） |
| `.msi` | 内嵌 WiX 3.14.1（candle/light/wix.dll，`.NET Framework 4.5`、32 位进程） | **仅 Windows**（代码硬拒非 Windows：`PlatformNotSupportedException`） | 宿主须预装 .NET Framework 4.5+：Windows 10/11 预装满足零额外安装；Windows 7 需另装（未实测）；ARM64 宿主走 x86 仿真（验证矩阵见 `docs/msi-roadmap.md`） | `InstallerVersion="500"` → Windows Installer 5.0 = **Windows 7 / Server 2008R2 起** |
| `.app` | 纯托管骨架+plist 写入器（无原生工具） | 任意 .NET 宿主 | 同后端下限；非 POSIX 宿主产物丢 unix 执行位（既有降级警告），完整 .app 建议 POSIX 宿主 | `.app` 对 macOS 的版本要求由应用自身 `LSMinimumSystemVersion` 决定（调用方显式配置，不配置不写入） |
| `.dmg` | 宿主 `hdiutil`/`osascript`/`SetFile`（不可再分发） | **仅 macOS**（代码硬拒） | Intel 宿主约 macOS 10.5+（hdiutil 自 10.5 起）；arm64 宿主天然 ≥ macOS 11.0（硬件边界） | 产物挂载侧按压缩格式：默认 `Ulmo` 需 macOS 10.12+（`Udzo`/`Udbz` 可到更老） |
| `.pkg` | 宿主 `pkgbuild`/`productbuild` | **仅 macOS**（代码硬拒） | macOS 10.7+（pkgbuild/productbuild 自 10.7 引入）；arm64 宿主天然 ≥ 11.0 | `.pkg` 安装侧随 Apple Installer 支持面（组件包/分发包格式与构建工具同代引入，保守记 10.7+，更低版本未实测） |
| `.deb` | 纯托管 ar/tar/gzip 写入器 | 任意 .NET 宿主 | 同后端下限 | 面向 dpkg 系发行版（Debian/Ubuntu 及衍生）；EOL 发行版只记可运行不承诺 |
| `.rpm` | 纯托管 lead/header/cpio/gzip 写入器 | 任意 .NET 宿主 | 同后端下限 | 面向 RPM 系发行版；SUSE/RHEL 旧版宏差异如实记录；载荷仅 gzip 压缩（xz/zstd 拒绝） |
| `.AppImage` | 内嵌 appimagetool + type2 runtime（x86_64/aarch64，SHA-256 provenance，始终 `--runtime-file` 外供） | **仅 Linux**（appimagetool 为 ELF，代码硬拒） | Linux x86_64/aarch64 glibc 宿主——已实测 ubuntu 22.04 x86_64 与 qemu aarch64；musl 宿主未实测 | 产物运行下限=载荷自身 glibc/运行时约束 + 宿主 FUSE 或 `--appimage-extract` 解出 |
| `.apk`（Alpine） | 纯托管三段 gzip 写入器（可选 RSA 签名经 BouncyCastle） | 任意 .NET 宿主 | 同后端下限 | 面向 apk 系发行版（Alpine 及衍生，musl）；已实测 `alpine:latest` 实装/卸载/签名验签、aarch64 binfmt 运行 |
| `.zip`/`.tar.gz` | 纯托管写入器（zip 自实现 unix mode/symlink，tar 复用 deb 件） | 任意 .NET 宿主 | 同后端下限 | 任意可解压宿主；`.zip` 拒绝 >4GB（Zip64 不支持）；mode/symlink 还原依赖解出工具能力 |
| 签名（Windows） | 内嵌 Authenticode provider（无 signtool 依赖） | **仅 Windows** 宿主 | 同该宿主 MSBuild/后端下限 | 生产证书/时间戳属外部待验收 |
| 签名/公证（macOS） | 宿主 `codesign`/`xcrun notarytool`/`xcrun stapler` | **仅 macOS** | codesign 自 macOS 10.5 起；**公证腿** `notarytool`/`stapler` 需 Xcode 13+（宿主约 macOS 11.3+，对齐 Tauri）；`.icon`→`Assets.car` 需 Xcode 26+（约 macOS 15.6+）为可降级可选 | 公证是唯一能抬高打包工具下限的环节；不公证时 `.app`+`.dmg`+`.pkg` 链下限如上表各行 |

## 2. MSBuild 入口下限（按后端分）

公共底座：任务链全部为 `netstandard2.0` 资产，须同时被 .NET Framework `MSBuild.exe` 与 `dotnet msbuild` 双宿主加载（契约见 `docs/development-rules.md` §3）。

- **契约口径**：`.NET Framework 4.7.2+` 的 `MSBuild.exe`（netstandard2.0 可靠加载下限）或任意版本 .NET SDK 的 `dotnet msbuild`；
- **官方支持口径**：建议现存受支持 .NET SDK（随 Microsoft 支持期滚动）；
- **已实测口径**：`dotnet` SDK（.NET 10）在 Windows x64 / ubuntu 22.04 x86_64 / macOS 26.5 arm64 / qemu aarch64 四宿主构建通过；`.NET Framework MSBuild.exe` 宿主为契约要求、尚未实测。
- `BundlerUniversalRuntimeIdentifiers`（osx universal 编排）与内层 `dotnet publish` 调用额外要求 PATH 上有 .NET SDK；该旋钮仅接受 `osx-*` RID。

| 后端 | MSBuild 入口可构建宿主 | 入口版本下限 | 附加条件 |
| --- | --- | --- | --- |
| `Bundler.Nsis` | Windows / Linux / macOS，x64 与 arm64 | 公共底座；NSIS 宿主二进覆盖同上表 | 无外部进程之外的依赖 |
| `Bundler.Wix` | **仅 Windows** | 公共底座 + 宿主预装 .NET Framework 4.5+（WiX 工具自身需求，Win10+ 预装满足） | MSI 需 Windows ANSI 代码页（UTF-7/8 拒绝） |
| `Bundler.MacApp` | 任意 .NET 宿主 | 公共底座 | 非 POSIX 宿主丢 exec 位（降级警告）；签名/公证见矩阵 1 |
| `Bundler.MacDmg` | **仅 macOS** | 公共底座 + 矩阵 1 的 macOS 版本行 | `hdiutil`/`osascript`/`SetFile` 须在 PATH/系统位 |
| `Bundler.MacPkg` | **仅 macOS** | 公共底座 + macOS 10.7+ | `pkgbuild`/`productbuild` 须在 PATH |
| `Bundler.Deb` / `Bundler.Rpm` / `Bundler.AlpineApk` / `Bundler.Archive` | 任意 .NET 宿主 | 公共底座 | 零外部进程；入口不抬高后端下限 |
| `Bundler.AppImage` | **仅 Linux**（x86_64/aarch64） | 公共底座 | appimagetool ELF 硬门控 |
| `Bundler.MSBuild`（universal 编排） | 公共底座宿主 + PATH 有 `dotnet` | 内层 `dotnet publish` 走调用方 SDK | RID 限 `osx-*`，逐条校验（非 osx-*/`..`/分隔符/`;`/`"` 拒） |

## 3. CLI 入口下限（含 AOT 二进制）

CLI 两种分发形态下限分开记：

### 3.1 `dotnet tool` 形态（`DotNet.Bundler.Cli` nupkg，`bundler` 命令）

- 宿主须装 **.NET 10 运行时**（TFM `net10.0`）：官方支持口径= .NET 10 支持矩阵（Windows 10 1607+/Server 2016+、macOS 14+、现代 glibc/musl 发行版，随支持期滚动）。
- `centos7`/glibc 2.17 级宿主明确不支持（矩阵已载，不属回归）。
- `DOTNET_ROOT` 须指向 .NET 安装根（shim 常规事项，非缺陷）。
- 格式可产性跟随各后端宿主门控（表 1）：非本宿主格式 `validate`/`plan` 可达，`bundle` 由后端如实拒绝。

### 3.2 原生 AOT 二进制形态（`dotnet publish -r <rid>`）

- 免 .NET 运行时；RID 扇出=NativeAOT 支持集（`win-x64`/`win-arm64`、`linux-x64`/`linux-arm64`、`linux-musl-x64`/`linux-musl-arm64`、`osx-x64`/`osx-arm64`）。
- 运行下限≈.NET 10 支持面：Windows 10 1607+/Server 2016+、macOS 14+、Linux glibc ≥2.23 / musl ≥1.2.2（技术口径；官方支持以滚动矩阵为准）。
- **已实测**：linux-x64（ubuntu 22.04，~50MB ELF，`--version`+bundle 实跑）、osx-arm64（macOS 26.5，17.2MB Mach-O 实跑）；win-x64 侧 CLI 经 dotnet tool 形态在 Windows Server 2022 冒烟通过，win-x64 AOT 二进制未实测；其余 RID 为 `dotnet publish` 例行扩展（CLI-OI-01 已消解，不再单独立项）。
- 宿主裁剪：非本宿主后端被裁（linux 二进制内无 wix3141/hdiutil 残段，`Verify.sh` 硬断言）；可产性格式仍受表 1 门控（linux-x64 二进制产 msi/dmg/pkg 会退出 2）。
- 体积与全 RID 分发成本：约 17–50MB/二进制，随 RID 扇出成本未做正式分发承诺。

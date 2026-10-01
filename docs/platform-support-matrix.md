# 宿主与版本下限矩阵

本文汇总三类下限：各后端打包工具（能力）下限、MSBuild 入口下限（按后端分）、CLI 入口下限（含 AOT 二进制）。
口径分三级并如实标注：**契约/代码口径**（源码硬约束）、**官方支持口径**（上游支持矩阵，随支持期滚动）、**已实测口径**（本仓库实际跑通的宿主，单台结果不外推）。
三层下限定义见 `docs/development-rules.md` 第 3 节；安装侧版本下限（产物对目标系统的要求）单列，不与构建宿主混记。

## 1. 各后端打包工具：生产侧 × 消费侧

记号：

- **✓** = 已实测通过
- **○** = 支持但未实测
- **留空** = 不支持

格内 = 记号 + 版本下限（无版本下限记"不限"，条件跟在版本后）；版本数写在下限约束的格子上。

### 1.1 生产侧：什么宿主能产出该格式

| 格式＼宿主 | Win x64 | Win arm64 | Win x86 | mac x64 | mac arm64 | Linux x64 | Linux arm64 | Linux musl |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `nsis` | ✓ NT4+ | ○ 仿真 | ○ NT4+ | ○ 15.5+ | ✓ 11+ | ✓ glibc2.14+ | ✓ glibc2.17+ |  |
| `msi` | ✓ .NETFx4.5 | ○ .NETFx4.5 | ○ .NETFx4.5 |  |  |  |  |  |
| `app` | ✓ 不限·未签名·丢exec位 | ○ 不限·未签名·丢exec位 | ○ 不限·未签名·丢exec位 | ○ 不限 | ✓ 不限 | ✓ 不限·未签名 | ✓ 不限·未签名 | ○ 不限·未签名 |
| `dmg` |  |  |  | ○ 10.15+ | ✓ 11+ |  |  |  |
| `pkg` |  |  |  | ○ 10.7+ | ✓ 11+ |  |  |  |
| `deb` | ✓ 不限 | ○ 不限 | ○ 不限 | ○ 不限 | ✓ 不限 | ✓ 不限 | ✓ 不限 | ○ 不限 |
| `rpm` | ✓ 不限 | ○ 不限 | ○ 不限 | ○ 不限 | ✓ 不限 | ✓ 不限 | ✓ 不限 | ○ 不限 |
| `AppImage` |  |  |  |  |  | ✓ 不限 | ✓ 不限 | ○ 不限 |
| `apk` | ✓ 不限 | ○ 不限 | ○ 不限 | ○ 不限 | ✓ 不限 | ✓ 不限 | ✓ 不限 | ○ 不限 |
| `zip` / `tar.gz` | ✓ 不限 | ○ 不限 | ○ 不限 | ○ 不限 | ✓ 不限 | ✓ 不限 | ✓ 不限 | ○ 不限 |

- nsis：win-x86 工具覆盖全部 Windows 架构（x64 WoW64、arm64 仿真契约口径），PE 子系统下限 4.0（NT4 头口径）；osx-x64 二进 Mach-O 头声明 MIN_MACOSX=15.5，osx-arm64 未声明（arm64 硬件≥11）；linux 为 glibc 动态链接，musl 与 32 位无工具。依据：`third_party/nsis` 内嵌二进头 + [NSIS 官方文档](https://nsis.sourceforge.io/Docs/Chapter1.html)。
- WiX（msi）工具目标框架 `.NETFramework,Version=v4.5` 且为 32 位进程（见 [msi-roadmap.md](msi-roadmap.md)）；.NET Framework 4.5 可装于 Vista SP2+/Win7 SP1+，Win8+ 预装（[Microsoft 系统要求](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)）。
- `app`/`deb`/`rpm`/`apk`/`zip`·`tar.gz` 纯托管零外部进程；`app` 仅 macOS 宿主可产已签名束，Windows 宿主产 `.app` 丢执行位（警告建议经 zip/tar 投递）；zip/tar.gz 条目模式由 libc 或 ELF/shebang/Mach-O 魔数探测，Windows 宿主不丢 exec 位。
- `dmg` 默认 `Ulmo` 压缩创建/挂载均需 macOS 10.15+（`Ulfo` 10.11+，`Udzo`/`Udbz` 可到更老，[hdiutil(1) man](https://keith.github.io/xcode-man-pages/hdiutil.1.html)）；`pkg` 系统工具自 macOS 10.7 起存在（[mac-pkg-roadmap.md](mac-pkg-roadmap.md)）。
- AppImage 内嵌 appimagetool 与运行时均为 static-pie ELF、无 libc 依赖（`third_party/appimagetool`），musl 未实测记 ○。
- 实测宿主：Windows Server 2022、macOS 26.5 arm64、ubuntu 22.04 x86_64、qemu aarch64。

### 1.2 消费侧：产物能装/能跑在什么系统

| 格式＼宿主 | Win x64 | Win arm64 | Win x86 | mac x64 | mac arm64 | Linux x64 | Linux arm64 | Linux musl |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `nsis` | ✓ NT4+ | ○ 仿真 | ✓ NT4+ |  |  |  |  |  |
| `msi` | ✓ 7+ | ○ 7+ | ✓ 7+ |  |  |  |  |  |
| `app` |  |  |  | ○ 自设 | ✓ 自设 |  |  |  |
| `dmg` |  |  |  | ○ 10.15+ | ✓ 10.15+ |  |  |  |
| `pkg` |  |  |  | ○ 10.7+ | ✓ 10.7+ |  |  |  |
| `deb` |  |  |  |  |  | ✓ 不限 | ✓ 不限 |  |
| `rpm` |  |  |  |  |  | ✓ 不限 | ✓ 不限 |  |
| `AppImage` |  |  |  |  |  | ✓ FUSE | ✓ FUSE | ○ FUSE |
| `apk` |  |  |  |  |  |  |  | ✓ 不限 |
| `zip` / `tar.gz` | ✓ 不限 | ○ 不限 | ○ 不限 | ○ 不限 | ✓ 不限 | ✓ 不限 | ✓ 不限 | ○ 不限 |

- nsis 安装器 NT 系全兼容：Unicode stub 不支持 95/98/ME（[NSIS Chapter1](https://nsis.sourceforge.io/Docs/Chapter1.html)），内嵌 stub PE 子系统=4.0；arm64 经 x86 仿真属契约口径未实装。
- `msi` 由 `InstallerVersion=500` 推出：Windows Installer 5.0 随 Win7/Server2008R2 起（[Microsoft 版本对应表](https://learn.microsoft.com/en-us/windows/win32/msi/released-versions-of-windows-installer)）。
- `app` 自设 = 应用自定 `LSMinimumSystemVersion`；`dmg` 默认 `Ulmo` 挂载/创建均需 10.15+（[hdiutil(1) man](https://keith.github.io/xcode-man-pages/hdiutil.1.html)）。
- `deb`/`rpm`/`apk` 各对 dpkg/rpm/apk 系发行版；`AppImage` 运行时为 static-pie（无 libc 依赖），需 FUSE 或 `--appimage-extract`，musl 未实测记 ○。
- `zip`/`tar.gz` 任意可解压宿主；`.zip` 拒 >4GB，mode/symlink 还原依赖解出工具。

### 1.3 签名（横切能力，单独记不占格式行）

| 签名项＼宿主 | Win x64 | Win arm64 | Win x86 | mac x64 | mac arm64 | Linux | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Windows Authenticode | ✓ | ○ | ○ |  |  |  | 内嵌 provider，无 signtool 依赖；生产证书/时间戳外部待验 |
| macOS codesign/公证 |  |  |  | ○ | ○ |  | codesign≥10.5；公证 Xcode 13+（宿主约 11.3+）；Assets.car 需 Xcode 26+（可降级）；生产证书/真机公证外部待验 |

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

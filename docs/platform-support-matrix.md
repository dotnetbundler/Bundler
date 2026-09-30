# 宿主与版本下限矩阵

本文汇总三类下限：各后端打包工具（能力）下限、MSBuild 入口下限（按后端分）、CLI 入口下限（含 AOT 二进制）。
口径分三级并如实标注：**契约/代码口径**（源码硬约束）、**官方支持口径**（上游支持矩阵，随支持期滚动）、**已实测口径**（本仓库实际跑通的宿主，单台结果不外推）。
三层下限定义见 `docs/development-rules.md` 第 3 节；安装侧版本下限（产物对目标系统的要求）单列，不与构建宿主混记。

## 1. 各后端打包工具：生产侧 × 消费侧

记号：**✓**=已实测通过，**○**=支持（契约或官方口径，未实测），**✗**=不支持，**—**=不适用（该格式不面向该宿主）。
宿主版本下限另列于表注；格内只答"行/不行"。

### 1.1 生产侧：什么宿主能产出该格式

| 格式 | Win x64 | Win arm64 | Win x86 | Linux x64 | Linux arm64 | Linux musl | mac x64 | mac arm64 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `.nsis` [a] | ✓ | ○ | ○ | ✓ | ✓ | ✗ | ○ | ✓ |
| `.msi` [b] | ✓ | ○ | ○ | ✗ | ✗ | ✗ | ✗ | ✗ |
| `.app` [c] | ✓ | ○ | ○ | ✓ | ✓ | ○ | ○ | ✓ |
| `.dmg` [d] | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ○ | ✓ |
| `.pkg` [e] | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ○ | ✓ |
| `.deb` [f] | ✓ | ○ | ○ | ✓ | ✓ | ○ | ○ | ✓ |
| `.rpm` [f] | ✓ | ○ | ○ | ✓ | ✓ | ○ | ○ | ✓ |
| `.AppImage` [g] | ✗ | ✗ | ✗ | ✓ | ✓ | ✗ | ✗ | ✗ |
| `.apk` [f] | ✓ | ○ | ○ | ✓ | ✓ | ○ | ○ | ✓ |
| `.zip` / `.tar.gz` [f] | ✓ | ○ | ○ | ✓ | ✓ | ○ | ○ | ✓ |
| Windows 签名（Authenticode） [h] | ✓ | ○ | ○ | ✗ | ✗ | ✗ | ✗ | ✗ |
| macOS codesign/公证 [i] | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ○ | ○ |

### 1.2 消费侧：产物能装/能跑在什么系统

| 格式 | Win x64 | Win arm64 | Win x86 | mac x64 | mac arm64 | Linux x64 | Linux arm64 | Linux musl |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `.nsis` [a] | ✓ | ✓ | ✓ | — | — | — | — | — |
| `.msi` [b] | ✓ | ○ | ✓ | — | — | — | — | — |
| `.app` [c] | — | — | — | ○ | ✓ | — | — | — |
| `.dmg` [d] | — | — | — | ○ | ✓ | — | — | — |
| `.pkg` [e] | — | — | — | ○ | ✓ | — | — | — |
| `.deb` [f] | — | — | — | — | — | ✓ | ✓ | — |
| `.rpm` [f] | — | — | — | — | — | ✓ | ✓ | — |
| `.AppImage` [g] | — | — | — | — | — | ✓ | ✓ | ✗ |
| `.apk` [f] | — | — | — | — | — | — | — | ✓ |
| `.zip` / `.tar.gz` [f] | ✓ | ○ | ○ | ○ | ✓ | ✓ | ✓ | ○ |

### 表注（版本下限与条件）

- **[a] `.nsis`**：生产侧任意 Windows 架构皆走内嵌 `win-x86` makensis（x64 经 WoW64、arm64 经 x86 仿真）；linux/osx 走内嵌 `linux-{x64,arm64}`、`osx-{x64,arm64}` 二进，版本随工具构建基线（已实测 ubuntu 22.04/macOS 26.5 arm64/Win Server 2022/qemu aarch64）；musl 宿主无对应工具（glibc ELF，推断不可跑）。消费侧=Windows NT 系（官方面 XP 起，更老未实测），x86 stub 经 WoW64/x86 仿真在全架构实过。
- **[b] `.msi`**：生产侧仅 Windows，宿主须预装 .NET Framework 4.5+（Win10+ 自带，Win7 需另装未实测）；消费侧 `InstallerVersion="500"` → Windows Installer 5.0 = **Windows 7/Server 2008R2 起**；win-arm64 消费格为 ○（arm64 MSI 已产出并结构验证，真机 arm64 安装未验）。
- **[c] `.app`**：生产侧任意 .NET 宿主，非 POSIX 宿主产物丢 unix 执行位（降级警告）。消费侧 macOS 版本由应用 `LSMinimumSystemVersion` 决定（不配置不写入）；实测 macOS 26.5 arm64 实装+运行。
- **[d] `.dmg`**：生产侧仅 macOS——Intel 宿主约 10.5+（hdiutil 自 10.5 起）、arm64 宿主 ≥11.0（硬件边界）。消费侧按压缩格式：默认 `Ulmo` 需 macOS 10.12+（`Udzo`/`Udbz` 可到更老）。
- **[e] `.pkg`**：生产侧仅 macOS ≥10.7（pkgbuild/productbuild 自 10.7 引入）、arm64 ≥11.0；消费侧随 Apple Installer 支持面（保守记 10.7+，更低未实测）；实过 per-user 域免提权安装+收据。
- **[f] 纯托管后端（deb/rpm/apk/zip/targz）**：零外部进程，生产侧=任意 .NET 宿主，○ 格仅为未实测而非不支持。消费侧 deb→dpkg 系、rpm→RPM 系、apk→apk 系（Alpine 及衍生，musl）；zip/targz 任意可解压宿主，`.zip` 拒 >4GB，mode/symlink 还原依赖解出工具。
- **[g] `.AppImage`**：生产侧仅 Linux x86_64/aarch64（appimagetool 为 ELF，代码硬拒），musl 宿主无对应运行时。消费侧 type2 runtime 为 glibc 链接 → musl ✗；运行需 FUSE 或 `--appimage-extract`，版本下限=载荷自身约束。
- **[h] Windows 签名**：内嵌 Authenticode provider（无 signtool 依赖）仅 Windows 宿主；生产证书/时间戳属外部待验收。
- **[i] macOS 签名/公证**：codesign ≥10.5；`notarytool`/`stapler` 需 Xcode 13+（宿主约 macOS 11.3+）；`.icon`→`Assets.car` 需 Xcode 26+（约 macOS 15.6+，可降级可选）。公证是唯一抬高生产侧下限的环节；生产证书/真机公证属外部待验收。

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

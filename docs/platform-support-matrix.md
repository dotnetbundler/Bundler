# 宿主与版本下限矩阵

本文汇总三类下限：各后端打包工具（能力）下限、MSBuild 入口下限（按后端分）、CLI 入口下限（含 AOT 二进制）。
口径分三级并如实标注：**契约/代码口径**（源码硬约束）、**官方支持口径**（上游支持矩阵，随支持期滚动）、**已实测口径**（本仓库实际跑通的宿主，单台结果不外推）。
三层下限定义见 `docs/development-rules.md` 第 3 节；安装侧版本下限（产物对目标系统的要求）单列，不与构建宿主混记。

## 1. 各打包工具：生产侧 × 消费侧

| 格式＼系统 | 工具 | Windows | macOS | Debian Family | Red Hat Family | Alpine |
| --- | --- | --- | --- | --- | --- | --- |
| `nsis` 生产 | [NSIS 3.12](https://nsis.sourceforge.io/)（[toolset r1](https://github.com/dotnetbundler/NsisToolset)） | ✓ 2000+ | ✓ 10.13+ | ✓ glibc2.17+ | ✓ glibc2.17+ |   |
| `nsis` 消费 |  | ✓ NT4+ |   |   |   |   |
| `msi` 生产 | [WiX 3.14.1](https://github.com/wixtoolset/wix3) | ✓ Fx4.5 |   |   |   |   |
| `msi` 消费 |  | ✓ 7+ |   |   |   |   |
| `app` 生产 | 托管实现 | ✓\*  | ✓  | ✓\*  | ✓\*  | ✓\*  |
| `app` 消费 |  |   | ✓ |   |   |   |
| `dmg` 生产 | 系统 [hdiutil](https://keith.github.io/xcode-man-pages/hdiutil.1.html) |   | ✓ 10.15+ |   |   |   |
| `dmg` 消费 |  |   | ✓ 10.15+ |   |   |   |
| `pkg` 生产 | 系统 [pkgbuild](https://keith.github.io/xcode-man-pages/pkgbuild.1.html)/[productbuild](https://keith.github.io/xcode-man-pages/productbuild.1.html) |   | ✓ 10.7+ |   |   |   |
| `pkg` 消费 |  |   | ✓ 10.7+ |   |   |   |
| `deb` 生产 | 托管实现 | ✓  | ✓  | ✓  | ✓  | ✓  |
| `deb` 消费 |  |   |   | ✓ |   |   |
| `rpm` 生产 | 托管实现 | ✓ | ✓ | ✓ | ✓ | ✓ |
| `rpm` 消费 |  |   |   |   | ✓ |   |
| `AppImage` 生产 | [appimagetool](https://github.com/AppImage/appimagetool) [b295](https://github.com/AppImage/appimagetool/actions/runs/19475763690) |   |   | ✓ | ✓ | ✓ |
| `AppImage` 消费 |  |   |   | ✓ FUSE | ✓ FUSE | ✓ FUSE |
| `apk` 生产 | 托管实现 | ✓ | ✓ | ✓ | ✓ | ✓ |
| `apk` 消费 |  |   |   |   |   | ✓ |
| `zip` / `tar.gz` 生产 | 托管实现 | ✓ | ✓ | ✓ | ✓ | ✓ |
| `zip` / `tar.gz` 消费 |  | ✓ | ✓ | ✓ | ✓ | ✓ |

- Linux 只列三大派系：
  - `Debian Family`：`Debian`、`Ubuntu`、`UOS`、`Ubuntu Kylin`等
  - `Red Hat Family`：`RHEL`、`Fedora`、`CentOS`、`openSUSE` 等
  - `Alpine`：`Alpine Linux(musl)`
- `nsis`
  - 使用 `nsis` 作为打包工具，宿主下限以 [NsisToolset](https://github.com/dotnetbundler/NsisToolset#system-support) 声明为准。
  - 签名(可选)：Authenticode 纯托管（`PfxFile`/`CertificateThumbprint` 供证书，支持时间戳），签安装器/卸载器与载荷文件；也支持外部签名命令。
  - 安装包支持 NT4+ 系统（[NSIS 文档](https://nsis.sourceforge.io/Docs/Chapter1.html)）。
- `msi`
  - 使用 `wix` 作为打包工具，他依赖于 `.NETFramework,Version=v4.5`（简称 Fx4.5）。Fx4.5 在 Win8+ 预装，支持装于 Win7 SP1+ （[.NET Framework 系统要求](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)）。
  - 签名(可选)：Authenticode 纯托管（与 `nsis` 共享签名器），签 MSI 与载荷文件。
  - 安装包支持 Win7+ 系统；使用 `Windows Installer 5.0` 安装，它随 Win7/Server2008R2 起发行（[Windows Installer 发行版本](https://learn.microsoft.com/en-us/windows/win32/msi/released-versions-of-windows-installer)）
- `app`
  - 打包能力纯托管无外部工具依赖。
    - 注：**Assets.car** 图标依赖于 `Xcode 26+` 的 `actool`
  - 签名/公证(可选)：依赖 `codesign` 依赖 `Xcode` 的 `notarytool`、`stapler`，仅 macOS 宿主。
  - Windows 宿主产 `.app` 无执行位，可再套 `zip`/`tar.gz` 携带执行位。
- `dmg`
  - 使用系统 `hdiutil` 打包；默认压缩（`Ulmo`）需 macOS 10.15+（[hdiutil(1) man](https://keith.github.io/xcode-man-pages/hdiutil.1.html)）。
  - 签名(可选)：依赖 `codesign`（ad-hoc 或开发者身份），仅 macOS 宿主。
  - 安装包挂载支持 macOS 10.15+（`Ulmo` 压缩）。
- `pkg`
  - 使用系统 `pkgbuild`/`productbuild` 打包，macOS 10.7+。
  - 签名(可选)：依赖 `productbuild --sign`（安装器证书），仅 macOS 宿主。
  - 安装包安装支持 macOS 10.7+。
- `deb`
  - 打包能力纯托管无外部工具依赖，不提供签名能力。
  - 安装包支持 `dpkg` 系发行版。
- `rpm`
  - 打包能力纯托管无外部工具依赖。
  - 签名(可选)：GPG 签名为纯托管（BouncyCastle OpenPGP），任意宿主可签，签名嵌于包内签名头，形态与 `rpmsign --addsign` 一致（v3 OpenPGP 签名包）。
    - `rpm -K`/`rpm --checksig`：验 `RPMSIGTAG_PGP`（主头+载荷签名）与完整性摘要，均随包嵌入。
    - `dnf`/`zypper`（安装时经 `librpm`、`libzypp` 验签）：验 `RPMSIGTAG_RSA`（主头签名），随包嵌入。
  - 安装包支持 `rpm` 系发行版。
- `AppImage`
  - 使用 `appimagetool` 作为打包工具，仅支持 x64/arm64；musl 未实测。
  - 签名(可选)：GPG 签名嵌 `.sha256_sig` ELF 段，`gpgv` 可验。
  - 安装包运行依赖于 FUSE 或 `--appimage-extract`。
- `apk`
  - 打包能力纯托管无外部工具依赖。
  - 签名(可选)： RSA 签名为纯托管（BouncyCastle，PEM/PKCS8/加密私钥），签名为 `.SIGN.RSA` 前置段。
    - 验签需公钥预置目标机 `/etc/apk/keys/`，未签名或密钥未分发装时需 `apk add --allow-untrusted`。
  - 安装包支持 `apk` 系发行版。
- `zip`·`tar.gz`
  - 打包能力纯托管无外部工具依赖。
  - 任意宿主可解压，`.zip` 拒 >4GB。
- 实测宿主：Windows Server 2022、macOS 26.5 arm64、ubuntu 22.04 x86_64、qemu aarch64。

### 签名（横切能力，单独记不占格式行）

| 签名项＼宿主 | Win x64 | Win arm64 | Win x86 | mac x64 | mac arm64 | Linux | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Windows Authenticode | ✓ | ✓ | ✓ |  |  |  | 内嵌 provider，无 signtool 依赖；生产证书/时间戳外部待验 |
| macOS codesign/公证 |  |  |  | ✓ | ✓ |  | codesign≥10.5；公证 Xcode 13+（宿主约 11.3+）；Assets.car 需 Xcode 26+（可降级）；生产证书/真机公证外部待验 |

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

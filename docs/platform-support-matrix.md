# 打包能力平台支持矩阵

- `msbuild` 方式打包只要能安装 `.NET SDK` 的机器都能使用，见[安装 .NET](https://learn.microsoft.com/en-us/dotnet/core/install/)。
- `cli` 方式打包的支持与发布时对应的 .NET 的支持一致，见[.NET 支持的操作系统策略](https://github.com/dotnet/core/blob/main/os-lifecycle-policy.md)。

| 格式 | 工具 | Windows | macOS | Debian Family | Red Hat Family | Alpine |
| --- | --- | --- | --- | --- | --- | --- |
| `nsis` 生产 | [NSIS 3.12](https://nsis.sourceforge.io/)（[toolset r1](https://github.com/dotnetbundler/NsisToolset)） | ✓ 2000+ | ✓ 10.13+ | ✓ glibc2.17+ | ✓ glibc2.17+ |   |
| `nsis` 消费 |  | ✓\* NT4+ |   |   |   |   |
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
  - `Red Hat Family`：`RHEL`、`Fedora`、`CentOS` 等
  - `Alpine`：`Alpine Linux(musl)`
- `nsis`
  - 使用 `nsis` 作为打包工具，宿主下限以 [NsisToolset](https://github.com/dotnetbundler/NsisToolset#system-support) 声明为准。
  - 安装包支持 NT4+ 系统（[NSIS 文档](https://nsis.sourceforge.io/Docs/Chapter1.html)）。
    - 因安装器内嵌 `.NET 10 Native AOT` 构建的插件，消费机实际下限抬至 Windows 10 1607+（[.NET 10 支持的操作系统](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)）。
- `msi`
  - 使用 `wix` 作为打包工具，他依赖于 `.NETFramework,Version=v4.5`（简称 Fx4.5）。Fx4.5 在 Win8+ 预装，支持装于 Win7 SP1+ （[.NET Framework 系统要求](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)）。
  - 安装包支持 Win7+ 系统；使用 `Windows Installer 5.0` 安装，它随 Win7/Server2008R2 起发行（[Windows Installer 发行版本](https://learn.microsoft.com/en-us/windows/win32/msi/released-versions-of-windows-installer)）
- `app`
  - 打包能力纯托管无外部工具依赖。
    - Windows 宿主产 `.app` 无执行位，可再套 `zip`/`tar.gz` 携带执行位。
    - 注：**Assets.car** 图标能力依赖于 `Xcode 26+` 的 `actool`
  - 所有 macOS 均可使用 `.app` 包
- `dmg`
  - 使用系统 `hdiutil` 打包；默认压缩（`Ulmo`）需 macOS 10.15+（[hdiutil(1) man](https://keith.github.io/xcode-man-pages/hdiutil.1.html)）。
  - 安装包挂载支持 macOS 10.15+（`Ulmo` 压缩）。
- `pkg`
  - 使用系统 `pkgbuild`/`productbuild` 打包，macOS 10.7+。
  - 安装包安装支持 macOS 10.7+。
- `deb`
  - 打包能力纯托管无外部工具依赖，不提供签名能力。
  - 安装包支持 `dpkg` 系发行版。
- `rpm`
  - 打包能力纯托管无外部工具依赖。
  - 安装包支持 `rpm` 系发行版。
- `AppImage`
  - 使用 `appimagetool` 作为打包工具，仅支持 `x64`/`arm64` 架构宿主。
  - 安装包运行依赖于 FUSE 或 `--appimage-extract`。
- `apk`
  - 打包能力纯托管无外部工具依赖。
  - 安装包支持 `apk` 系发行版。
- `zip`/`tar.gz`
  - 打包能力纯托管无外部工具依赖，`.zip` 拒 >4GB。
  - 任意宿主可解压。

## 签名

| 格式 | 签名 | 实现方式 | 可签宿主 |
| --- | --- | --- | --- |
| `nsis` | ✓  | 内嵌 Authenticode（P/Invoke `mssign32` `SignerSignEx2`）<br>外部签名命令 `{path}` 占位；签安装器/卸载器/`SigningFiles` 载荷 | 内嵌仅 Windows<br>外部命令任意宿主 |
| `msi` | ✓  | 与 `nsis` 共享同一签名器面 | 仅 Windows |
| `app` | ✓  | 系统 `codesign` inside-out； `notarytool`/`stapler` 公证 | 仅 macOS |
| `dmg` | ✓  | 系统 `codesign`（identity 或 ad-hoc `-`） | 仅 macOS |
| `pkg` | ✓  | `pkgbuild --sign`/`productsign --sign`（Developer ID Installer，无 ad-hoc） | 仅 macOS |
| `deb` | — | — | — |
| `rpm` | ✓  | 纯托管 BouncyCastle v3 OpenPGP<br>`RPMSIGTAG_RSA`+`RPMSIGTAG_PGP` 双 tag<br>可过 `rpm -K`、`rpm --checksig`、`dnf`、`zypper` 验签 | 任意宿主 |
| `AppImage` | ✓  | `appimagetool --sign`（`.sha256_sig` ELF 段）<br>需宿主 `gpg`（隔离 GNUPGHOME 导钥） | Linux |
| `apk` | ✓  | 纯托管 BouncyCastle RSA PKCS1v1.5/SHA1（`.SIGN.RSA.<密钥>.rsa.pub` 前置段；PEM/PKCS8/加密私钥）<br>验签需公钥预置目标机 `/etc/apk/keys/`，未签名或密钥未分发装时需 `apk add --allow-untrusted` | 任意宿主 |
| `zip`/`tar.gz` | — | — | — |

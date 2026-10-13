# 使用文档

本目录是 Bundler 的**使用文档族**：每个入口、每个后端一件，从最小可用配置写到全量旋钮。
契约与决策（为什么这么设计、能力边界、验收口径）在 [`../formats/`](../formats/) 与各专题文档；管线机制见 [`../architecture.md`](../architecture.md)。

## 入口

| 入口 | 文档 | 适用 |
| --- | --- | --- |
| MSBuild（`DotNet.Bundler` 元包） | [`msbuild.md`](msbuild.md) | .NET 项目 `dotnet publish` 一条命令连发带打 |
| `bundler` CLI | [`cli.md`](cli.md) | 任意语言/构建系统，吃已发布目录 |
| 直接 API | 各后端文档 `§直接 API` | 任意 .NET 宿主内嵌打包 |

三入口产出同一套产物；同一格式旋钮在三入口的名字一一对应（`Bundler<Format><Knob>` ↔ `<format>.<knob>` ↔ `<Format>BundleConfiguration` 成员，详见 [`cli.md`](cli.md) 的映射表）。

## 后端

| 产物 | 格式名（`BundlerFormats`/`--formats`） | 使用文档 | 宿主要求 |
| --- | --- | --- | --- |
| Windows NSIS 安装器 `.exe` | `nsis` | [`win-nsis.md`](formats/win-nsis.md) | 任意宿主 |
| Windows MSI `.msi` | `msi` | [`win-msi.md`](formats/win-msi.md) | 仅 Windows |
| macOS `.app` | `app` | [`mac-app.md`](formats/mac-app.md) | 任意宿主（签名/公证需 macOS） |
| macOS `.dmg` | `dmg` | [`mac-dmg.md`](formats/mac-dmg.md) | 仅 macOS |
| macOS `.pkg` | `pkg` | [`mac-pkg.md`](formats/mac-pkg.md) | 仅 macOS |
| Debian `.deb` | `deb` | [`linux-deb.md`](formats/linux-deb.md) | 任意宿主 |
| RPM `.rpm` | `rpm` | [`linux-rpm.md`](formats/linux-rpm.md) | 任意宿主 |
| Linux `.AppImage` | `appimage` | [`linux-appimage.md`](formats/linux-appimage.md) | 仅 Linux |
| Alpine `.apk` | `alpineapk` | [`linux-alpine-apk.md`](formats/linux-alpine-apk.md) | 任意宿主 |
| `.zip`/`.tar.gz` | `zip`/`targz` | [`archive.md`](formats/archive.md) | 任意宿主 |

> "任意宿主"=托管实现随包携带工具链，不依赖宿主原生工具也不在线下载。
> 各目标架构与产物消费面的完整矩阵见 [`../support-matrix.md`](../support-matrix.md)；target 语法与命名契约见 [`../targets.md`](../targets.md)。

## 横切配置

- Windows Authenticode 签名：MSBuild `BundlerWindowsSigning*` / API `IBundleSigner`——见 [`msbuild.md` §签名](msbuild.md) 与各 Windows 格式文档。
- UPDATE 自更新：`BundlerUpdate*`/`update.*`/`UpdateBundleConfiguration`——见 [`msbuild.md`](msbuild.md)、[`cli.md`](cli.md)，应用侧接入见 [`../update.md`](../update.md)。
- 示例工程：[`samples/HelloBundlerApp`](../../samples/HelloBundlerApp/hello-bundler-app-sample.md) 覆盖全部后端全部公开旋钮，可直接照抄组织方式。

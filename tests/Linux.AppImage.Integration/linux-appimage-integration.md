# Linux .AppImage 集成验证（tests/Linux.AppImage.Integration）

`Verify.sh` 是 `.AppImage` 后端的真实验证入口，覆盖 `LINUX-APPIMAGE-1` 的退出条件。

## 用法

```bash
bash tests/Linux.AppImage.Integration/Verify.sh
```

需要 Linux 宿主与 dotnet SDK；可选工具缺失记 SKIP（docker、desktop-file-validate）。
所有产物落在 `artifacts/linux-appimage-integration/` 并在结束时自清理。

## 断言面

- 仓库打包：三个 nupkg 存在；`DotNet.Bundler.MSBuild` 包内装载 `DotNet.Bundler.AppImage.dll`；AppImage 包内带 appimagetool 许可证。
- 默认 publish：命名 `<pkg>_<ver>_amd64.AppImage`、sha256 侧车校验、ELF magic + e_machine=0x3e。
- `--appimage-extract` 结构断言：`AppRun`（sh 脚本、exec 经 usr/bin 链接）、根 `<pkg>.desktop` 符号链接、`usr/share/applications/` staged 件（`desktop-file-validate`）、`<pkg>.png`/`.DirIcon`、hicolor 图标、metainfo、`usr/lib/<pkg>/` 载荷与 `usr/bin` 链接、`BundlerResource` 落位。
- 真实运行：解出的 `AppRun hello world` 与整包 `--appimage-extract-and-run hi` 均输出 `BundlerAppImageIntegrationFixture:<args>`。
- 覆盖变体：包名/版本/BinLink/InstallRoot/IconFile 逐一断言；`.desktop` 整文件覆盖变体断言内容与根链接。
- 失败变体：`Architecture=ppc64` 必须使 publish 失败。
- arm64 结构断言：`-r linux-arm64` → `*_aarch64.AppImage`、ELF magic、e_machine=0xb7（EM_AARCH64）；宿主为 x86_64 不解包不运行。
- 扇出：`BundlerTestFormats=deb%3Brpm%3Bappimage` 一次 publish 三件产物齐备。
- docker 冒烟：`debian:stable`、`ubuntu:latest`、`fedora:latest` 容器内 `--appimage-extract-and-run` 真实运行断言输出；镜像拉取失败记 SKIP。
- 直 API 消费：`tests/AppImage.Api.PackageFixture` 经 nupkg 调 `AppImageBundler` 产出真实 `.AppImage`（仅 x86_64 宿主执行）。

## 与 deb/rpm 的差异

AppImage 无包管理器，验证以"规范级运行形态"为准（`--appimage-extract`/`-and-run`），不存在 `dpkg -i`/`rpm -i` 等价物；scriptlet/conffile/依赖字段不适用。

# 第三方组件声明

## NsisToolset 3.12-r1 / NSIS 3.12

- 项目：Nullsoft Scriptable Install System
- 工具集项目：https://github.com/dotnetbundler/NsisToolset
- 工具集版本：https://github.com/dotnetbundler/NsisToolset/releases/tag/v3.12-r1
- 发布归档 SHA-256：`41F15B7F7E3A0349185606EDE939C7B2E5B31FF76F0EB479143D6659EF1EDDBC`
- 上游许可证文件：生成的 NuGet 包中的 `licenses/nsis/COPYING`

NSIS 及随附的压缩模块适用多种许可证。准确条款以随包保留的上游 `COPYING` 原文为准。

## NsisPlugin 1.0.2

- 项目：https://github.com/dotnetbundler/NsisPlugin
- NuGet 包：`NsisPlugin` 1.0.2
- 许可证：MIT
- 许可证文件：生成的 NuGet 包中的 `licenses/nsis-plugin/LICENSE`

随包分发的 Native AOT 插件 `DotNetBundlerNsis.dll` 使用 NsisPlugin 构建。

## WiX Toolset 3.14.1

- 项目：https://github.com/wixtoolset/wix3
- 发布版本：https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm
- 二进制子集 SHA-256：`ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358`（包含原始 WiX UI 扩展）
- 许可证：Microsoft Reciprocal License；WiX 包中的 `licenses/wix/LICENSE.TXT`
- 对应源码：WiX 包中的 `licenses/wix/wix3141-source.zip`

源码和声明随 WiX 后端包分发。来源、原始归档及逐文件校验值见 `third_party/wix/msi-wix-provenance.md`。

## BouncyCastle.Cryptography 2.5.1

- 项目：https://www.bouncycastle.org/ （仓库 https://github.com/bcgit/bc-csharp）
- NuGet 包：`BouncyCastle.Cryptography` 2.5.1
- 许可证：MIT（upstream legion of the Bouncy Castle license，与 MIT 等价条款）

`DotNet.Bundler.Rpm` 用它以纯托管方式生成嵌入 rpm signature header 的
OpenPGP 签名包（`RPMSIGTAG_PGP`）；`DotNet.Bundler.MSBuild` 包内随带其程序集。

## appimagetool（continuous build 295）与 type2-runtime

- 项目：https://github.com/AppImage/appimagetool 与 https://github.com/AppImage/type2-runtime
- 发布版本：上游 `continuous` tag 滚动构建（所钉构建 `--version` 报 git 8c8c91f / build 295 / 2025-12-04）
- 许可证：MIT（`LICENSE-appimagetool`、`LICENSE-runtime` 随 `DotNet.Bundler.AppImage` 包分发）

`DotNet.Bundler.AppImage` 内嵌 `appimagetool-x86_64/aarch64.AppImage` 与
`runtime-x86_64/aarch64` 四个固定文件，运行时按 SHA-256 校验后落临时目录执行，
type2 runtime 始终经 `--runtime-file` 供应（所钉构建不内嵌 runtime）。
来源 URL、逐文件 SHA-256 与复核方式见 `third_party/appimagetool/appimagetool-provenance.md`。

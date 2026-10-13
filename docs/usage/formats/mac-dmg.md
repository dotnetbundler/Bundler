# macOS `.dmg` 使用指南

产出拖放安装卷 `.dmg`。语义契约见 [`../../design/formats/mac-dmg.md`](../../design/formats/mac-dmg.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.MacDmg`（`netstandard2.0`）：**仅 macOS 宿主**（`hdiutil` 全链）。
- 打包流程：先经 `.app` 中间产物（全部 [`BundlerMacApp*`](mac-app.md) 旋钮生效），再封装为卷。
- API 构造特殊：`new MacDmgBundler(dmgConfig, appConfig)`——两个配置对象。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>dmg</BundlerFormats>` + `dotnet publish -c Release -r osx-arm64`（macOS 宿主；`dmg` 也是 `macos-*` 目标的默认格式）。

**CLI**：`bundler bundle --input-dir <publish> --target macos-aarch64 --formats dmg`

**直接 API**：

```csharp
var artifacts = await new MacDmgBundler(
    new MacDmgBundleConfiguration(),
    new MacAppBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.Dmg] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerMacDmg*`；`bundler.json` 分节 `dmg.*`；API `MacDmgBundleConfiguration`。

| MSBuild | `dmg.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerMacDmgCompression` | `compression` / `Compression` | `Ulmo` | `Udzo`/`Udbz`；Ulmo=lzma 需 macOS 10.15+ 挂载 |
| `BundlerMacDmgVolumeName` | `volumeName` / `VolumeName` | `BundlerProductName` | 挂载显示卷名 |
| `BundlerMacDmgLayoutSkip` | `layout=null`（对象置空）/ `Layout=null` | `false`（有 `Layout`） | 显式跳过 `.DS_Store` 布局；无 GUI 宿主自动降级+警告仍产可挂载 DMG |
| `BundlerMacDmgLayoutWindowX`/`Y`/`Width`/`Height` | `layout.windowX`/`windowY`/`windowWidth`/`windowHeight` | `200`/`120`/`660`/`400` | Finder 窗口位/尺寸 |
| `BundlerMacDmgLayoutAppIconX`/`Y` | `layout.appIconX`/`appIconY` | `180`/`170` | `.app` 图标位 |
| `BundlerMacDmgLayoutApplicationsIconX`/`Y` | `layout.applicationsIconX`/`applicationsIconY` | `480`/`170` | `/Applications` 图标位 |
| `BundlerMacDmgLayoutIconSize` | `layout.iconSize` | `128` | 图标尺寸 |
| `BundlerMacDmgBackgroundFile` | `backgroundFile` / `BackgroundFile` | 无 | 拷入卷内 `.background/` 并被 `.DS_Store` 引用 |
| `BundlerMacDmgVolumeIconFile` | `volumeIconFile` / `VolumeIconFile` | 无 | 落盘 `.VolumeIcon.icns` + `SetFile -a C` |
| `BundlerLicenseFile`（全局） | 顶层 `licenseFile` | 无 | 注入 SLA 挂载许可面板；不配置即不注入 |
| `BundlerMacDmgSignIdentity`/`…TemporaryCertificateFile`/`…Password` | `signing.identity`/`temporaryCertificateFile`/`temporaryCertificatePassword` | 无 | DMG 本体 `codesign`；identity `-`=ad-hoc，与临时证书互斥（`MacDmgSigningConfiguration`） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerMacDmgCompression>Udbz</BundlerMacDmgCompression>
  <BundlerMacDmgVolumeName>My App</BundlerMacDmgVolumeName>
  <BundlerMacDmgBackgroundFile>Assets\dmg\bg.png</BundlerMacDmgBackgroundFile>
  <BundlerMacDmgVolumeIconFile>Assets\dmg\volume.icns</BundlerMacDmgVolumeIconFile>
  <BundlerLicenseFile>Assets\common\LICENSE.txt</BundlerLicenseFile>
</PropertyGroup>
```

签名+公证组合（内层 `.app` 签名走 `BundlerMacAppSign*`，DMG 本体签名走 `BundlerMacDmgSignIdentity`，公证走 `BundlerMacAppNotarize` 组——**公证的是内层 `.app`，DMG 本体不送 notarytool**）见 [`mac-app.md`](mac-app.md) 与 [`../../design/signing.md`](../../design/signing.md)。

## 5. 产物与边界

- `artifacts\<产品名>-<版本>-<生态词>.dmg`。
- 仅 macOS 宿主；无 GUI 时布局自动降级（仍产可挂载 DMG）。
- `.app` 内层旋钮（图标/plist/签名/公证）全部生效，见 [`mac-app.md`](mac-app.md)。
- DMG 无自更新支持（`update` 对 `dmg` 不产出）。

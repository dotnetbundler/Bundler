# AppImage 使用指南

产出 `.AppImage`。内嵌固定版本 `appimagetool` + type2 runtime（SHA-256 provenance），**仅 Linux 宿主构建**。语义契约见 [`../../design/formats/linux-appimage.md`](../../design/formats/linux-appimage.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.AppImage`（`netstandard2.0`）：内嵌工具链经 `ZipToolCache` 内容寻址缓存。
- 目标：`linux-x86_64`/`linux-aarch64`/`linux-musl-x86_64`/`linux-musl-aarch64`——随包仅内嵌 `x86_64`/`aarch64` 两族 runtime（静态 ELF 两 libc 皆可），`linux-i686` 等其他目标矩阵放行但构建必败。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>appimage</BundlerFormats>` + `dotnet publish -c Release -r linux-x64`（Linux 宿主）。

**CLI**：`bundler bundle --input-dir <publish> --target linux-x86_64 --formats appimage`

**直接 API**：

```csharp
var artifacts = await new AppImageBundler(new AppImageBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.AppImage] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerAppImage*`；`bundler.json` 分节 `appimage.*`；API `AppImageBundleConfiguration`。

| MSBuild | `appimage.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerAppImagePackageName` | `packageName` | `BundlerProductName` kebab-case | 进文件名与根 `.desktop`/图标名 |
| `BundlerAppImageVersion` | `version` | `BundlerVersion` 原样 | 无 EVR 规则 |
| `BundlerAppImageArchitecture` | `architecture` | RID 映射 | 仅 `x86_64`/`aarch64`（`amd64` 归一；`i686` 显式拒绝） |
| `BundlerAppImageInstallRoot` | `installRoot` | `usr/lib/<包名>` | AppDir 相对路径（拒绝对路径/`..`/空段） |
| `BundlerAppImageBinLink` | `binLink` | 包名 | `none` 关闭 `usr/bin` 链接（`AppRun` 直连主程序） |
| `BundlerAppImageIconFile` | `iconFile` | hicolor 最大方图→内置默认 PNG | PNG 拷为根 `<包名>.png`+`.DirIcon` |
| `BundlerAppImageDesktopFile` | `desktopFile` | 自动生成 | 整文件覆盖（须含 `Categories=`，appimagetool 硬要求） |
| `BundlerAppImageCategories` | `categories` | `Utility` | 分号/数组 freedesktop 分类 |
| `BundlerAppImageMetainfoFile` | `metainfoFile` | 无 | → `usr/share/metainfo/<包名>.metainfo.xml` |
| `BundlerAppImageSigningKeyFile` | `signing.keyFile` | 无 | OpenPGP 私钥（ASCII-armored）；走 `appimagetool --sign`（需宿主 `gpg`） |
| `BundlerAppImageSigningKeyPassphrase` | `signing.passphrase` | 无 | 经 `APPIMAGETOOL_SIGN_PASSPHRASE` 注入；只给口令为配置错误 |
| `@BundlerAppImageFile`（`Destination`） | `files` / `Files` | 无 | 任意文件→AppDir 相对路径（含文件名；拒绝对路径/`..`/空段/反斜杠/生成件碰撞） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerAppImageIconFile>Assets\appimage\icon.png</BundlerAppImageIconFile>
  <BundlerAppImageCategories>Utility;Development</BundlerAppImageCategories>
  <BundlerAppImageSigningKeyFile>$(AppImageSigningKey)</BundlerAppImageSigningKeyFile>
</PropertyGroup>
<ItemGroup>
  <BundlerAppImageFile Include="Assets\appimage\defaults.conf" Destination="opt/myapp/defaults.conf" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<包名>-<版本>-<架构>.AppImage`（musl 目标带 `-musl` 后缀）。
- **仅 Linux 宿主**；压缩固定 `zstd`。
- 不承诺逐字节一致（squashfs 元数据与内嵌 runtime 摘要随构建环境变化，属上游工具性质）。
- 自更新支持：走三段式换包（UPDATE）。

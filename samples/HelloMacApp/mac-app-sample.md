# HelloMacApp：macOS .app 功能演示

这个项目不是自动化测试，而是 `DotNet.Bundler` 当前 macOS .app 功能的可操作演示。
项目只通过普通 `PackageReference` 使用本地生成的 NuGet 包。

## 生成 .app

在仓库根目录执行（macOS 宿主）：

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloMacApp/HelloMacApp.csproj -c Release
```

产物位于：

```text
samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app
```

产物是未签名的 `.app` 目录：可直接拖到 `/Applications` 或任意位置；卸载 = 删除该目录；升级 = 整体替换。

## 默认演示内容

- `Contents/MacOS/` 承载发布目录（apphost `HelloMacApp` + 依赖），`Contents/Resources/` 承载 `BundlerResource` 与图标，`Contents/SharedSupport/` 承载 `BundlerMacContent` 显式映射；
- Info.plist 核心键：bundle ID、双版本键、`LSMinimumSystemVersion=12.0`、分类 `public.app-category.developer-tools`、版权与显示名；
- `Assets/icon-256.png` + `icon-512.png` 自动合成 `Contents/Resources/HelloMacAppIcon.icns`；
- Mach-O 主可执行自动获得 `+x`。

## 验证

```bash
plutil -lint "samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app/Contents/Info.plist"
"samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app/Contents/MacOS/HelloMacApp"
```

## 未演示（后续阶段）

文件关联与 URL scheme（MAC-APP-2）、签名与公证（MAC-APP-3）、DMG/PKG 容器。

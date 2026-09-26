# HelloMacApp：macOS .app 功能演示

这个项目不是自动化测试，而是 `DotNet.Bundler` 当前 macOS `.app` **全部公开能力**的可操作演示。
项目只通过普通 `PackageReference` 使用本地生成的 NuGet 包。

## 生成 .app

在仓库根目录执行（macOS 宿主；未签名构建也可在非 macOS 宿主执行，产物带权限位告警）：

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloMacApp/HelloMacApp.csproj -c Release
```

产物位于：

```text
samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app
```

产物是 `.app` 目录：可直接拖到 `/Applications` 或任意位置；卸载 = 删除该目录；升级 = 整体替换。

## 默认演示内容

- `Contents/MacOS/` 承载发布目录，`Contents/Resources/` 承载 `BundlerResource` 与图标，`Contents/SharedSupport/` 承载 `BundlerMacContent`，`Contents/Frameworks/` 承载 `BundlerMacFramework`（随附真实 fat dylib `Assets/libhello.dylib`）；
- Info.plist 核心键：bundle ID、`CFBundleVersion`+`CFBundleShortVersionString` 显式覆盖、`LSMinimumSystemVersion=12.0`、分类、版权与显示名；
- `.hellomac` 文件关联：`BundlerFileAssociation` 提供扩展名/名称/描述/MIME，`BundlerMacDocumentType` 叠加 `Role=Editor`、`Rank=Owner` 并导出 UTI `com.example.hellomacapp.hellomac`；
- `hellomac://` URL scheme：`BundlerUrlProtocol` + `BundlerMacUrlType`（`Role=Viewer`）；
- `BundlerMacAppExceptionDomain` → `NSAppTransportSecurity.NSExceptionDomains` HTTP 例外；
- 调用方 plist 合并：默认走 `BundlerMacAppInfoPlistFile=extra.plist`。

程序自身识别启动方式（URL scheme / 关联文件 / 普通启动），把启动记录与载荷存在性写入 `~/Library/Application Support/com.example.hellomacapp/last-launch.txt`。

## 可选旋钮（MSBuild 属性）

| 旋钮 | 取值 | 演示的能力 |
| --- | --- | --- |
| `HelloMacAppIconMode` | `png`（默认） / `car` / `icon` | PNG 合成 `.icns`；`car` 直接采用 `Assets/hello.car`（`CFBundleIconName=AppIcon`）；`icon` 用 `HelloMacAppIconSource` 指向 Icon Composer `.icon` 目录经 `actool` 编译 |
| `HelloMacAppIconSource` | `.icon` 目录路径 | 仅 `HelloMacAppIconMode=icon` 时生效 |
| `HelloMacAppInfoPlistMode` | `file`（默认） / `xml` | `xml` 改用 `BundlerMacAppInfoPlistXml` 内联合并（同样的键） |
| `HelloMacAppSignIdentity` | `-`=ad-hoc / 证书 CN | 开启签名管线（inside-out + `codesign --verify`）；`-` 可在无证书机器上演示 |
| `HelloMacAppSigningCertificatePath` / `…Password` | p12 路径 + 密码 | 临时钥匙串证书导入签名 |
| `HelloMacAppHardenedRuntime` | `true`（默认开签名时） | hardened runtime |
| `HelloMacAppEntitlementsFile` | 默认 `Assets/entitlements.plist` | 演示 `allow-jit` + `disable-library-validation`（后者是 ad-hoc/加固下加载 Microsoft 签名 hostfxr 的必要项） |
| `HelloMacAppNotarize` | `true`/`false` | 显式公证管线（ditto→notarytool→stapler）；需凭证 |
| `HelloMacAppNotaryWait` / `HelloMacAppSkipStapling` | `true`/`false` | notarytool 不等待 / 跳过 stapler |
| `HelloMacAppNotaryProfile` | keychain profile 名 | 公证凭证三模式之一 |
| `HelloMacAppAppleId` / `…Password` / `…TeamId` | 三元组 | Apple ID 凭证模式 |
| `HelloMacAppNotaryApiKeyPath` / `…KeyId` / `…Issuer` | 三元组 | App Store Connect API key 凭证模式 |

示例（ad-hoc 签名 + car 图标 + xml 合并）：

```bash
dotnet publish samples/HelloMacApp/HelloMacApp.csproj -c Release \
  -p:HelloMacAppSignIdentity=- -p:HelloMacAppIconMode=car -p:HelloMacAppInfoPlistMode=xml
```

未设置的公证凭证自动回退 `APPLE_*` 环境变量；公证与真实 Developer ID 证书属外部待验收（见 `docs/mac-app-open-items.md`）。

## 验证

```bash
plutil -lint "samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app/Contents/Info.plist"
"samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app/Contents/MacOS/HelloMacApp"
codesign --verify --deep --strict "samples/HelloMacApp/artifacts/osx-arm64/app/Hello Mac App.app"  # 签名模式时
```

## 已实测的模式

- 默认 `png`/`file`：`plutil` 全键回读、`.icns` 合成、dylib 落 Frameworks、直接启动正常；
- `car`：`Assets.car` 直接采用、`CFBundleIconName=AppIcon`；
- `xml`：内联键与 `file` 模式等价落盘；
- `HelloMacAppSignIdentity=-`：`codesign --verify --deep --strict` 通过、entitlements 写入签名、加固运行时可正常启动；
- `.icon` 编译与真实证书签名/公证：代码路径可用，需对应输入/凭证，见示例旋钮与外部待办。

# macOS `.app` 使用指南

产出 `.app` bundle 目录。语义契约（Info.plist 合并/图标管线/签名公证/UTI）见 [`../formats/mac-app.md`](../../formats/mac-app.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.MacApp`（`netstandard2.0`）：**任意宿主可产未签名 `.app`**；codesign/公证管线仅 macOS 宿主（不内嵌 Apple 工具）。
- 目标：`macos-x86_64`/`macos-aarch64`/`macos-universal`。
- `.dmg`/`.pkg` 打包会先经 `.app` 中间产物，本文全部旋钮在那种情况下同样生效（经 `BundlerMacApp*`/`<Format>BundleConfiguration` 的 app 配置对象传入）。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>app</BundlerFormats>` + `dotnet publish -c Release -r osx-arm64`。

**CLI**：`bundler bundle --input-dir <publish> --target macos-aarch64 --formats app`

**直接 API**：

```csharp
var artifacts = await new MacAppBundler(new MacAppBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.App] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerMacApp*`；`bundler.json` 分节 `app.*`；API `MacAppBundleConfiguration`。

### 3.1 身份与元数据

| MSBuild | `app.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerMacAppPackageName` | `packageName` / `PackageName` | `BundlerProductName` | `CFBundleName` |
| `BundlerMacAppDisplayName` | `bundleDisplayName` / `BundleDisplayName` | `BundlerProductName` | `CFBundleDisplayName` |
| `BundlerMacAppShortVersion` | `shortVersion` / `ShortVersion` | `BundlerVersion` | `CFBundleShortVersionString` |
| `BundlerMacAppBuildVersion` | `buildVersion` / `BuildVersion` | `BundlerVersion` | `CFBundleVersion` |
| `BundlerMacAppMinimumSystemVersion` | `minimumSystemVersion` / `MinimumSystemVersion` | 不写键 | `LSMinimumSystemVersion` |
| `BundlerMacAppCategory` | `category` / `Category` | 不写键 | `LSApplicationCategoryType`（如 `public.app-category.developer-tools`） |
| `BundlerMacAppExceptionDomain` | `exceptionDomain` / `ExceptionDomain` | 无 | ATS `NSExceptionDomains` 单一域（允许不安全 HTTP，含子域） |
| `BundlerMacAppInfoPlistFile` | `infoPlistFile` / `InfoPlistFile` | 无 | 调用方 plist 整文件合并（与 `InfoPlistXml` 二选一；身份键回读拒绝） |
| `BundlerMacAppInfoPlistXml` | `infoPlistXml` / `InfoPlistXml` | 无 | 内联 XML 合并 |

### 3.2 图标

图标一律经共享 `@BundlerIcon` 项（JSON `icons[]` / API `BundleConfiguration.Icons`）进入，按扩展名自动分模式：
`.png`（位图集自动合成 `.icns`）/ `.icns`（直给）/ `.car`（预编译 asset 目录）/ `.icon` 目录（Icon Composer，需 macOS 宿主上 Xcode 26+ 的 `actool`，缺失/过旧时降级警告回退 `.icns` 路径）。
`BundlerMacAppIconName`（`iconName`/`IconName`）默认 `AppIcon`：位图/`.icns` 模式下产 `<名>.icns` 并写 `CFBundleIconFile`；`.car`/`.icon` 模式无效（`CFBundleIconName` 由 `assetutil` 从 asset 目录回读）。

### 3.3 文件关联与 URL scheme

| 项 | `app.*` / API | 说明 |
| --- | --- | --- |
| `@BundlerMacDocumentType` | `documentTypes` / `DocumentTypes` | `Extensions`/`Name`/`Description`/`Role`/`Rank`/`ContentTypes`/`MimeType`/`ExportedTypeIdentifier`/`ExportedTypeConformsTo` → `CFBundleDocumentTypes`+UTI 导出 |
| `@BundlerMacUrlType` | `urlTypes` / `UrlTypes` | `Schemes`/`Name`/`Role` → `CFBundleURLTypes` |

通用 `BundlerFileAssociation`/`BundlerUrlProtocol` 也会映射成对应项组；格式专属项提供更细的 UTI/角色控制。

### 3.4 Contents 载荷

| 项 | `app.*` / API | 说明 |
| --- | --- | --- |
| `@BundlerMacAppFile`（`Destination`） | `files` / `Files` | 任意文件 → `Contents/` 下相对路径 |
| `@BundlerMacAppFrameworkDirectory` | `frameworkDirectories` / `FrameworkDirectories` | 目录原样进 `Contents/Frameworks/`（项值即源目录路径，无 `Destination` 元数据） |

### 3.5 签名与公证（仅 macOS 宿主）

`BundlerMacAppSign*`/`app.signing.*`/`MacAppSigningConfiguration`：

| MSBuild | 键/成员 | 说明 |
| --- | --- | --- |
| `BundlerMacAppSignIdentity` | `signing.identity` | `-`=ad-hoc / 证书 CN；空即不签。与临时证书互斥 |
| `BundlerMacAppTemporaryCertificateFile`/`…Password` | `signing.temporaryCertificateFile`/`…Password` | p12 导入临时钥匙串（密码属秘密勿入库） |
| `BundlerMacAppHardenedRuntime` | `signing.hardenedRuntime` | 每个可执行签名加 `--options runtime`（公证必需） |
| `BundlerMacAppEntitlementsFile` | `signing.entitlementsFile` | entitlements plist |
| `BundlerMacAppNotarize` | `signing.notarize` | ditto→`notarytool`→`stapler` 公证管线 |
| `BundlerMacAppNotaryWait` | `signing.notaryWait` | 默认 `true`；`false` 不等待结果 |
| `BundlerMacAppSkipStapling` | `signing.skipStapling` | 跳过 stapler |
| `BundlerMacAppNotaryProfile` | `signing.keychainProfile` | 凭证模式一：keychain profile 名 |
| `BundlerMacAppNotaryApiKeyFile`/`…KeyId`/`…KeyIssuer` | `signing.apiKeyFile`/`apiKeyId`/`apiKeyIssuer` | 凭证模式二：App Store Connect API key 三元组 |
| `BundlerMacAppAppleId`/`…ApplePassword`/`…AppleTeamId` | `signing.appleId`/`applePassword`/`appleTeamId` | 凭证模式三：Apple ID 三元组 |

凭证按**字段**逐一 `配置 ?? 环境变量` 合并（顺序 `APPLE_PROFILE` → `APPLE_API_KEY_PATH`+`APPLE_API_KEY`+`APPLE_API_ISSUER` → `APPLE_ID`+`APPLE_PASSWORD`+`APPLE_TEAM_ID`，配置与 env 可拼成完整组）；某组只凑出一部分属配置错误直接抛异常。
**注意**：独立 `.app` 后端按契约硬拒 `BundlerLicenseFile`（许可载荷归 dmg/pkg）；`formats` 含独立 `app` 时不要配置许可文件。

## 4. 产物与边界

- `artifacts\<产品名>-<版本>-<生态词>.app\` 目录。
- 签名/公证需 macOS 宿主与真实凭证；公证属外部网络调用，时长不可控（`notaryWait=false` 可异步）。

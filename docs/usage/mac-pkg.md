# macOS `.pkg` 使用指南

产出 `.pkg` 安装包：`pkgbuild` 组件包（扁平包），配置分发面后经 `productbuild` 升级为分发包。
语义契约见 [`../formats/mac-pkg.md`](../formats/mac-pkg.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.MacPkg`（`netstandard2.0`）：**仅 macOS 宿主**（`pkgbuild`/`productbuild`/`productsign`/`notarytool`）。
- 打包流程：先经 `.app` 中间产物（全部 [`BundlerMacApp*`](mac-app.md) 旋钮生效）。
- API 构造特殊：`new MacPkgBundler(pkgConfig, appConfig)`——两个配置对象。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>pkg</BundlerFormats>` + `dotnet publish -c Release -r osx-arm64`（macOS 宿主）。

**CLI**：`bundler bundle --input-dir <publish> --target macos-aarch64 --formats pkg`

**直接 API**：

```csharp
var artifacts = await new MacPkgBundler(
    new MacPkgBundleConfiguration(),
    new MacAppBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.Pkg] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerMacPkg*`；`bundler.json` 分节 `pkg.*`；API `MacPkgBundleConfiguration`。

### 3.1 组件包（`pkgbuild`）

| MSBuild | `pkg.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerMacPkgPackageName` | `packageName` / `PackageName` | `BundlerIdentifier` | 组件 identifier |
| `BundlerMacPkgVersion` | `version` / `Version` | `BundlerVersion` | |
| `BundlerMacPkgInstallRoot` | `installRoot` / `InstallRoot` | `/Applications` | install-location |
| `BundlerMacPkgScriptsDirectory` | `scriptsDirectory` / `ScriptsDirectory` | 无 | 专家旋钮：目录原样给 `pkgbuild --scripts`，脚本以安装器权限运行 |
| `@BundlerMacPkgFile`（`Destination`） | `files` / `Files`（`{source,destination}`） | 无 | 任意文件树 → install-location 相对路径 |

### 3.2 分发包（任一配置即升级 `productbuild`）

| MSBuild | `pkg.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerMacPkgTitle` | `title` / `Title` | `BundlerProductName` | `<title>` |
| `BundlerMacPkgWelcomeFile` | `welcomeFile` / `WelcomeFile` | 无 | 欢迎页富文本 |
| `BundlerMacPkgConclusionFile` | `conclusionFile` / `ConclusionFile` | 无 | 结语页富文本 |
| `BundlerLicenseFile`（全局） | 顶层 `licenseFile` | 无 | 分发包许可页 |
| `BundlerMacPkgInstallScope` | `installScope` / `InstallScope` | `System` | `CurrentUserHome`（免提权装 `~/`，枚举不区分大小写） |

### 3.3 签名与公证（仅 macOS 宿主）

`.pkg` 只有真实 **Developer ID Installer** 签名，**无 ad-hoc 等价物**。

| MSBuild | `pkg.signing.*` / API | 说明 |
| --- | --- | --- |
| `BundlerMacPkgSignIdentity` | `identity` | `pkgbuild --sign`/`productsign --sign` 证书 CN |
| `BundlerMacPkgTemporaryCertificateFile`/`…Password` | `temporaryCertificateFile`/`…Password` | p12 临时钥匙串（与 identity 互斥；密码属秘密） |
| `BundlerMacPkgNotarize` | `notarize` | `.pkg` 直接过 `notarytool`+`stapler` |
| `BundlerMacPkgNotaryWait`/`…SkipStapling`/`…NotaryProfile`/`…AppleId`/`…ApplePassword`/`…AppleTeamId`/`…NotaryApiKeyFile`/`…KeyId`/`…KeyIssuer` | `notaryWait`/`skipStapling`/`keychainProfile`/`appleId`/`applePassword`/`appleTeamId`/`apiKeyFile`/`apiKeyId`/`apiKeyIssuer` | 同 `.app` 公证组（凭证与环境变量回退顺序见 [`mac-app.md` §3.5](mac-app.md)） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerMacPkgInstallScope>CurrentUserHome</BundlerMacPkgInstallScope>
  <BundlerMacPkgWelcomeFile>Assets\pkg\welcome.txt</BundlerMacPkgWelcomeFile>
  <BundlerMacPkgConclusionFile>Assets\pkg\conclusion.rtf</BundlerMacPkgConclusionFile>
  <BundlerMacPkgNotarize>true</BundlerMacPkgNotarize>
</PropertyGroup>
<ItemGroup>
  <BundlerMacPkgFile Include="Assets\pkg\readme.txt" Destination="support/readme.txt" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<产品名>-<版本>-<生态词>.pkg`。
- 仅 macOS 宿主；分发包触发条件 = title/welcome/conclusion/license 任一配置**或** `InstallScope` 显式设为非 `System`（显式 `System` 不触发），否则产扁平组件包。
- `.pkg` 无自更新支持。

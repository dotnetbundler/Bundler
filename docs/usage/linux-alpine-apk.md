# Alpine `.apk` 使用指南

产出 Alpine Linux `.apk`（`.PKGINFO`+三段 gzip）。纯托管写入器 + `ApkPackageReader` 回读器，**任意宿主可构建**。语义契约见 [`../formats/linux-alpine-apk.md`](../formats/linux-alpine-apk.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.AlpineApk`（`netstandard2.0`）。
- 目标：`linux-musl-*`（架构词映射 `x86_64`/`x86`/`aarch64`/`armv7`/`armel`/`riscv64`/`loongarch64`/`ppc64le`/`s390x` 等 musl 架构）。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>alpineapk</BundlerFormats>` + `dotnet publish -c Release -r linux-musl-x64`（`alpineapk` 是 `linux-musl-*` 默认格式）。

**CLI**：`bundler bundle --input-dir <publish> --target linux-musl-x86_64 --formats alpineapk`

**直接 API**：

```csharp
var artifacts = await new AlpineApkBundler(new AlpineApkBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.AlpineApk] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerAlpineApk*`；`bundler.json` 分节 `alpineapk.*`；API `AlpineApkBundleConfiguration`。

| MSBuild | `alpineapk.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerAlpineApkPackageName` | `packageName` | `BundlerProductName` kebab-case | `pkgname` 与文件名 |
| `BundlerAlpineApkVersion` | `version` | SemVer→apk 映射 | 显式值须 apk 版本串 |
| `BundlerAlpineApkRelease` | `release` | `0`（预发布映射值优先） | 非负整数，`pkgver=<version>-r<release>` |
| `BundlerAlpineApkArchitecture` | `architecture` | RID 映射 | |
| `BundlerAlpineApkOrigin` | `origin` | 包名 | |
| `BundlerAlpineApkDescription` | `description` | `BundlerDescription` | |
| `BundlerAlpineApkUrl` | `url` | `BundlerHomepage` | |
| `BundlerAlpineApkLicense` | `license` | 无 | SPDX 表达式 |
| `BundlerAlpineApkBuildDate` | `buildDate` | `0` | Unix 秒；确定性构建不建议覆盖 |
| `BundlerAlpineApkDepends`/`Provides` | `depends`/`provides` | 无 | 分号/数组逐行写 `.PKGINFO`；musl 自含 .NET 建议 `libstdc++;libgcc;icu-libs` |
| `BundlerAlpineApkTriggers` | `triggers` | 无 | 绝对路径列表，空格串联 `triggers` 键 |
| `BundlerAlpineApkBinLink` | `binLink` | 包名 | `none` 关闭 `usr/bin` 链接 |
| `BundlerAlpineApkPreInstallFile`/`PostInstallFile`/`PreUninstallFile`/`PostUninstallFile`/`PreUpgradeFile`/`PostUpgradeFile` | `preInstallFile`/`postInstallFile`/`preUninstallFile`/`postUninstallFile`/`preUpgradeFile`/`postUpgradeFile` | 无 | 六段脚本入控制段 0755（须非空+LF） |
| `BundlerAlpineApkSigningKeyFile` | `signing.keyFile` | 无 | PEM RSA 私钥；供则前置 `.SIGN.RSA.<文件名>.rsa.pub` 签名段 |
| `BundlerAlpineApkSigningKeyPassphrase` | `signing.passphrase` | 无 | 只给口令不给密钥文件为配置错误；口令属秘密 |
| `@BundlerAlpineApkFile`（`Destination`） | `files` / `Files` | 无 | 任意文件→绝对路径（含文件名；拒相对/`..`/`.`/尾斜杠/重复目标） |
| —（仅 API/JSON） | `extraPkgInfo` / `ExtraPkgInfo` | 无 | 任意 `.PKGINFO` 键值追加（MSBuild 无对应旋钮） |

**签名公钥部署**：目标机把 `<私钥文件名>.rsa.pub` 落 `/etc/apk/keys/` 即视为已信任。

## 4. 场景

```xml
<PropertyGroup>
  <BundlerAlpineApkDepends>libstdc++;libgcc;icu-libs</BundlerAlpineApkDepends>
  <BundlerAlpineApkLicense>MIT</BundlerAlpineApkLicense>
  <BundlerAlpineApkPostInstallFile>Assets\apk\post-install.sh</BundlerAlpineApkPostInstallFile>
  <BundlerAlpineApkSigningKeyFile>$(ApkSigningKey)</BundlerAlpineApkSigningKeyFile>
</PropertyGroup>
<ItemGroup>
  <BundlerAlpineApkFile Include="Assets\apk\defaults.conf" Destination="/etc/myapp/defaults.conf" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<包名>-<version>-r<release>.<arch>.apk`，逐字节确定（含可选 RSA 签名段——PKCS#1 v1.5 确定性签名）。
- 无自更新；安装需 `apk`。

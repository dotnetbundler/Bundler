# RPM `.rpm` 使用指南

产出 RPM `.rpm`。纯托管 lead/header/cpio/gzip 写入器 + `RpmPackageReader` 回读器，无原生工具依赖。语义契约见 [`../formats/linux-rpm.md`](../formats/linux-rpm.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Rpm`（`netstandard2.0`）：**任意宿主可构建**。
- 目标：全部 glibc `linux-*`（musl 目标矩阵拒绝）；架构词映射 `x86_64`/`i686`/`aarch64`/`armv7hl`/`riscv64`/`loongarch64`/`ppc64le`/`s390x`（`Architecture` 显式可覆盖，如 `noarch`）。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>rpm</BundlerFormats>` + `dotnet publish -c Release -r linux-x64`。

**CLI**：`bundler bundle --input-dir <publish> --target linux-x86_64 --formats rpm`

**直接 API**：

```csharp
var artifacts = await new RpmBundler(new RpmBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.Rpm] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerRpm*`；`bundler.json` 分节 `rpm.*`；API `RpmBundleConfiguration`。

| MSBuild | `rpm.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerRpmPackageName` | `packageName` | `BundlerProductName` kebab-case | `[A-Za-z0-9][A-Za-z0-9+._-]*` |
| `BundlerRpmVersion` | `version` | SemVer→rpm 映射 | 显式值禁含 `-`，须 `[0-9A-Za-z.+_]+`；预发布段→Release `0.1.<label>`，`+build`→Release 后缀 |
| `BundlerRpmRelease` | `release` | `1`（或映射值） | rpm Release 字符串 |
| `BundlerRpmEpoch` | `epoch` | 无 | 非负整数写 EPOCH tag，不进文件名 |
| `BundlerRpmArchitecture` | `architecture` | RID 映射 | rpm 架构名（可 `noarch`） |
| `BundlerRpmVendor` | `vendor` | `BundlerPublisher`→`BundlerIdentifier` 回退 | |
| `BundlerRpmInstallRoot` | `installRoot` | `/usr/lib/<包名>` | 绝对路径无 `..`/空格；非 `/usr` 根下时 `usr/bin` 链接转绝对 |
| `BundlerRpmBinLink` | `binLink` | 包名 | `none` 关闭 `usr/bin` 链接 |
| `BundlerRpmDepends`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests` | 同名 camelCase | 空 | 分号/数组逐条；rich deps 可写 `(libgcc or libgcc_s1)`；`-p:` 需 `%3B` |
| `BundlerRpmLicense` | `license` | 无 | SPDX 表达式惯例 |
| `BundlerRpmGroup` | `group` | 无 | 现代包可留空 |
| `BundlerRpmUrl` | `url` | `BundlerHomepage` | |
| `BundlerRpmCategories` | `categories` | 省略 | `.desktop` Categories |
| `BundlerRpmDesktopFile` | `desktopFile` | 自动生成 | 整文件覆盖 |
| `BundlerRpmMetainfoFile` | `metainfoFile` | 无 | AppStream → `usr/share/metainfo/` |
| `BundlerRpmChangelogFile` | `changelogFile` | 无 | |
| `BundlerRpmConfigLocations` | `configLocations` | 无 | 显式 `%config` 绝对路径列表 |
| `BundlerRpmSystemdServiceFile` | `systemdServiceFile` | 无 | unit→`usr/lib/systemd/system/`；自动合成 daemon-reload |
| `BundlerRpmPreInstallFile`/`PostInstallFile`/`PreUninstallFile`/`PostUninstallFile` | `preInstallFile`/`postInstallFile`/`preUninstallFile`/`postUninstallFile` | 无 | scriptlet 脚本文件 |
| `BundlerRpmPreInstallProgram`/`PostInstallProgram`/`PreUninstallProgram`/`PostUninstallProgram` | `preInstallProgram`/`postInstallProgram`/`preUninstallProgram`/`postUninstallProgram` | 无 | 对应 scriptlet 的替代解释程序（如 `/bin/sh -e`） |
| `BundlerRpmSigningKeyFile` | `signing.keyFile` | 无 | OpenPGP 私钥（ASCII-armored 或二进制）；供则签 `RPMSIGTAG_RSA`+`RPMSIGTAG_PGP` 双标签 v3，纯托管 BouncyCastle |
| `BundlerRpmSigningKeyPassphrase` | `signing.passphrase` | 无 | 只给口令不给密钥文件为配置错误；口令属秘密勿入库 |
| `@BundlerRpmFile`（`Destination`） | `files` / `Files` | 无 | 任意文件→包内绝对路径（`/etc` 下自动标 `%config(noreplace)`） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerRpmDepends>glibc;(libgcc or libgcc_s1);libstdc++;zlib;libicu</BundlerRpmDepends>
  <BundlerRpmLicense>MIT</BundlerRpmLicense>
  <BundlerRpmSystemdServiceFile>Assets\rpm\myapp.service</BundlerRpmSystemdServiceFile>
  <BundlerRpmSigningKeyFile>$(RpmSigningKey)</BundlerRpmSigningKeyFile>
</PropertyGroup>
<ItemGroup>
  <BundlerRpmFile Include="Assets\rpm\defaults.conf" Destination="/etc/myapp/defaults.conf" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<包名>-<版本>-<release>.<架构>.rpm`，逐字节确定（含签名包——签名在构建内完成）。
- 无自更新。
- 文件清单含 `%doc`/`%license` 标记；`license` 键与 `BundlerLicenseFile` 落位独立。

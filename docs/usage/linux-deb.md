# Debian `.deb` 使用指南

产出 Debian `.deb`。纯托管 ar/tar/gzip 写入器，无原生工具依赖。语义契约（版本映射/conffile/脚本/桌面集成）见 [`../formats/linux-deb.md`](../formats/linux-deb.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Deb`（`netstandard2.0`）：**任意宿主可构建**。
- 目标：`linux-x86_64`/`linux-i686`/`linux-aarch64`（架构按 RID 映射 `amd64`/`arm64` 等）。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>deb</BundlerFormats>` + `dotnet publish -c Release -r linux-x64`（`deb` 是 `linux-*` 默认格式）。

**CLI**：`bundler bundle --input-dir <publish> --target linux-x86_64 --formats deb`

**直接 API**：

```csharp
var artifacts = await new DebBundler(new DebBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.Deb] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerDeb*`；`bundler.json` 分节 `deb.*`；API `DebBundleConfiguration`。

| MSBuild | `deb.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerDebPackageName` | `packageName` | `BundlerProductName` kebab-case | `[a-z0-9][a-z0-9+.-]+` |
| `BundlerDebVersion` | `version` | SemVer→deb 映射（预发布 `-`→`~`、`+build` 保留） | 显式值须完整 Debian 版本（可含 `epoch:`与`-revision`） |
| `BundlerDebRelease` | `release` | `1` | 空值省略 `-revision`（直接 API 用 `""`） |
| `BundlerDebEpoch` | `epoch` | 无 | 纯数字前缀 `N:`，不进文件名 |
| `BundlerDebArchitecture` | `architecture` | RID 映射（x86_64→`amd64`、aarch64→`arm64`） | Debian 架构名 |
| `BundlerDebVendor` | `vendor` | `BundlerPublisher`→`BundlerIdentifier` 回退 | maintainer |
| `BundlerDebInstallRoot` | `installRoot` | `/usr/lib/<包名>` | 绝对路径无 `..`；非 `/usr` 根下时 `usr/bin` 链接转绝对目标 |
| `BundlerDebBinLink` | `binLink` | 包名 | `none`（不分大小写）关闭 `usr/bin` 链接 |
| `BundlerDebDepends`/`Recommends`/`Provides`/`Conflicts`/`Replaces` | `depends`/`recommends`/`provides`/`conflicts`/`replaces` | 空 | 分号/数组子句原样透传 control（Debian 语法里逗号是候选分隔，`-p:` 需 `%3B`）。自含 .NET 载荷建议：`libc6;libgcc-s1;libstdc++6;zlib1g;libicu76 \| libicu74 \| …` |
| `BundlerDebSection` | `section` | 省略 | |
| `BundlerDebPriority` | `priority` | `optional` | `required`/`important`/`standard`/`optional`/`extra` |
| `BundlerDebCategories` | `categories` | 省略 | `.desktop` Categories（freedesktop 列表） |
| `BundlerDebDesktopFile` | `desktopFile` | 自动生成 | `.desktop` 整文件覆盖 |
| `BundlerDebMetainfoFile` | `metainfoFile` | 无 | AppStream → `usr/share/metainfo/<包名>.metainfo.xml` |
| `BundlerDebChangelogFile` | `changelogFile` | 无 | → `usr/share/doc/<包名>/changelog.gz`（自动 gzip；`BundlerLicenseFile`→`copyright`） |
| `BundlerDebPreInstallFile`/`PostInstallFile`/`PreUninstallFile`/`PostUninstallFile` | `preInstallFile`/`postInstallFile`/`preUninstallFile`/`postUninstallFile` | 无 | 维护者脚本入 control 归档 0755（须 shebang+LF） |
| `BundlerDebSystemdServiceFile` | `systemdServiceFile` | 无 | unit→`usr/lib/systemd/system/<包名>.service`；postinst 自动合成 `daemon-reload` 尾段（装而不启） |
| `BundlerDebConfigLocations` | `configLocations` | 无 | 显式 conffile 绝对路径列表（须存在于载荷） |
| `@BundlerDebFile`（`Destination`） | `files` / `Files` | 无 | 任意文件→包内绝对路径（含文件名；拒相对/`..`/空段；`/etc` 下自动登记 conffile） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerDebDepends>libc6;libgcc-s1;libstdc++6;zlib1g;libicu76 | libicu74 | libicu72</BundlerDebDepends>
  <BundlerDebSystemdServiceFile>Assets\deb\myapp.service</BundlerDebSystemdServiceFile>
  <BundlerDebPostInstallFile>Assets\deb\postinst.sh</BundlerDebPostInstallFile>
  <BundlerDebMetainfoFile>Assets\deb\app.metainfo.xml</BundlerDebMetainfoFile>
</PropertyGroup>
<ItemGroup>
  <BundlerDebFile Include="Assets\deb\defaults.conf" Destination="/etc/myapp/defaults.conf" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<包名>_<debian 版本串>_<架构>.deb`（默认 release 进版本串：如 `myapp_1.0.0-1_amd64.deb`），逐字节确定（纯托管写入器）。
- 无自更新（`deb` 按权威实践走系统包管理器）。
- 安装需 `dpkg`/`apt`；GUI 双击唤起属桌面环境行为，非包内能力。

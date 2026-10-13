# `.zip`/`.tar.gz` 归档使用指南

产出分发归档：`.zip` 或 `.tar.gz` + `.sha256` 侧车。纯托管写入器（unix mode/符号链接保留），**任意宿主可构建**；唯一例外通路：macOS 宿主持 xattr 载荷时改走系统 `ditto`/`bsdtar`（产 `__MACOSX/` AppleDouble 段，失败回退托管写出器并告警）。语义契约见 [`../../design/formats/archive.md`](../../design/formats/archive.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Archive`（`netstandard2.0`）。
- 格式名：`zip`、`targz`——两个格式名共享同一 `BundlerArchive*`/`archive.*`/`ArchiveBundleConfiguration` 旋钮面。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>zip;targz</BundlerFormats>` + `dotnet publish -c Release -r <rid>`。

**CLI**：`bundler bundle --input-dir <publish> --target linux-x86_64 --formats zip,targz`

**直接 API**：

```csharp
var artifacts = await new ArchiveBundler(new ArchiveBundleConfiguration()).BuildAsync(
    new BundleConfiguration { /* … Formats = [PackageFormat.Zip, PackageFormat.TarGz] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerArchive*`；`bundler.json` 分节 `archive.*`；API `ArchiveBundleConfiguration`。
三旋钮默认空走后端推导，显式设置即覆盖。

| MSBuild | `archive.*` / API | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerArchivePackageName` | `packageName` | `BundlerProductName` kebab-case | 进归档名与顶层目录 |
| `BundlerArchiveVersion` | `version` | `BundlerVersion` 原样 | |
| `BundlerArchiveName` | `archiveName` | `<包名>-<版本>-<生态词>[-musl]` | 整名覆盖，须为单一路径段 |
| `@BundlerArchiveFile`（`Destination`） | `files` / `Files` | 无 | 任意文件→顶层目录内相对 POSIX 路径（拒绝对路径/`..`/`.`/空段/反斜杠/载荷碰撞） |

## 4. 场景

```xml
<PropertyGroup>
  <BundlerFormats>zip;targz</BundlerFormats>
</PropertyGroup>
<ItemGroup>
  <BundlerArchiveFile Include="Assets\archive\readme.txt" Destination="docs/readme.txt" />
</ItemGroup>
```

## 5. 产物与边界

- `artifacts\<包名>-<版本>-<生态词>.zip` / `….tar.gz`（+`….sha256` 侧车）。
- 逐字节确定（纯托管写入器；macOS xattr→ditto/bsdtar 例外通路不保证逐字节一致）。
- 归档内容：发布载荷 + `BundlerArchiveFile` 映射 + `BundlerResource`；顶层目录名=产物 stem `<包名>-<版本>-<生态词>[-musl]`（与归档文件同名；`BundlerArchiveName` 覆盖时同步）。
- 自更新支持：`zip`/`targz` 走三段式换包（UPDATE）。

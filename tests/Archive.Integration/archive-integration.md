# Archive 集成验证说明

`tests/Archive.Integration/Verify.sh` 是 `.zip`/`.tar.gz` 归档格式的真实集成入口。

## 覆盖范围

- `BundlerFormats=zip;targz` 真实 publish 产物的命名与 `.sha256` 侧车。
- `unzip -l` / `zipinfo -l` 清单与 unix mode 断言（0755/0644/0755/0777）。
- `tar -tvf` 清单、mode 与符号链接目标断言。
- `unzip` / `tar -xzf` 真实解包后逐路径断言、载荷真实运行、执行位与符号链接还原。
- `BundlerArchivePackageName`/`Version`/`ArchiveName` 覆盖变体。
- `@(BundlerArchiveFile)` 映射落位与非法目标失败变体（不留半成品）。
- `deb;rpm;appimage;zip;targz` 单 publish 五格式扇出。
- `win-x64`/`osx-arm64` 交叉目标接受 `zip`。
- `tests/Archive.Api.PackageFixture`：`DotNet.Bundler.Archive` nupkg 仓库外直 API 消费。

## 前提

- Linux 宿主、dotnet SDK、`unzip`、`zipinfo`、`tar`、`sha256sum`。
- fixture 内 `StageArchiveSymlink` 目标在打包前向 `PublishDir` 注入一条符号链接，
  模拟真实 .NET 载荷中常见的 `lib*.so` 链接（publish 自身不产生链接）。

## 用法

```bash
bash tests/Archive.Integration/Verify.sh
```

全部产物落在 `artifacts/archive-integration/` 并由脚本退出时清理。

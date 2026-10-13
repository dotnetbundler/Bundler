# CLI（`bundler` 命令行）使用指南

本文件是 `bundler` CLI 的唯一文档：安装、命令、最小到全量配置、schema 映射、分发形态与边界。
已冻结（基线 `0.1.0-alpha.62`）；后端语义见各格式文档。

## 1. 安装

```powershell
dotnet tool install --global DotNet.Bundler.Cli
```

`DotNet.Bundler.Cli` 是 dotnet tool 包（`ToolCommandName=bundler`），需 `DOTNET_ROOT` 指向 SDK 安装。
免运行时形态：`dotnet publish -r <rid>` 产原生 AOT 单二进制（宿主裁剪见 §6）。

## 2. 命令面

| 命令 | 作用 |
| --- | --- |
| `bundler bundle` | 校验→规划→产出安装包 |
| `bundler validate` | 只校验配置与输入目录，不产出 |
| `bundler plan` | 打印格式×target→产物路径计划，不构建（`--json` 给机器读） |
| `bundler init` | 生成最小 `bundler.json` 模板（身份占位+targets 注释示例） |
| `bundler update-keygen --key-file <path>` | 生成 UPDATE ECDSA 私钥并打印公钥；拒绝覆盖已有文件 |

- 退出码三级：`0` 成功、`1` 打包/后端执行失败、`2` 用法或配置校验失败。
- `--json`：stdout 只放机器可读 JSON，人类日志走 stderr。
- `--quiet`/`--verbose` 三档日志；`--help`/`--version`；环境变量仅 `DOTNET_BUNDLER_VERBOSE`。
- **输入契约：只吃已发布目录**（`--input-dir` + `--target`）——`dotnet publish` 由用户或 MSBuild 路径负责，CLI 不做二次构建编排。

## 3. 最小使用

```powershell
# 只指发布目录，其余全部推断：target=宿主（≥2 个 --input-dir 时推 macos-universal）、
# formats=目标 OS 常用集（universal→dmg）、productName=目录名、version=0.1.0、
# identifier=dev.bundler.<名>、mainExecutable=目录内唯一可执行文件——推断值逐条打到 stderr
bundler bundle --input-dir <publish目录>
```

## 4. 常用参数

```powershell
bundler bundle --input-dir <publish目录> --target linux-x86_64 --formats zip,deb \
  --product-name MyApp --identifier com.example.myapp --package-version 1.0.0 \
  --output-dir dist
# --strict 关闭推断：缺什么报什么，适合发布脚本
# 多目录 universal 合并：--input-dir 可重复，≥2 个目录先合并再打包
bundler bundle --input-dir bin/osx-x64 --input-dir bin/osx-arm64 --target macos-universal --formats zip
```

| 参数 | 对应 `bundler.json` 键 | 对应 MSBuild 属性 |
| --- | --- | --- |
| `--input-dir`（可重复） | `targets[].inputDirectory` | `BundlerInputDir` |
| `--target` | `targets[].target` | `BundlerTarget` |
| `--formats a,b` | `targets[].formats` | `BundlerFormats` |
| `--product-name` | `productName` | `BundlerProductName` |
| `--identifier` | `identifier` | `BundlerIdentifier` |
| `--package-version` | `version` | `BundlerVersion` |
| `--main-executable` | `targets[].mainExecutable` | `BundlerMainExecutable` |
| `--output-dir` | `outputDirectory` | `BundlerOutputPath` |
| `--output-layout` | `outputLayout` | `BundlerOutputLayout` |
| `--config <path>` | — | 显式指定 `bundler.json`（默认自动发现 cwd） |
| `--no-config` | — | 跳过自动发现 |
| `--reference-dir <dir>` | — | universal 合并的托管件来源目录 |
| `--publisher`/`--description`/`--homepage`/`--copyright`/`--license-file`/`--icons` | 同名顶层键 | `BundlerPublisher`/`…Description`/`…Homepage`/`…Copyright`/`…LicenseFile`/`@BundlerIcon` |
| `--strict` | — | 关闭推断：缺什么报什么 |
| `--<fmt>.<knob>=<v>` | `<fmt>.<knob>` | `Bundler<Format><Knob>` |

顶层 `target`/`formats` 不存在——target 面只在 `targets[]` 数组里；多 target 各配各的 inputDirectory/formats。

格式名：`nsis` `msi` `app` `dmg` `pkg` `deb` `rpm` `appimage` `zip` `targz`（`tar.gz` 别名）`alpineapk`，另有 `all` 全量别名。
格式选择优先级：`--formats` > `bundler.json` 的 `formats` > 目标 OS 缺省集。

## 5. `bundler.json` 全量 schema

当前目录的 `bundler.json` 自动生效；CLI 参数覆盖文件值；未知键/未知旋钮拒绝；相对路径按配置文件目录解析。
schema 与 MSBuild `Bundler*` 属性一一对应：`Bundler<Format><Knob>` → `<format>.<camelCaseKnob>`（kebab-case 参数名自动归一）；`<Format>File` 项 → `<format>.files: [{source,destination}]`。

```jsonc
{
  "identifier": "com.example.myapp",
  "productName": "My App",
  "version": "1.0.0",
  "publisher": "Example Co.",
  "description": "…", "homepage": "…", "copyright": "…",
  "licenseFile": "LICENSE.txt",
  "icons": ["assets/app.ico"],
  "resources": [{ "source": "assets/readme.txt", "destination": "docs/readme.txt" }],
  "fileAssociations": [{ "extensions": ["mydoc"], "name": "…", "mimeType": "application/x-…" }],
  "urlProtocols": [{ "schemes": ["myapp"], "name": "…" }],
  "targets": [{
    "target": "windows-x86_64",
    "inputDirectory": "publish/win-x64",
    "mainExecutable": "MyApp.exe",
    "formats": ["nsis", "zip"],
    "signingFiles": ["tools/Updater.exe"]  // 供 API 侧签名器追签
  }],
  "outputDirectory": "artifacts",
  "outputLayout": "flat",                  // flat | byFormat
  "nsis": { "installScope": "perMachine", "languages": ["English", "SimpChinese"] },
  "deb":  { "depends": ["libc6"], "files": [{ "source": "…", "destination": "/etc/…" }] },
  "rpm":  { "signing": { "keyFile": "…", "passphrase": "…" } },
  "dmg":  { "layout": { "iconSize": 96 } },
  "archive": { "archiveName": "myapp-dist" },
  "update": {
    "feedUrl": "https://example.com/feed",
    "channel": "stable",
    "signingKeyFile": "keys/update.key",
    "publicKey": "<base64>",
    "notes": "…",
    "bootstrapperDirectory": "…"           // 空时回退程序集内嵌引导件资源
  }
}
```

各格式分节可用键 = 该后端 `<Format>BundleConfiguration` 成员的 camelCase（嵌套 `signing`/`shortcuts`/`layout`/`documentTypes`/`urlTypes`/`files` 同规则递归）；逐键表见各后端使用文档 §全旋钮。
`update` 段不是格式，但同走节解析与 `--update.<knob>` 覆盖。
`--<fmt>.<knob>` 透传：`--nsis.install-scope=perMachine`（kebab→camel 归一）。

## 6. 分发形态与宿主边界

- **dotnet tool**：见 §1。
- **原生 AOT 二进制**：按 RID 裁剪自研后端——`win-*` 剔 `MacDmg`/`MacPkg`/`AppImage`，`linux-*` 剔 `Wix`/`MacDmg`/`MacPkg`，`osx-*` 剔 `Wix`/`AppImage`；`MacApp` 全宿主保留（非 mac 宿主可产未签名 `.app`），`Nsis`/`Deb`/`Rpm`/`Archive`/`AlpineApk` 全保留。
  被剔格式分发时抛 `PlatformNotSupportedException`，对应 `bundler.json` 段与 `--<fmt>.<knob>` 覆盖报"本宿主不支持"专用错误；无 RID 构建保留全部后端。
- Windows/macOS/Linux 全格式经六宿主 CI 矩阵验证（各宿主跑其可产格式）。
- centos7 等旧 glibc 宿主**明确不支持**（net10.0 托管工具 + 现代 glibc AOT）；产物装向旧发行版无格式障碍，载荷运行时兼容属载荷侧。

## 7. 明确拒绝

- `info`/`formats` 独立命令（`plan --json` 已覆盖）。
- hooks/脚本化扩展（干净宿主立场）；shell completions；遥测/联网（打包工具不联网）。
- Windows Authenticode 无配置面（`IBundleSigner` 只在 API/MSBuild 入口接线）；`rpm`/`appimage`/`alpineapk` 的 key-file 签名经各自分节 `signing.*` 配置。
- 旧 `src/Bundler.Cli` NSIS-only 原型已删除，不承诺兼容。

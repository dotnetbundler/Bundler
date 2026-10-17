# Alpine `.apk` 使用指南

产出 Alpine Linux `.apk`（`.PKGINFO`+三段 gzip）。纯托管写入器 + `ApkPackageReader` 回读器，**任意宿主可构建**。语义契约见 [`../../design/formats/linux-alpine-apk.md`](../../design/formats/linux-alpine-apk.md)。

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
| `BundlerAlpineApkVersion` | `version` | SemVer→apk 映射（`-`→`_`、`~`/`+` 按字符集收） | 显式值同走该映射 |
| `BundlerAlpineApkRelease` | `release` | `0` | 非负整数，`pkgver=<version>-r<release>`（预发布 `-`→`_` 并入上游段，不影响 release） |
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

## 5. 自更新（UPDATE installer-replay）

`alpineapk` 自更新已实现：走 installer-replay——提权重放系统包管理器安装命令，不走三段式换包。
打包侧与其他格式同一套 `BundlerUpdate*` 旋钮（[`msbuild.md` §8](../msbuild.md)）。
契约见 [`../../design/update.md`](../../design/update.md) §2。

- **链路面**：应用内 `UpdateClient` Check→Download→ECDSA `.sig` 验签后，客户端做预检三件套——`bundler-update.json` 旁车在场 + `apk` 前端 `command -v` 可达 + `apk info -e <包名>` 账本归属双检。
  预检过则留存回滚件、经三级提权退化链拉起安装位 `bundler-updater pkg-apply`（身份 `alpineapk` 在动词面映射 `--format apk`），客户端随即退出。
- **提权退化链**：`pkexec`（polkit 弹窗）→ `zenity`/`kdialog` 图形问密码 + `sudo -S` → 裸 `sudo`。
  退化判据=可用性探测+拉起即时失败退化：`pkexec` 腿拉起经有界退出观察，在场但会话无 polkit agent 的拉起即败会退化下一级。
  `DISPLAY`/`WAYLAND_DISPLAY` 全缺席的 headless 环境跳过图形问密码腿直落裸 `sudo`（tty 密码提示/NOPASSWD 场景存活）。
  `pkexec` 弹窗在场被用户拒绝授权属用户裁决面，归外部待验收（`docs/project/special-acceptance.md`）。
- **引导件编排**：随包注入的 musl 静态件 `bundler-updater` 落 `<installRoot>/bundler-updater`（默认 `/usr/lib/<包名>/`），以 root 执行 `apk add ./x.apk` 装本地包——公钥未预置目标机 `/etc/apk/keys/` 时带 `--allow-untrusted`（TOFU 边界：信任已由 ECDSA 验签建立，apk 签名层仅作运输完整性）。
  装完经 `setpriv`/`sudo -u`/`runuser`/`su` 降权回原用户、回灌会话 env 后拉起 `--app` 重启——提权语境下不以 root 重启应用（`PKEXEC_UID`/`SUDO_UID` 均缺席时按当前权限起 `--app`）。
- **回滚**：应用调 `UpdateClient.Rollback`（或引导件 `pkg-apply --rollback`）按同路径同提权链重放留存旧包——`apk add` 旧版本地件原生放行降级。
  留存目录在用户数据区（`$XDG_DATA_HOME` 或 `~/.local/share` 下 `dotnet-bundler/backups/`），retention 只增不剪不设上限。
  留存按工件文件名落位同名覆盖——无版本号文件名会逐次覆盖使留存池塌缩至一件。
  回滚拣 `applied` 标记之外的最新留存件——连续 rollback 在两最新留存件间往复，非沿版本链回走。
  留存为恒驻行为不经 `KeepRollbackBackup` 开关（该旋钮只管三段式换包路径）；`RollbackBackupDirectory` 仍可改留存落点。
  留存失败按 WARN 降级放行、pkg-apply 直用调用方原始下载件——自清下载件的调用方须把清理排在更新完成之后，过早删除会使装包无源。
  非 Bundler 链首装或无留存件时确定性拒绝"无留存包可回滚"，不空转。
- **约束**：`apk add` 原生解析 `depend`。
  新增/变更依赖属依赖变更场景，登记外部验收边界（`docs/project/special-acceptance.md`）。
- **约束**：同 `format`+同 RID 才换装，绝不跨格式换装。
  旁车在而账本未登记（repack/手动铺文件）时预检确定性拒绝"不参与更新"。

## 6. 产物与边界

- `artifacts\<包名>-<version>-r<release>.<arch>.apk`，逐字节确定（含可选 RSA 签名段——PKCS#1 v1.5 确定性签名）。
- 自更新走 installer-replay（系统包管理器重放），已实现——用法见 §5，契约见 [`../../design/update.md`](../../design/update.md) §2。
- 安装需 `apk`。

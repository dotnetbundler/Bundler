# RPM `.rpm` 使用指南

产出 RPM `.rpm`。纯托管 lead/header/cpio/gzip 写入器 + `RpmPackageReader` 回读器，无原生工具依赖。语义契约见 [`../../design/formats/linux-rpm.md`](../../design/formats/linux-rpm.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Rpm`（`netstandard2.0`）：**任意宿主可构建**。
- 目标：全部 glibc `linux-*`（musl 目标矩阵拒绝；`linux-armv7sf` 无 rpm 架构词）；架构词映射 `x86_64`/`i686`/`aarch64`/`armv7hl`/`riscv64`/`loongarch64`/`ppc64le`/`s390x`（`Architecture` 显式可覆盖，如 `noarch`）。

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
| `BundlerRpmSigningKeyPassphraseEnvironmentVariable` | `signing.passphrase` | 无 | 只收**存口令的环境变量名**（口令不进项目文件/日志）；只给口令不给密钥文件为配置错误 |
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

## 5. 自更新（UPDATE installer-replay）

`rpm` 自更新已实现：走 installer-replay——提权重放系统包管理器安装命令，不走三段式换包。
打包侧与其他格式同一套 `BundlerUpdate*` 旋钮（[`msbuild.md` §8](../msbuild.md)）。
契约见 [`../../design/update.md`](../../design/update.md) §2。

- **链路面**：应用内 `UpdateClient` Check→Download→ECDSA `.sig` 验签后，客户端做预检三件套——`bundler-update.json` 旁车在场 + `dnf`/`zypper`/`rpm` 前端 `command -v` 可达 + `rpm -q <包名>` 账本归属双检。
  预检过则留存回滚件、经三级提权退化链拉起安装位 `bundler-updater pkg-apply`，客户端随即退出。
- **提权退化链**：`pkexec`（polkit 弹窗）→ `zenity`/`kdialog` 图形问密码 + `sudo -S` → 裸 `sudo`。
  退化判据=可用性探测+spawn 前失败（`Process.Start` 未起成才依序换下一腿）——已启动腿即终局：
  任意退出码（含 pkexec 126 取消/127 鉴权失败、pkg-apply 3/4/5/6）原样交还调用方，绝不重放已执行安装器；
  用户取消/拒绝授权一律停链，需其他提权方式时显式重试（返回 `Process` 只表示已交付执行、不表示安装成功；`applied` 标记只是"最近交付工件"面）。
  `DISPLAY`/`WAYLAND_DISPLAY` 全缺席的 headless 环境跳过图形问密码腿直落裸 `sudo`（tty 密码提示/NOPASSWD 场景存活）。
  问密码腿先收对话框退出码：取消/失败即 126 停链不调 sudo（sudo 缓存票据不会越过取消放行），成功才把密码经管道喂 `sudo -S`。
- **引导件编排**：随包注入的 musl 静态件 `bundler-updater` 落 `<installRoot>/bundler-updater`（默认 `/usr/lib/<包名>/`），以 root 按 `dnf install -y ./x.rpm` → `zypper install -y` → `rpm -U` 退化链装本地包。
  装完经 `setpriv`/`sudo -u`/`runuser`/`su` 降权回原用户、回灌会话 env 后拉起 `--app` 重启——提权语境下不以 root 重启应用（`PKEXEC_UID`/`SUDO_UID` 均缺席时按当前权限起 `--app`）。
- **回滚**：应用调 `UpdateClient.Rollback`（或引导件 `pkg-apply --rollback`）按同路径同提权链重放留存旧包——`rpm -U` 腿此时加 `--oldpackage`。
  `zypper install -y` 重放旧版的非交互降级形态仍归外部待验收（`docs/project/special-acceptance.md`）。
  留存目录在用户数据区（`$XDG_DATA_HOME` 或 `~/.local/share` 下 `dotnet-bundler/backups/`），retention 只增不剪不设上限。
  留存按工件文件名落位同名覆盖——无版本号文件名会逐次覆盖使留存池塌缩至一件。
  回滚拣 `applied` 标记之外的最新留存件——连续 rollback 在两最新留存件间往复，非沿版本链回走。
  留存为恒驻行为不经 `KeepRollbackBackup` 开关（该旋钮只管三段式换包路径）；`RollbackBackupDirectory` 仍可改留存落点。
  留存失败按 WARN 降级放行、pkg-apply 直用调用方原始下载件——自清下载件的调用方须把清理排在更新完成之后，过早删除会使装包无源。
  非 Bundler 链首装或无留存件时确定性拒绝"无留存包可回滚"，不空转。
- **约束**：`dnf`/`zypper` 前端原生解析依赖（末级 `rpm -U` 不解析）。
  新增/变更依赖属依赖变更场景，登记外部验收边界（`docs/project/special-acceptance.md`）。
- **约束**：同 `format`+同 RID 才换装，绝不跨格式换装。
  旁车在而账本未登记（repack/手动铺文件）时预检确定性拒绝"不参与更新"。

## 6. 产物与边界

- `artifacts\<包名>-<版本>-<release>.<架构>.rpm`，逐字节确定（含签名包——签名在构建内完成）。
- 自更新走 installer-replay（系统包管理器重放），已实现——用法见 §5，契约见 [`../../design/update.md`](../../design/update.md) §2。
- 文件清单含 `%doc`/`%license` 标记；`license` 键与 `BundlerLicenseFile` 落位独立。

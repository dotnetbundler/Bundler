# Debian `.deb` 使用指南

产出 Debian `.deb`。纯托管 ar/tar/gzip 写入器，无原生工具依赖。语义契约（版本映射/conffile/脚本/桌面集成）见 [`../../design/formats/linux-deb.md`](../../design/formats/linux-deb.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Deb`（`netstandard2.0`）：**任意宿主可构建**。
- 目标：glibc `linux-*`（musl 目标矩阵拒绝；`linux-loongarch64` 无 deb 架构词）；架构词映射 `amd64`/`i386`/`arm64`/`armhf`/`armel`/`riscv64`/`ppc64el`/`s390x`。

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

## 5. 自更新（UPDATE installer-replay）

`deb` 自更新已实现：走 installer-replay——提权重放系统包管理器安装命令，不走三段式换包。
打包侧与其他格式同一套 `BundlerUpdate*` 旋钮（[`msbuild.md` §8](../msbuild.md)）。
契约见 [`../../design/update.md`](../../design/update.md) §2。

- **链路面**：应用内 `UpdateClient` Check→Download→ECDSA `.sig` 验签后，客户端做预检三件套——`bundler-update.json` 旁车在场 + `apt-get`/`dpkg` 前端 `command -v` 可达 + `dpkg -s <包名>` 账本归属双检。
  预检过则留存回滚件、经三级提权退化链拉起安装位 `bundler-updater pkg-apply`，客户端随即退出。
- **提权退化链**：`pkexec`（polkit 弹窗）→ `zenity`/`kdialog` 图形问密码 + `sudo -S` → 裸 `sudo`。
  退化判据=可用性探测+spawn 前失败（`Process.Start` 未起成才依序换下一腿）——已启动腿即终局：
  任意退出码（含 pkexec 126 取消/127 鉴权失败、pkg-apply 3/4/5/6）原样交还调用方，绝不重放已执行安装器；
  用户取消/拒绝授权一律停链，需其他提权方式时显式重试（返回 `Process` 只表示已交付执行、不表示安装成功；`applied` 标记只是"最近交付工件"面）。
  `DISPLAY`/`WAYLAND_DISPLAY` 全缺席的 headless 环境跳过图形问密码腿直落裸 `sudo`（tty 密码提示/NOPASSWD 场景存活）。
  问密码腿先收对话框退出码：取消/失败即 126 停链不调 sudo（sudo 缓存票据不会越过取消放行），成功才把密码经管道喂 `sudo -S`。
- **引导件编排**：随包注入的 musl 静态件 `bundler-updater` 落 `<installRoot>/bundler-updater`（默认 `/usr/lib/<包名>/`），以 root 按 `apt-get install -y ./x.deb` → `dpkg -i` 退化链装本地包（逐级 `command -v` 预检、失败退化；两级恒带 `DEBIAN_FRONTEND=noninteractive --force-confdef --force-confold` 静默 conffile——`apt-get` 经 `-o Dpkg::Options` 透传）。
  装完经 `setpriv`/`sudo -u`/`runuser`/`su` 降权回原用户、回灌会话 env 后拉起 `--app` 重启——提权语境下不以 root 重启应用（`PKEXEC_UID`/`SUDO_UID` 均缺席时按当前权限起 `--app`）。
- **回滚**：应用调 `UpdateClient.Rollback`（或引导件 `pkg-apply --rollback`）按同路径同提权链重放留存旧包。
  留存目录在用户数据区（`$XDG_DATA_HOME` 或 `~/.local/share` 下 `dotnet-bundler/backups/`），retention 只增不剪不设上限。
  留存按工件文件名落位同名覆盖——无版本号文件名会逐次覆盖使留存池塌缩至一件。
  回滚拣 `applied` 标记之外的最新留存件——连续 rollback 在两最新留存件间往复，非沿版本链回走。
  留存为恒驻行为不经 `KeepRollbackBackup` 开关（该旋钮只管三段式换包路径）；`RollbackBackupDirectory` 仍可改留存落点。
  留存失败按 WARN 降级放行、pkg-apply 直用调用方原始下载件——自清下载件的调用方须把清理排在更新完成之后，过早删除会使装包无源。
  非 Bundler 链首装或无留存件时确定性拒绝"无留存包可回滚"，不空转。
- **约束**：更新包不得新增 `Depends`——`dpkg -i` 退化腿不解析依赖，缺依赖会留 unconfigured 态（归 `dpkg --configure -a`/`apt-get -f install` 原生机制修）。
  新增/变更依赖属依赖变更场景，登记外部验收边界（`docs/project/special-acceptance.md`）。
- **约束**：同 `format`+同 RID 才换装，绝不跨格式换装。
  旁车在而账本未登记（repack/手动铺文件）时预检确定性拒绝"不参与更新"。

## 6. 产物与边界

- `artifacts\<包名>_<debian 版本串>_<架构>.deb`（默认 release 进版本串：如 `myapp_1.0.0-1_amd64.deb`），逐字节确定（纯托管写入器）。
- 自更新走 installer-replay（系统包管理器重放），已实现——用法见 §5，契约见 [`../../design/update.md`](../../design/update.md) §2。
- 安装需 `dpkg`/`apt`；GUI 双击唤起属桌面环境行为，非包内能力。

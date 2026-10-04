# HelloBundlerApp 示例

DotNet.Bundler 全后端统一演示：单个 `HelloBundlerApp.csproj` 覆盖全部 11 种产物格式的全部公开旋钮。
公共旋钮（产品身份/元数据/许可/主程序名/签名共享项）在主工程文件；
各后端专属旋钮拆分为 `formats/<Format>.props`，由主工程 `<Import>` 导入——这也演示了真实项目
组织多格式打包配置的推荐方式（等价于 Tauri 的 `bundle.targets[]` 与 electron-builder 的分格式键）。

## 结构与产出规则

```
samples/HelloBundlerApp/
├── HelloBundlerApp.csproj    # 公共旋钮 + BundlerFormats 默认规则 + 公共项
├── formats/                  # 每后端全套公开旋钮（HelloBundler<Format>* 变体传透）
│   ├── Nsis.props  Msi.props  MacApp.props  MacDmg.props  MacPkg.props
│   ├── Deb.props  Rpm.props  AppImage.props  Archive.props  AlpineApk.props
├── Assets/<format>/          # 各格式专属素材（脚本/图标/许可/plist/wxl/nsh…）
├── Assets/common/            # 公共素材（LICENSE.txt、readme.txt）
└── Program.cs                # 启动方式/打包资源/Hook 标记自检的演示入口
```

`BundlerFormats` 默认按 `$(RuntimeIdentifier)` × 宿主 OS 推导——产出全部当前环境可构建的格式：

| RuntimeIdentifier | Windows 宿主 | macOS 宿主 | Linux 宿主 | 其他宿主 |
|---|---|---|---|---|
| `win-x64`/`win-arm64` | nsis;msi;zip;targz | nsis;zip;targz | nsis;zip;targz | — |
| `osx-x64`/`osx-arm64`/`osx` | app;zip;targz | dmg;pkg;zip;targz | app;zip;targz | — |
| `linux-x64`/`linux-arm64`/`linux-riscv64` | deb;rpm;zip;targz | deb;rpm;zip;targz | deb;rpm;appimage;zip;targz | — |
| `linux-musl-x64`/`linux-musl-arm64` | alpineapk;zip;targz | alpineapk;zip;targz | alpineapk;zip;targz | — |

msi/dmg/pkg 需要对应本机工具链宿主（WiX→Windows、hdiutil/pkgbuild→macOS），
appimage 仅 Linux 宿主；nsis/deb/rpm/zip/targz/alpineapk 为纯托管实现任意宿主可产。
`-p:BundlerFormats=deb`（等任意子集）可覆盖默认值。

两条产品契约约束（mac 宿主实测记录）：

- **独立 `.app` 与许可文件互斥**：`BundlerLicenseFile` 是全局旋钮，独立 `.app` 后端无许可载荷契约会硬拒。
  macOS 宿主默认集因此不含独立 `app`（dmg 内层照样产出 .app，`BundlerMacApp*` 旋钮全部生效）；
  显式 `-p:BundlerFormats=app`（或含 app 的子集）时许可自动缺席该轮 publish，
  跨宿主 `osx-*` 默认 `app;zip;targz` 同理不带许可。
- **MSI 许可只收 RTF**：formats 集含 `msi` 时 `formats/Msi.props` 自动把全局许可切到 `Assets/msi/license.rtf`
  （nsis/dmg/pkg 同样接受 RTF；archive 不内嵌许可，deb/rpm 以文件载荷携带扩展名无影响）。
  `HelloBundlerMsiLicenseFile` 传透可整体接管。
- **universal 合并要求非 Mach-O 载荷逐字节一致**：`-r osx -p:BundlerUniversalRuntimeIdentifiers=osx-x64;osx-arm64`
  做双 RID 内层 publish + 托管合并；framework-dependent 应用的 `*.deps.json` 逐 RID 不同会按契约拒绝合并
  （`Universal merge conflict ... is not a Mach-O file`），非样本缺陷，需自包含/同构载荷场景适用。

## 运行

```bash
# 产出该 RID 下全部可构建格式
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r <RID>

# 只产指定格式子集
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r linux-x64 -p:BundlerFormats=deb;rpm

# 变体旋钮：全部按 HelloBundler<Format><Knob> 传透
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r win-x64 \
    -p:HelloBundlerNsisInstallMode=perMachine \
    -p:HelloBundlerMsiLanguage=zh-CN -p:HelloBundlerMsiInstallScope=perMachine
```

产物统一落 `samples/HelloBundlerApp/artifacts/<rid>/<format>/`（由 `BundlerOutputPath` 可调）。

## 变体旋钮前缀约定

- `HelloBundlerNsis*`：安装模式、压缩、语言集、外观图、安装钩子、快捷方式、旧 MSI 迁移、工具链覆盖（CompilerPath/DataDirectory/ToolsetArchivePath/Template）、降级策略。
- `HelloBundlerMsi*`：InstallScope、Language(s)、UpgradeCode、FipsCompliant、MsiVersion、AllowDowngrades、安装目录选择/AddToPath/卸载快捷方式/装后启动、位图、专家扩展（ExtensionIdPrefix、ExtensionFragment、ExtensionComponentRef、ExtensionComponentGroupRef、ExtensionFeatureRef、ExpertMergeModule、ExpertTemplate）。
- `HelloBundlerMacApp*`：图标三模式（png/car/icon）、InfoPlist 合并双模式（file/xml）、BundleName、MinimumSystemVersion、签名/硬化运行时/entitlements、公证 12 旋钮。
- `HelloBundlerDmg*`：压缩/卷名、Finder 布局 9 项、背景图/卷图标、EULA（`none` 关闭）、DMG 签名。
- `HelloBundlerPkg*`：identifier/version/install-location、title/welcome/conclusion/license、安装域名、脚本目录、签名/公证 12 旋钮。
- `HelloBundlerDeb*`：包名/版本/revision/epoch/架构/维护者/install-root/bin-link、关系字段 5 项、Section/Priority/Categories、DesktopFile/MetainfoFile/ChangelogFile、维护者脚本 4+systemd+conffiles、Compression。
- `HelloBundlerRpm*`：包名/版本/release/epoch/架构/vendor/install-root/bin-link、关系字段 6 项、License/Group/Url/Categories、DesktopFile/MetainfoFile/ChangelogFile、ConfigFiles/SystemdServiceFile、安装脚本 File×4+Program×4、Compression、GPG 签名。
- `HelloBundlerAppImage*`：包名/版本/架构/install-root/bin-link/icon/desktop/categories/metainfo、`HelloBundlerAppImageFiles=1` 任意载荷、GPG 签名。
- `HelloBundlerApk*`：包名/版本/release/架构/origin/description/url/license/builddate、depends/provides/triggers/bin-link、六段安装脚本、RSA 签名。
- `HelloBundlerArchive*`：归档名/版本覆盖。
- `HelloBundler*`（无格式段）：Version、OutputPath、MainExecutable、UniversalRuntimeIdentifiers、Signing*（Windows 签名共享 7 旋钮）。

## 演示的能力

- **单工程全后端**：一次 `publish` 按 RID 产出全部格式，公共旋钮一处定义、格式旋钮按文件分离；
  互斥旋钮（独立 app × 许可文件）有明确的条件化取舍演示。
- **旋钮 100% 覆盖**：`buildTransitive/DotNet.Bundler.MSBuild.props` 中全部公开 `Bundler<Format>*` 旋钮均有对应默认演示或 `HelloBundler*` 传透——包括专家级项（WiX ExtensionFragment/MergeModule、NSIS 工具链覆盖、pkg ScriptsDirectory）与签名/公证链路（空即不启用）。
- **载荷项全类型**：`BundlerIcon`（RID 条件化）、`BundlerResource`、`BundlerFileAssociation`、`BundlerUrlProtocol`、`BundlerDebFile`/`BundlerRpmFile`/`BundlerAppImageFile`/`BundlerArchiveFile`/`BundlerAlpineApkFile`（任意路径映射）、`BundlerNsisLanguageFile`、`BundlerWixLanguageFile`、`BundlerMacContent`/`BundlerMacFramework`/`BundlerMacDocumentType`/`BundlerMacUrlType`、`BundlerPkgPayload`、`BundlerWindowsSigningFile`/`BundlerWindowsSigningCommandArgument`。
- **启动自检**：`Program.cs` 区分普通启动/深链接/关联文件，落 `last-launch.txt` 日志，检查各格式载荷资源与 NSIS 生命周期 Hook 标记，便于安装后人工核验。

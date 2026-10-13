# MSI 安装器使用指南

产出 Windows Installer `.msi`（WiX 3.14.1 工具链随包）。语义契约（版本映射/升降级/UI 序列/扩展与专家模式）见 [`../formats/win-msi.md`](../../formats/win-msi.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Wix`（`netstandard2.0`）：WiX 3.14.1 工具随包，**仅 Windows 宿主构建**。
- 目标：`windows-i686`/`windows-x86_64`/`windows-aarch64`（x86 可在 x64 Windows 构建并安装）。

## 2. 最小使用

**MSBuild**：`<BundlerFormats>msi</BundlerFormats>` + `dotnet publish -c Release -r win-x64`（Windows 宿主）。

**CLI**：`bundler bundle --input-dir <publish> --target windows-x86_64 --formats msi`

**直接 API**：

```csharp
using DotNet.Bundler;
using DotNet.Bundler.Wix;

var artifacts = await new WixBundler(new WixBundleConfiguration
{
    Languages = ["zh-CN"]
}).BuildAsync(new BundleConfiguration { /* 同 NSIS 示例，Formats = [PackageFormat.Msi] */ });
```

## 3. 全旋钮

MSBuild 前缀 `BundlerWix*`；`bundler.json` 分节 `msi.*`；API `WixBundleConfiguration` + `WixBundlerOptions`。

| MSBuild | `msi.*` 键 / API 成员 | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerWixInstallScope` | `installScope` / `InstallScope` | `currentUser` | `perMachine`；两者是**独立产品线**（不互升）；安装目录限定 `%LOCALAPPDATA%` 子目录 / Program Files 子目录，静默 `INSTALLFOLDER=` 同校验 |
| `BundlerWixVersion` | `version` / `Version` | 空→三段 `BundlerVersion` | 预发布应用须显式给有效三段 MSI 版本；第四段/越界拒绝；不同应用版本不得复用同一 MSI 版本 |
| `BundlerWixUpgradeCode` | `upgradeCode` / `UpgradeCode` | 自动稳定生成 | 仅在有依据的旧产品迁移中显式指定 |
| `BundlerWixAllowDowngrades` | `allowDowngrades` / `AllowDowngrades` | `false` | 仅显式开启才允许旧版替换新版 |
| `BundlerWixLanguages` | `languages` / `Languages` | `en-US` | 分号/数组 culture 列表（`-p:` 用 `%3B`）；**每语言一个独立 MSI**，非英语包名带 `-<culture>` 后缀，安装目录与 UpgradeCode 隔离；支持 38 个 WiX 内嵌 culture |
| `BundlerWixLanguage` | — / — | 旧单语言回退 | `Languages` 为空时生效 |
| `BundlerWixCodepage` | `codepage` / `Codepage` | `0`（按语言表自动，如英文 1252/简体中文 936） | 显式值覆盖所有语言且须能编码包内文字 |
| `BundlerWixFipsCompliant` | `fipsCompliant` / `FipsCompliant` | `false` | 仅向 `candle.exe` 透传 `-fips`，不构成 FIPS 认证声明 |
| `BundlerWixStartMenuShortcut` | `startMenuShortcut` / `StartMenuShortcut` | `false` | |
| `BundlerWixDesktopShortcut` | `desktopShortcut` / `DesktopShortcut` | `false` | |
| `BundlerWixInstallDirectorySelection` | `installDirectorySelection` / `InstallDirectorySelection` | `false` | 交互可选范围内子目录；静默 `INSTALLFOLDER` 走同一校验 |
| `BundlerWixBannerFile` | `bannerFile` / `BannerFile` | WiX 默认 | `.bmp` 且必须 493×58 |
| `BundlerWixDialogFile` | `dialogFile` / `DialogFile` | WiX 默认 | `.bmp` 且必须 503×314 |
| `BundlerWixAddToPath` | `addToPath` / `AddToPath` | `false` | 用户/系统 PATH 仅追加安装目录，卸载移除 |
| `BundlerWixUninstallShortcut` | `uninstallShortcut` / `UninstallShortcut` | `false` | 开始菜单生成 `msiexec /x` 入口 |
| `BundlerWixLaunchAfterInstall` | `launchAfterInstall` / `LaunchAfterInstall` | `false` | 仅交互完成页勾选时启动；静默/被动/修复/升级不触发 |
| `BundlerWixToolsetArchivePath` | — / `WixBundlerOptions.ToolsetArchivePath` | 包内 WiX 3.14.1 | 测试/高级覆盖自带工具集压缩包 |

**项**：

| 项 | `msi.*` 键 / API 成员 | 说明 |
| --- | --- | --- |
| `@BundlerWixLanguageFile`（`Language` 元数据） | `localeFiles` / `LocaleFiles` | culture→`.wxl` 覆盖；键须属 Bundler 自有键集，编码/culture 校验 |
| `@BundlerWixExtensionFragment` | `extensionFragmentFiles` / `ExtensionFragmentFiles` | 常规模式 `.wxs` fragment；白名单声明式元素，`@Id` 须以 `BundlerWixExtensionIdPrefix` 开头 |
| `BundlerWixExtensionIdPrefix` | `extensionIdPrefix` / `ExtensionIdPrefix` | 常规模式必填自定义前缀（如 `Ext.`）；Bundler/WiX 保留前缀不可用 |
| `@BundlerWixExtensionComponentRef`/`…ComponentGroupRef`/`…FeatureRef` | `extensionComponentRefs`/`…GroupRefs`/`extensionFeatureRefs` | 显式引用 id，注入 Product Feature |
| `BundlerWixExpertTemplateFile` | `expertTemplateFile` / `ExpertTemplateFile` | 专家模式：整份 `.wxs` 替换生成的产品文档；身份经 `candle -d` 变量与构建后回读强制 |
| `@BundlerWixExpertMergeModule` | `expertMergeModuleFiles` / `ExpertMergeModuleFiles` | `.msm` 直传 light；专家产物不受管、不担保可回滚 |

## 4. 场景

### 4.1 多语言 + 品牌界面

```xml
<PropertyGroup>
  <BundlerWixLanguages>en-US;zh-CN;ja-JP</BundlerWixLanguages>
  <BundlerWixInstallDirectorySelection>true</BundlerWixInstallDirectorySelection>
  <BundlerWixBannerFile>Assets\banner.bmp</BundlerWixBannerFile>
  <BundlerWixDialogFile>Assets\dialog.bmp</BundlerWixDialogFile>
</PropertyGroup>
```

启用目录选择或品牌位图等新 UI 能力时走自定义 dialog 序列（提供 `.rtf` `BundlerLicenseFile` 时含许可页）；未启用任何 UI 能力时用 Windows Installer 原生基础界面。

### 4.2 per-machine 产品线

```xml
<PropertyGroup>
  <BundlerWixInstallScope>perMachine</BundlerWixInstallScope>
</PropertyGroup>
```

current-user 与 per-machine 是两条独立产品线，各自独立升级——切换 scope 等于换产品。

### 4.3 签名

`WixBundlerOptions.Signer` 或 MSBuild `BundlerWindowsSigning*`：先签隔离副本中的主程序与 `SigningFiles`，再签最终 MSI。
签名失败移除新 MSI；已签名同版本包不会被静默复用，重签需新输出目录或新版本。
生产证书与时间戳由发行方提供，私钥不入库。

## 5. 产物与边界

- `artifacts\<产品名>-<版本>-<生态词>.msi`（非英语 `…-<culture>.msi`）。
- 各语言是独立单语言 MSI 产品线。
- 构建宿主限 Windows；当前开发版未上公共 NuGet 源时先本仓 `dotnet pack` 指本地源。

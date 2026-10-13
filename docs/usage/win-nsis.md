# NSIS 安装器使用指南

产出 Windows NSIS `.exe` 安装器。语义契约（journal/恢复/退出码/锁定载荷/CLI 协议）见 [`../formats/win-nsis.md`](../formats/win-nsis.md)。

## 1. 接入

- 后端包 `DotNet.Bundler.Nsis`（`netstandard2.0`）：内嵌固定版本 `NsisToolset 3.12-r1`（上游 NSIS 3.12）+ `win-x86` Native AOT 插件，**任意宿主可构建**，无需安装 NSIS、不在线下载工具。
- 目标：`windows-i686`/`windows-x86_64`/`windows-aarch64`。

## 2. 最小使用

**MSBuild**：

```xml
<PropertyGroup>
  <BundlerFormats>nsis</BundlerFormats>
</PropertyGroup>
```

```powershell
dotnet publish -c Release -r win-x64
```

**CLI**：`bundler bundle --input-dir <publish> --target windows-x86_64 --formats nsis`

**直接 API**：

```csharp
using DotNet.Bundler;
using DotNet.Bundler.Nsis;

var artifacts = await new NsisBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "我的应用",
    Identifier = "com.example.myapp",
    Version = "1.0.0",
    OutputDirectory = "artifacts",
    Targets = [ new BundleTargetConfiguration
    {
        Target = "windows-x86_64",
        InputDirectory = "publish/win-x64",
        MainExecutable = "MyApp.exe",
        Formats = [PackageFormat.Nsis]
    }]
});
```

## 3. 全旋钮

MSBuild 前缀 `BundlerNsis*`；`bundler.json` 分节 `nsis.*`（`--nsis.<knob>` 透传）；API `NsisBundleConfiguration`（`new NsisBundler(config)`）+ `NsisBundlerOptions`。

| MSBuild | `nsis.*` 键 / API 成员 | 默认 | 说明 |
| --- | --- | --- | --- |
| `BundlerNsisInstallScope` | `installScope` / `InstallScope` | `currentUser` | `perMachine`（提权+Program Files+HKLM）/`both`（MultiUser 页选择，启动即请求最高可用权限）；x64/arm64 用 64 位注册表视图 |
| `BundlerNsisCompression` | `compression` / `Compression` | `lzma` | `zlib`/`bzip2`/`none` |
| `BundlerNsisLanguages` | `languages` / `Languages` | `English` | 分号/数组语言列表；第一项为回退语言。可用 22 语言：Arabic Bulgarian Dutch English French German Italian Japanese Korean Norwegian Persian(→Farsi) Portuguese PortugueseBR Russian SimpChinese Spanish SpanishInternational Swedish TradChinese Turkish Ukrainian Vietnamese |
| `BundlerNsisDisplayLanguageSelector` | `displayLanguageSelector` / `DisplayLanguageSelector` | `false` | 多语言时才显示选择器 |
| `BundlerNsisInstallerIconFile` | `installerIconFile` / `InstallerIconFile` | 第一个 `BundlerIcon` `.ico` | 安装器图标 |
| `BundlerNsisUninstallerIconFile` | `uninstallerIconFile` / `UninstallerIconFile` | 安装器图标 | |
| `BundlerNsisHeaderFile` | `headerFile` / `HeaderFile` | NSIS 默认 | `.bmp`，建议 150×57 |
| `BundlerNsisSidebarFile` | `sidebarFile` / `SidebarFile` | NSIS 默认 | `.bmp`，建议 164×314 |
| `BundlerNsisUninstallerHeaderFile` | `uninstallerHeaderFile` / `UninstallerHeaderFile` | Header 图 | `.bmp` |
| `BundlerNsisInstallerHooksFile` | `installerHooksFile` / `InstallerHooksFile` | 无 | `.nsh` 定义 `NSIS_HOOK_PREINSTALL`/`POSTINSTALL`/`PREUNINSTALL`/`POSTUNINSTALL` 宏（§4.3） |
| `BundlerNsisAllowDowngrades` | `allowDowngrades` / `AllowDowngrades` | `false` | 显式开启才允许降级安装 |
| `BundlerNsisLegacyMsiProductCodes` | `legacyMsiProductCodes` / `LegacyMsiProductCodes` | 空 | 分号/数组 ProductCode GUID（精确产品） |
| `BundlerNsisLegacyMsiUpgradeCodes` | `legacyMsiUpgradeCodes` / `LegacyMsiUpgradeCodes` | 空 | 分号/数组 UpgradeCode GUID（查全部关联产品） |
| `BundlerNsisLegacyMsiAutoDetect` | `legacyMsiAutoDetect` / `LegacyMsiAutoDetect` | `false` | 按 DisplayName+Publisher+msiexec 自动匹配旧 MSI（§4.2 警告） |
| `BundlerNsisShortcutDesktop` | `shortcuts.desktop` / `Shortcuts.Desktop` | `true` | 桌面快捷方式 |
| `BundlerNsisShortcutStartMenu` | `shortcuts.startMenu` / `Shortcuts.StartMenu` | `true` | 开始菜单快捷方式 |
| `BundlerNsisShortcutArguments` | `shortcuts.arguments` / `Shortcuts.Arguments` | 无 | |
| `BundlerNsisShortcutWorkingDirectory` | `shortcuts.workingDirectory` / `Shortcuts.WorkingDirectory` | 安装目录 | 安装目录内相对路径 |
| `BundlerNsisShortcutIcon` | `shortcuts.icon` / `Shortcuts.Icon` | 主程序 | 安装目录内相对路径 |
| `BundlerNsisShortcutAppUserModelId` | `shortcuts.appUserModelId` / `Shortcuts.AppUserModelId` | `BundlerIdentifier` | |
| `BundlerNsisShortcutStartMenuFolder` | `shortcuts.startMenuFolder` / `Shortcuts.StartMenuFolder` | `BundlerProductName` | `.` 表示 Programs 根目录 |
| `BundlerNsisShortcutLegacyProductNames` | `shortcuts.legacyProductNames` / `Shortcuts.LegacyProductNames` | 空 | 改名后显式启用安全迁移 |
| `BundlerNsisShortcutLegacyMainExecutables` | `shortcuts.legacyMainExecutables` / `Shortcuts.LegacyMainExecutables` | 空 | 旧主程序安装相对路径 |
| `BundlerNsisTemplate` | — / `NsisBundlerOptions.TemplatePath` | 包内模板 | 自定义模板须保留占位符（§4.4） |
| `BundlerNsisToolsetArchivePath`/`…CompilerPath`/`…DataDirectory` | — / `NsisBundlerOptions` | 包内嵌 | 测试/高级覆盖；自定义编译器需 `NSISDIR` 时还应给 `DataDirectory` |

**项**：`@BundlerNsisLanguageFile`（`Language` 元数据）整文件替换所选语言内置文案——同语言仅一件，必须恰好含模板要求的全部 `LangString`，缺失/重复/未知键/错 `LANG_*` 常量打包时报错。API/JSON 侧为 `nsis.customLanguageFiles`（language→file 字典）。

## 4. 场景

### 4.1 多语言安装器

```xml
<PropertyGroup>
  <BundlerNsisLanguages>English;SimpChinese;Japanese</BundlerNsisLanguages>
  <BundlerNsisDisplayLanguageSelector>true</BundlerNsisDisplayLanguageSelector>
</PropertyGroup>
```

### 4.2 旧 MSI 迁移

```xml
<PropertyGroup>
  <BundlerNsisLegacyMsiProductCodes>{PRODUCT-CODE-GUID}</BundlerNsisLegacyMsiProductCodes>
  <BundlerNsisLegacyMsiUpgradeCodes>{UPGRADE-CODE-GUID}</BundlerNsisLegacyMsiUpgradeCodes>
</PropertyGroup>
```

安装器运行时用 Windows Installer API 精确查询并先卸载全部匹配 MSI 再写载荷；"成功但需重启"返回码被正确处理。
**不要**用产品名猜测；`LegacyMsiAutoDetect` 有误删风险，真实迁移标识必须取自历史 MSI，仓库测试夹具不能代替生产验证。

### 4.3 生命周期 Hook

`BundlerNsisInstallerHooksFile` 指向 `.nsh`，定义 `NSIS_HOOK_PREINSTALL`/`NSIS_HOOK_POSTINSTALL`/`NSIS_HOOK_PREUNINSTALL`/`NSIS_HOOK_POSTUNINSTALL` 任意几项，按安装器当前权限执行。
安装 Hook 用 `SetErrors`/`Abort` 报失败即回滚已激活事务；`Quit`/终止进程无法即时回滚，active journal 由下次安装启动恢复——卸载 Hook 必须按幂等编写。

### 4.4 自定义模板

复制 `src/Bundler.Nsis/templates/installer.nsi`，把 `BundlerNsisTemplate` 指到副本绝对路径。
必须保留签名两阶段编译占位符 `uninstaller_finalize_command`/`uninstaller_import_define`/`signed_uninstaller`；用配置压缩时保留 `compression_directive`。
模板占位符共 53 个（写法 `{{name}}`），分身份（`product_name`/`version`/`numeric_version`/`publisher`/`identifier`/`copyright`/`description`/`homepage*`）、安装（`main_executable`/`install_folder`/`install_mode`/`target_architecture`/`allow_downgrades`/`input_glob`/`output_file`/`estimated_size`/`plugin_directory`/`uninstall_payload`）、迁移（`legacy_msi_*`）、语言（`language_*`/`display_language_selector`）、快捷方式（`shortcut_*`×12）、事务 journal（`transaction_*`×5）、文件关联（`association_*`）、资源（`resource_install_commands`）、许可页（`license_page`）、Hook（`installer_hooks_include`）、图标（`installer_icon_directives`）等族——**丢弃 `transaction_*`/`shortcut_*` 占位会静默失去 journal 恢复与快捷方式功能**；完整集合以 `templates/installer.nsi` 的 `{{…}}` 为准。

### 4.5 签名

MSBuild 用共享 `BundlerWindowsSigning*`（见 [`msbuild.md` §7](msbuild.md)）；API 用 `NsisBundlerOptions.Signer`（`WindowsAuthenticodeSigner` 或自定义 `IBundleSigner`，目标级 `SigningFiles` 追加追签文件）。
CLI 无签名配置面。

本地自签名端到端验证（只验证签名流程；自签名证书无 CA 链，`Get-AuthenticodeSignature` 报 `UnknownError` 属预期）：

```powershell
# 在仓库根目录创建一次性测试证书。
$certificate = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=Hello Bundler App Test Publisher" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter ([DateTime]::Now.AddDays(1))
$thumbprint = $certificate.Thumbprint

dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r win-x64 `
  -p:BundlerFormats=nsis `
  -p:HelloBundlerSigningCertificateThumbprint=$thumbprint

$installer = Resolve-Path `
  "samples/HelloBundlerApp/artifacts/Hello Bundler App-1.0.0-x64-setup.exe"
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
$installerSignature | Select-Object Status, StatusMessage
if ($installerSignature.SignerCertificate.Thumbprint -ne $thumbprint) {
  throw "安装器没有使用预期证书签名。"
}

# 安装后再验主程序与卸载器（改目录则替换路径）：
$mainExecutable = "$env:LOCALAPPDATA\Programs\Hello Bundler App\HelloBundlerApp.exe"
$uninstaller = "$env:LOCALAPPDATA\Programs\Hello Bundler App\Uninstall.exe"
Get-AuthenticodeSignature -LiteralPath $mainExecutable
Get-AuthenticodeSignature -LiteralPath $uninstaller

# 验收完成后删除一次性证书。
Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -Force
```

观察 UAC 发布者页可加 `-p:HelloBundlerNsisInstallScope=perMachine`；正式发布必须换受信任 CA 证书 + RFC 3161 时间戳。

## 5. 产物与边界

- `artifacts\<产品名>-<版本>-<生态词>-setup.exe`。
- x86 载荷运行需 32 位 .NET 运行时（仅 x64 运行时宿主报 `0x800700C1`），属目标环境前置。
- 产物不承诺逐字节一致（makensis 编入源时间戳）。

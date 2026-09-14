# HelloBundledApp 功能演示

这个项目不是自动化测试，而是 `DotNet.Bundler` 当前 Windows + NSIS 功能的可操作演示。项目只通过普通 `PackageReference` 使用本地生成的 NuGet 包。

## 生成安装程序

在仓库根目录执行：

```powershell
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release
```

安装程序位于：

```text
samples/HelloBundledApp/artifacts/win-x64/nsis/Hello Bundled App-1.0.0-setup.exe
```

构建时会从仓库固定版本的 NsisToolset 中把演示图片提取到 `obj`，用于展示安装器图标、卸载器图标、Header、卸载器 Header 和 Sidebar。它们不会写入项目源码目录。

## 默认演示内容

默认安装程序直接展示以下功能：

- English 与简体中文语言选择器，以及项目提供的自定义简体中文语言文件；
- 许可证页面、发布者、描述、主页、版权和版本元数据；
- 自定义安装器/卸载器图标、Header、卸载器 Header 和 Sidebar；
- 自定义安装目录、非空目录警告和记忆上次安装目录；
- 可选桌面快捷方式和开始菜单快捷方式；
- 安装前检测并关闭正在运行的应用；
- 安装前、安装后、卸载前、卸载后的 NSIS Hook；
- `.hello` 文件关联和 `hello-bundled:` 深链接；
- 安装目录自带 `demo.hello` 和 `运行深链接.cmd`，可以直接操作两项关联功能；
- 安装额外资源到 `演示资源\说明.txt`；
- 同版本重装、升级、降级控制；
- 卸载时选择是否删除应用数据；
- 静默安装和静默卸载。

程序启动后会显示收到的参数、额外资源是否存在以及安装 Hook 是否执行，并将最近一次启动写入：

```text
%LOCALAPPDATA%\com.example.hellobundledapp\last-launch.txt
```

卸载时选择删除应用数据会同时删除这份记录。

## 文件关联与深链接

深链接安装后可以直接启动：

```powershell
Start-Process "hello-bundled:welcome"
```

文件关联：

```powershell
Start-Process "$env:LOCALAPPDATA\Programs\Hello Bundled App\demo.hello"
```

Windows 不允许安装器静默替换用户选择的默认程序。第一次打开 `.hello` 文件时可能显示应用选择器，此时选择 **Hello Bundled App**。程序会显示收到的文件路径。

也可以直接双击安装目录中的 `运行深链接.cmd`，该脚本会运行 `hello-bundled:welcome`。如果安装时修改了目录，请从实际安装目录运行这两个演示文件。

## 安装范围

三种安装范围互斥，因此通过构建参数分别生成：

```powershell
# 当前用户安装，不需要管理员权限
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:HelloBundledAppInstallMode=currentUser

# 所有用户安装，请求管理员权限并安装到 Program Files
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:HelloBundledAppInstallMode=perMachine

# 安装时由用户选择当前用户或所有用户
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:HelloBundledAppInstallMode=both
```

每次生成使用相同的文件名，演示不同模式时应先复制或重命名上一次的产物。

## 重装、升级与降级

先安装 `1.0.0`，再用不同版本生成安装程序：

```powershell
# 生成升级包
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:Version=1.1.0

# 生成默认禁止降级的 0.9.0
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:Version=0.9.0

# 生成明确允许降级的 0.9.0
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -p:Version=0.9.0 -p:HelloBundledAppAllowDowngrades=true
```

再次运行同版本安装程序会展示重装策略；运行高版本和低版本安装程序会分别展示升级及降级策略。让程序保持在“按任意键退出”界面，再运行安装或卸载程序，可以展示运行进程检测和关闭。

## 静默模式

NSIS 的 `/D=` 必须是最后一个参数：

```powershell
& '.\Hello Bundled App-1.0.0-setup.exe' /S '/D=C:\演示安装目录\Hello Bundled App'
& 'C:\演示安装目录\Hello Bundled App\Uninstall.exe' /S
& 'C:\演示安装目录\Hello Bundled App\Uninstall.exe' /S /DELETEAPPDATA
```

## 生命周期 Hook

示例的 Hook 会在临时目录创建以下标记：

```text
%TEMP%\HelloBundledApp-hook-preinstall.txt
%TEMP%\HelloBundledApp-hook-postinstall.txt
%TEMP%\HelloBundledApp-hook-preuninstall.txt
%TEMP%\HelloBundledApp-hook-postuninstall.txt
```

## 旧 MSI 迁移

迁移功能不能使用随意生成的 GUID 演示，否则可能匹配不到任何产品，甚至误卸载其他软件。必须使用待迁移 MSI 的真实 ProductCode 或 UpgradeCode：

```powershell
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release `
  -p:HelloBundledAppLegacyMsiProductCodes='{真实的-PRODUCT-CODE}' `
  -p:HelloBundledAppLegacyMsiUpgradeCodes='{真实的-UPGRADE-CODE}'
```

仓库的自动化集成测试使用一次性 MSI Fixture 完整验证这条路径；本示例只保留安全、明确的参数入口。

## 高级覆盖入口

`BundlerToolCachePath`、`BundlerNsisToolsetArchivePath`、`BundlerNsisCompilerPath`、`BundlerNsisDataDirectory` 和 `BundlerNsisTemplate` 是工具链或模板的高级覆盖入口。示例默认不设置它们，因为默认路径本身要演示“NuGet 包自带多宿主 NSIS 工具且多个项目共享缓存”的正常行为。需要调试自定义工具链或模板时，可以通过同名 MSBuild 属性传入。

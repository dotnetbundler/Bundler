# HelloMsiApp：Windows MSI 示例

本示例用 MSBuild 调用同一 Core 和 WiX 后端，生成 Windows x64、current user MSI，并声明开始菜单/桌面快捷方式、`.hellomsi` 文件与 MIME 候选处理程序及 `hello-msi:` 协议候选处理程序。干净宿主、其他架构、per-machine 提权安装及默认应用界面的真实选择/唤起仍待外部验收。`alpha.37` 是当前已实现能力的本机冻结基线；计划中的 WIN-MSI-5..9（x86、更多语言、安装 UI 等）尚不可用于此示例。

与 NSIS 示例相同，项目文件把还原源指定为仓库的 `artifacts/packages`。在仓库根目录先构建本地 NuGet 包，再直接发布：

```powershell
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release
```

示例项目与 NSIS 示例一样固定引用当前开发包版本，并以本地 `artifacts/packages` 为还原源。`dotnet publish` 会使用已经还原的 `obj/project.assets.json` 与 NuGet 包缓存；它不会直接编译当前 `src/` 源码，也不会自动重打本仓库的 NuGet 包。修改 Bundler 后先迭代包版本并执行 `pack`，再发布示例。若缺少当前版本的本地包，先运行上述 `dotnet pack`。

产物路径为 `samples/HelloMsiApp/artifacts/win-x64/msi/Hello MSI App-1.0.0.msi`。MSI 构建只在 Windows 宿主开放，使用包内固定的 WiX 3.14.1 工具子集和用户级校验缓存；运行时不下载工具或应用依赖。

MSI 只接受稳定的 `major.minor.patch` 版本，major/minor 不超过 255、patch 不超过 65535。示例界面语言仍为默认 `en-US`，设置 `BundlerWixCodepage=936` 以容纳中文描述；可以传入 `-p:BundlerWixLanguage=zh-CN` 构建独立中文 MSI，输出文件、产品身份和安装目录都会带 `-zh-cn`。提供应用自己的 `.rtf` 文件并设置 `BundlerLicenseFile` 才启用 WiX 最小交互许可界面；本示例不代应用提供许可条款。签名可使用通用 `BundlerWindowsSigning*` 属性；生产证书与时间戳由发行方管理。默认英文包安装到 `%LOCALAPPDATA%\Programs\com.example.hellomsiapp-x64`。示例应用版本固定为 `1.0.0`；升级/降级用独立 fixture 测试。快捷方式默认关闭，本例显式打开；关联和协议只注册自身候选处理程序，不修改用户默认项。可在 Windows 默认应用设置中选择处理程序，当前仓库尚未在人工环境验证实际唤起。per-machine 可设置 `BundlerWixInstallScope=perMachine` 生成独立包，但提权安装尚未验收。

安装后可在 Windows **设置 → 应用 → 已安装的应用** 中找到“Hello MSI App”并选择卸载；也可用安装包的 ProductCode 执行 `msiexec /x <ProductCode>`。包版本（如 `0.1.0-alpha.37`）只标识打包工具；示例应用的 `BundlerVersion=1.0.0` 决定 MSI 产品版本和 ProductCode。开发期的示例内容可能变化；同一应用版本的不同 MSI 不应当作可升级的正式版本。若已有旧版示例 MSI 安装在本机，先卸载后再试装新构建。正式发布的应用修改安装内容时仍需按产品版本规则升级版本。

MSI 后端拒绝在同一路径覆盖内容不同、但应用版本相同的 MSI。开发时修改示例内容后，可以指定新的 `BundlerOutputPath`，或先把该示例上一次生成的 MSI 及其 `.bundler-manifest` 移出输出目录；这只处理构建产物，不会卸载已经安装的应用。当前工作区原先的阶段一 `1.0.0` 产物及 manifest 已保存在 `samples/HelloMsiApp/artifacts/previous-msi/`，当前输出目录里的 `1.0.0` 是阶段二示例产物。

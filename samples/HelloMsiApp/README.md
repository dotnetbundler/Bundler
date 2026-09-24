# HelloMsiApp：Windows MSI 最小示例

本示例展示 WIN-MSI-1 已实现并在当前 Windows x64 开发机通过独立 fixture 验证的能力：用 MSBuild 调用同一 Core 和 WiX 后端，生成 Windows x64、current user 范围的 MSI。第一阶段的本机范围退出条件已完成，工具供应/许可工程核查亦通过；干净宿主和其他架构的外部矩阵尚未验收，后续 MSI 能力与格式冻结尚未完成，不应把当前结果当成广泛发行支持声明。

在仓库根目录先构建本地 NuGet 包，再从本地源还原并发布：

```powershell
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet restore samples/HelloMsiApp/HelloMsiApp.csproj --source artifacts/packages
dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release --no-restore
```

产物路径为 `samples/HelloMsiApp/artifacts/win-x64/msi/Hello MSI App-1.0.0.msi`。MSI 构建只在 Windows 宿主开放，使用包内固定的 WiX 3.14.1 工具子集和用户级校验缓存；运行时不下载工具或应用依赖。

首版只接受稳定的 `major.minor.patch` 版本，且 major/minor 均不超过 255、patch 不超过 65535。示例设置 `BundlerWixCodepage=936` 以容纳中文描述；WiX 3 的 MSI 数据库不能安全地把 UTF-8 当作通用代码页，其他语种应选择相容的 Windows ANSI 代码页。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\com.example.hellomsiapp-x64`。升级、per-machine、快捷方式、文件关联、签名和多语言 UI 属于后续阶段；当前配置这些能力会报错。

# DotNet.Bundler

`DotNet.Bundler` 是一个通用桌面应用打包工具。
使用者引用包并配置少量 MSBuild 属性后，可以把 `dotnet publish` 产物转换为桌面安装程序；`bundler` CLI 与直接公共 API 复用同一套实现，为其他语言和构建系统提供入口。

## 快速开始

```powershell
dotnet publish -c Release -r win-x64   # csproj 里加 PackageReference DotNet.Bundler + BundlerEnabled=true
bundler bundle --input-dir <publish目录>  # 或 CLI：只指发布目录，其余推断
```

**使用文档族在 [`docs/usage/`](docs/usage/README.md)**：每个入口、每个后端一件，从最小配置到全量旋钮。
实现原理见 [`docs/architecture.md`](docs/architecture.md)；格式契约见 [`docs/formats/`](docs/formats/)；项目规则见 [`docs/project-rules.md`](docs/project-rules.md)。

## 当前状态

全部打包格式、CLI 与 UPDATE 自更新模块均已冻结（冻结=功能面不再扩展，缺陷修复照走）。

| 平台 | 格式 | 使用文档 | 契约文档 |
| --- | --- | --- | --- |
| Windows | `nsis`、`msi` | [`win-nsis.md`](docs/usage/win-nsis.md)、[`win-msi.md`](docs/usage/win-msi.md) | [`docs/formats/`](docs/formats/) |
| macOS | `.app`、`.dmg`、`.pkg` | [`mac-app.md`](docs/usage/mac-app.md)、[`mac-dmg.md`](docs/usage/mac-dmg.md)、[`mac-pkg.md`](docs/usage/mac-pkg.md) | 同上 |
| Linux | `.deb`、`.rpm`、`.AppImage`、Alpine `.apk` | [`linux-deb.md`](docs/usage/linux-deb.md)、[`linux-rpm.md`](docs/usage/linux-rpm.md)、[`linux-appimage.md`](docs/usage/linux-appimage.md)、[`linux-alpine-apk.md`](docs/usage/linux-alpine-apk.md) | 同上 |
| 跨平台 | `.zip`/`.tar.gz` | [`archive.md`](docs/usage/archive.md) | 同上 |
| 其他 | CLI、MSBuild、UPDATE、签名 | [`cli.md`](docs/usage/cli.md)、[`msbuild.md`](docs/usage/msbuild.md) | [`docs/update.md`](docs/update.md)、[`docs/signing.md`](docs/signing.md) |

各格式打包/消费的平台支持范围汇总见 [`docs/support-matrix.md`](docs/support-matrix.md)。
全后端统一示例见 [`samples/HelloBundlerApp`](samples/HelloBundlerApp/hello-bundler-app-sample.md)：单个工程覆盖全部格式的全部公开旋钮。
外部待验收项登记在 [`docs/special-acceptance.md`](docs/special-acceptance.md)；后续路线和产品边界见 [`docs/roadmap.md`](docs/roadmap.md)。

## 包结构

`DotNet.Bundler` 只是便利元包，实际打包代码位于以下各层：

| 包 | 职责 |
| --- | --- |
| `DotNet.Bundler.Abstractions` | 公共请求、目标、结果、日志契约和后端接口 |
| `DotNet.Bundler.Core` | 验证、规划、编排、工作目录和共享的内容寻址 ZIP 工具缓存 |
| `DotNet.Bundler.Nsis` | NSIS API、配置、脚本生成、内置 `makensis` 工具链 |
| `DotNet.Bundler.Wix` | MSI API、WiX 3.14.1 工具子集和声明式 Windows Installer 数据库生成 |
| `DotNet.Bundler.Signing.Windows` | 可复用的 Windows Authenticode 签名实现 |
| `DotNet.Bundler.MacApp` / `MacDmg` / `MacPkg` | `.app`/`.dmg`/`.pkg` API（`dmg`/`pkg` 仅 macOS 宿主） |
| `DotNet.Bundler.Deb` / `Rpm` / `AlpineApk` / `Archive` | Linux 各格式与归档 API（纯托管写入器，无原生工具依赖） |
| `DotNet.Bundler.AppImage` | `.AppImage` API、内嵌 appimagetool+type2 runtime（仅 Linux 宿主） |
| `DotNet.Bundler.Update` / `Updater` / `Updater.Bootstrap` | UPDATE 清单侧车 / 应用内更新客户端 / Native AOT 引导件 |
| `DotNet.Bundler.MSBuild` | MSBuild 参数转换与后端 API 调用 |
| `DotNet.Bundler.Cli` | `bundler` dotnet tool 命令包（`validate`/`plan`/`bundle`/`init`/`update-keygen`） |
| `DotNet.Bundler` | 空元包，引入 MSBuild 包且不屏蔽传递性构建资产 |

`DotNet.Bundler.Nsis` 内嵌固定版本 `NsisToolset 3.12-r1`（上游 NSIS 3.12）：公共 NSIS 数据目录 + Windows/Linux/macOS 宿主编译器，无需安装 NSIS 或在线下载。
缓存经压缩包 SHA-256 + 逐文件 manifest 校验，跨进程锁内安全重建。

所有格式共用同一管线（校验→计划→隔离工作目录→后端→产物核验→清理）。
产物确定性：`.deb`/`.rpm`/`.apk`/`.zip`/`.tar.gz` 逐字节确定；`.nsis` 与 `.AppImage` 不承诺（makensis 时间戳、squashfs 元数据属上游工具性质）。

## 仓库命令

```powershell
dotnet build Bundler.slnx -c Release
dotnet test tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet test tests/Bundler.ApiTests/Bundler.ApiTests.csproj -c Release
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class "*NsisIntegrationTests*"
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundlerApp/HelloBundlerApp.csproj -c Release -r win-x64
```

集成测试按格式类/资源 trait 筛选与真装同意闸见 [`tests/README.md`](tests/README.md)；只能人工/外部付费完成的验收统一登记在 [`docs/special-acceptance.md`](docs/special-acceptance.md)。

## 安全与许可证

NsisToolset 发布包的校验值固定在源码中；NuGet 包携带第三方声明和 NSIS 上游许可证。
本仓库采用 MIT 许可证（见根目录 `LICENSE`），NuGet 包同步携带 `MIT` license expression 与许可证文件。

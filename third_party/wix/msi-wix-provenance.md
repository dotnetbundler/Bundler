# WiX Toolset 3.14.1 来源与分发核查

MSI 后端使用官方 `wix3141rtm` 发布版本。
仅供构建的二进制子集 `wix3141-tools.zip` 从官方 `wix314-binaries.zip` 原样选取文件：

- 发布地址：https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm
- 原始二进制归档 SHA-256：`6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31`
- 原始二进制归档大小：41,297,555 字节
- 随包子集 SHA-256：`ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358`
- 随包子集大小：1,642,074 字节
- 对应标签源码归档：https://codeload.github.com/wixtoolset/wix3/zip/refs/tags/wix3141rtm
- 随包对应源码 `wix3141-source.zip` 的 SHA-256：`A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F`
- 源码归档大小：13,599,826 字节
- 许可证：`LICENSE.TXT` 与源码归档中的 Microsoft Reciprocal License（MS-RL）

子集包含 `candle.exe`、`light.exe`、各自的配置文件、`wix.dll`、`wconsole.dll`、`winterop.dll`、`darice.cub`、五个 `Microsoft.Deployment.*` 程序集、`WixUIExtension.dll` 和 `LICENSE.TXT`。
逐文件 SHA-256 见 `SHA256SUMS`。

## 再分发核查（2026-09-24）

最初的 14 个子集条目均按固定的逐文件哈希与官方发布包比对，并与 `wix3141rtm` 源码归档中的文件或项目对应。
源码位置包括 `src/tools/candle`（`candle.exe` 及配置）、`src/tools/light`（`light.exe`、配置和 `darice.cub`）、`src/tools/wix`（`wix.dll`）、`src/tools/wconsole`、`src/tools/winterop` 和 `src/DTF/Libraries/{Compression,Compression.Cab,Resources,WindowsInstaller,WindowsInstaller.Package}`。
已检查的 12 处适用源码、配置或项目文件头标明 MS-RL；
根部 `LICENSE.TXT` 原样随包。
已检查生成的 NuGet 包，包含完整标签源码归档、许可证、校验清单及 `THIRD-PARTY-NOTICES.md`。
未发现所选子集另有独立许可证；
如随包文件变动须重新核查。
这是对选定文件的工程核查，不构成法律保证。

WIN-MSI-3 增加第 15 个条目：从同一官方二进制归档原样取得 `WixUIExtension.dll`（3,768,320 字节，SHA-256 `C6B8227782A7268B54C0C161CA300FAE869A53CE9889D19C16B6B900AC468DC3`）。
随包标签源码归档中，对应项目及 UI 源码位于 `src/ext/UIExtension/wixext` 和 `src/ext/UIExtension/wixlib`。
当应用提供 RTF 许可时，该扩展提供 WiX 3 内置的本地化 MSI 对话框，属于免费提供的扩展。
其源码文件头和项目许可证已对照随包 MS-RL 核查。
许可证、源码归档和校验清单继续随 NuGet 包提供；
正式发布前重新核对生成包。

MS-RL 在满足其条件（包括对应源码和声明保留）的情况下授予免版税的版权及专利许可。
WiX 后续的 Open Source Maintenance Fee 从 v6 引入，不适用于 v3.14.1。
来源：[上游许可证](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT)、[WiX 版本历史](https://docs.firegiant.com/wix/whatsnew/)。

WIN-MSI-4 于 2026-09-25 重新核对未改变的 15 文件子集。
Release 构建现将 WiX 编译器和链接器警告视为错误，只有已说明的纯当前用户安装 `ICE91` 例外。
MSI 集成测试的打包核对按 SHA-256 逐字节比对 NuGet 包内的许可证、对应源码归档、校验清单、本文与 `THIRD-PARTY-NOTICES.md`。
最终包大小和 SHA-256 记于 `docs/msi-roadmap.md` 第 9 节，以免包内哈希自我引用。
没有新增 WiX 或其他第三方二进制。
WiX v3 已不再获得免费社区服务，包括安全修复：[官方状态](https://docs.firegiant.com/wix/wix3/)。
大范围公开分发前须重新评估维护风险。

对应源码归档随 `DotNet.Bundler.Wix` NuGet 包提供。
便利元包 `DotNet.Bundler` 的消费者通过其 `DotNet.Bundler.Wix` 依赖获得它。
工具解压到经过哈希校验的本地缓存；打包时不下载工具或应用运行时。

WiX v3 已退出免费社区服务。
本项目不依赖付费 FireGiant 支持或扩展。
构建宿主的 .NET Framework 与 ARM64 兼容性仍以 `docs/msi-roadmap.md` 中 Windows 虚拟机矩阵的验证为准。

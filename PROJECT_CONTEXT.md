# DotNet.Bundler 项目上下文与后续实施基线

> 最后整理：2026-09-25
> 当前分支：`msi-development`（NSIS 开发线为 `nsis-development`）
> NSIS 冻结起点：`71a5c90 feat(nsis): 冻结安全打包基线`；当前提交以 `git rev-parse --short HEAD` 为准
> 当前包版本：`0.1.0-alpha.40`（WIN-MSI-5 实现与测试；Git 状态与包版本均须实时核查）
> 当前阶段：`WIN-MSI-1..5` 的当前 Windows 11 x64 本机自动化范围已完成；`alpha.37` 是既有 x64/ARM64 MSI 身份基线，`alpha.40` 增加 x86、显式 MSI 版本映射与可选降级。`WIN-MSI-6..9` 尚未实施；默认下一实施阶段为 `WIN-MSI-6`，`MAC-APP` 顺延。per-machine、生产签名、干净宿主和其他架构等外部验收边界不变。

本文档记录当前事实、决策与验证证据，供后续开发任务接续。跨格式开发与交接规则以 `docs/development-rules.md` 为唯一规范入口；正式路线见 `docs/roadmap.md`，MSI 细则见 `docs/msi-roadmap.md`。本文档不是面向最终用户的使用手册。代码与自动化测试始终是实现事实的最终依据。

状态标记：

- **已实现并自动验证**：代码已完成，仓库测试或集成测试已通过。
- **已实现但需外部验收**：通用实现和仓库内 Fixture 已完成，但仍缺生产凭据、真实旧包、UAC 或多版本 Windows 等外部条件。
- **未实现**：只能作为路线，不得在 README 或发布说明中宣称支持。

## 1. 产品目标与范围

### 1.1 最终使用方式

`DotNet.Bundler` 的产品目标是通用桌面打包工具，不以 .NET 应用或 MSBuild 为最终边界。Core 与各格式后端接受普通文件目录、入口程序和格式配置，不应要求输入来自 `dotnet publish`。

当前首先发布 NuGet/MSBuild 集成：.NET 使用者引用包、填写 MSBuild 配置后，正常执行 `dotnet publish` 即可生成桌面安装包，不需要单独安装 CLI，也不需要预装打包工具。同时保留独立公共 API；其他语言或构建系统后续通过正式 CLI 使用同一套 Core 和后端。MSBuild Task 和 CLI 都只是参数适配层，不拥有 NSIS、MSI 等格式的实现。

### 1.2 当前边界

- 当前只做桌面端。
- 第一条完整链路是 **Windows 目标 + NSIS 安装包**。
- MSI/WiX 已完成 WIN-MSI-1..5 的当前主机本机自动化范围，alpha 格式配置与身份规则已冻结；WIN-MSI-5 新增 x86、显式 MSI 版本映射与可选降级。外部 Windows/UAC/生产验收仍待执行；WIN-MSI-6..9 尚未实施，macOS 与 Linux 安装格式尚未实现。
- NSIS 目标包可以由 Windows、Linux x64/arm64、macOS x64/arm64 宿主编译；工具解析与嵌入结构已经实现，但所有宿主的真实 CI 执行矩阵仍需补齐。
- 快速推进阶段允许破坏性修改，不要求兼容早期 alpha API 或配置。
- 解决方案文件使用 `Bundler.slnx`。

### 1.3 不可回退的设计原则

1. 打包所需工具随 NuGet 包发布，普通用户不自行准备工具。
2. 工具不解压到每个使用者项目的中间输出目录；使用内容寻址的共享用户缓存，默认位于 `%LOCALAPPDATA%\DotNetBundler\tools`。缓存以固定归档 SHA-256 和归档内逐文件哈希为信任锚，持久化 manifest，并在跨进程锁内检测和恢复缺失、篡改、额外文件、manifest 损坏或重解析点污染。
3. MSBuild Task 及其直接加载的程序集必须提供 `netstandard2.0` 资产。
4. MSBuild Task 在进程内调用 Core 和后端公共 API，不再启动额外的 .NET CLI 驱动。
5. 后端仍必须启动对应的原生编译器，例如 NSIS 的 `makensis`；这是生成安装器本身所必需的原生工具调用，不等同于用 CLI 承载业务逻辑。
6. 各格式共用校验、规划、编排、工作目录与工具缓存流程；新增格式应新增后端，不复制整条管线。
7. NSIS 模板保存在独立文件中，不能把完整脚本硬编码在 C# 字符串里；新增 NSIS 脚本注释使用中文。
8. 不确定的安装器语义优先核对 Tauri 的现行行为，但只对齐适用于通用桌面打包器的用户能力，不能机械照搬字段、Rust 数据模型或 Tauri runtime 行为。
9. 本项目不内建任意应用运行时/先决条件的发现、下载、版本检测、安装和卸载编排；WebView2、VC Runtime 等框架运行时部署不因 Tauri 支持而自动进入路线。调用方仍可装入已准备的文件或使用格式扩展点。

## 2. 已确定的项目结构

| 项目/包                          | 职责                                                              | 当前目标框架            |
| -------------------------------- | ----------------------------------------------------------------- | ----------------------- |
| `DotNet.Bundler.Abstractions`    | 公共请求、目标、结果、日志、签名与后端契约                        | `netstandard2.0`        |
| `DotNet.Bundler.Core`            | 格式无关的验证、规划、编排、工作目录、内容寻址 ZIP 工具缓存       | `netstandard2.0;net8.0` |
| `DotNet.Bundler.Nsis`            | 可独立使用的 NSIS API、配置模型、脚本渲染、语言、内嵌工具集和插件 | `netstandard2.0`        |
| `DotNet.Bundler.Signing.Windows` | 可复用的 Windows Authenticode 签名 API                            | `netstandard2.0`        |
| `DotNet.Bundler.MSBuild`         | 将 MSBuild 参数转换为公共请求，调用 Core/后端并输出 `ITaskItem`   | `netstandard2.0`        |
| `DotNet.Bundler`                 | 便利元包，引入 MSBuild 集成                                       | NuGet 元包              |
| `DotNet.Bundler.Cli`             | 已有的命令行原型；复用 Core/后端，但参数有限且当前不发布           | 默认 `net8.0`           |
| `DotNet.Bundler.Wix`             | MSI 公共 API、生成逻辑与固定 WiX 3.14.1 工具资源                 | `netstandard2.0`        |

核心调用关系：

```text
普通直接 API 消费者 -> DotNet.Bundler.Nsis / DotNet.Bundler.Wix
MSBuild 应用层 -> DotNet.Bundler.MSBuild -> 同一后端公共 API
未来 CLI 应用层 -> 同一 Core/后端公共 API
格式后端 -> DotNet.Bundler.Core -> Abstractions 契约与共享管线
```

`DotNet.Bundler.Nsis` 和 `DotNet.Bundler.Wix` 均为可独立引用的后端包，不是 MSBuild 内部实现；调用者可以分别直接调用 `NsisBundler` 和 `WixBundler`。MSBuild/未来 CLI 是应用层。独立包消费测试见 `docs/development-rules.md` 第 5 节。

## 3. NSIS 工具供应链

### 3.1 工具来源与布局

NSIS 工具统一使用通用项目 [dotnetbundler/NsisToolset](https://github.com/dotnetbundler/NsisToolset)。该项目服务所有潜在消费者，不能描述成只供 `DotNet.Bundler.Nsis` 使用。

当前仓库嵌入：

```text
third_party/nsis/nsis-toolset-3.12-r1.zip
```

其上游 NSIS 版本为 3.12，逻辑布局为：

```text
nsis/<版本>/
├─ common/
├─ hosts/
│  ├─ win/
│  ├─ linux-x64/
│  ├─ linux-arm64/
│  ├─ osx-x64/
│  └─ osx-arm64/
├─ toolset-manifest.json
├─ COPYING
└─ README.md
```

每个平台不需要复制公共脚本、Include、Contrib 和 Stub；`common/` 只保留一份，每个 `hosts/<rid>/` 只放该宿主执行编译所需的原生文件。

重要实现事实：

- Windows 根目录的 `makensis.exe` 是启动器；`Bin/makensis.exe` 才是编译器，且需要 `Bin/zlib1.dll`。
- 当 NSIS 使用 `NSIS_CONFIG_CONST_DATA_PATH=no` 构建时，不会自动找到顶层拆分出来的 `common/`；启动器或调用方必须设置 `NSISDIR`。
- `makensis.cmd` 的作用只是为这种拆分布局设置环境并启动真实编译器，不是额外的打包实现。程序化调用也可以直接设置 `NSISDIR` 后启动真实二进制。
- NsisToolset 仓库中的 Python 文件负责下载、暂存、生成 manifest、校验、打包以及来源信息，不进入最终工具集运行时。
- Windows 工具来自官方二进制；Linux/macOS 宿主编译器应从同版本 NSIS 源码在匹配的原生 CI runner 上构建，不应假设能在 Windows 上可靠地产出所有原生宿主程序。

### 3.2 校验值与来源措辞

NSIS 3.12 官方页面提供并已核对：

- SHA-1：`364fd795b0cafc1fbff3e966f103a8f8fc8fb7f1`
- MD5：`757c22153dd8b90f5e297310d9966997`

本地计算的官方 ZIP SHA-256：

```text
56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f
```

不得把这个 SHA-256 表述为上游官方发布值。每次更换工具集 Release 时必须重新核验下载产物、manifest、许可证和各宿主可执行文件。

### 3.3 许可证

- `third_party/nsis/COPYING` 与 `THIRD-PARTY-NOTICES.md` 保留在本仓库和发布包中。
- 即使 NsisToolset 压缩包内部也包含 `COPYING`，NuGet 包级别仍应保留清晰可见的第三方许可归属；不要因为压缩包内已有一份就盲目删除外层许可文件。

## 4. NSIS 原生插件

需要自行实现 NSIS 插件时，统一使用 [dotnetbundler/NsisPlugin](https://github.com/dotnetbundler/NsisPlugin)。当前插件项目与产物：

```text
tools/Bundler.Nsis.Plugin/
third_party/nsis/plugins/x86-unicode/DotNetBundlerNsis.dll
```

插件使用 `win-x86` Native AOT 构建，因为 NSIS 插件宿主采用 x86 Unicode ABI。当前插件承担：

- SemVer 比较；
- 已安装 MSI 查询和旧 MSI 迁移辅助；
- 安装完成后的降权进程启动；
- 快捷方式创建、读取、所有权判断、更新、迁移与安全删除。

Native AOT 下不能依赖经典 `ComImport`/RCW 自动封送来操作 Shell Link；此前运行时出现 `InvalidOperation_ComInteropRequireComWrapperInstance`，现已改成直接调用 COM vtable。

## 5. Windows + NSIS 当前已完成能力

除单独标明外，下列功能均属于 **已实现并自动验证**。

### 5.1 安装与卸载基础行为

- 用户可在安装时选择安装目录，并记住上次目录。
- 默认安装到独立的应用目录。
- 交互安装选择无本产品标记的非空目录时会警告；自动安装会拒绝覆盖无关的非空目录。
- 桌面和开始菜单快捷方式可由用户选择，不再固定创建。
- 安装/卸载前检测运行中的主程序并尝试关闭。
- 卸载时可选择删除应用数据。
- 未选择删除应用数据时，只移除构建载荷中记录的路径；程序运行后新建的不同路径会保留。如果程序覆盖了构建载荷中的同名文件/路径，该路径仍属于卸载清单，会被删除。
- 选择删除应用数据时，递归删除完整安装目录，并删除 `%APPDATA%\<identifier>` 和 `%LOCALAPPDATA%\<identifier>`。

运行进程检测通过 Windows Restart Manager 注册 `$INSTDIR` 下当前主程序和显式旧主程序的完整路径，并只关闭实际占用这些文件的进程。Windows 集成测试同时运行安装目录内的程序和另一路径中的同名程序，验证重装只关闭前者。

### 5.2 多语言

- 内置固定快照中的 22 种语言：Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian、Vietnamese；对外 `Persian` 在 NSIS 内部映射为 `Farsi`。
- 支持以分号配置语言列表，第一项是回退语言。
- 配置多个语言且启用选择器时显示语言选择页。
- 支持项目提供自定义 NSIS 语言文件；它完整替换该语言的内置文件，严格校验缺失、重复、未知键和 `LANG_*` 常量。
- 当前示例选择 English、自定义 SimpChinese 和内置 Japanese，展示多语言选择器。

22 种语言均已由真实 NSIS 编译测试，另有非拉丁 Unicode 安装/卸载集成测试。译文内容、RTL 和不同缩放下的布局仍需 MT-11 外部审校；结构与可编译不等于母语质量验收。

### 5.3 安装范围

- `currentUser`
- `perMachine`
- `both`（安装时选择）
- 已生成对应的 UAC、注册表视图与 Shell Context 脚本逻辑。

真实 HKLM/Program Files 写入和完整 UAC 交互仍属于 **已实现但需外部验收**，不能由普通本地自动化静默触发。

### 5.4 版本策略与旧 MSI 迁移

- 版本要求 SemVer 2.0；Windows 文件版本的三个数字核心段均限制在 `0-65535`。
- 检测已安装版本并比较语义版本。
- 交互处理同版本重装、升级与降级。
- 静默同版本执行原位修复；静默升级先移除旧构建载荷并保留应用数据。
- 默认禁止降级，可显式允许。
- 支持通过准确的历史 MSI ProductCode/UpgradeCode 迁移旧 WiX/MSI 产品，不按产品名猜测。

一次性 MSI Fixture 已验证通用迁移机制。真实已发布产品的历史 GUID、x86/x64、current-user/per-machine MSI 仍需生产输入验收。

### 5.5 生命周期 Hook

支持项目提供 `.nsh` 宏，介入：

- 安装前；
- 安装后；
- 卸载前；
- 卸载后。

模板扩展入口必须保持稳定，并避免让自定义 Hook 绕过目录、所有权与卸载安全规则。

### 5.6 品牌、许可证与元数据

- 安装器图标；
- 卸载器图标；
- Header 图片；
- 卸载器 Header 图片；
- Sidebar 图片；
- License 页面；
- Publisher、Description、Homepage、Copyright；
- 产品版本和文件版本；
- “应用和功能”卸载元数据。

### 5.7 文件关联与深链接

- 文件扩展名关联使用应用专属 ProgID，并注册“打开方式”和默认应用 Capabilities。
- 不静默抢占用户当前选择的默认应用。
- 自定义 URL 协议可启动应用。
- 卸载只在协议仍指向本次安装目录时删除注册，避免破坏后来接管同一协议的程序。

概念说明：文件关联让 `.hello` 等文件可由应用打开；深链接让 `hello-bundled:welcome` 这样的 URI 直接启动应用并传递目标。

### 5.8 Authenticode 签名

- 独立 `DotNet.Bundler.Signing.Windows` 公共 API。
- 支持 PFX/P12 和 Windows `My` 证书存储区指纹。
- PFX 密码通过环境变量名传递，避免写入项目文件或命令行。
- 默认 SHA-256，支持 RFC 3161 时间戳 URL。
- 完整签名顺序：临时副本中的主 EXE、调用方显式选择的附加 payload、工作区中的 Bundler 原生插件、临时卸载器、最终安装器。
- `BundleTargetConfiguration.SigningFiles` / `BundlerWindowsSigningFile` 只选择输入目录内的 DLL、sidecar 或辅助程序；不会自动重签其他第三方文件。
- 签名在工作区副本完成，不修改调用方输入目录或内容寻址共享插件缓存；任一步失败都会删除最终安装器。
- `WindowsExternalCommandSigner` / `BundlerWindowsSigningCommand` 为云 HSM、USB Token 和远程 provider 提供独立参数入口，支持路径、产物类别、目标 RID 和产品名占位符；普通失败信息不回显 provider 参数或输出。
- 内置实现不依赖 Windows SDK 或 `signtool.exe`，但内置 Authenticode provider 要求 Windows 宿主；外部 provider 的宿主范围由其命令决定。
- 本地自签名流程已经写入中英文 README 和中文示例 README。

一次性自签名证书已验证 payload、Bundler 插件、卸载器和安装器的 PE 签名机制；无秘密 Fixture 已验证外部 provider 参数替换、退出码与脱敏。正式发布还需要生产证书/HSM、私钥保护方式和公共 RFC 3161 服务；自签名状态不受信任是正常现象，不能证明公开信任链或 SmartScreen 声誉。

### 5.9 安装器自动化协议

生成的 NSIS 安装器支持：

| 参数             | 语义                                                            |
| ---------------- | --------------------------------------------------------------- |
| `/S`             | NSIS 原生静默模式                                               |
| `/P`             | 被动安装/卸载，只显示进度并跳过输入页                           |
| `/UPDATE`        | 自动更新；没有 `/S` 时隐含 `/P`，保留应用数据和已有快捷方式状态 |
| `/NS`            | 不创建桌面或开始菜单快捷方式                                    |
| `/R`             | 自动模式成功后，以桌面用户而不是安装器管理员令牌启动应用        |
| `/ARGS=<参数行>` | 与 `/R` 配合，直接向应用传参，不经过 `cmd.exe` 或 PowerShell    |
| `/D=<目录>`      | NSIS 原生安装目录参数，必须是命令最后一个参数                   |
| `/DELETEAPPDATA` | 卸载时删除应用数据和完整安装目录                                |

`/ARGS` 也兼容不带等号的形式，此时其后全部文本成为应用参数；若还需 `/D`，应优先使用 `/ARGS=...` 并保持 `/D` 最后。

稳定退出码：

| 退出码 | 含义                         |
| ------ | ---------------------------- |
| `0`    | 成功                         |
| `1`    | 用户取消                     |
| `2`    | 一般失败                     |
| `3`    | 参数或自动安装目录无效       |
| `4`    | 版本策略阻止                 |
| `5`    | 无法关闭运行中的应用         |
| `3010` | 成功，但需要重新启动 Windows |

`/R` 的实现规则：非提权安装使用当前令牌 `CreateProcessW`；提权安装使用桌面 Shell 令牌、`CreateEnvironmentBlock` 和 `CreateProcessWithTokenW`。不能通过 Shell 拼接命令行来启动应用。

安装与卸载成功回调都会把 NSIS reboot flag 映射为 `3010`；安装事务在成功回调前已经提交并删除 journal，`/R` 在需要重启时不会启动应用。仓库内 Fixture 已通过可控 `SetRebootFlag` 验证这些契约。真实 UAC 场景的 `/R`、由锁定文件产生并跨真实重启完成的 `3010` 场景，以及无法关闭进程的退出码 `5` 仍需要外部/专门环境验证。

### 5.10 快捷方式完整性

公共 `NsisShortcutConfiguration` 和对应 MSBuild 属性支持：

- Desktop / StartMenu 开关；
- Arguments；
- WorkingDirectory；
- Icon；
- AppUserModelId；
- StartMenuFolder；
- LegacyProductNames；
- LegacyMainExecutables。

安全规则：

- 工作目录与图标必须是安装目录内安全相对路径，并且存在于最终载荷。
- 创建快捷方式时写入目标、参数、工作目录、图标和稳定 AppUserModelID。
- `/UPDATE` 只刷新仍存在且仍由本产品拥有的快捷方式，不重建用户手动删除的快捷方式。
- 卸载只删除仍由本产品拥有的快捷方式；同名 `.lnk` 被其他程序接管后会保留。
- 产品或主程序改名只能通过显式旧产品名/旧主程序列表迁移。

Windows 的开始菜单/任务栏固定存储和取消固定 API 随系统版本、策略变化，当前不保证自动取消固定；需要在支持的 Windows 10/11 版本上人工验收。

### 5.11 安装事务与失败恢复

- 安装和升级在修改旧状态前快照完整安装目录、产品相关注册表项、共享注册表值以及当前/旧名称快捷方式。
- journal 按安装范围保存在 `%LOCALAPPDATA%\DotNetBundler\transactions` 或 `%ProgramData%\DotNetBundler\transactions`；只有快照完成后才标记为 active。
- 恢复前先逐项比较 journal 快照目标与安装器编译时生成的固定清单；文件路径以及注册表 root、view、subkey、value name 任一不符都会在恢复载荷前失败。实际恢复再次校验，并只使用安装器清单中的目标；额外 journal 项不会被枚举执行。因此 journal 不能单独授权修改产品清单外的文件或注册表项。
- 清单不同时返回专用退出码 `6`，保留 active journal；用创建该 journal 的原安装器及原 `/D=` 目录运行 `/S /RECOVERONLY`，只恢复旧状态，再运行新配置安装器。更早、不支持该开关的原包可按原范围/目录重试，由其自动恢复并继续自身安装，但不能保证“仅恢复”。缺失原安装器或其自身也无法完成时不会放弃清单校验自动猜测目标，需取得可信原包或人工排障；这是明确的恢复依赖而非静默兼容。
- active 安装 journal 的静态快照树在激活时计算 SHA-256，并在相应 HKCU/HKLM 的独立注册表锚点保存；恢复前核对内容与目录结构，包括载荷和注册表快照。校验失败返回 `2` 且保留 journal，不执行部分恢复。同一用户可同时修改其 HKCU 锚点和 currentUser journal，因此这不是对同用户恶意进程的安全隔离。
- 安装 Hook 可通过 `SetErrors` 或 `Abort` 触发失败；可继续执行失败处理时会立即回滚载荷、注册表和快捷方式。
- 安装器进程被直接终止、无法运行 `.onInstFailed` 时保留 active journal；下一次启动同一产品安装器时先恢复旧状态，再执行版本检测和新事务。
- journal 绑定原安装目录，恢复调用必须提供同一路径；安装目录、快捷方式路径和注册表恢复目标均不能只由可修改的 journal 决定。首次自定义 `/D` 安装若在注册安装目录前中断，重试时需要继续提供同一 `/D`。
- 成功安装以将 active journal 原子重命名为同级 `.committed` 目录作为提交点，再尝试清理已提交快照。提交后清理失败不再回滚已完成的安装；下次启动先重试清理 `.committed`，再处理 active journal。快照阶段若磁盘空间不足或遇到不能安全复制的重解析点，会在修改旧状态前失败。
- Windows 集成测试覆盖 post-install Hook 失败即时回滚、进程被直接终止后的下次启动恢复、可控快捷方式/注册表持久化失败和提交后清理失败。此外还在事务快照、事务激活、载荷恢复、注册表恢复和 active journal 清理五个检查点执行一次性故障注入，验证旧 EXE 哈希、版本注册表、运行时文件、快捷方式、journal 中间状态以及下次启动恢复/清理。
- 升级的“先卸载旧版本”路径显式复制旧卸载器到唯一临时文件并同步等待；既避免安装目录内的 `Uninstall.exe` 因自锁被安排到重启删除，也避免旧卸载器与新事务并发产生竞态。
- 安装载荷提取使用 `SetOverwrite try` 并检查 NSIS error flag；锁定的非主程序载荷不能被覆盖时返回 `2`，不再跳过文件后误报成功。交互模式会提示关闭可能占用安装目录文件的程序后重试；静默/被动模式不弹窗。若锁同时阻止即时回滚，则保留 active journal，释放锁后的下一次安装启动会先恢复旧状态。
- 快捷方式目录/偏好以及卸载信息、文件关联和 URL 协议注册都会在事务提交前检查 NSIS error flag；检测到持久化失败时进入同一事务回滚路径，不提交不完整安装。

当前实现会临时占用接近现有安装目录大小的额外空间。构建输入、资源、安装快照/恢复、journal 和工具缓存统一拒绝 symlink、junction 与其他重解析点；安全清理只删除链接本身，不跟随目标。普通文件快照保留内容、基础属性和时间戳，但不承诺完整保真恢复自定义 ACL、ADS、稀疏文件等任意文件系统元数据。安装事务范围只覆盖 NSIS 安装和升级。旧 MSI 卸载是无法在缺少原 MSI 包时自动逆转的外部迁移边界，因此迁移后的失败只清理新 NSIS 状态，不宣称重新安装旧 MSI。安装载荷明确采用“锁定时安全失败并恢复”策略，不使用通用延迟替换：`MoveFileEx` 的延迟操作需要管理员上下文，无法为 `currentUser` 提供一致能力；共享 pending rename 队列也没有可安全纳入安装回滚事务的撤销机制。详见 `docs/nsis-locked-payload-policy.md`。

权限边界核查（2026-09-23，本机当前 shell 为 Medium integrity）：`%LOCALAPPDATA%\DotNetBundler` 由当前用户拥有，当前用户具有继承的 Full Control，currentUser journal 与 HKCU 锚点均可被同一用户修改；这不构成普通用户到管理员的提权隔离。`%ProgramData%` 由 SYSTEM 拥有，SYSTEM/Administrators 为 Full Control，普通 Users 在根上主要为读取/执行并具有限定的子目录创建权。代码现在要求 perMachine 产品目录、事务目录和 journal 树的所有者与允许 ACE 只属于 Administrators/SYSTEM，拒绝预建的宽权限目录；但本机尚无真实提权安装创建的最终 ACL，MT-01/MT-07 仍须验证创建、继承、重解析点和注册表锚点权限。目标清单约束独立于 ACL；快照内容和恢复卸载器的提权完整性则依赖受保护的锚点及目录边界。

### 5.12 卸载前向恢复

- 卸载使用独立的 `${PRODUCT_ID}.uninstall` journal，不复用安装回滚快照，也不宣称能撤销已经删除或通过 `/REBOOTOK` 排队删除的文件。
- 修改持久状态前保存原安装目录、`/DELETEAPPDATA` 选择和恢复卸载器；active 后的失败或进程中断保留这些状态。恢复卸载器从已安装的 `Uninstall.exe` 复制，复制前后与卸载注册项中的 `BundlerRecoverySha256` 对照；执行前再次校验 journal 副本的哈希，不匹配时不启动。
- 卸载注册项保留到 finalizing 阶段，为下一次安装器提供受保护的 `InstallLocation`。恢复插件只接受与该目录一致的 journal，避免信任可修改 journal 选择递归删除目标。
- 下一次安装启动会以 `_?=` 直接模式同步运行 journal 中的恢复卸载器。恢复阶段幂等重做所有权安全的快捷方式、关联、数据和载荷删除；成功进入 finalizing 后由父安装器原子提交并清理 journal，再继续新安装。
- 若恢复返回 `3010`，新安装会停止，避免系统待删除队列在重启后删除刚写入的新载荷。
- 卸载 Hook 可能在 active 恢复时重复执行，必须幂等。post-uninstall `SetErrors` 和进程树强制终止均有 Windows 集成回归。
- NSIS 从安装目录直接启动 `Uninstall.exe` 时使用外层自复制 launcher，该 launcher 不可靠地传播实际卸载进程退出码。内部升级、恢复和重启验收脚本显式复制卸载器并用 `_?=` 同步等待实际进程；不得把外层 `0` 当作完成证据。

### 5.13 NSIS 能力审计与压缩

- 已按固定 Tauri commit `5d995ed35b029cecd780fdbe614dc6023a89b81b` 完成 `NsisConfig`、`WindowsConfig` 和与 NSIS 有关的通用 `BundleConfig` 逐项审计，矩阵位于 `docs/nsis-capability-matrix.md`。
- `NsisBundleConfiguration.Compression` 和 `BundlerNsisCompression` 支持 `lzma`、`zlib`、`bzip2`、`none`，默认 LZMA；四种模式均已实际调用内嵌 `makensis` 编译验证。
- JSON `BundleConfigurationLoader` 现会保留文件关联和 URL 协议及其元数据，不再让未来 CLI 的共享配置入口静默丢失这些能力。
- WebView2、VC Runtime 和 Tauri updater 产物已分类为框架/runtime 专属或尚无通用契约，不进入当前 NSIS 实现；签名已在 `NSIS-R2` 收口，完整语言已在 `NSIS-R3` 收口。

### 5.14 完整签名流水线

- `IBundleSigner` 请求现在携带产物类别、产品名和目标 RID；类别区分主程序、显式 payload、Bundler 原生组件、卸载器和安装器。
- 目标级 `SigningFiles` 是格式无关公共模型的一部分，JSON loader、直接 API 和 MSBuild 均保留同一语义；未配置 signer 时显式签名文件会失败而非被静默忽略。
- NSIS 启用签名后复制 payload 与插件到私有工作区，保证源输入和共享缓存字节不变。真实 `PublishDir` 尾部分隔符路径已有回归覆盖。
- 外部命令签名器使用可执行文件加独立参数数组，不经 shell；至少一个参数必须含 `{path}` 或 `%1`，并支持 `{artifactKind}`、`{target}`、`{productName}`。provider 输出和参数默认不进入异常消息。
- 最终 installer 签名失败会删除已编译的输出，避免调用方把未签名文件误当成功产物。

## 6. HelloNsisApp 示例的定位

`samples/HelloNsisApp` **不是自动化测试**，而是所有已公开功能的可操作演示。示例说明只使用中文。

当前示例必须持续展示：

- English、自定义简体中文和内置 Japanese 的语言选择；
- 许可证、品牌图片和版本元数据；
- 自定义安装目录、非空目录保护与目录记忆；
- 可选桌面/开始菜单快捷方式及参数、工作目录、图标、AUMID、目录和改名迁移；
- 运行程序检测与关闭；
- 安装/卸载四个 Hook；
- 同版本、升级、降级策略；
- 旧 MSI 标识迁移配置入口；
- 文件关联和深链接；
- 自动安装、更新、启动参数和退出码；
- 可选删除应用数据；
- Authenticode 配置与本地自签验收方法；
- `currentUser`、`perMachine`、`both` 三种构建方式。
- 可配置的 NSIS 压缩算法；示例使用 `zlib`，默认仍为 `lzma`。

安装载荷中默认必须包含：

- `demo.hello`，用于演示 `.hello` 文件关联；
- `运行深链接.cmd`，用于运行 `hello-bundled:welcome`；
- `演示资源\说明.txt`，用于展示额外资源和快捷方式工作目录。

示例程序把最近一次启动信息写入 `%LOCALAPPDATA%\com.example.hellobundledapp\last-launch.txt`，供参数和数据删除行为验收。

## 7. 已完成阶段与提交历史

当前 MSI 实施线：`msi-development`。NSIS 开发线：`nsis-development`。原 `codex/modular-bundler-backends` 已按用户要求拆分；旧 `codex/nsis` 分支已删除，`master` 已快进至 NSIS 开发线的 `6946dae`。这些分支指针仍须实时核查。

| 提交      | 内容                         |
| --------- | ---------------------------- |
| `7928d5b` | NSIS 品牌资源和包元数据      |
| `3d2a602` | NSIS 生命周期 Hook           |
| `11f36c8` | 将 NSIS 重构为可复用后端包   |
| `a543acb` | 完成模块化 Bundler 包结构    |
| `8c82f13` | 嵌入多宿主 NsisToolset       |
| `688966e` | 语义版本安装策略             |
| `a362886` | 旧 MSI 迁移                  |
| `2de3752` | 文件关联与深链接             |
| `db3da24` | 在示例中展示 NSIS 功能       |
| `d32a816` | Windows Authenticode 签名    |
| `76ecaa9` | 安装器自动化协议             |
| `af01dc0` | 快捷方式生命周期和所有权安全 |
| `471e5d0` | 增加项目交接基线文档         |
| `b25a514` | 安装事务和进程中断恢复       |
| `5c0d2cf` | 传播重启退出码并增加重启验收 |
| `7ace218` | 锁定载荷安全失败和恢复       |
| `f0fafb9` | 安装持久化错误检测         |
| `3283d51` | 原子提交和提交后清理       |
| `6e6da77` | 锁定载荷策略和交互提示     |
| `708285a` | 集中人工与外部环境验收手册 |
| `1225eec` | 覆盖事务恢复检查点         |
| `b6a7221` | 卸载失败与中断后的前向恢复 |
| `aa2e3e9` | 固化通用打包器产品边界与路线 |
| `c50201d` | NSIS 能力审计、压缩配置与共享配置修复 |
| `f124ee7` | 完整 Windows 签名流水线 |
| `783b835` | 完整内置多语言与本地化验证 |
| `71a5c90` | 冻结 NSIS 安全打包基线 |

`3723cd7` 是安装范围支持的历史提交，位于这条提交链的更早位置。

## 8. 最近一次验证证据

`0.1.0-alpha.33` 在 2026-09-23 的实际验证：41 项 Release 单元/契约测试通过；Pack 成功，六个同版本 NuGet 包存在；完整 Windows NSIS 安装/卸载集成测试通过。集成新增用配置 A 的 1.2 安装器生成 active journal，配置不同的 B 1.3 安装器返回 `6` 并保留现场，原安装器 `/S /RECOVERONLY` 恢复旧版本与 EXE 哈希且不继续升级。载荷文件和注册表快照内容分别被篡改时返回 `2` 并保留 journal；恢复卸载器副本被篡改时同样拒绝执行。测试还覆盖原有目标路径和注册表目标篡改、失败注入、签名、语言等回归。首次新测试运行因断言把“中断时版本”误认为“恢复后版本”而失败；修正断言后完整集成通过。实际运行命令：

```powershell
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.33
```

以下保留 `0.1.0-alpha.32` 的历史验证记录，不应扩写成未执行过的平台兼容承诺：

```powershell
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release -PackageVersion 0.1.0-alpha.32
dotnet publish samples/HelloNsisApp/HelloNsisApp.csproj -c Release -r win-x64 --force
```

结果：

- 41 项单元/契约测试全部通过，覆盖原有 22 种语言与完整 NSIS 契约回归，并新增验证同一快照名、文件路径和注册表目标贯穿备份、校验与恢复，且模板不再暴露 journal 定向的通用回滚入口；
- 生成六个 `0.1.0-alpha.32` 本地 NuGet 包；
- Windows NSIS 安装/卸载集成测试通过；
- 自签名集成实际验证最终安装器、安装后主 EXE 和卸载器均包含预期证书；构建日志确认 Bundler 原生插件也经过内置 signer；
- Windows 集成在 zh-CN UI 环境运行仅含 Japanese 的安装器，成功保留 Unicode 产品名、安装路径、开始菜单目录、快捷方式参数、卸载显示名和描述，并完成清理；
- `Persian` 对外名称已在内部映射到 NSIS 3.12 的 `Farsi`/`LANG_FARSI`，由真实编译回归保护；
- `HelloBundledApp` 使用 English、SimpChinese 自定义文件和内置 Japanese 展示语言选择器，并从当前本地包发布；
- 烟雾测试从 `.lnk` 读取并验证参数、工作目录、图标和 AppUserModelID；
- 集成覆盖用户手动删除快捷方式后更新不重建、旧名称迁移、外部程序接管同名快捷方式后卸载保留；
- Native AOT COM 修复后完整流程通过；
- Restart Manager 精确关闭安装目录内主程序，另一路径同名进程保持运行；
- post-install Hook 失败后立即恢复旧 EXE、版本注册表、运行时数据和快捷方式；
- 直接终止安装器进程后保留 active journal，下一次启动自动恢复并清理；
- Windows 集成分别篡改 active journal 的快捷方式 `path.txt` 和注册表 snapshot subkey；两次恢复均实际返回 `2`、保留 journal，并保持清单外文件和 HKCU sentinel 不变，恢复原始 metadata 后正常恢复；
- 可控 reboot flag 场景返回 `3010`，载荷和注册表保持已提交状态、journal 已清理且 `/R` 未启动应用；
- 升级时显式临时复制并同步等待旧卸载器的回归路径通过；
- 锁定非主程序载荷时稳定返回 `2`，保留 active journal；释放锁后的下一次启动恢复原文件、版本注册表并清理 journal；
- 契约测试验证交互安装在载荷写入失败时引用中英文 `PayloadWriteFailed` 提示，再进入同一事务回滚；静默锁定载荷集成回归仍返回 `2`；
- Native AOT 插件已重建，SHA-256 为 `ED3A50B0466CEDF319AA51384E7B7BDB14ED0D5938487E28561D2DD494E423B3`；
- 可控快捷方式和注册表持久化失败均返回 `2`，并恢复 1.1.0 版本、原 EXE 哈希和快捷方式状态；
- 可控提交清理失败仍返回 `0` 并保留 `.committed`，下次安装器启动先清理它，不回滚已安装的 1.2.0；
- 事务快照和激活故障均在修改持久状态前返回 `2`，保持 1.1.0 载荷、注册表、运行时数据和快捷方式不变，且不留下 active journal；
- 载荷恢复、注册表恢复和 active journal 清理故障均在预期检查点返回 `2` 并保留 active journal；下次启动 1.1.0 安装器后恢复旧载荷、版本注册表和运行时数据，并清理 journal；
- post-uninstall Hook 故障返回实际卸载进程退出码 `2`，保留 active 前向 journal、恢复卸载器与注册表锚点；下一次安装先完成卸载并清理 journal；
- post-uninstall 进程树被终止后保留相同恢复状态；下一次安装幂等完成旧卸载，再写入新载荷；
- Windows 集成新增安装快照、transaction journal、普通卸载载荷树和 `/DELETEAPPDATA` 应用数据树 junction：安装器/卸载器均返回 `2`，保持原状态和外部 sentinel 不变，不遗留 transaction journal 或 forward journal；
- 完整正常安装回归通过。真实 ACL/权限拒绝、磁盘耗尽和真实重启仍未在本机验证。
- 集成脚本的 `finally` 已清理其安装目录、注册表、进程、临时 Hook 标记和测试证书；保留生成的 `artifacts/windows-nsis-integration` 作为构建产物。

文档变更至少执行：

```powershell
git diff --check
```

功能变更应重新执行单元、Pack 和 Windows NSIS 集成脚本。涉及 UAC、Windows 版本差异、真实证书或真实旧 MSI 的结论必须单独列出测试环境和证据。

## 9. 已实现但仍需外部验收的事项

这些 NSIS 事项的外部输入摘要保存在 `docs/nsis-open-items.md`，完整的人工执行顺序、命令、预期结果、证据和清理要求集中在 `docs/nsis-manual-testing.md`。总入口见 `docs/manual-testing-index.md`，MSI 采用独立文档。新增无法在普通本地自动化环境完成的已实现能力时，必须同步对应格式的两个文档；可自动化的测试仍由仓库测试承担。

1. **正式 Authenticode**：使用生产签名身份、私钥保护设施和公开 RFC 3161 服务验证公开信任链、时间戳策略、硬件/云签名行为。
2. **生产旧 MSI 迁移**：使用真实发布过的 ProductCode/UpgradeCode，以及 x86/x64、current-user/per-machine 旧包验证识别和权限行为。
3. **提权安装矩阵**：在明确的提权 CI 或人工环境中验证 `perMachine`、`both`、HKLM、Program Files、卸载与 `/R` 降权启动。
4. **固定项清理**：在支持的 Windows 10/11 构建和组策略下验证开始菜单/任务栏已固定项的升级与卸载行为。
5. **真实重启删除**：在可抛弃、已提权的 Windows 虚拟机运行 `tests/Windows.Nsis.Reboot/Verify.ps1` 的 `Prepare` 和 `Verify` 两阶段，验证锁定文件触发 `3010`、系统 pending rename 项和重启后删除完成；脚本不会修改或清空共享的 `PendingFileRenameOperations`。

## 10. 未完成路线与建议顺序

正式路线见 `docs/roadmap.md`。当前顺序为：

1. `WIN-MSI-1..5`：按 `docs/msi-roadmap.md` 已完成当前主机本机自动化范围并形成 MSI alpha 冻结基线；Windows 宿主使用 WiX 3.14.1，外部兼容矩阵仍待验收；默认下一阶段为 `WIN-MSI-6`；
2. `WIN-MSI-6..9`：完成已确认的 MSI 通用桌面能力并冻结格式；
3. `MAC-APP`、`MAC-DMG`；macOS 决策若正式纳入 PKG，则在 Linux 前完成 `MAC-PKG`；
4. `LINUX-DEB`、`LINUX-RPM`、`LINUX-APPIMAGE`；
5. 所有上述打包格式完成后再进入 `CLI-C1`，把现有 CLI 原型产品化。

Tauri 能力按“通用打包能力、格式特定能力、Tauri runtime 专属能力”分类。签名是正式路线的一部分；WebView2、VC Runtime 等任意应用运行时依赖的自动发现、下载和安装当前明确不做。真实重启、UAC、生产证书、真实旧 MSI、多宿主/ARM64 和真实 ACL/磁盘耗尽保留在外部验收队列，不反复阻塞快速开发进入下一阶段。

### 仍不得宣称完成

- 已完成跨环境/生产验收并可广泛发行的 MSI/WiX 后端；当前 WIN-MSI-1..5 的本机范围已验证，x86 已在 x64 宿主完成真实生命周期，但原生 x86/ARM64 宿主、per-machine UAC、外部宿主、生产签名、真实 UI 和重启仍待验收；
- macOS `.app`/DMG；
- Linux DEB/AppImage；
- 正式发布并受支持的 CLI（仓库当前只有功能有限的原型）；
- 22 种内置语言的母语/专业内容审校、RTL 和完整 UI 缩放矩阵（MT-11）；
- 所有 Windows 版本的自动取消固定；
- 所有宿主和架构的真实 CI；
- 正式证书和生产旧 MSI 的端到端验收；
- 包含锁定文件安装替换、经真实重启确认的待删除完成、完整 ACL/ADS 保真、链接迁移和可回滚卸载在内的全场景恢复；重解析点当前是已验证的安全拒绝策略，不是链接迁移能力。

## 11. 文档、测试与提交约定

跨格式的文档、测试分层、版本、示例、NuGet 包消费、报告、Git 与安全清理规则集中在 `docs/development-rules.md`，此处不再维护第二份可能漂移的清单。当前测试入口：快速单元/契约 `tests/Bundler.Tests`；独立 API 包消费 `tests/Nsis.Api.PackageFixture` 和 `tests/Msi.Api.PackageFixture`；真实 Windows 测试分别在 `tests/Windows.Nsis.Integration`、`tests/Windows.Msi.Integration`。MSI 本机安装需 `-ConfirmLocalInstall`；外部环境边界仍按各格式人工文档记录。

## 12. 已知历史问题与已采取方向

- 早期卸载注册命令曾出现把 `$"...\Uninstall.exe$"` 当作文件名的错误引用；后续模板必须始终验证注册表中的卸载命令能被 Windows 实际解析。
- 早期每个项目中间目录解压工具的思路已放弃，改为 NuGet 内嵌压缩包 + 用户级内容寻址共享缓存。示例为了复用 NSIS 自带图片，会单独把演示图片解压到自身 `obj`，这不是工具链重复解压策略。
- 早期安装器显示英文或语言选择器行为不符合预期，现通过显式语言列表、选择器开关和完整中文 `LangString` 文件处理；完整多语言仍在路线中。
- “程序目录里运行时创建的文件是否被卸载”取决于它是否属于构建载荷记录：新路径默认保留，同名覆盖载荷会删除；选择删除应用数据则整个安装目录都删除。
- “未知发布者”提示并不是每次都出现；它受文件是否带网络来源标记、SmartScreen、系统策略、签名和启动入口影响，不能用是否弹窗单独判断签名是否存在。应使用 `Get-AuthenticodeSignature` 验证。

## 13. 任务/对话衔接信息

- 本文档创建时的当前任务是原长对话的继任整理入口。
- 曾用于记录 NSIS 路线的任务唯一标识为 `01a09dd0-8e9b-7e31-bd0e-97b39d3b3e64`，其当时标题为 `NSIS：后续功能实施路线`。标题可被用户修改，唯一标识才是稳定定位依据。
- 快捷方式阶段的继任任务唯一标识为 `01a0ba2e-9d6c-7563-94c9-4996cba4d0e9`，其工作已完成并提交为 `af01dc0`。
- 用户后续若没有明确要求记录到其他地方，NSIS 后续信息应记录在当前工作任务以及本文档，不再把已归档旧任务当作默认记录位置。

## 14. AI 接管协议

新的 AI 收到“开始接管 Bundler 项目”后，必须先完整阅读：

1. `PROJECT_CONTEXT.md`；
2. `AGENTS.md` 和 `docs/development-rules.md`；
3. `docs/roadmap.md`、`README.md`；
4. `docs/manual-testing-index.md` 和当前格式的路线、能力矩阵、open-items、人工测试文档；NSIS 历史内容仍在 `docs/nsis-upstream-reference.md`、`docs/nsis-capability-matrix.md`、`docs/nsis-open-items.md` 和 `docs/nsis-manual-testing.md`；
5. 与准备处理的阶段直接相关的代码和测试。

本文档是项目交接基线，但代码和自动化测试才是最终事实。如果文档、代码、测试或 Git 状态不一致，接管者必须先调查并向用户说明差异，不能自行假设，也不能要求用户重新复述本文档已经包含的信息。

### 14.1 开始前检查

```powershell
git branch --show-current
git rev-parse --short HEAD
git status --short
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
```

预期基线：

- MSI 分支：`msi-development`，从 `71b028c` 继续；NSIS 开发分支：`nsis-development`，指向 `6946dae`。两者均须实时核查。
- NSIS 冻结起点：`71a5c90`；当前 HEAD 应实时核查，不把本文档的历史提交误认为最新提交
- 包版本：`0.1.0-alpha.35`（交接快照；Git HEAD 与包版本均须实时核查）
- 安装事务、Restart Manager、对应测试、示例和文档已经实现并提交；不得重新制作原型或把这些能力当作未完成项。

上述分支、提交和版本是本文档最后整理时的快照。如果仓库已经向前推进，应调查后续提交和改动，并更新本文档，而不是强行退回该提交。

### 14.2 修改前必须向用户汇报

正式修改前，接管者必须先汇报：

1. 对当前架构的理解；
2. 已完成能力与未完成边界；
3. 下一阶段准备解决的问题；
4. 计划修改的项目、文件范围和测试；
5. 发现的文档、代码、测试或 Git 状态差异。

只有完成这次核对后才能开始正式实现。不要重新实现已经完成的 NSIS、签名、自动化协议或快捷方式原型。

### 14.3 默认下一阶段

以下为 WIN-MSI-1/2 时的历史接管记录；**当前下一阶段以文首及最新的 14.8 节为准**。

用户已明确说“提交，然后开始”；规划文档提交为 `71b028c`，`WIN-MSI-1` 代码已完成本机范围阶段一；用户已要求提交，实际 HEAD 和工作区状态以 Git 为准。NSIS 迁移 fixture 的 `WixToolset.Sdk/5.0.2` 不代表正式选型；本阶段使用 WiX 3.14.1。当前已有直接 API、MSBuild 映射、固定工具子集与源码分发、最小 current user MSI 构建和数据库自动化验证。按用户要求与 NSIS 测试分层一致，2026-09-24 在当前 Windows x64 开发机使用每轮独立产品身份执行真实静默安装与卸载，检查文件、产品注册、未知用户文件保留及清理，均通过；脚本为 `tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall`，日志、MSI SHA-256、ProductCode 和环境见 `docs/msi-roadmap.md` 第 6 节。当前工作区的 49 项自动化测试及完整 Windows NSIS 安装/卸载集成回归也已通过，NSIS 固定测试路径和卸载注册项无残留。干净宿主 Framework、ARM64 宿主/用户端、UAC 与高影响故障仍待独立人工/外部验收，不宣称广泛支持。用户已于 2026-09-24 接受当前约 13.8 MB 的 WiX NuGet 包体积；第一阶段已完成本机范围退出条件：14 个工具子集文件逐项对应官方源码和许可，生成的 WiX NuGet 包包含许可证、源码及声明；49 项测试以 --no-restore 再次通过，涵盖新缓存解包 WiX 和实际编译 MSI，打包复核也通过。干净 Windows/ARM64 环境依用户说明暂不执行，保留待验收且不扩大支持声明。评价 WiX 3.14.1 自身的零环境要求时，只看 candle.exe/light.exe 及其依赖，不混入 Bundler 的 MSBuild 或应用构建环境：两个 EXE 及 wix.dll 均目标 .NET Framework 4.5；Windows 7 SP1 未预装所需 Framework，Windows 10/11 预装版本理论上足够，但干净宿主实际编译仍需 VM 验证，详见 docs/msi-roadmap.md 第 2 节。CLI 在计划的 MSI、macOS 和 Linux 打包格式完成后再做。

快速开发期按 NSIS 的分层测试方式继续 MSI：本机可安全运行的功能在同阶段增加自动化和真实安装/卸载回归，不把代码生成/数据库断言当作系统行为证据；缺环境的测试单列 `docs/msi-manual-testing.md`，不阻塞下一阶段，也不扩大支持声明。WIN-MSI-1 的详细证据见 `docs/msi-roadmap.md` 第 6 节。

WIN-MSI-2 已按用户“开始”在当前 Windows 11 Pro build 26200 x64 主机推进：`MajorUpgrade` 两版本真实安装/升级/卸载，降级和同版本异内容包拒绝，桌面/开始菜单快捷方式、关联和协议候选注册、用户文件保留均经 `tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -ConfirmLocalInstall` 验证；产物哈希与 verbose log 见 `docs/msi-roadmap.md` 第 7 节。per-machine 只已生成独立身份、Program Files/HKLM 的包并检查数据库，未执行 UAC/真实提权安装。协议实际在默认应用 UI 中选择并唤起、快捷方式同路径被接管行为也仍需人工；原生 MSI 不提供 NSIS 式的内容级快捷方式所有权保护。新增数据库测试使当前自动化共 50 项并通过；阶段一 smoke 也以新后端重跑通过。下一阶段为 `WIN-MSI-3`，处理签名、语言、修复/维护与失败/回滚/重启，不做 CLI 或 NSIS 扩展。未经用户明确要求不提交或推送。

阶段二实现时遗漏 NuGet 包版本迭代，用户指出后按 NSIS 的既有规则将仓库统一版本从 `0.1.0-alpha.33` 升为 `0.1.0-alpha.34`。随后用户明确要求示例应用版本不随工具开发迭代，因此 Hello MSI 示例的 `BundlerVersion` 保持 `1.0.0`；升级/降级由独立集成 fixture 的版本参数测试。示例和 NSIS 示例一样在项目文件中指定仓库 `artifacts/packages` 为 `RestoreSources`，并固定引用当前开发包版本。NuGet 的 `project.assets.json` 和缓存决定 `dotnet publish` 实际加载哪个已打包版本，不直接引用正在修改的 `src/`。当前机器先前缓存的 alpha.33 元数据来源是仓库 `artifacts/packages`。alpha.34 已 pack 出七个同版本包；此前用隔离缓存还原示例验证七个依赖均为 alpha.34，曾生成 `Hello MSI App-1.1.0.msi`，那是纠正示例版本前的历史产物，不作为当前示例版本。工具包版本与 MSI 产品版本是两套独立标识；此前安装的示例 `1.0.0` 不会因只升级 NuGet 包版本而自动成为新 MSI 产品。本轮未运行该示例的卸载命令；本轮末查询其 ProductCode 的 ProductState 为 `-1`（未安装），应以实时系统状态为准。

版本补正后再次执行 Release 构建与 pack、50 项 Bundler.Tests、alpha.34 的 Windows NSIS 安装/卸载集成、Windows MSI lifecycle 集成，均通过；后者覆盖真实升级、卸载、降级和同版本异包拒绝。当前机器、MSI SHA-256、临时日志目录等可核对证据见 `docs/msi-roadmap.md` 第 7 节。Hello MSI App 未被这些测试操作。

按用户要求统一公开示例工作流：NSIS 与 MSI 示例都在 `.csproj` 中固定当前开发包版本、设置本地 `RestoreSources`，从仓库根目录执行 `dotnet pack Bundler.slnx -c Release -o artifacts/packages` 后即可直接 `dotnet publish`。MSI 示例曾缺少 `RestoreSources`，alpha.34 不在公网源导致 NU1101；补齐后原样 `dotnet publish .\samples\HelloMsiApp\HelloMsiApp.csproj` 已成功还原。随后发现旧阶段一 `1.0.0` MSI 及其 manifest 占用输出路径，按 MSI 同版本异内容保护不能覆盖；已将这两个历史构建产物及 wixpdb 保存在该示例 `artifacts/previous-msi/`，保留用户已安装的应用不动。原命令再次执行两次均成功，生成当前 `Hello MSI App-1.0.0.msi`；NSIS 示例的普通 Release publish 也通过。以后升级/降级只由独立测试 fixture 操作，不迭代公开示例应用版本。各格式的构建产物冲突仍按格式自身规则处理。

按用户指出的 NSIS 包消费测试基线，已补 `tests/Msi.Api.PackageFixture`：只引用 `DotNet.Bundler.Wix`，在 MSI smoke 脚本中复制到仓库外临时目录，使用本轮打出的本地包与隔离缓存，直接调用 `WixBundler` 生成 MSI。MSI 的 MSBuild fixture 与 lifecycle 脚本也核对 `project.assets.json`、实际包版本和隔离缓存；smoke 脚本检查 WiX 包的程序集及许可/源码/哈希/声明。2026-09-24 当前 Windows 11 Pro build 26200 x64 本机再次运行 MSI smoke 和生命周期集成，两者均通过；独立 API MSI 哈希、真实安装产物 ProductCode、生命周期包哈希及日志见 `docs/msi-roadmap.md` 第 7 节。本次测试差异由跨格式规范 `docs/development-rules.md` 固化，根 `AGENTS.md` 指向该规范。MSI 尚未冻结，下一阶段仍是 WIN-MSI-3。未经明确要求不提交或推送。

本轮最终验证还加入共享的 `tests/AssertLocalRestore.ps1`，同时由 NSIS 与 MSI 集成入口核对本地源、包版本和隔离缓存，并拒绝意外公网源；51 项快速测试通过，其中新增公开示例/API fixture/集成脚本包版本同步断言。MSI smoke 复跑通过，独立 API 包实际编译 MSI 的 SHA-256 为 `91071954ED7CBB7FA90B62EBC775A4176382FA65A53ABFF1B16E3A679F38EE06`，MSBuild 包编译及真实安装/卸载 MSI 为 `DD7BAE8709DCA5B5299A39A7A77792E426DACBDC244F37745220A02D0ADECBDD`，ProductCode `{1BAE73D8-BE20-5B82-8064-149E01BCBDE8}`；证据在 `%TEMP%\Bundler-Msi-Smoke-e286c715a3d04cb1afaf89807337ba2a`。MSI lifecycle 复跑通过，v1/v2/异包 SHA-256 依次为 `953C7CF84C42865C3404D08D63B3482156DD8D5FDD40932EBEE080B75EF928C1`、`A88EDF3C168B10C4B2D705877A69961DF10984A3F02EB19310C92AF0AE19B582`、`0B31B9232989AD69F5B489E664ABF6ACDC2AE9CC049B3B614CB13B2081EE0D27`；证据在 `%TEMP%\Bundler-Msi-Lifecycle-f1203328eac740ffbd099e7201142927`。NSIS 全量集成在共享检查加入后第一次复跑于旧有的“Committed rollback journal was not cleaned up”断言失败，同一脚本第二次复跑通过；未查明第一次失败原因，不能把它当作稳定无故障证据，也不能归因于包源检查。下一次复现时应先保存 active journal 和安装器状态再清理测试现场。

最后重新 `dotnet pack Bundler.slnx -c Release -o artifacts/packages`，在独立 NuGet 缓存、独立输出目录中分别 `dotnet publish` HelloBundledApp 和 HelloMsiApp，并以共享还原断言确认两个公开示例都只使用本地包源和 alpha.34；两种安装器均生成成功，未安装公开示例。验证目录 `%TEMP%\Bundler-Public-Samples-4ab3e0334a87448e8c4496a66a2b34d6`，NSIS/MSI 示例 SHA-256 分别为 `DFA8EAE223FE1E5F2B054687B4EB6227EAEB5503E096E31743BCDD2E16DED0CC`、`2CACDF6816FD41BA57C57279DBC2F433E0711A60990D1ABC145727EE59457715`。最终 `dotnet build Bundler.slnx -c Release --no-restore` 为 0 警告、0 错误。

**包源约定补正（2026-09-24）**：用户发现 MSI 的 MSBuild 集成 fixture 没有 NSIS 的 `<RestoreSources>$(BundlerPackageSource)</RestoreSources>`，此前只有 MSI 脚本的 `dotnet restore --source` 指定包源。核对后没有 MSI 特有理由，现已在 `BundlerMsiSmoke.csproj` 加入与 NSIS 相同的声明，MSI smoke/lifecycle 脚本传 `BundlerPackageSource`，API fixture 同样由项目属性选择本地源，不再依靠脚本隐藏的 `--source`。51 项快速测试中的版本/包源对齐断言已覆盖 NSIS/MSI 两类 fixture；实际 `project.assets.json` 和隔离缓存仍由 `tests/AssertLocalRestore.ps1` 核验。当前 Windows 11 Pro build 26200 x64 上重新运行 MSI smoke 与 lifecycle，均通过真实安装/卸载、升级与拒绝场景。Smoke 的独立 API/MSBuild MSI SHA-256 为 `7228EFEFA6F1B9D67D31C7804BE050813ADA4A0FF23CFBDA982A64B45BA539DE`、`E24D9D739322146ED38F1D77B2D95917AA01357E1E5DD5C4BB8BB4AE2376150C`，日志 `%TEMP%\Bundler-Msi-Smoke-f6e19c853c1445828ff8ca2083cc20ec`；lifecycle v1/v2/异包 SHA-256 为 `C364CC5A416ECB5FEC5F71C9254351FA496FFD623187E105FD599535F4E8A581`、`E106C9A9D3C35B162E4CE5EEB48719D2B9D40039192017C30C2514C56842AE9B`、`2217F66F46F973EBD4A42FDDF3C3CD9AC8A0A7EAD5ADFE80B2C950B1F71ECF21`，日志 `%TEMP%\Bundler-Msi-Lifecycle-d27c202d862c48aa9508a92a8c5bfa66`。普通沙箱下第一次启动测试时 MSBuild 无法读取本机 `AppData\Local\Microsoft SDKs`，这是沙箱访问失败、测试未执行；取得所需本机读取权限后 51 项测试通过。此次只改测试 fixture、脚本与规则文档，NuGet 包内容未变，包版本保持 alpha.34。

用户进一步澄清：希望各后端的**测试风格**统一，包括目录、命名、fixture、包源、入口脚本、断言、日志和清理习惯；不同格式的具体测试内容和数量按其能力决定，不要求逐项对应。根 `AGENTS.md` 提供入口，`docs/development-rules.md` 第 5 节记录规范；确有组织或调用方式上的特殊原因时说明即可。此处仅记录决策，规范细节以该文件为准。

### 14.4 WiX 结构与测试重写（2026-09-24）

本轮在 `codex/msi-development` 的 `44f18e6` 基础上整理 WIN-MSI-2 已有实现，**未启动 WIN-MSI-3，也未改变 MSI 产品语义**。`WixBundleBackend` 保留构建、输出校验与文件收集；`WixProductDocument` 承担 WiX XML 生成，`WixPackagePaths` 集中路径规范化。快速测试仍由 `tests/Bundler.Tests` 同一入口执行，WiX 用例从过长的 `Program.cs` 移到 `WixTests.cs`，MSI 数据库读取器单独存放；把原先混在一个构建用例里的已验证产物复用、同版本载荷变化、重解析点拒绝和编译器缓存恢复拆为独立回归，并新增公开 API 层的非法路径拒绝。测试用例按 MSI 语义设计，不要求 NSIS 有一一对应的场景。

两份 Windows MSI 脚本共用 `MsiTestSupport.ps1`。MSBuild fixture 现在明确复制项目、程序和资源到每轮仓库外目录；lifecycle 的 v1/v2/异包分别拥有独立项目和 `obj`，同时共用本轮隔离 NuGet 缓存。首次隔离运行揭示 fixture 原来隐含依赖仓库级 `ImplicitUsings`，已在 fixture 项目中显式声明；修复后真实 current-user smoke 与 lifecycle 均通过。工具包内容变化，版本升为 `0.1.0-alpha.35`；公开示例应用版本保持 `1.0.0`。56 项快速测试通过；七个 alpha.35 NuGet 包 pack 通过。Windows 11 Pro build 26200 x64 上，smoke 的独立 API/MSBuild MSI SHA-256 分别为 `29FCAB8789ED0927E28E8697D5A7FEAE848DCB99FAB4E9BB801EFA8F30C1CC37`、`2C8FD6AF20184D18ADF75B1270FC26F1C4A94AF6E84D43A699CF036731F6EF9E`，ProductCode `{E56AC800-8B14-5155-8833-9F382F6F3611}`，包与 verbose log 留于 `%TEMP%\Bundler-Msi-Smoke-143cd6323c00486c821be65e32c671e4`；lifecycle 的 v1/v2/异包 SHA-256 分别为 `BFE26B5DF9E2648BD8FFAFAF8CDE6643655DAFF3F12314F1DC2DA31EAC81A9A1`、`6DF340A3115C0D88CE800AE16CC22574FC3CB7E1A1BEB9DF65794E70B08257D8`、`BB4BE96F33AC87FBDC3F17B0E49FC3FEBF4DD76FCC4961C40E85F89AA9348BFC`，日志留于 `%TEMP%\Bundler-Msi-Lifecycle-03d852c11f204657a8649a88569f18de`。两者均退出 0，分别实测安装/卸载及升级、降级/异包拒绝、桌面注册与用户数据保留。

NSIS 全量 Windows 集成在先打出仓库本地 alpha.35 包后退出 0，输出 `PASS Windows NSIS install/uninstall integration`；第一次启动因本地包尚未生成而在测试前报 `Package not found`，不属于安装断言失败。`HelloMsiApp` 与 `HelloBundledApp` 从本地 alpha.35 包源还原并分别生成 MSI/NSIS；其 `project.assets.json` 显示七个 Bundler 包均为 alpha.35，本地源为 `artifacts/packages`。示例 MSI SHA-256 为 `FEA2458256C6796DDF29F1D278E805F7FE2F67F4CF9A794D1D864F7159AEA180`，NSIS 安装器为 `C05306150F03913310E2E0BEC284C2332FBA52C54DF7455C6EB798CC9FC74151`；公开示例未安装。WiX 包大小 13,790,283 字节。per-machine 安装、干净 Windows/ARM64、生产签名等外部验收边界不变；下一阶段仍为 WIN-MSI-3。

### 14.5 执行约束

执行时遵守根目录 `AGENTS.md` 与 `docs/development-rules.md`。本文只保留当前状态和证据；完成阶段后更新状态，即使尚未提交也要保证下一次仅凭仓库文档即可接续。

### 14.6 WIN-MSI-3 本机结项与下一阶段（2026-09-24）

当前分支 `codex/msi-development`，阶段开始时 HEAD `84c1e46` 且工作区干净；本节阶段三变更后来提交为 `89a535c`，当前状态仍以 `git status` 和 `git rev-parse HEAD` 实时核对。阶段三包版本为 `0.1.0-alpha.36`，公开示例的应用版本仍为 `1.0.0`。只修改 MSI 后端、共享 MSBuild 的 MSI 映射、工具供应、版本引用、相应测试与文档；未做 CLI 或继续扩展 NSIS。

实现事实：`WixBundlerOptions.Signer` 复用已有 Windows 签名组件，先签隔离载荷再签最终 MSI；签名失败清理输出，同版本已签产物不静默复用。`WixBundleConfiguration.Language` 提供英文 `1033` 和简体中文 `2052` 单语言产物；中文使用独立升级身份、组件、目录和文件名，英文历史身份未改。应用提供 RTF 许可时使用随包固定的官方 `WixUIExtension.dll` 最小交互 UI；未提供许可时不代应用展示许可条款。原生 `msiexec` 负责 `/qn`、`/passive`、`/fomus`、失败码与回滚；生产 MSI 不加入测试故障动作或自定义运行时代码。WiX 归档新增一个官方文件，哈希/源码/许可审计见 `third_party/wix/msi-wix-provenance.md` 和 `THIRD-PARTY-NOTICES.md`。

本机 Windows 11 Pro build 26200 x64：`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 最终 **60 项全部通过**，包含新增的语言身份/数据库、签名顺序、真实短期自签名证书签 PE/MSI、签名失败及同版本拒绝。`tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall`、`VerifyLifecycle.ps1 -ConfirmLocalInstall` 和新增 `VerifyMaintenance.ps1 -ConfirmLocalInstall` 均通过。维护脚本从隔离本地 alpha.36 包源构建随机 current-user MSI；损坏包返回 1620，测试副本的延迟失败在日志记录 `FileCopy` 后返回 1603 且无托管残留；`/passive` 安装/卸载、`/fomus /qn` 修复、中文 RTF 包与英文包并存及分别卸载通过。三轮 MSI SHA-256、ProductCode、日志目录见 `docs/msi-roadmap.md` 第 8 节。NSIS Windows 集成使用同一版本本地包后退出 0，输出 `PASS Windows NSIS install/uninstall integration`；最初一次在打出 alpha.36 本地包前于测试前报 `Package not found`，随后先 pack 再完整复跑通过。任何测试证书都未进入仓库。

最终 `dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages` 产出七个 alpha.36 包。`DotNet.Bundler.Wix` 包大小 14,397,524 字节、SHA-256 `A43C43F1731F6ABB45163EECE6A25866666C9301C06572AE0523D003A7862B99`；实际包包含与仓库一致的 `THIRD-PARTY-NOTICES.md`、WiX MS-RL、对应源码归档和 SHA256SUMS。公开 `HelloMsiApp` 与 `HelloBundledApp` 都从项目声明的本地 `artifacts/packages` 恢复七个 alpha.36 依赖并发布成功；示例安装器分别留在 `%TEMP%\Bundler-Public-Sample-Msi-alpha36` 与 `%TEMP%\Bundler-Public-Sample-Nsis-alpha36`，SHA-256 分别为 `F13B8951EA105C80B4035E4B059260BC4EA51BA8C2D94EFBFD4B82B01DBAAF46` 和 `AFDF83FAC325C88E537A2AC2A32359559CBD6834C22F85B34A9B6D8145BDBD51`。公开示例未安装；真实安装使用随机 fixture。

未验收：生产证书/时间戳、真实双语言交互 UI/辅助功能、UAC/per-machine、干净 Windows 10/11/ARM64、缺失修复源、锁定文件和实际重启/3010。相关人工步骤与外部待办分别保存在 `docs/msi-manual-testing.md` 和 `docs/msi-open-items.md`，不能写成已通过。该段为阶段三结束时的历史交接；阶段四的实际状态和下一步见下节。未经用户明确要求不提交或推送。

### 14.7 WIN-MSI-4 本机冻结基线（2026-09-25）

用户已说“开始”启动 WIN-MSI-4。起点为 `codex/msi-development` 的 `89a535c`、干净工作区、`0.1.0-alpha.36`。本轮修复 WiX 成功退出时警告被吞的问题：`candle`/`light` 使用 `-wx`，仅对纯 current-user MSI 保留有依据的 `ICE91` 例外。严格编译首轮发现 `CNDL1091`；移除显式 `Package/@Id`，由 WiX 自动生成不同 PackageCode，现有 UpgradeCode/ProductCode 与组件身份不变。新增预处理警告拒绝和独立构建 PackageCode 区分测试；公开 `WixIdentity` 不再返回后端不应预先指定的 PackageCode。所有 NuGet 包版本升至 `0.1.0-alpha.37`，示例应用版本仍为 `1.0.0`。

本机 Windows 11 Pro build 26200 x64：Release 快速测试 62/62，通过；解决方案 `--no-restore` 构建 0 警告/0 错误，Pack 生成七个 alpha.37 包。随包说明定稿后最后一次重打的 WiX 包 14,397,760 字节、SHA-256 `8892C11C9F18958E1A7916049BC524467C7E65EF82DDC9B9C0B151666C7D9025`。MSI 的 smoke、lifecycle、maintenance 三个入口均从隔离本地包源构建，每轮按哈希检查 NuGet 包内的许可证、对应源码、SHA256SUMS、供应说明和第三方声明；真实 current-user 安装、卸载、升级、降级/异包拒绝、修复、被动操作、中文并存及受限故障回滚均通过。独立直接 API 消费者只引用 `DotNet.Bundler.Wix` 包。NSIS Windows 全量集成、两个公开示例的本地 alpha.37 包消费与发布也通过。示例未安装；已知随机 MSI ProductCode 和安装目录的只读残留复核正常退出 0，均不存在。具体命令、各产物哈希、ProductCode、日志目录和首次权限/警告失败记录见 `docs/msi-roadmap.md` 第 9 节。

MSI alpha 格式的本机验证范围与配置/身份规则已冻结；`docs/msi-capability-matrix.md` 逐行标注已自动验证、外部待验收或不支持，并明确只有一台非干净 Windows 11 x64 主机的实际证据。干净 Windows 10/11、ARM64 真实安装、per-machine UAC、生产证书、真实交互 UI、缺源、锁定文件及重启/3010 仍在 MSI 专用人工和外部清单中，不能扩大支持声明。按 `docs/roadmap.md`，下一个默认阶段为 `MAC-APP`；macOS 原生构建/测试环境和 PKG 是否纳入路线须在开始该格式前核对。本阶段完成后用户已明确要求提交；未要求推送。

### 14.8 MSI 通用能力补齐路线确认（2026-09-25，规划阶段）

第 14.7 节的 `MAC-APP` 下一阶段判断是 WIN-MSI-4 提交时的**历史结论**。用户随后澄清：现有环境无法测试的项目不阻塞快速开发；先补齐 Tauri 中适用于通用 Windows MSI 打包器的能力，应用运行时依赖自动部署仍明确不做。用户接受常规模式的受控 WiX fragments/引用，以及显式开启、由调用方承担自备安装逻辑责任的完整模板/原始 merge module 专家模式。新路线为 `WIN-MSI-5..9`，完成后再进入 `MAC-APP`。固定 Tauri 参考、逐项选择和风险见 `docs/msi-tauri-capability-audit.md`；阶段前置/交付/新增测试/退出条件见 `docs/msi-roadmap.md` 第 10 节；当前与计划状态见 `docs/msi-capability-matrix.md`。无环境的人工项继续在 MSI 专用清单/外部待办中准确保留，不冒充已通过。

本轮核对时分支 `codex/msi-development`，HEAD `adce4f0b160f6ed52a9b6152186fbb032521308c`，包版本 `0.1.0-alpha.37`，开始时工作区干净。本轮仅落规划文档，不修改后端代码、测试或 NuGet 版本，不执行新能力测试；现有 WIN-MSI-4 的 62 项及集成结果是历史基线而非 WIN-MSI-5..9 结果。下一次用户明确要求“开始 WIN-MSI-5”时才按已落地路线推进整个阶段：先核 Git/包/身份向量，再做 x86、版本映射和可选降级、对应自动化、独立包消费、本机真实安装生命周期、示例/文档和版本迭代。未经用户明确要求不提交或推送。

### 14.9 公开 MSI 示例补齐（2026-09-25）

用户要求先提交第 14.8 节的规划，再核对 MSI 公开示例是否像 NSIS 示例一样完整。规划文档已提交为 `7484346`；随后对照当时尚未改名的 `samples/HelloBundledApp` 与当前 MSI 后端 API，发现原 `HelloMsiApp` 仅展示快捷方式、关联和协议声明，缺乏可实际打开的演示资源、应用参数反馈、图标、许可页面、签名配置及分范围/语言的操作说明。当前工作区补齐 `samples/HelloMsiApp` 的可操作演示、根 README 链接、本交接及 `VerifyPublicSample.ps1` 自动化，**未修改后端代码、未启动 WIN-MSI-5、未迭代 NuGet 包版本，示例应用仍为 1.0.0**。NSIS 专有 Hook、安装器图片、语言选择器、快捷方式参数和 journal 不适用于现有 MSI；WIN-MSI-5..9 计划能力不提前声称已支持。

Windows 11 x64 本机验证：从已有本地 `0.1.0-alpha.37` NuGet 包构建默认 `en-US/currentUser`、`zh-CN/currentUser` 和 `en-US/perMachine` 三份示例 MSI，均成功；另一次 `dotnet restore` 使用隔离缓存并通过 `Assert-LocalBundlerRestore` 检查本地包源及 `DotNet.Bundler`/MSBuild/Wix 包版本，再 `dotnet publish --no-restore` 成功。Windows Installer 数据库只读检查三份包的产品语言/独立 ProductCode 与 UpgradeCode、演示文件、RTF 许可 UI、产品图标和两个快捷方式，默认包还检查关联/协议的自身候选注册表项。默认、中文和 per-machine 三份包分别位于 `%TEMP%\Bundler-HelloMsiApp-Sample-f638475803d84cf094c55a982f35727e`、`%TEMP%\Bundler-HelloMsiApp-zhCN-cbf1b025d4e1469ab3b47d2f6e5ed88e`、`%TEMP%\Bundler-HelloMsiApp-perMachine-472e58ccbb9346a79404877293301328`，SHA-256 分别为 `EAFDCBCA878DD5771D2B83D9BA29BDC7216B0E29560964B319D3B490D2F89682`、`AA01F2F187E7C18047B85F8101F48CB2D6ED78317423ABB7737A2449AF30180E`、`D896093B9E482F22AE07FDD5429A7DF6B939224821B21189CA8D2176EA682EBD`。隔离还原产物 `%TEMP%\Bundler-HelloMsiApp-Isolated-691cf376f8374be0b01b2b10501db8ec\output` 的 SHA-256 为 `F20B0E8A62D9BCA4CCDAAA5D2CACE5249A14E3520C95D667B273A4D672262380`。三份公开示例**均未实际安装或人工验收 UI**；真实安装、升级、修复、故障行为的既有证据仍来自随机 fixture。

首次为默认英语包加入 RTF UI 后，WiX `light` 报 `LGHT0311`：英语 UI 本地化资源要求数据库 1252，原中文描述与目标路径无法编码。示例将默认安装数据库文本改为英语、`BundlerWixCodepage=0` 交由现有语言配置选择（英语 1252、简体中文 936）；中文可保留在文件内容和运行参数里。重跑上述三种构建后均通过。仓库原 `artifacts/win-x64/msi` 中有早期同版本 MSI，不应覆盖；示例默认 `BundlerOutputPath` 改为 `artifacts/feature-demo`，在本机直接执行 `dotnet publish samples/HelloMsiApp/HelloMsiApp.csproj -c Release` 成功，产物 SHA-256 `FB193DCC3E43861BA5BE6DC768CCD6A8EE9EB815EEFE2A2F6F20A626C9896F34`。新增 `VerifyPublicSample.ps1` 首跑因脚本把缺失的 `ALLUSERS` 属性与空字符串直接比较而失败，修正为空字符串规范化后复跑三变体全部通过；最后一轮日志与产物在 `%TEMP%\Bundler-Msi-PublicSample-e220eaf6eb0b45c3b2e92a273460d205`，三包 SHA-256 分别为 `031FF0C6E280145D0210DE67531CC3C926F5DB0FB83DDB58722F419D8776F750`、`72C49B649B5B5B8E3FF0ED11CC6AF9022A141CAE637F24425C31CE0B8E241CF7`、`98CB5331BEBADAC64F9FDE3B2D7D68F28A1A7015E71395307984922F399BC382`；脚本只读检查数据库，不安装 MSI。若未来要支持英语 UI 搭配中文数据库字段，需要单独审计 WiX 本地化与代码页规则，不能仅在示例中继续写 936。用户现已明确要求提交本轮示例改动；未要求推送，提交哈希以 Git 为准。

### 14.10 中文文档与格式命名整理（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `0e37880`、工作区干净；**仅做文档组织、中文化和随包文档配置调整**，未开始 WIN-MSI-5，未改变 NSIS/MSI 安装语义。根 `README.md` 改为中文跨格式入口，旧英文内容移出，原中文镜像不再重复维护。NSIS 历史人工清单改名 `docs/nsis-manual-testing.md`，原 `MT-01..MT-11` 编号不变；示例、MSI 集成说明、NSIS 插件与三类第三方来源说明均改为带格式名称的文件名。NSIS 上游审计、外部待办、插件说明及第三方声明译为中文；原始 `COPYING`/`LICENSE`/`LICENSE.TXT` 许可文本未改。跨格式命名与语言规则写入 `docs/development-rules.md`；活动链接和包内文件列表随之调整。

因根 README、WiX 来源说明及第三方声明进入 NuGet 包，工具包版本从 `0.1.0-alpha.37` 迭代至 `0.1.0-alpha.38`，两个示例应用版本仍是 `1.0.0`。`Directory.Build.props`、示例引用、API fixture 默认值及 Windows 集成入口默认值已同步。Windows 11 x64 本机：`dotnet build Bundler.slnx -c Release -v:q` 通过，0 警告/0 错误；`dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release --no-restore` 全部通过；`dotnet pack Bundler.slnx -c Release --no-restore -o artifacts/packages -v:q` 生成七个 alpha.38 包。逐包 ZIP 检查确认中文 `README.md` 与仓库一致、原中文镜像不再随包；WiX/MSBuild 包内 `licenses/wix/msi-wix-provenance.md` 与仓库逐字节一致，现有 MSI 包审计辅助函数再次通过。WiX 包为 14,398,423 字节，SHA-256 `213BB494106CC7B82F77B96539AF6A435D6E61575E38A836A08B7E66E75BB398`。

公开 MSI 示例的 `VerifyPublicSample.ps1` 使用本地 alpha.38 包和隔离缓存，英语当前用户、简体中文当前用户和英语整机三种只读数据库检查均通过；本轮未安装 MSI。NSIS 公开示例从本地 alpha.38 包源 `dotnet publish` 成功，产生的会话专用输出已按确切路径清理。仓库 Markdown 相对链接全部解析成功，全部维护中的 Markdown 均含中文，旧文档路径搜索无命中，`git diff --check` 无空白错误。首次沙箱内 build 因无法读取本机 `C:\Users\Lin\AppData\Local\Microsoft SDKs` 被拒；获准在沙箱外读取后成功，未发现项目编译错误。本轮未提交或推送。下一实施阶段仍为 WIN-MSI-5；外部环境的既有人工验收边界不变。

### 14.11 测试与示例项目命名、本地包配置收敛（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `414864c`、工作区干净。原 NSIS 示例只演示 NSIS，因此项目目录和 `.csproj` 改为 `samples/HelloNsisApp/HelloNsisApp.csproj`；NSIS 集成 fixture 的项目文件改为 `BundlerNsisIntegrationFixture.csproj`。跨格式的 `tests/Bundler.Tests` 保留泛用名，既有 MSI 项目名已带格式名。为只整理项目名、不改变安装身份，NSIS 示例仍使用 `AssemblyName=HelloBundledApp`、原产品名与标识符、`1.0.0` 应用版本；NSIS fixture 仍使用 `AssemblyName=BundlerIntegrationFixture`。示例专属 MSBuild 参数和图片准备目标从 `HelloBundledApp*` 改为 `HelloNsisApp*`，对应中文命令已同步；这些是示例构建入口名称，不改变安装身份。旧示例目录在本轮开始前已有被忽略的构建产物，整体移动因文件占用失败；仅移动九个受 Git 跟踪的源码文件，旧产物原位保留。

根 `Directory.Build.props` 是 `BundlerPackageVersion` 的唯一当前值，版本迭代到 `0.1.0-alpha.39`。新增跨格式 `Bundler.LocalPackages.props`，集中设置示例的仓库本地包源及 fixture 的 `RestoreSources=$(BundlerPackageSource)`。两个示例的 `PackageReference` 均引用 `$(BundlerPackageVersion)`；五个 Windows 集成入口省略 `-PackageVersion` 时由共用 PowerShell helper 读取根 props。四个包消费 fixture 显式导入共享 props；复制到仓库外的 MSI MSBuild/API fixture 同时复制该文件，并由脚本传入包版本、包源和隔离缓存。fixture 仍自行声明必需的 SDK 属性，不依赖仓库根 props。配置只服务开发包消费，不随 NuGet 包发布；后端和安装逻辑未改。具体规则见 `docs/development-rules.md`。

本机 Windows 11 Pro build 26200 x64：Release 解决方案构建 0 警告/0 错误，更新后的快速测试全部通过；Pack 生成七个 alpha.39 包，包内 README 与仓库一致，Bundler 依赖版本均为 alpha.39。WiX 包 14,398,397 字节，SHA-256 为 `3A13CA19A63D8336538E56453ACB01D5BE76CD6D4F0DE383AF572DED5DFD9657`。NSIS 公开示例从本地包成功发布，仍生成 `HelloBundledApp.dll` 与 `Hello Bundled App-1.0.0-setup.exe`，安装器 SHA-256 为 `D80BAA724FB4D64C96A107F3D1D3A7312B8CCAC0CBF81277D7DF27ACD2DC5468`；本轮专用示例输出已按确切路径清理。MSI 公开示例省略版本参数运行 `VerifyPublicSample.ps1`，英语当前用户、中文当前用户、英语整机三种只读数据库检查通过，未安装公开示例，产物保留在 `%TEMP%\Bundler-Msi-PublicSample-f2b16791c841400a82adec761ce5fbb4`。

真实 Windows 集成入口也均省略版本参数并退出 0：MSI `Verify.ps1 -ConfirmLocalInstall` 通过随机 current-user 安装/卸载和仓库外直接 API 包消费，MSI SHA-256 为 `ECBAFF26859A5458784A5D8035B4C9912E0A766D2667DA1A3E568A7D2277094D`，日志在 `%TEMP%\Bundler-Msi-Smoke-394db4e914b14e04b1c18b929ad7d0f1`；`VerifyLifecycle.ps1 -ConfirmLocalInstall` 通过升级、降级/异包拒绝与数据所有权，日志在 `%TEMP%\Bundler-Msi-Lifecycle-552806128459422086e891156501983b`；`VerifyMaintenance.ps1 -ConfirmLocalInstall` 通过损坏包 1620、故障回滚、被动安装/卸载、静默修复与语言并存，日志在 `%TEMP%\Bundler-Msi-Maintenance-34b34c995872424093c8ca0257bfbd8e`。NSIS `Verify.ps1` 全量安装/卸载集成通过，产物在 `artifacts/windows-nsis-integration`。Markdown 相对链接、旧项目路径及 `git diff --check` 均核对通过。未启动 WIN-MSI-5，未提交或推送；原有外部人工验收边界不变。

### 14.12 跨格式规则重整与 NSIS 路线归档（2026-09-25）

本轮起点为 `codex/msi-development`、HEAD `30cae4d`、工作区干净，工具包版本仍是 `0.1.0-alpha.39`。用户要求将先规划完整后端路线、适用的 Tauri 通用能力审计、独立后端包直接消费与离线工具随包供应、完整可操作示例，以及此前有效但分散的规则写成稳定规范。本轮只重写协作/路线文档，未改代码、包内容或示例，也未启动 `WIN-MSI-5`；因此不迭代 NuGet 版本。

`docs/development-rules.md` 是跨格式规则的唯一规范入口，`AGENTS.md` 保留接管摘要；总 `docs/roadmap.md` 只保留产品边界、格式顺序和阶段入口。原总路线中的 NSIS 能力基线、旧阶段映射与 `NSIS-R1..R4` 记录原样迁至 `docs/nsis-roadmap.md`，人工测试索引增加格式路线链接；NSIS 历史证据不因此变成当前新验证。新后端必须先完成直到格式冻结的路线并与用户确认关键选择，才开始第一阶段代码。当前下一实施阶段仍为 `WIN-MSI-5`，已确认的 MSI 方案以 `docs/msi-roadmap.md` 第 10 节为准；外部验收仍按各格式专用清单。

本轮以 Git 旧版总路线为基准核对 NSIS 历史迁移内容，仅更改标题编号和一处已失效的“本文开头”引用，正文保留；仓库 Markdown 相对链接全部可解析，`git diff --check` 无空白错误。由于只修改未随 NuGet 包分发的协作和路线文档，没有新增或修改打包功能，本轮不重新执行安装集成测试，也不迭代包版本。未提交或推送。

另对照可读取的旧“Nsis 开发”与早期架构任务记录，补回两条容易遗漏的要求：公开示例覆盖当前格式所有适用的用户能力（互斥或需外部条件的场景给可复现说明），以及新增人类语言代码/脚本注释使用中文；MSBuild Task 在进程内调用 Core/后端，不另起 .NET CLI 驱动。旧时要求维护中英文 README 已被后来的中文单文档决定替代，故未重新引入。

### 14.13 提交消息统一为中文（2026-09-25）

用户确认改写**所有项目分支**的提交消息：主题统一为 `type(scope): 中文描述`，单后端改动使用 `nsis`、`msi` 等格式 scope。执行前工作区干净；本地有 `master`、`codex/nsis-development`、`codex/msi-development` 三个项目分支，无远端和标签。当前 MSI 分支包含 50 个提交，两个 NSIS/master 分支共用前 39 个。已将这些提交的消息逐项改为中文，保留每个提交的文件树、作者/提交者身份与时间；改写后 `master` 和 `codex/nsis-development` 指向 `6946dae`，MSI 分支在附加本次文档校正提交前指向 `23c4b8a`。当前文档中引用的 58 处旧提交 SHA 已映射到对应新 SHA，具体映射保存在忽略目录 `artifacts/history-message-map.json`。

### 历史恢复记录（不代表当前状态）

完整历史恢复包为 `artifacts/history-before-message-rewrite.bundle`，已通过 `git bundle verify`，SHA-256 为 `19A38501608257FA652881DCD77C29C75C18A3DB41E693D46B999197EC6FAD0F`。Codex 管理的快照/检查点、旧 `refs/original` 和停在旧提交的独立工作树不是项目分支，予以保留作为历史恢复点；因此 `git log --all` 仍可能显示其旧消息，正常三个项目分支的历史已统一。若要清理或改写这些应用管理的引用，应单独评估其用途。当前包版本和阶段以文首及最新阶段记录为准。

### 14.14 开发分支移除 `codex/` 前缀（2026-09-25）

用户要求所有项目分支名不带 `codex/` 前缀。核对时只有三个本地项目分支，且没有远端或标签：`master` 保持原名，`codex/nsis-development` 改为 `nsis-development`（`6946dae`），当前 `codex/msi-development` 改为 `msi-development`（`e85ebd0`）。另一 Codex 工作树处于分离 HEAD，没有分支要改。两次重命名均未修改提交历史、文件树或包内容；历史实施记录中的旧分支名保留为当时事实，当前分支名以文首和 `git branch -vv` 为准。本轮未提交或推送，下一实施阶段仍为 `WIN-MSI-5`。

### 14.15 WIN-MSI-5：x86、版本映射与可选降级（2026-09-25）

本轮起点为分支 `msi-development`、HEAD `3cca7a0`、包版本 `0.1.0-alpha.39`；用户要求“提交然后开始下一步”。先提交分支改名文档为 `3cca7a0 docs: 同步开发分支改名记录`，随后按 MSI 路线启动 WIN-MSI-5。实现和包内容变更将版本迭代到 `0.1.0-alpha.40`；公开示例应用版本保持 `1.0.0`。

实现事实：公共 `BundleTarget`/`CpuArchitecture` 增加 `win-x86`/`X86`，Core 只允许 x86 与 MSI 组合，NSIS 不因共享模型扩展而接受 x86。WiX 使用 `-arch x86`；x86 per-machine 使用 `ProgramFilesFolder`、current-user 使用既有用户目录，安装目录和组件/产品身份包含 x86 RID；x64/ARM64 既有 identity vector 未改。`WixBundleConfiguration.MsiVersion` 与 MSBuild `BundlerWixMsiVersion` 支持显式三段 MSI 版本，第四字段、预发布自动映射和越界值拒绝；未指定时保留原稳定三段版本映射。`AllowDowngrades`/`BundlerWixAllowDowngrades` 默认 false，显式 true 才允许降级；该场景只定向豁免 WiX ICE61，其他编译/链接警告仍失败。独立 API、MSBuild、示例说明和包消费 fixture 已同步。

新增自动化覆盖公共 RID/格式矩阵、x86 英文/中文 identity vector、显式版本边界、x86 Intel 模板与 32 位组件/目录/注册表视图、MSBuild 属性映射、版本/降级策略。获准读取本机 SDK 路径后，`tests/Bundler.Tests` 快速测试全量通过；普通沙箱再次运行时因访问 `C:\Users\Lin\AppData\Local\Microsoft SDKs` 被拒，属于环境权限限制，不是测试断言失败。

真实 Windows 证据：`tests/Windows.Msi.Integration/VerifyWinMsi5.ps1 -Configuration Release -ConfirmLocalInstall` 在 Windows 11 Pro build 26200 x64 上从本地 `alpha.40` 包源验证仓库外 API 与 MSBuild x86 消费；随机 current-user 产品完成 v1 安装、预发布应用显式映射到 v2 升级、同 MSI 版本异内容拒绝（1638）、默认降级拒绝（1603）、显式允许降级（0）、卸载（0），并检查 32 位注册表视图、受管文件和未知用户文件保留。日志和五个 MSI 保留在 `%TEMP%\Bundler-Msi-WinMsi5-c9fb31e8471a4996878963d70a4b8e7b`。原 x64 `VerifyLifecycle.ps1 -ConfirmLocalInstall` 用 alpha.40 回归通过，日志在 `%TEMP%\Bundler-Msi-Lifecycle-ba0954338b454f1ab5312db811500ea0`。本轮尝试先把 alpha.40 包写入 `artifacts/packages` 再运行 NSIS 集成，但当前受限执行环境拒绝读取 `C:\Users\Lin\AppData\Local\Microsoft SDKs`，因此 pack 在 MSB4184 处失败，NSIS alpha.40 回归未执行；这不是代码回归证据，待有 SDK 访问权限时按 `tests/Windows.Nsis.Integration/Verify.ps1 -Configuration Release` 补跑。干净 Windows、原生 x86/ARM64 用户端、per-machine UAC、生产证书和真实重启仍是人工/专用环境边界，不能由本机结果扩大支持声明。

本轮工作区尚未提交或推送；下一阶段为 `WIN-MSI-6`。提交前应重新核对 Git、完整 diff、包内容和测试入口。

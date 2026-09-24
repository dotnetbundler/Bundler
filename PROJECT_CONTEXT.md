# DotNet.Bundler 项目上下文与后续实施基线

> 最后整理：2026-09-24
> 当前分支：`codex/msi-development`（NSIS 开发线为 `codex/nsis-development`）
> NSIS 冻结起点：`b116d09 feat(nsis): freeze secure packaging baseline`；当前提交以 `git rev-parse --short HEAD` 为准
> 当前包版本：`0.1.0-alpha.34`（Git 状态与包版本均须实时核查）
> 当前阶段：`WIN-MSI-1` 与 `WIN-MSI-2` 的本机范围退出条件已通过；WIN-MSI-2 已实测 current-user 两版本升级/降级及桌面集成，per-machine 只检查产物。下一阶段 `WIN-MSI-3`；外部 UAC/Windows/ARM64 矩阵未验收。

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
- MSI/WiX 已有 WIN-MSI-1/2 实现和本机验证，但格式尚未冻结；macOS 与 Linux 安装格式尚未实现。
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

`DotNet.Bundler.Nsis` 和 `DotNet.Bundler.Wix` 均为可独立引用的后端包，不是 MSBuild 内部实现；调用者可以分别直接调用 `NsisBundler` 和 `WixBundler`。MSBuild/未来 CLI 是应用层。独立包消费测试见 `docs/development-rules.md` 第 4 节。

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

## 6. HelloBundledApp 示例的定位

`samples/HelloBundledApp` **不是自动化测试**，而是所有已公开功能的可操作演示。示例说明只使用中文。

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

当前 MSI 实施线：`codex/msi-development`。NSIS 开发线：`codex/nsis-development`。原 `codex/modular-bundler-backends` 已按用户要求拆分；旧 `codex/nsis` 分支已删除，`master` 已快进至 NSIS 开发线的 `11dbde5`。这些分支指针仍须实时核查。

| 提交      | 内容                         |
| --------- | ---------------------------- |
| `3e50870` | NSIS 品牌资源和包元数据      |
| `45b1322` | NSIS 生命周期 Hook           |
| `e5ede0b` | 将 NSIS 重构为可复用后端包   |
| `156327c` | 完成模块化 Bundler 包结构    |
| `2cdfe21` | 嵌入多宿主 NsisToolset       |
| `fb6d954` | 语义版本安装策略             |
| `252b457` | 旧 MSI 迁移                  |
| `822fafb` | 文件关联与深链接             |
| `9c4f7be` | 在示例中展示 NSIS 功能       |
| `91667df` | Windows Authenticode 签名    |
| `fb1cca7` | 安装器自动化协议             |
| `8090ccf` | 快捷方式生命周期和所有权安全 |
| `20c06da` | 增加项目交接基线文档         |
| `b507b9e` | 安装事务和进程中断恢复       |
| `7200b7a` | 传播重启退出码并增加重启验收 |
| `3a79ed1` | 锁定载荷安全失败和恢复       |
| `e900eaf` | 安装持久化错误检测         |
| `41bfbb5` | 原子提交和提交后清理       |
| `11148f4` | 锁定载荷策略和交互提示     |
| `d04a7b1` | 集中人工与外部环境验收手册 |
| `ac74bf2` | 覆盖事务恢复检查点         |
| `8f5a9c1` | 卸载失败与中断后的前向恢复 |
| `e143d33` | 固化通用打包器产品边界与路线 |
| `1943c58` | NSIS 能力审计、压缩配置与共享配置修复 |
| `49fa6e6` | 完整 Windows 签名流水线 |
| `0f91d8f` | 完整内置多语言与本地化验证 |
| `b116d09` | 冻结 NSIS 安全打包基线 |

`057aca1` 是安装范围支持的历史提交，位于这条提交链的更早位置。

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
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -r win-x64 --force
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

这些 NSIS 事项的外部输入摘要保存在 `docs/nsis-open-items.md`，完整的人工执行顺序、命令、预期结果、证据和清理要求集中在 `docs/manual-testing.md`。总入口见 `docs/manual-testing-index.md`，MSI 采用独立文档。新增无法在普通本地自动化环境完成的已实现能力时，必须同步对应格式的两个文档；可自动化的测试仍由仓库测试承担。

1. **正式 Authenticode**：使用生产签名身份、私钥保护设施和公开 RFC 3161 服务验证公开信任链、时间戳策略、硬件/云签名行为。
2. **生产旧 MSI 迁移**：使用真实发布过的 ProductCode/UpgradeCode，以及 x86/x64、current-user/per-machine 旧包验证识别和权限行为。
3. **提权安装矩阵**：在明确的提权 CI 或人工环境中验证 `perMachine`、`both`、HKLM、Program Files、卸载与 `/R` 降权启动。
4. **固定项清理**：在支持的 Windows 10/11 构建和组策略下验证开始菜单/任务栏已固定项的升级与卸载行为。
5. **真实重启删除**：在可抛弃、已提权的 Windows 虚拟机运行 `tests/Windows.Nsis.Reboot/Verify.ps1` 的 `Prepare` 和 `Verify` 两阶段，验证锁定文件触发 `3010`、系统 pending rename 项和重启后删除完成；脚本不会修改或清空共享的 `PendingFileRenameOperations`。

## 10. 未完成路线与建议顺序

正式路线见 `docs/roadmap.md`。当前顺序为：

1. `WIN-MSI-1..4`：按 `docs/msi-roadmap.md` 先完成并冻结 WiX/MSI，暂定 Windows 宿主使用 WiX 3.14.1；
2. `MAC-APP`、`MAC-DMG`；macOS 决策若正式纳入 PKG，则在 Linux 前完成 `MAC-PKG`；
3. `LINUX-DEB`、`LINUX-RPM`、`LINUX-APPIMAGE`；
4. 所有上述打包格式完成后再进入 `CLI-C1`，把现有 CLI 原型产品化。

Tauri 能力按“通用打包能力、格式特定能力、Tauri runtime 专属能力”分类。签名是正式路线的一部分；WebView2、VC Runtime 等任意应用运行时依赖的自动发现、下载和安装当前明确不做。真实重启、UAC、生产证书、真实旧 MSI、多宿主/ARM64 和真实 ACL/磁盘耗尽保留在外部验收队列，不反复阻塞快速开发进入下一阶段。

### 仍不得宣称完成

- 已完成全部阶段并可广泛发行的 MSI/WiX 后端；当前 WIN-MSI-1/2 的本机 current-user 范围已验证，per-machine 仅做数据库检查，外部宿主/UAC、签名及 WIN-MSI-3/4 仍待后续阶段；
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
- 快捷方式阶段的继任任务唯一标识为 `01a0ba2e-9d6c-7563-94c9-4996cba4d0e9`，其工作已完成并提交为 `8090ccf`。
- 用户后续若没有明确要求记录到其他地方，NSIS 后续信息应记录在当前工作任务以及本文档，不再把已归档旧任务当作默认记录位置。

## 14. AI 接管协议

新的 AI 收到“开始接管 Bundler 项目”后，必须先完整阅读：

1. `PROJECT_CONTEXT.md`；
2. `AGENTS.md` 和 `docs/development-rules.md`；
3. `docs/roadmap.md`、`README.zh-CN.md`；
4. `docs/manual-testing-index.md` 和当前格式的路线、能力矩阵、open-items、人工测试文档；NSIS 历史内容仍在 `docs/nsis-upstream-reference.md`、`docs/nsis-capability-matrix.md`、`docs/nsis-open-items.md` 和 `docs/manual-testing.md`；
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

- MSI 分支：`codex/msi-development`，从 `75738ed` 继续；NSIS 开发分支：`codex/nsis-development`，指向 `11dbde5`。两者均须实时核查。
- NSIS 冻结起点：`b116d09`；当前 HEAD 应实时核查，不把本文档的历史提交误认为最新提交
- 包版本：`0.1.0-alpha.34`（交接快照；Git HEAD 与包版本均须实时核查）
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

用户已明确说“提交，然后开始”；规划文档提交为 `75738ed`，`WIN-MSI-1` 代码已完成本机范围阶段一；用户已要求提交，实际 HEAD 和工作区状态以 Git 为准。NSIS 迁移 fixture 的 `WixToolset.Sdk/5.0.2` 不代表正式选型；本阶段使用 WiX 3.14.1。当前已有直接 API、MSBuild 映射、固定工具子集与源码分发、最小 current user MSI 构建和数据库自动化验证。按用户要求与 NSIS 测试分层一致，2026-09-24 在当前 Windows x64 开发机使用每轮独立产品身份执行真实静默安装与卸载，检查文件、产品注册、未知用户文件保留及清理，均通过；脚本为 `tests/Windows.Msi.Integration/Verify.ps1 -ConfirmLocalInstall`，日志、MSI SHA-256、ProductCode 和环境见 `docs/msi-roadmap.md` 第 6 节。当前工作区的 49 项自动化测试及完整 Windows NSIS 安装/卸载集成回归也已通过，NSIS 固定测试路径和卸载注册项无残留。干净宿主 Framework、ARM64 宿主/用户端、UAC 与高影响故障仍待独立人工/外部验收，不宣称广泛支持。用户已于 2026-09-24 接受当前约 13.8 MB 的 WiX NuGet 包体积；第一阶段已完成本机范围退出条件：14 个工具子集文件逐项对应官方源码和许可，生成的 WiX NuGet 包包含许可证、源码及声明；49 项测试以 --no-restore 再次通过，涵盖新缓存解包 WiX 和实际编译 MSI，打包复核也通过。干净 Windows/ARM64 环境依用户说明暂不执行，保留待验收且不扩大支持声明。评价 WiX 3.14.1 自身的零环境要求时，只看 candle.exe/light.exe 及其依赖，不混入 Bundler 的 MSBuild 或应用构建环境：两个 EXE 及 wix.dll 均目标 .NET Framework 4.5；Windows 7 SP1 未预装所需 Framework，Windows 10/11 预装版本理论上足够，但干净宿主实际编译仍需 VM 验证，详见 docs/msi-roadmap.md 第 2 节。CLI 在计划的 MSI、macOS 和 Linux 打包格式完成后再做。

快速开发期按 NSIS 的分层测试方式继续 MSI：本机可安全运行的功能在同阶段增加自动化和真实安装/卸载回归，不把代码生成/数据库断言当作系统行为证据；缺环境的测试单列 `docs/msi-manual-testing.md`，不阻塞下一阶段，也不扩大支持声明。WIN-MSI-1 的详细证据见 `docs/msi-roadmap.md` 第 6 节。

WIN-MSI-2 已按用户“开始”在当前 Windows 11 Pro build 26200 x64 主机推进：`MajorUpgrade` 两版本真实安装/升级/卸载，降级和同版本异内容包拒绝，桌面/开始菜单快捷方式、关联和协议候选注册、用户文件保留均经 `tests/Windows.Msi.Integration/VerifyLifecycle.ps1 -ConfirmLocalInstall` 验证；产物哈希与 verbose log 见 `docs/msi-roadmap.md` 第 7 节。per-machine 只已生成独立身份、Program Files/HKLM 的包并检查数据库，未执行 UAC/真实提权安装。协议实际在默认应用 UI 中选择并唤起、快捷方式同路径被接管行为也仍需人工；原生 MSI 不提供 NSIS 式的内容级快捷方式所有权保护。新增数据库测试使当前自动化共 50 项并通过；阶段一 smoke 也以新后端重跑通过。下一阶段为 `WIN-MSI-3`，处理签名、语言、修复/维护与失败/回滚/重启，不做 CLI 或 NSIS 扩展。未经用户明确要求不提交或推送。

阶段二实现时遗漏 NuGet 包版本迭代，用户指出后按 NSIS 的既有规则将仓库统一版本从 `0.1.0-alpha.33` 升为 `0.1.0-alpha.34`。随后用户明确要求示例应用版本不随工具开发迭代，因此 Hello MSI 示例的 `BundlerVersion` 保持 `1.0.0`；升级/降级由独立集成 fixture 的版本参数测试。示例和 NSIS 示例一样在项目文件中指定仓库 `artifacts/packages` 为 `RestoreSources`，并固定引用当前开发包版本。NuGet 的 `project.assets.json` 和缓存决定 `dotnet publish` 实际加载哪个已打包版本，不直接引用正在修改的 `src/`。当前机器先前缓存的 alpha.33 元数据来源是仓库 `artifacts/packages`。alpha.34 已 pack 出七个同版本包；此前用隔离缓存还原示例验证七个依赖均为 alpha.34，曾生成 `Hello MSI App-1.1.0.msi`，那是纠正示例版本前的历史产物，不作为当前示例版本。工具包版本与 MSI 产品版本是两套独立标识；此前安装的示例 `1.0.0` 不会因只升级 NuGet 包版本而自动成为新 MSI 产品。本轮未运行该示例的卸载命令；本轮末查询其 ProductCode 的 ProductState 为 `-1`（未安装），应以实时系统状态为准。

版本补正后再次执行 Release 构建与 pack、50 项 Bundler.Tests、alpha.34 的 Windows NSIS 安装/卸载集成、Windows MSI lifecycle 集成，均通过；后者覆盖真实升级、卸载、降级和同版本异包拒绝。当前机器、MSI SHA-256、临时日志目录等可核对证据见 `docs/msi-roadmap.md` 第 7 节。Hello MSI App 未被这些测试操作。

按用户要求统一公开示例工作流：NSIS 与 MSI 示例都在 `.csproj` 中固定当前开发包版本、设置本地 `RestoreSources`，从仓库根目录执行 `dotnet pack Bundler.slnx -c Release -o artifacts/packages` 后即可直接 `dotnet publish`。MSI 示例曾缺少 `RestoreSources`，alpha.34 不在公网源导致 NU1101；补齐后原样 `dotnet publish .\samples\HelloMsiApp\HelloMsiApp.csproj` 已成功还原。随后发现旧阶段一 `1.0.0` MSI 及其 manifest 占用输出路径，按 MSI 同版本异内容保护不能覆盖；已将这两个历史构建产物及 wixpdb 保存在该示例 `artifacts/previous-msi/`，保留用户已安装的应用不动。原命令再次执行两次均成功，生成当前 `Hello MSI App-1.0.0.msi`；NSIS 示例的普通 Release publish 也通过。以后升级/降级只由独立测试 fixture 操作，不迭代公开示例应用版本。各格式的构建产物冲突仍按格式自身规则处理。

按用户指出的 NSIS 包消费测试基线，已补 `tests/Msi.Api.PackageFixture`：只引用 `DotNet.Bundler.Wix`，在 MSI smoke 脚本中复制到仓库外临时目录，使用本轮打出的本地包与隔离缓存，直接调用 `WixBundler` 生成 MSI。MSI 的 MSBuild fixture 与 lifecycle 脚本也核对 `project.assets.json`、实际包版本和隔离缓存；smoke 脚本检查 WiX 包的程序集及许可/源码/哈希/声明。2026-09-24 当前 Windows 11 Pro build 26200 x64 本机再次运行 MSI smoke 和生命周期集成，两者均通过；独立 API MSI 哈希、真实安装产物 ProductCode、生命周期包哈希及日志见 `docs/msi-roadmap.md` 第 7 节。本次测试差异由跨格式规范 `docs/development-rules.md` 固化，根 `AGENTS.md` 指向该规范。MSI 尚未冻结，下一阶段仍是 WIN-MSI-3。未经明确要求不提交或推送。

本轮最终验证还加入共享的 `tests/AssertLocalRestore.ps1`，同时由 NSIS 与 MSI 集成入口核对本地源、包版本和隔离缓存，并拒绝意外公网源；51 项快速测试通过，其中新增公开示例/API fixture/集成脚本包版本同步断言。MSI smoke 复跑通过，独立 API 包实际编译 MSI 的 SHA-256 为 `91071954ED7CBB7FA90B62EBC775A4176382FA65A53ABFF1B16E3A679F38EE06`，MSBuild 包编译及真实安装/卸载 MSI 为 `DD7BAE8709DCA5B5299A39A7A77792E426DACBDC244F37745220A02D0ADECBDD`，ProductCode `{1BAE73D8-BE20-5B82-8064-149E01BCBDE8}`；证据在 `%TEMP%\Bundler-Msi-Smoke-e286c715a3d04cb1afaf89807337ba2a`。MSI lifecycle 复跑通过，v1/v2/异包 SHA-256 依次为 `953C7CF84C42865C3404D08D63B3482156DD8D5FDD40932EBEE080B75EF928C1`、`A88EDF3C168B10C4B2D705877A69961DF10984A3F02EB19310C92AF0AE19B582`、`0B31B9232989AD69F5B489E664ABF6ACDC2AE9CC049B3B614CB13B2081EE0D27`；证据在 `%TEMP%\Bundler-Msi-Lifecycle-f1203328eac740ffbd099e7201142927`。NSIS 全量集成在共享检查加入后第一次复跑于旧有的“Committed rollback journal was not cleaned up”断言失败，同一脚本第二次复跑通过；未查明第一次失败原因，不能把它当作稳定无故障证据，也不能归因于包源检查。下一次复现时应先保存 active journal 和安装器状态再清理测试现场。

最后重新 `dotnet pack Bundler.slnx -c Release -o artifacts/packages`，在独立 NuGet 缓存、独立输出目录中分别 `dotnet publish` HelloBundledApp 和 HelloMsiApp，并以共享还原断言确认两个公开示例都只使用本地包源和 alpha.34；两种安装器均生成成功，未安装公开示例。验证目录 `%TEMP%\Bundler-Public-Samples-4ab3e0334a87448e8c4496a66a2b34d6`，NSIS/MSI 示例 SHA-256 分别为 `DFA8EAE223FE1E5F2B054687B4EB6227EAEB5503E096E31743BCDD2E16DED0CC`、`2CACDF6816FD41BA57C57279DBC2F433E0711A60990D1ABC145727EE59457715`。最终 `dotnet build Bundler.slnx -c Release --no-restore` 为 0 警告、0 错误。

**包源约定补正（2026-09-24）**：用户发现 MSI 的 MSBuild 集成 fixture 没有 NSIS 的 `<RestoreSources>$(BundlerPackageSource)</RestoreSources>`，此前只有 MSI 脚本的 `dotnet restore --source` 指定包源。核对后没有 MSI 特有理由，现已在 `BundlerMsiSmoke.csproj` 加入与 NSIS 相同的声明，MSI smoke/lifecycle 脚本传 `BundlerPackageSource`，API fixture 同样由项目属性选择本地源，不再依靠脚本隐藏的 `--source`。51 项快速测试中的版本/包源对齐断言已覆盖 NSIS/MSI 两类 fixture；实际 `project.assets.json` 和隔离缓存仍由 `tests/AssertLocalRestore.ps1` 核验。当前 Windows 11 Pro build 26200 x64 上重新运行 MSI smoke 与 lifecycle，均通过真实安装/卸载、升级与拒绝场景。Smoke 的独立 API/MSBuild MSI SHA-256 为 `7228EFEFA6F1B9D67D31C7804BE050813ADA4A0FF23CFBDA982A64B45BA539DE`、`E24D9D739322146ED38F1D77B2D95917AA01357E1E5DD5C4BB8BB4AE2376150C`，日志 `%TEMP%\Bundler-Msi-Smoke-f6e19c853c1445828ff8ca2083cc20ec`；lifecycle v1/v2/异包 SHA-256 为 `C364CC5A416ECB5FEC5F71C9254351FA496FFD623187E105FD599535F4E8A581`、`E106C9A9D3C35B162E4CE5EEB48719D2B9D40039192017C30C2514C56842AE9B`、`2217F66F46F973EBD4A42FDDF3C3CD9AC8A0A7EAD5ADFE80B2C950B1F71ECF21`，日志 `%TEMP%\Bundler-Msi-Lifecycle-d27c202d862c48aa9508a92a8c5bfa66`。普通沙箱下第一次启动测试时 MSBuild 无法读取本机 `AppData\Local\Microsoft SDKs`，这是沙箱访问失败、测试未执行；取得所需本机读取权限后 51 项测试通过。此次只改测试 fixture、脚本与规则文档，NuGet 包内容未变，包版本保持 alpha.34。

用户进一步澄清：希望各后端的**测试风格**统一，包括目录、命名、fixture、包源、入口脚本、断言、日志和清理习惯；不同格式的具体测试内容和数量按其能力决定，不要求逐项对应。根 `AGENTS.md` 提供入口，`docs/development-rules.md` 第 4 节记录规范；确有组织或调用方式上的特殊原因时说明即可。此处仅记录决策，规范细节以该文件为准。

### 14.4 执行约束

执行时遵守根目录 `AGENTS.md` 与 `docs/development-rules.md`。本文只保留当前状态和证据；完成阶段后更新状态，即使尚未提交也要保证下一次仅凭仓库文档即可接续。

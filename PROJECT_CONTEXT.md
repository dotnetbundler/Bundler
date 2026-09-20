# DotNet.Bundler 项目上下文与后续实施基线

> 最后整理：2026-09-20  
> 当前分支：`codex/modular-bundler-backends`  
> 上一已提交基线：`7200b7a feat(nsis): handle reboot-required exits`
> 当前包版本：`0.1.0-alpha.22`
> 本文档所在提交：包含已完成仓库内验证的锁定载荷失败检测与事务恢复回归测试

本文档是当前项目的“事实、决策、验证证据、协作约束与后续路线”汇总，供后续开发任务直接接续。它不是面向最终用户的使用手册；当前需要维护的用户文档以根目录的 `README.zh-CN.md` 和 `samples/HelloBundledApp/README.md` 为准。代码与自动化测试始终是实现事实的最终依据。

状态标记：

- **已实现并自动验证**：代码已完成，仓库测试或集成测试已通过。
- **已实现但需外部验收**：通用实现和仓库内 Fixture 已完成，但仍缺生产凭据、真实旧包、UAC 或多版本 Windows 等外部条件。
- **未实现**：只能作为路线，不得在 README 或发布说明中宣称支持。

## 1. 产品目标与范围

### 1.1 最终使用方式

`DotNet.Bundler` 要发布为 NuGet 包。使用者引用包、填写 MSBuild 配置后，正常执行 `dotnet publish` 即可生成桌面安装包，不需要单独安装本项目的 CLI，也不需要预装打包工具。

同时保留独立公共 API：其他项目可以直接引用 `DotNet.Bundler.Nsis` 等后端包并调用 API。MSBuild Task 只是参数适配层，不拥有 NSIS、MSI 等格式的实现。将来若恢复 CLI，也必须复用同一套 Core 和后端 API。

### 1.2 当前边界

- 当前只做桌面端。
- 第一条完整链路是 **Windows 目标 + NSIS 安装包**。
- MSI/WiX、macOS 安装格式、Linux 安装格式尚未实现。
- NSIS 目标包可以由 Windows、Linux x64/arm64、macOS x64/arm64 宿主编译；工具解析与嵌入结构已经实现，但所有宿主的真实 CI 执行矩阵仍需补齐。
- 快速推进阶段允许破坏性修改，不要求兼容早期 alpha API 或配置。
- 解决方案文件使用 `Bundler.slnx`。

### 1.3 不可回退的设计原则

1. 打包所需工具随 NuGet 包发布，普通用户不自行准备工具。
2. 工具不解压到每个使用者项目的中间输出目录；使用内容寻址的共享用户缓存，默认位于 `%LOCALAPPDATA%\DotNetBundler\tools`。
3. MSBuild Task 及其直接加载的程序集必须提供 `netstandard2.0` 资产。
4. MSBuild Task 在进程内调用 Core 和后端公共 API，不再启动额外的 .NET CLI 驱动。
5. 后端仍必须启动对应的原生编译器，例如 NSIS 的 `makensis`；这是生成安装器本身所必需的原生工具调用，不等同于用 CLI 承载业务逻辑。
6. 各格式共用校验、规划、编排、工作目录与工具缓存流程；新增格式应新增后端，不复制整条管线。
7. NSIS 模板保存在独立文件中，不能把完整脚本硬编码在 C# 字符串里；新增 NSIS 脚本注释使用中文。
8. 不确定的安装器语义优先核对 Tauri 的现行行为，但必须按本项目的 .NET/MSBuild 架构重新设计，不能机械照搬 Rust 数据模型。

## 2. 已确定的项目结构

| 项目/包                          | 职责                                                              | 当前目标框架            |
| -------------------------------- | ----------------------------------------------------------------- | ----------------------- |
| `DotNet.Bundler.Abstractions`    | 公共请求、目标、结果、日志、签名与后端契约                        | `netstandard2.0`        |
| `DotNet.Bundler.Core`            | 格式无关的验证、规划、编排、工作目录、内容寻址 ZIP 工具缓存       | `netstandard2.0;net8.0` |
| `DotNet.Bundler.Nsis`            | 可独立使用的 NSIS API、配置模型、脚本渲染、语言、内嵌工具集和插件 | `netstandard2.0`        |
| `DotNet.Bundler.Signing.Windows` | 可复用的 Windows Authenticode 签名 API                            | `netstandard2.0`        |
| `DotNet.Bundler.MSBuild`         | 将 MSBuild 参数转换为公共请求，调用 Core/后端并输出 `ITaskItem`   | `netstandard2.0`        |
| `DotNet.Bundler`                 | 便利元包，引入 MSBuild 集成                                       | NuGet 元包              |
| `DotNet.Bundler.Cli`             | 将来复用同一套 API 的命令行入口；当前不发布                       | 默认 `net8.0`           |
| `DotNet.Bundler.Wix`             | 计划中的 MSI 公共 API、生成逻辑与 WiX 工具资源                    | **尚未创建/实现**       |

核心调用关系：

```text
使用者项目
  -> DotNet.Bundler.MSBuild
      -> DotNet.Bundler.Core
          -> IBundleBackend
              -> DotNet.Bundler.Nsis
              -> 将来的 DotNet.Bundler.Wix / 其他后端
```

`DotNet.Bundler.Nsis` 不是 MSBuild 的内部实现细节；不使用 MSBuild 的调用者也能直接调用 `NsisBundler`。

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

- 内置 `English` 和 `SimpChinese` 模板文案。
- 支持以分号配置语言列表，第一项是回退语言。
- 配置多个语言且启用选择器时显示语言选择页。
- 支持项目提供自定义 NSIS 语言文件，但必须覆盖模板所需的全部 `LangString`。
- 当前示例会显示 English/简体中文选择器，并提供项目自己的简体中文文件。

Tauri 覆盖的完整语言集合目前尚未实现。不要把“两种内置语言 + 自定义扩展入口”描述成“已经支持 Tauri 的全部语言”。

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
- 两阶段 NSIS 签名：先签临时卸载器，再封装进安装器，最后签安装器。
- 内置实现不依赖 Windows SDK 或 `signtool.exe`，但当前签名实现要求 Windows 宿主。
- 本地自签名流程已经写入中英文 README 和中文示例 README。

一次性自签名证书已验证 PE 签名机制。正式发布还需要生产证书、私钥保护方式和公共 RFC 3161 服务；自签名状态不受信任是正常现象，不能证明公开信任链。

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
- 安装 Hook 可通过 `SetErrors` 或 `Abort` 触发失败；可继续执行失败处理时会立即回滚载荷、注册表和快捷方式。
- 安装器进程被直接终止、无法运行 `.onInstFailed` 时保留 active journal；下一次启动同一产品安装器时先恢复旧状态，再执行版本检测和新事务。
- journal 绑定原安装目录，恢复调用必须提供同一路径；不信任可修改的 journal 内容去选择递归删除目标。首次自定义 `/D` 安装若在注册安装目录前中断，重试时需要继续提供同一 `/D`。
- 成功安装会提交事务并删除 journal，再由成功回调决定返回 `0` 还是 `3010`。快照阶段若磁盘空间不足或遇到不能安全复制的重解析点，会在修改旧状态前失败。
- Windows 集成测试覆盖 post-install Hook 失败即时回滚、进程被直接终止后的下次启动恢复、旧 EXE 哈希、版本注册表、运行时文件、快捷方式以及 journal 清理。
- 升级的“先卸载旧版本”路径显式复制旧卸载器到唯一临时文件并同步等待；既避免安装目录内的 `Uninstall.exe` 因自锁被安排到重启删除，也避免旧卸载器与新事务并发产生竞态。
- 安装载荷提取使用 `SetOverwrite try` 并检查 NSIS error flag；锁定的非主程序载荷不能被覆盖时返回 `2`，不再跳过文件后误报成功。若锁同时阻止即时回滚，则保留 active journal，释放锁后的下一次安装启动会先恢复旧状态。

当前实现会临时占用接近现有安装目录大小的额外空间；自定义 ACL 不保证逐项还原。事务范围只覆盖 NSIS 安装和升级，不应描述成事务式卸载。旧 MSI 卸载是无法在缺少原 MSI 包时自动逆转的外部迁移边界，因此迁移后的失败只清理新 NSIS 状态，不宣称重新安装旧 MSI。卸载可通过 `/REBOOTOK` 安排删除并返回 `3010`，但安装载荷仍使用 `File /r`，尚未实现锁定文件的延迟替换；当前的保证是安全失败和后续恢复，不能宣称支持“锁定文件升级后重启完成替换”。

## 6. HelloBundledApp 示例的定位

`samples/HelloBundledApp` **不是自动化测试**，而是所有已公开功能的可操作演示。示例说明只使用中文。

当前示例必须持续展示：

- English/简体中文语言选择与自定义中文语言文件；
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

安装载荷中默认必须包含：

- `demo.hello`，用于演示 `.hello` 文件关联；
- `运行深链接.cmd`，用于运行 `hello-bundled:welcome`；
- `演示资源\说明.txt`，用于展示额外资源和快捷方式工作目录。

示例程序把最近一次启动信息写入 `%LOCALAPPDATA%\com.example.hellobundledapp\last-launch.txt`，供参数和数据删除行为验收。

## 7. 已完成阶段与提交历史

当前主实施线：`codex/modular-bundler-backends`。

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

`057aca1` 是安装范围支持的历史提交，位于这条提交链的更早位置。

## 8. 最近一次验证证据

以下是本阶段 `0.1.0-alpha.22` 完成的验证，不应扩写成未执行过的平台兼容承诺：

```powershell
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
tests/Windows.Nsis.Integration/Verify.ps1
```

结果：

- 28 项单元/契约测试全部通过；
- 生成六个 `0.1.0-alpha.22` 本地 NuGet 包；
- Windows NSIS 安装/卸载集成测试通过；
- 烟雾测试从 `.lnk` 读取并验证参数、工作目录、图标和 AppUserModelID；
- 集成覆盖用户手动删除快捷方式后更新不重建、旧名称迁移、外部程序接管同名快捷方式后卸载保留；
- Native AOT COM 修复后完整流程通过；
- Restart Manager 精确关闭安装目录内主程序，另一路径同名进程保持运行；
- post-install Hook 失败后立即恢复旧 EXE、版本注册表、运行时数据和快捷方式；
- 直接终止安装器进程后保留 active journal，下一次启动自动恢复并清理；
- 可控 reboot flag 场景返回 `3010`，载荷和注册表保持已提交状态、journal 已清理且 `/R` 未启动应用；
- 升级时显式临时复制并同步等待旧卸载器的回归路径通过；
- 锁定非主程序载荷时稳定返回 `2`，保留 active journal；释放锁后的下一次启动恢复原文件、版本注册表并清理 journal；
- 集成脚本的 `finally` 已清理其安装目录、注册表、进程、临时 Hook 标记和测试证书；保留生成的 `artifacts/windows-nsis-integration` 作为构建产物。

文档变更至少执行：

```powershell
git diff --check
```

功能变更应重新执行单元、Pack 和 Windows NSIS 集成脚本。涉及 UAC、Windows 版本差异、真实证书或真实旧 MSI 的结论必须单独列出测试环境和证据。

## 9. 已实现但仍需外部验收的事项

这些事项也保存在 `docs/nsis-open-items.md`；更新其中任一处时应同步另一处。

1. **正式 Authenticode**：使用生产签名身份、私钥保护设施和公开 RFC 3161 服务验证公开信任链、时间戳策略、硬件/云签名行为。
2. **生产旧 MSI 迁移**：使用真实发布过的 ProductCode/UpgradeCode，以及 x86/x64、current-user/per-machine 旧包验证识别和权限行为。
3. **提权安装矩阵**：在明确的提权 CI 或人工环境中验证 `perMachine`、`both`、HKLM、Program Files、卸载与 `/R` 降权启动。
4. **固定项清理**：在支持的 Windows 10/11 构建和组策略下验证开始菜单/任务栏已固定项的升级与卸载行为。
5. **真实重启删除**：在可抛弃、已提权的 Windows 虚拟机运行 `tests/Windows.Nsis.Reboot/Verify.ps1` 的 `Prepare` 和 `Verify` 两阶段，验证锁定文件触发 `3010`、系统 pending rename 项和重启后删除完成；脚本不会修改或清空共享的 `PendingFileRenameOperations`。

## 10. 未完成路线与建议顺序

### 第一优先级：重启恢复和事务边界补强

安装事务基础、进程中断恢复、精确进程协调、`3010` 传播和真实重启验收脚本已经完成；本阶段剩余可靠性缺口为：

1. 在可抛弃的管理员 Windows 虚拟机实际执行两阶段重启脚本，保存系统版本、退出码、pending rename 和重启后状态证据。
2. 为锁定的安装载荷设计持久化前向恢复和延迟替换策略，并处理它与事务提交、回滚、下次启动恢复及旧文件待删除项之间的一致性；当前只支持卸载侧延迟删除，安装侧会安全失败并恢复。
3. 锁定载荷导致即时回滚失败并由下次启动恢复的路径已经覆盖；继续增加注册表写入失败、磁盘耗尽、权限拒绝及其他回滚失败注入测试。
4. 明确是否扩展到事务式卸载；当前卸载仍沿用安全所有权检查和 `/REBOOTOK`，不属于安装事务。
5. 评估是否需要保存自定义 ACL/ADS 等完整文件安全元数据；当前恢复依赖目标父目录继承的 ACL。
6. 为重解析点制定显式产品策略；当前在修改旧状态前拒绝为其建立事务快照。

先稳定事务和载荷阶段，再扩大语言或签名范围，可以避免后续重复翻译和重复调整待签名文件集合。

### 第二优先级：完整多语言

1. 对齐 Tauri 当前支持语言集合和系统语言回退逻辑。
2. 为每种语言提供模板所需全部 `LangString`，并自动校验缺失/重复键。
3. 增加非 ASCII 产品名、目录、参数、注册表、卸载信息测试。
4. 由母语使用者或专业翻译复核发布文案。

### 第三优先级：签名覆盖扩展

1. 明确主应用 EXE、DLL、sidecar、原生插件、卸载器和安装器各自的签名范围与顺序。
2. 增加可插拔签名器，以适配 Azure Trusted Signing、云 HSM、USB Token 等不能导出私钥的环境。
3. 补公共 RFC 3161 和证书过期/撤销/时间戳失败策略。
4. 评估远程签名与非 Windows 构建宿主的签名方案。

### 第四优先级：平台与宿主矩阵

1. 在 Windows、Linux x64/arm64、macOS x64/arm64 runner 上真实调用内嵌 `makensis`。
2. 验证工具缓存并发、损坏恢复、权限、长路径和 Unicode 路径。
3. 重新审计缓存命中是否只检查文件存在；若没有按 manifest/hash 校验已解压内容，则补强。
4. 增加 Windows x64/ARM64 目标安装和卸载矩阵。
5. Windows + NSIS 可靠后再实现 `DotNet.Bundler.Wix`，随后再规划 macOS/Linux 安装格式。

### 仍不得宣称完成

- MSI/WiX 后端；
- macOS `.app`/DMG；
- Linux DEB/AppImage；
- Tauri 全语言集合；
- 所有 Windows 版本的自动取消固定；
- 所有宿主和架构的真实 CI；
- 正式证书和生产旧 MSI 的端到端验收；
- 包含锁定文件安装替换、经真实重启确认的待删除完成、完整 ACL/重解析点语义和事务式卸载在内的全场景恢复。

## 11. 文档、测试与提交约定

### 11.1 文档

- 当前阶段只要求维护根 `README.zh-CN.md`，不要求同步修改英文 `README.md`；英文文件继续保留，除非用户以后重新要求维护英文版。
- `samples/HelloBundledApp/README.md` 只使用中文。
- 新功能要同时更新公共 API、MSBuild 属性表、示例和测试说明。
- 实现事实、外部验收和未来计划必须分开描述。
- 数字、版本、哈希、提交和平台结论尽量用实际产物或命令核验。
- 每完成一个阶段，都要更新本文档中的当前版本、HEAD、验证证据、已完成能力、外部验收项和后续顺序，使下一位接管者不需要依赖旧对话恢复上下文。

### 11.2 测试

- `HelloBundledApp` 负责演示，不代替自动化测试。
- 单元/契约测试位于 `tests/Bundler.Tests`。
- NuGet API 消费 Fixture 位于 `tests/Nsis.Api.PackageFixture`。
- Windows 端到端 Fixture 位于 `tests/Windows.Nsis.Integration`。
- 每个新增或修改的功能都必须在同一阶段新增或更新对应的自动化测试；只运行已有测试不能证明新行为已经完成。
- 修复缺陷时必须增加能够复现原问题的回归测试，使该测试在修复前失败、修复后通过。
- 公共 API、配置验证、MSBuild 参数映射、模板渲染和工具选择应优先添加快速的单元或契约测试。
- 安装、升级、卸载、回滚、注册表、快捷方式、文件关联、进程协调、签名和退出码等真实 Windows 行为必须增加端到端集成测试，不能只断言生成的 NSIS 脚本包含某段文本。
- 失败路径必须使用可控的失败注入，并断言退出码、文件、注册表、快捷方式、应用数据和 journal 等可观察状态；不能只断言“发生了失败”。
- 新增公开功能除了自动化测试外，还必须在 `HelloBundledApp` 中提供可操作演示；示例和自动化测试两者不能互相替代。
- 如果某项行为受 UAC、正式证书、真实旧安装包、重启或特定系统版本限制而无法在普通 CI 中自动验证，必须完成能够自动化的部分，并在 `docs/nsis-open-items.md` 和本文档中记录未覆盖条件、人工验收步骤与证据边界，不能直接省略测试说明。
- 不得为了让测试通过而无依据地删除、跳过或弱化已有断言；若产品语义确实改变，应同步修改实现、测试、中文文档和本文档，并说明原因。
- 一个阶段只有在新增测试、既有回归测试和相关集成测试全部通过后，才能标记为“已实现并自动验证”；否则必须标记为“已实现但需外部验收”或“未完成”。
- 验证报告必须列出实际执行的命令、环境、通过/失败数量、未覆盖情形和测试遗留清理结果，不能把之前阶段的结果当成本次结果。
- 清理测试遗留时只删除本次测试明确创建的文件和目录，不递归清空整个 `artifacts`，不覆盖用户无关改动。

### 11.3 Git

- 未收到用户明确的“提交”指令时，不提交实现或文档改动。
- 提交前检查分支、工作区、diff、测试证据，只提交本阶段已验证范围。
- 不使用破坏用户工作区的 `git reset --hard` 或宽泛清理命令。

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
2. `AGENTS.md`（如果存在）；
3. `README.zh-CN.md`；
4. `docs/nsis-open-items.md`；
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

- 分支：`codex/modular-bundler-backends`
- 上一已提交基线：`7200b7a`；本文档所在提交包含随后完成的锁定载荷失败检测与恢复测试
- 包版本：`0.1.0-alpha.22`
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

若用户没有另外指定范围，下一阶段先在可抛弃管理员 Windows 虚拟机执行真实重启验收，再设计带持久化前向恢复的锁定安装载荷延迟替换；不能直接写入共享 pending rename 队列后仍声称可以无条件回滚。若外部环境暂不可用，则继续第 10 节的权限拒绝、注册表失败和回滚失败注入测试。具体目标与实施顺序见第 10 节第一优先级。

### 14.4 执行约束

- 未经用户明确要求，不提交 Git commit。
- 新增 NSIS 脚本注释使用中文。
- 当前只维护 `README.zh-CN.md`。
- `samples/HelloBundledApp/README.md` 只使用中文。
- `HelloBundledApp` 是公开功能演示，不是自动化测试；所有新增公开功能都必须在示例中提供可操作展示。
- 新增或修改功能时必须同步编写自动化测试；缺陷修复必须带回归测试。功能完成后执行相关单元/契约测试、NuGet Pack 和 Windows NSIS 集成测试，并准确记录没有覆盖的外部环境。不得只运行旧测试就宣称新功能完成。
- 保留用户和其他任务的无关改动，不使用破坏性 Git 或宽泛清理操作。

完成一个阶段后，即使用户暂时不要求提交，也应更新本文档的状态，使下一次只凭本文档就能继续工作。

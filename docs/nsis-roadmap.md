# Windows NSIS 后端路线与冻结记录

本文件保存 NSIS 专有能力基线、工具链事实、能力契约、旧阶段映射和收口证据；当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准。
跨格式规则见 `docs/development-rules.md`，后续格式顺序见 `docs/roadmap.md`，逐项状态见 `docs/nsis-capability-matrix.md`。
以下历史版本、提交和验证结果仅代表当时证据。

## 1. 当前能力基线

原路线的身份/行为契约、工具供应、版本升级、MSI 迁移、事务恢复、自动化协议、文件关联/深链接、快捷方式、本地化、安全边界和缓存完整性已经完成。
NSIS 第一条完整产品链路现已冻结；后续只接受有明确用例和完成条件的缺陷修复或增强，不再以“继续打磨 NSIS”阻塞 CLI 与新格式。

为避免新文档看起来像更换了方向，旧任务“NSIS：后续功能实施路线”的阶段映射如下。
新编号是收口后的执行分组，不是删除旧计划：

| 旧阶段 | 原主题 | 当前去向 |
| --- | --- | --- |
| 0 | 身份、行为与兼容契约 | 已完成并成为回归基线 |
| 1 | 工具供应链、缓存、原生插件 | 已在 `NSIS-R4` 完成并冻结；真实宿主见 MT-08 |
| 2 | 已安装版本、升级、旧 MSI 迁移 | 已完成；真实历史 MSI 留在 MT-05 |
| 3 | 事务、进程、重启与恢复 | 自动化边界已在 `NSIS-R4` 收口；真实 OS 条件留在人工队列 |
| 4 | 自动化参数与退出码 | 已完成并回归保护 |
| 5 | 文件关联与深链接 | 已完成并回归保护 |
| 6 | 快捷方式完整性 | 已完成；跨 Windows 固定项行为留在 MT-06 |
| 7 | 完整内置多语言 | 已在 `NSIS-R3` 完成；内容质量见 MT-11 |
| 8 | Authenticode 完整流水线 | 已完成；生产身份/HSM/公开时间戳见 MT-04 |
| 9 | 真实 UAC、范围和注册表矩阵 | 实现已存在，外部验收见 MT-01/02/09 |
| 10 | 跨宿主、跨架构与发布门禁 | 自动化部分已完成，原生环境证据进入 MT-08/09 |

| 能力 | 当前状态 | 后续位置 |
| --- | --- | --- |
| 产品元数据、图标、Header/Sidebar、许可证 | 已实现 | 回归保护 |
| `currentUser` / `perMachine` / `both` | 已实现，真实 UAC 外部待验收 | 人工 MT-01/02 |
| 生命周期 Hook、自定义模板入口 | 已实现并完成入口审计 | 回归保护 |
| 工具内嵌、内容寻址缓存、多宿主解析 | 已实现；逐文件完整性、并发和损坏恢复已自动验证，真实宿主矩阵外部待验收 | 回归保护、MT-08 |
| SemVer、升级/降级、旧 MSI 精确迁移 | 已实现 | 回归保护、MT-05 |
| 自动化参数和稳定退出码 | 已实现 | 回归保护 |
| 文件关联与 URL 协议 | 已实现 | 回归保护 |
| 快捷方式生命周期和所有权保护 | 已实现 | 回归保护、MT-06 |
| 安装事务、进程中断恢复、卸载前向恢复、重解析点安全拒绝 | 已实现 | 回归保护、MT-03/07/10 |
| 内置语言 | 已实现：22 种内置语言、严格键校验、自定义覆盖、回退和非拉丁 E2E；内容审校外部待验收 | 回归保护、MT-11 |
| Authenticode | 已实现：staged payload、Bundler 插件、卸载器和安装器；内置与外部 provider | 回归保护、MT-04 |
| NSIS 压缩和配置面审计 | 已实现：LZMA、ZLIB、BZIP2、无压缩 | 回归保护 |
| 任意应用运行时依赖安装 | 不适用/明确不做 | 不进入路线 |

## 2. 工具供应链

### 2.1 工具来源与布局

NSIS 工具统一使用通用项目 [dotnetbundler/NsisToolset](https://github.com/dotnetbundler/NsisToolset)。
该项目服务所有潜在消费者，不能描述成只供 `DotNet.Bundler.Nsis` 使用。

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
│  ├─ linux-x86_64/
│  ├─ linux-aarch64/
│  ├─ macos-x86_64/
│  └─ macos-arm64/
├─ toolset-manifest.json
├─ COPYING
└─ README.md
```

每个平台不需要复制公共脚本、Include、Contrib 和 Stub；`common/` 只保留一份，每个 `hosts/<rid>/` 只放该宿主执行编译所需的原生文件。

重要实现事实：

- Windows 根目录的 `makensis.exe` 是启动器；`Bin/makensis.exe` 才是编译器，且需要 `Bin/zlib1.dll`。
- 当 NSIS 使用 `NSIS_CONFIG_CONST_DATA_PATH=no` 构建时，不会自动找到顶层拆分出来的 `common/`；启动器或调用方必须设置 `NSISDIR`。
- `makensis.cmd` 的作用只是为这种拆分布局设置环境并启动真实编译器，不是额外的打包实现。
  程序化调用也可以直接设置 `NSISDIR` 后启动真实二进制。
- NsisToolset 仓库中的 Python 文件负责下载、暂存、生成 manifest、校验、打包以及来源信息，不进入最终工具集运行时。
- Windows 工具来自官方二进制；Linux/macOS 宿主编译器应从同版本 NSIS 源码在匹配的原生 CI runner 上构建，不应假设能在 Windows 上可靠地产出所有原生宿主程序。
- NSIS 模板保存在后端包内的独立文件中，不能把完整脚本硬编码在 C# 字符串里；新增 NSIS 脚本注释使用中文。

### 2.2 校验值与来源措辞

NSIS 3.12 官方页面提供并已核对：

- SHA-1：`364fd795b0cafc1fbff3e966f103a8f8fc8fb7f1`
- MD5：`757c22153dd8b90f5e297310d9966997`

本地计算的官方 ZIP SHA-256：

```text
56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f
```

不得把这个 SHA-256 表述为上游官方发布值。
每次更换工具集 Release 时必须重新核验下载产物、manifest、许可证和各宿主可执行文件。

### 2.3 许可证

- `third_party/nsis/COPYING` 与 `THIRD-PARTY-NOTICES.md` 保留在本仓库和发布包中。
- 即使 NsisToolset 压缩包内部也包含 `COPYING`，NuGet 包级别仍应保留清晰可见的第三方许可归属；不要因为压缩包内已有一份就盲目删除外层许可文件。

## 3. NSIS 原生插件

需要自行实现 NSIS 插件时，统一使用 [dotnetbundler/NsisPlugin](https://github.com/dotnetbundler/NsisPlugin)。
当前插件项目与产物：

```text
tools/Bundler.Nsis.Plugin/
third_party/nsis/plugins/x86-unicode/DotNetBundlerNsis.dll
```

插件使用 `windows-i686` Native AOT 构建，因为 NSIS 插件宿主采用 x86 Unicode ABI。
当前插件承担（`installer.nsi` 调用面 24 个导出函数）：

- 安装/卸载事务日志：事务开启、激活、提交、崩溃恢复、注册表回滚与快照完整性校验；
- 快捷方式生命周期：创建、读取、所有权判断、更新、迁移与安全删除；
- 已安装 MSI 查询和旧 MSI 迁移辅助；
- 锁定进程处理（Restart Manager：`rstrtmgr` 会话枚举与关闭）；
- 安装完成后的降权进程启动（RunAsUser）；
- SemVer 比较。

因插件为 `.NET 10` Native AOT 构建且加载期依赖 UCRT 与 Win8+ API，消费机实际下限为 Windows 10 1607+（[.NET 10 支持的操作系统](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)）。

Native AOT 下不能依赖经典 `ComImport`/RCW 自动封送来操作 Shell Link；
此前运行时出现 `InvalidOperation_ComInteropRequireComWrapperInstance`，现已改成直接调用 COM vtable。

## 4. 当前能力契约

除单独标明外，下列功能均属于 **已实现并自动验证**。

### 4.1 安装与卸载基础行为

- 用户可在安装时选择安装目录，并记住上次目录。
- 默认安装到独立的应用目录。
- 交互安装选择无本产品标记的非空目录时会警告；自动安装会拒绝覆盖无关的非空目录。
- 桌面和开始菜单快捷方式可由用户选择，不再固定创建。
- 安装/卸载前检测运行中的主程序并尝试关闭。
- 卸载时可选择删除应用数据。
- 未选择删除应用数据时，只移除构建载荷中记录的路径；程序运行后新建的不同路径会保留。
  如果程序覆盖了构建载荷中的同名文件/路径，该路径仍属于卸载清单，会被删除。
- 选择删除应用数据时，递归删除完整安装目录，并删除 `%APPDATA%\<identifier>` 和 `%LOCALAPPDATA%\<identifier>`。

运行进程检测通过 Windows Restart Manager 注册 `$INSTDIR` 下当前主程序和显式旧主程序的完整路径，并只关闭实际占用这些文件的进程。
Windows 集成测试同时运行安装目录内的程序和另一路径中的同名程序，验证重装只关闭前者。

### 4.2 多语言

- 内置固定快照中的 22 种语言：Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian、Vietnamese；
  对外 `Persian` 在 NSIS 内部映射为 `Farsi`。
- 支持以分号配置语言列表，第一项是回退语言。
- 配置多个语言且启用选择器时显示语言选择页。
- 支持项目提供自定义 NSIS 语言文件；它完整替换该语言的内置文件，严格校验缺失、重复、未知键和 `LANG_*` 常量。
- 当前示例选择 English、自定义 SimpChinese 和内置 Japanese，展示多语言选择器。

22 种语言均已由真实 NSIS 编译测试，另有非拉丁 Unicode 安装/卸载集成测试。
译文内容、RTL 和不同缩放下的布局仍需 MT-11 外部审校；结构与可编译不等于母语质量验收。

### 4.3 安装范围

- `currentUser`
- `perMachine`
- `both`（安装时选择）
- 已生成对应的 UAC、注册表视图与 Shell Context 脚本逻辑。

真实 HKLM/Program Files 写入和完整 UAC 交互仍属于 **已实现但需外部验收**，不能由普通本地自动化静默触发。

### 4.4 版本策略与旧 MSI 迁移

- 版本要求 SemVer 2.0；Windows 文件版本的三个数字核心段均限制在 `0-65535`。
- 检测已安装版本并比较语义版本。
- 交互处理同版本重装、升级与降级。
- 静默同版本执行原位修复；静默升级先移除旧构建载荷并保留应用数据。
- 默认禁止降级，可显式允许。
- 支持通过准确的历史 MSI ProductCode/UpgradeCode 迁移旧 WiX/MSI 产品，不按产品名猜测。
- 可选的 Tauri 对齐自动检测（`LegacyMsiAutoDetect`/`BundlerNsisLegacyMsiAutoDetect`，默认关闭）：
  显式启用后按卸载注册项的 DisplayName+Publisher 匹配，并要求卸载命令含 `msiexec`；
  枚举 HKCU 与 HKLM 的 32/64 位视图，per-user 与 per-machine MSI 均可命中。
  与显式 GUID 列表相互独立、结果合并去重；匹配不到时不影响安装。
- 旧 MSI 卸载返回码：`0`/`1605`（已不存在）继续清理循环，`1641`/`3010` 置重启标记，
  `1602`（用户在 MSI 界面取消）按用户取消退出安装，其余非零视为迁移失败并回滚新状态。
- 同一 UpgradeCode 下多个版本并存时取最高版本做版本判定，迁移循环逐个移除全部匹配产品。

一次性 MSI Fixture 已验证通用迁移机制、自动检测与多版本清理。
真实已发布产品的历史 GUID、x86/x64、current-user/per-machine MSI 仍需生产输入验收。

### 4.5 生命周期 Hook

支持项目提供 `.nsh` 宏，介入：

- 安装前；
- 安装后；
- 卸载前；
- 卸载后。

模板扩展入口必须保持稳定，并避免让自定义 Hook 绕过目录、所有权与卸载安全规则。

### 4.6 品牌、许可证与元数据

- 安装器图标；
- 卸载器图标；
- Header 图片；
- 卸载器 Header 图片；
- Sidebar 图片；
- License 页面；
- Publisher、Description、Homepage、Copyright；
- 产品版本和文件版本；
- “应用和功能”卸载元数据。

### 4.7 文件关联与深链接

- 文件扩展名关联使用应用专属 ProgID，并注册“打开方式”和默认应用 Capabilities。
- 不静默抢占用户当前选择的默认应用。
- 自定义 URL 协议可启动应用。
- 卸载只在协议仍指向本次安装目录时删除注册，避免破坏后来接管同一协议的程序。

概念说明：文件关联让 `.hello` 等文件可由应用打开；深链接让 `hello-bundled:welcome` 这样的 URI 直接启动应用并传递目标。

### 4.8 Authenticode 签名

- 独立 `DotNet.Bundler.Signing.Windows` 公共 API。
- 支持 PFX/P12 和 Windows `My` 证书存储区指纹。
- PFX 密码通过环境变量名传递，避免写入项目文件或命令行。
- 默认 SHA-256，支持 RFC 3161 时间戳 URL。
- 完整签名顺序：临时副本中的主 EXE、调用方显式选择的附加 payload、工作区中的 Bundler 原生插件、临时卸载器、最终安装器。
- `BundleTargetConfiguration.SigningFiles` / `BundlerWindowsSigningFile` 只选择输入目录内的 DLL、sidecar 或辅助程序；不会自动重签其他第三方文件。
- 签名在工作区副本完成，不修改调用方输入目录或内容寻址共享插件缓存；任一步失败都会删除最终安装器。
- `WindowsExternalCommandSigner` / `BundlerWindowsSigningCommand` 为云 HSM、USB Token 和远程 provider 提供独立参数入口，支持路径、产物类别、目标 RID 和产品名占位符；普通失败信息不回显 provider 参数或输出。
- 内置实现不依赖 Windows SDK 或 `signtool.exe`，但内置 Authenticode provider 要求 Windows 宿主；外部 provider 的宿主范围由其命令决定。
- 本地自签名流程已经写入 README 和示例文档。

一次性自签名证书已验证 payload、Bundler 插件、卸载器和安装器的 PE 签名机制；无秘密 Fixture 已验证外部 provider 参数替换、退出码与脱敏。
正式发布还需要生产证书/HSM、私钥保护方式和公共 RFC 3161 服务；自签名状态不受信任是正常现象，不能证明公开信任链或 SmartScreen 声誉。

### 4.9 安装器自动化协议

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
| `6`    | 清单不同，需 `/RECOVERONLY` 恢复 |
| `3010` | 成功，但需要重新启动 Windows |

`/R` 的实现规则：非提权安装使用当前令牌 `CreateProcessW`；提权安装使用桌面 Shell 令牌、`CreateEnvironmentBlock` 和 `CreateProcessWithTokenW`。
不能通过 Shell 拼接命令行来启动应用。

安装与卸载成功回调都会把 NSIS reboot flag 映射为 `3010`；安装事务在成功回调前已经提交并删除 journal，`/R` 在需要重启时不会启动应用。
仓库内 Fixture 已通过可控 `SetRebootFlag` 验证这些契约。
真实 UAC 场景的 `/R`、由锁定文件产生并跨真实重启完成的 `3010` 场景，以及无法关闭进程的退出码 `5` 仍需要外部/专门环境验证。

### 4.10 快捷方式完整性

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

### 4.11 安装事务与失败恢复

- 安装和升级在修改旧状态前快照完整安装目录、产品相关注册表项、共享注册表值以及当前/旧名称快捷方式。
- journal 按安装范围保存在 `%LOCALAPPDATA%\DotNetBundler\transactions` 或 `%ProgramData%\DotNetBundler\transactions`；只有快照完成后才标记为 active。
- 恢复前先逐项比较 journal 快照目标与安装器编译时生成的固定清单；文件路径以及注册表 root、view、subkey、value name 任一不符都会在恢复载荷前失败。
  实际恢复再次校验，并只使用安装器清单中的目标；额外 journal 项不会被枚举执行。
  因此 journal 不能单独授权修改产品清单外的文件或注册表项。
- 清单不同时返回专用退出码 `6`，保留 active journal；用创建该 journal 的原安装器及原 `/D=` 目录运行 `/S /RECOVERONLY`，只恢复旧状态，再运行新配置安装器。
  更早、不支持该开关的原包可按原范围/目录重试，由其自动恢复并继续自身安装，但不能保证“仅恢复”。
  缺失原安装器或其自身也无法完成时不会放弃清单校验自动猜测目标，需取得可信原包或人工排障；这是明确的恢复依赖而非静默兼容。
- active 安装 journal 的静态快照树在激活时计算 SHA-256，并在相应 HKCU/HKLM 的独立注册表锚点保存；恢复前核对内容与目录结构，包括载荷和注册表快照。
  校验失败返回 `2` 且保留 journal，不执行部分恢复。
  同一用户可同时修改其 HKCU 锚点和 currentUser journal，因此这不是对同用户恶意进程的安全隔离。
- 安装 Hook 可通过 `SetErrors` 或 `Abort` 触发失败；可继续执行失败处理时会立即回滚载荷、注册表和快捷方式。
- 安装器进程被直接终止、无法运行 `.onInstFailed` 时保留 active journal；下一次启动同一产品安装器时先恢复旧状态，再执行版本检测和新事务。
- journal 绑定原安装目录，恢复调用必须提供同一路径；安装目录、快捷方式路径和注册表恢复目标均不能只由可修改的 journal 决定。
  首次自定义 `/D` 安装若在注册安装目录前中断，重试时需要继续提供同一 `/D`。
- 成功安装以将 active journal 原子重命名为同级 `.committed` 目录作为提交点，再尝试清理已提交快照。
  提交后清理失败不再回滚已完成的安装；下次启动先重试清理 `.committed`，再处理 active journal。
  快照阶段若磁盘空间不足或遇到不能安全复制的重解析点，会在修改旧状态前失败。
- Windows 集成测试覆盖 post-install Hook 失败即时回滚、进程被直接终止后的下次启动恢复、可控快捷方式/注册表持久化失败和提交后清理失败。
  此外还在事务快照、事务激活、载荷恢复、注册表恢复和 active journal 清理五个检查点执行一次性故障注入，验证旧 EXE 哈希、版本注册表、运行时文件、快捷方式、journal 中间状态以及下次启动恢复/清理。
- 升级的“先卸载旧版本”路径显式复制旧卸载器到唯一临时文件并同步等待；既避免安装目录内的 `Uninstall.exe` 因自锁被安排到重启删除，也避免旧卸载器与新事务并发产生竞态。
- 安装载荷提取使用 `SetOverwrite try` 并检查 NSIS error flag；锁定的非主程序载荷不能被覆盖时返回 `2`，不再跳过文件后误报成功。
  交互模式会提示关闭可能占用安装目录文件的程序后重试；静默/被动模式不弹窗。
  若锁同时阻止即时回滚，则保留 active journal，释放锁后的下一次安装启动会先恢复旧状态。
- 快捷方式目录/偏好以及卸载信息、文件关联和 URL 协议注册都会在事务提交前检查 NSIS error flag；检测到持久化失败时进入同一事务回滚路径，不提交不完整安装。

当前实现会临时占用接近现有安装目录大小的额外空间。
构建输入、资源、安装快照/恢复、journal 和工具缓存统一拒绝 symlink、junction 与其他重解析点；安全清理只删除链接本身，不跟随目标。
普通文件快照保留内容、基础属性和时间戳，但不承诺完整保真恢复自定义 ACL、ADS、稀疏文件等任意文件系统元数据。
安装事务范围只覆盖 NSIS 安装和升级。
旧 MSI 卸载是无法在缺少原 MSI 包时自动逆转的外部迁移边界，因此迁移后的失败只清理新 NSIS 状态，不宣称重新安装旧 MSI。
安装载荷明确采用“锁定时安全失败并恢复”策略，不使用通用延迟替换：`MoveFileEx` 的延迟操作需要管理员上下文，无法为 `currentUser` 提供一致能力；
共享 pending rename 队列也没有可安全纳入安装回滚事务的撤销机制。
详见 `docs/nsis-locked-payload-policy.md`。

权限边界核查（2026-09-23，本机当前 shell 为 Medium integrity）：`%LOCALAPPDATA%\DotNetBundler` 由当前用户拥有，当前用户具有继承的 Full Control，currentUser journal 与 HKCU 锚点均可被同一用户修改；这不构成普通用户到管理员的提权隔离。
`%ProgramData%` 由 SYSTEM 拥有，SYSTEM/Administrators 为 Full Control，普通 Users 在根上主要为读取/执行并具有限定的子目录创建权。
代码现在要求 perMachine 产品目录、事务目录和 journal 树的所有者与允许 ACE 只属于 Administrators/SYSTEM，拒绝预建的宽权限目录；
但本机尚无真实提权安装创建的最终 ACL，MT-01/MT-07 仍须验证创建、继承、重解析点和注册表锚点权限。
目标清单约束独立于 ACL；快照内容和恢复卸载器的提权完整性则依赖受保护的锚点及目录边界。

### 4.12 卸载前向恢复

- 卸载使用独立的 `${PRODUCT_ID}.uninstall` journal，不复用安装回滚快照，也不宣称能撤销已经删除或通过 `/REBOOTOK` 排队删除的文件。
- 修改持久状态前保存原安装目录、`/DELETEAPPDATA` 选择和恢复卸载器；active 后的失败或进程中断保留这些状态。
  恢复卸载器从已安装的 `Uninstall.exe` 复制，复制前后与卸载注册项中的 `BundlerRecoverySha256` 对照；执行前再次校验 journal 副本的哈希，不匹配时不启动。
- 卸载注册项保留到 finalizing 阶段，为下一次安装器提供受保护的 `InstallLocation`。
  恢复插件只接受与该目录一致的 journal，避免信任可修改 journal 选择递归删除目标。
- 下一次安装启动会以 `_?=` 直接模式同步运行 journal 中的恢复卸载器。
  恢复阶段幂等重做所有权安全的快捷方式、关联、数据和载荷删除；成功进入 finalizing 后由父安装器原子提交并清理 journal，再继续新安装。
- 若恢复返回 `3010`，新安装会停止，避免系统待删除队列在重启后删除刚写入的新载荷。
- 卸载 Hook 可能在 active 恢复时重复执行，必须幂等。
  post-uninstall `SetErrors` 和进程树强制终止均有 Windows 集成回归。
- NSIS 从安装目录直接启动 `Uninstall.exe` 时使用外层自复制 launcher，该 launcher 不可靠地传播实际卸载进程退出码。
  内部升级、恢复和重启验收脚本显式复制卸载器并用 `_?=` 同步等待实际进程；不得把外层 `0` 当作完成证据。

### 4.13 NSIS 能力审计与压缩

- 已按固定 Tauri commit `5d995ed35b029cecd780fdbe614dc6023a89b81b` 完成 `NsisConfig`、`WindowsConfig` 和与 NSIS 有关的通用 `BundleConfig` 逐项审计，矩阵位于 `docs/nsis-capability-matrix.md`。
- `NsisBundleConfiguration.Compression` 和 `BundlerNsisCompression` 支持 `lzma`、`zlib`、`bzip2`、`none`，默认 LZMA；四种模式均已实际调用内嵌 `makensis` 编译验证。
- JSON `BundleConfigurationLoader` 现会保留文件关联和 URL 协议及其元数据，不再让未来 CLI 的共享配置入口静默丢失这些能力。
- WebView2、VC Runtime 和 Tauri updater 产物已分类为框架/runtime 专属或尚无通用契约，不进入当前 NSIS 实现；签名已在 `NSIS-R2` 收口，完整语言已在 `NSIS-R3` 收口。

### 4.14 完整签名流水线

- `IBundleSigner` 请求现在携带产物类别、产品名和目标 RID；类别区分主程序、显式 payload、Bundler 原生组件、卸载器和安装器。
- 目标级 `SigningFiles` 是格式无关公共模型的一部分，JSON loader、直接 API 和 MSBuild 均保留同一语义；未配置 signer 时显式签名文件会失败而非被静默忽略。
- NSIS 启用签名后复制 payload 与插件到私有工作区，保证源输入和共享缓存字节不变。
  真实 `PublishDir` 尾部分隔符路径已有回归覆盖。
- 外部命令签名器使用可执行文件加独立参数数组，不经 shell；至少一个参数必须含 `{path}` 或 `%1`，并支持 `{artifactKind}`、`{target}`、`{productName}`。
  provider 输出和参数默认不进入异常消息。
- 最终 installer 签名失败会删除已编译的输出，避免调用方把未签名文件误当成功产物。

## 5. 收口阶段记录

### NSIS-R1：能力审计与配置面收口

**状态：已完成并提交（2026-09-21，`0.1.0-alpha.28`，`c50201d`）。**
完整矩阵见 `docs/nsis-capability-matrix.md`。
审计确认通用小型缺口为压缩配置，现已支持 LZMA、ZLIB、BZIP2 和无压缩；同时修复 JSON 配置加载时丢失文件关联和 URL 协议的问题。
31 项单元/契约测试、六包 Pack 和 `alpha.28` Windows NSIS 集成回归通过。

**目标**：以固定 Tauri commit 为参考完成一次有边界的 NSIS 能力审计，并在同一阶段实现适用于通用打包器的小型配置缺口，形成可持续维护的能力矩阵。
这不是只写计划的阶段。

实施范围：

1. 逐项核对当前 Tauri NSIS/Windows bundler 的公开配置和生成流程，更新 `docs/nsis-upstream-reference.md` 的固定 commit。
2. 建立“上游能力 → 本项目等价能力 → 状态 → 测试 → 决策”的完整矩阵。
3. 实现审计确认的通用小型缺口。
   当前已知候选包括 NSIS 压缩方式配置，以及自定义模板、Hook、语言文件、Artwork、安装范围等在直接 API 和当前正式入口 MSBuild 中的一致性；以代码审计结果为准，不凭名称猜测。
   现有 CLI 原型的完整映射留给 `CLI-C1`，但本阶段的公共模型不得妨碍 CLI 复用。
4. 可以在此阶段重整早期 alpha 配置模型，使格式配置与入口解耦；无需保留旧 alpha 兼容层。
5. 明确把 WebView2、VC Runtime 等应用运行时部署标为“不适用”，不实现假通用抽象。

自动化要求：

- 每个新增配置有验证、映射和脚本/编译测试；
- 直接 API 与 MSBuild 对同一配置产生等价后端输入，且不把 MSBuild 类型或 `.NET publish` 假设引入后端模型；
- 完整运行单元/契约测试、Pack 和 Windows NSIS 集成回归。

完成条件：矩阵没有“未调查”项；所有适用的小型配置缺口已实现或被分配到 `NSIS-R2`/`R3`/`R4`；不适用和拒绝项有理由；默认阶段切到 `NSIS-R2`。

### NSIS-R2：完整 Windows 签名流水线

**状态：已完成并提交（2026-09-21，`0.1.0-alpha.29`，`f124ee7`）。**
签名启用时先暂存输入，默认签主 EXE，仅签调用方显式选择的附加 payload，再签工作区内的 Bundler 原生插件、导出的卸载器和最终安装器；不修改调用方输入或共享插件缓存。
外部命令 provider 通过独立参数和占位符接入，失败信息不回显参数或 provider 输出。
34 项单元/契约测试、六包 Pack、示例发布和 `alpha.29` Windows NSIS 集成回归均通过；集成实际验证安装后的主 EXE、卸载器与安装器签名。

**目标**：把当前“两阶段签卸载器和安装器”扩展为可用于真实发布、可由所有入口配置的完整 Windows 签名能力。

实施范围：

1. 定义待签名产物分类和确定顺序：调用方指定/策略识别的主 EXE、适用 PE DLL、sidecar/辅助程序、Bundler 原生插件、导出的卸载器、最终安装器。
2. 在载荷进入 NSIS 之前完成 payload 签名；再导出并签名卸载器；最后生成并签名安装器。
   签名失败不得留下被误认为成功的最终产物。
3. 扩展 `IBundleSigner`/签名请求模型，使调用方能判断产物类别、目标和阶段；由于仍是 alpha，可直接修正现有抽象。
4. 保留内置 PFX/P12、证书存储区指纹和 RFC 3161 能力。
5. 增加安全的外部签名命令/自定义 provider 配置，服务 Azure Trusted Signing、云 HSM、USB Token 和远程签名；
   直接 API 与 MSBuild 先暴露同一模型，`CLI-C1` 再映射到 CLI。
   秘密不得进入项目文件、普通日志或可回显命令行。
6. 明确哪些 payload 默认签、哪些只能显式选择，避免擅自修改任意用户文件或第三方已签名文件。

自动化要求：

- fake signer 验证全部产物、顺序、失败传播和清理；
- 自签名 Windows E2E 验证 payload、卸载器和安装器的实际 PE 签名；
- 外部命令使用无秘密 Fixture 验证参数替换、退出码和日志脱敏；
- 生产证书、公开时间戳、HSM/云签名与 SmartScreen 继续进入人工 MT-04，不阻塞实现阶段完成。

完成条件：支持的入口能配置同一签名策略；所有声明纳入范围的 PE 产物均按顺序签名并验证；失败不会发布半成品；默认阶段切到 `NSIS-R3`。

### NSIS-R3：完整内置多语言

**状态：已完成并提交（2026-09-23，`0.1.0-alpha.30`，`783b835`）。**
固定上游快照中的 22 种语言均已内置，公开 `SupportedLanguages` 提供规范名称；自定义语言完整替换内置文件，并严格校验缺失、重复、未知键和 `LANG_*` 常量。
`Persian` 对外保持 Tauri 能力名称、内部映射到 NSIS 3.12 的 `Farsi`。
36 项 Release 单元/契约测试、六包 Pack 和完整 Windows NSIS 集成回归通过；集成在 zh-CN 系统运行仅含 Japanese 的安装器，验证 Unicode 产品名、安装路径、快捷方式参数和卸载元数据。
译文内容、RTL 与不同缩放布局留在 MT-11。

**目标**：完成与当前 Tauri NSIS 通用本地化能力等价的内置语言集合和稳定回退，不要求复制其内部文件结构。

实施范围：

1. 从 `NSIS-R1` 固定的上游快照确定语言集合和语言代码映射。
2. 建立单一规范键集合，生成或校验每种语言的全部 `LangString`，拒绝缺失键、重复键和未知键。
3. 保留自定义语言文件覆盖入口，并定义覆盖优先级、第一语言回退和系统语言不匹配时的行为。
4. 覆盖非 ASCII 产品名、安装目录、快捷方式、参数、注册表和卸载元数据。

自动化要求：

- 所有内置语言逐一编译；
- 键集合、选择器、回退和自定义覆盖有契约测试；
- 至少一个非拉丁文字端到端安装/卸载 Fixture；
- 母语或专业翻译复核记录为外部内容验收，不把机器校验冒充语言质量审校。

完成条件：能力矩阵中的语言项为“已实现”，内容复核边界写入人工文档，默认阶段切到 `NSIS-R4`。

### NSIS-R4：安全边界、缓存完整性与 NSIS 冻结

**状态：已完成并形成冻结基线（2026-09-23，`0.1.0-alpha.31`，`71a5c90`）；
随后以 `0.1.0-alpha.32` 加固 journal 恢复目标，并以 `0.1.0-alpha.33` 处理跨配置恢复及快照内容完整性，验证记入本条 dated 验收记录。
**
构建输入、外部资源、安装快照/恢复、卸载删除树、journal 和工具缓存统一拒绝 symlink、junction 与其他重解析点；安全删除不跟随链接。
工具缓存以固定 ZIP SHA-256 和 ZIP 内逐文件哈希为信任锚，持久化 manifest，并在跨进程锁内恢复缺失、篡改、额外文件、损坏 manifest 或链接污染；并发、Unicode 和长路径已自动验证。
恢复目标由安装器编译时清单授权，journal 中的文件路径、注册表根/view/子键/值名只用于一致性校验，不能把恢复重定向到清单外目标。
不同清单不会自动跨版本恢复：用原安装器 `/RECOVERONLY` 恢复旧状态后再升级。
active 快照内容另由 HKCU/HKLM 哈希锚点约束，恢复卸载器按安装时登记的哈希核验；同用户 currentUser 修改及真实 perMachine ACL 属不同威胁边界，详见人工验收。
ACL、ADS 与任意文件系统元数据完整保真明确不属于通用承诺。
NSIS 冻结后的格式顺序与当前进度见 `docs/roadmap.md`。

**目标**：关闭会影响安全或可恢复性的已知边界，为 NSIS 第一条完整链路建立冻结基线，然后停止无限打磨并进入下一个安装格式 WiX/MSI。

实施范围：

1. 对安装快照、回滚、卸载清单和 journal 中的重解析点/junction/symlink 制定并实现一致策略。
   默认优先“不跟随并安全拒绝或按链接本身处理”，禁止越出产品拥有的路径。
2. 明确 ACL 和 ADS 的产品承诺。
   除非已有真实跨格式需求，不追求任意文件系统元数据的完整镜像；把不支持项写成边界，并确保失败不会导致新旧载荷混合或数据越界。
3. 审计工具缓存的 manifest/hash 校验、并发、损坏恢复、权限、Unicode 和长路径；能自动验证的在本阶段补齐。
4. 运行完整 NSIS 自动化回归，整理最终能力矩阵、已知限制和人工验收队列。
5. 真实重启、UAC、生产证书、真实旧 MSI、ARM64/多宿主、真实 ACL/磁盘耗尽继续按人工文档执行。
   未取得这些外部条件时，准确标为“外部待验收”，不反复阻塞后续格式开发。

完成条件：没有已知的数据越界或恢复主路径空白；缓存命中有完整性依据；所有可本地自动化项目完成；NSIS 能力矩阵冻结；默认阶段切到下一个尚未实现的格式 `WIN-MSI-1`。

**外部待验收实证回填（2026-10-05，`0.1.0-alpha.70`，本机机械审校）**：

- **22 内置语言机械层复审**：22 个 `templates/languages/*.nsh` 各 26 条 `LangString` 键集逐位一致、`LANG_*` 常量与 `NsisLanguageCatalog` 映射全对（Persian→`LANG_FARSI` 为设计映射）、占位符集合（`${PRODUCT_NAME}`/`$InstalledVersion` 等）跨语言一致、零条译文与英文原文相同。
  剩余仅母语/专业审校、RTL 视觉与 DPI 截断（MT-11 真人工项）。

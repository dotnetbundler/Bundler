# NSIS 安装器（Windows）

本文件是 NSIS 后端的唯一文档：能力契约、工具链事实、关键决策与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project-rules.md`。

## 1. 状态与边界

- 第一条完整产品链路已冻结：只接受有明确用例和完成条件的缺陷修复或增强。
- 覆盖：身份/行为契约、工具供应、版本升级、旧 MSI 迁移、事务恢复、自动化协议、文件关联/深链接、快捷方式、本地化、签名、缓存完整性。
- 明确不做：任意应用运行时安装（WebView2、VC Runtime）、Tauri updater 产物、ACL/ADS/稀疏文件等任意文件系统元数据的完整保真。
- 受管文件树（构建输入、资源、安装快照/恢复、journal、工具缓存）统一拒绝 symlink、junction 与其他重解析点；安全清理只删链接本身，不跟随目标。
- 能力面参照：已按固定 Tauri 快照 `tauri-apps/tauri@5d995ed35b029cecd780fdbe614dc6023a89b81b`（`dev` 分支，核对于 2026-09-21）完成 `NsisConfig`/`WindowsConfig` 逐项审计，采用/拒绝结论并入本文决策。

## 2. 工具供应链

- 工具统一来自 [dotnetbundler/NsisToolset](https://github.com/dotnetbundler/NsisToolset)，仓库内嵌 `third_party/nsis/nsis-toolset-3.12-r1.zip`（上游 NSIS 3.12）。
- 布局：`nsis/<版本>/common/` 公共脚本一份 + `hosts/<rid>/` 各宿主编译所需原生文件（win、linux-x86_64、linux-aarch64、macos-x86_64、macos-arm64）。
- Windows 根的 `makensis.exe` 是启动器，`Bin/makensis.exe` 才是编译器（依赖 `Bin/zlib1.dll`）；拆分布局下必须设 `NSISDIR` 指向根，`makensis.cmd` 只做这件事。
- 官方页面已核对 SHA-1 `364fd795b0cafc1fbff3e966f103a8f8fc8fb7f1`、MD5 `757c22153dd8b90f5e297310d9966997`；官方 ZIP 的 SHA-256 `56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f` 是本地计算值，不得表述为上游发布值。
- 每次更换工具集 Release 必须重新核验下载产物、manifest、许可证和各宿主可执行文件。
- `third_party/nsis/COPYING` 与 `THIRD-PARTY-NOTICES.md` 保留在本仓库和发布包中；不因压缩包内已有 COPYING 而删除外层许可归属。

## 3. 原生插件

- 插件统一用 [dotnetbundler/NsisPlugin](https://github.com/dotnetbundler/NsisPlugin) 开发；产物为 `tools/Bundler.Nsis.Plugin` → `third_party/nsis/plugins/x86-unicode/DotNetBundlerNsis.dll`。
- `windows-i686` Native AOT 构建（NSIS 插件宿主是 x86 Unicode ABI）；`installer.nsi` 调用面 26 个导出函数：事务日志、快捷方式生命周期、MSI 迁移辅助、Restart Manager 锁定进程处理、RunAsUser 降权启动、SemVer 比较。
- 插件为 .NET 10 Native AOT 且加载期依赖 UCRT 与 Win8+ API，消费机实际下限为 Windows 10 1607+。
- Native AOT 下不能用 `ComImport`/RCW 操作 Shell Link，须直接调 COM vtable。

## 4. 能力契约

### 4.1 安装与卸载基础行为

- 可选安装目录并记住上次选择；默认安装到独立应用目录。
- 交互安装选择无本产品标记的非空目录时警告；自动安装拒绝覆盖无关非空目录。
- 桌面/开始菜单快捷方式由用户选择；卸载时可选择删除应用数据。
- 未选删除应用数据时只移除构建载荷清单中记录的路径（覆盖同名文件的仍属清单）；选择删除时递归删除安装目录及 `%APPDATA%\<id>`、`%LOCALAPPDATA%\<id>`。
- 运行进程检测经 Restart Manager 按完整路径关闭占用 `$INSTDIR` 下载荷的进程，不误杀其他目录同名进程。

### 4.2 多语言

- 内置 22 种语言：Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian（NSIS 内部映射 `Farsi`）、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian、Vietnamese。
- 分号配置语言列表，第一项为回退；多语言且启用选择器时显示语言选择页。
- 自定义语言文件完整替换同语言内置文件，严格校验缺失、重复、未知键和 `LANG_*` 常量。
- 22 种语言均经真实编译测试、有非拉丁 Unicode E2E；译文内容与 RTL/缩放布局属 `docs/special-acceptance.md` 母语审校。

### 4.3 安装范围

- `currentUser` / `perMachine` / `both` 已实现对应 UAC、注册表视图与 Shell Context 脚本逻辑；真实 HKLM/Program Files 写入与完整 UAC 交互属外部待验收。

### 4.4 版本策略与旧 MSI 迁移

- 版本要求 SemVer 2.0；Windows 文件版本三段核心均限 `0-65535`。
- 交互处理同版本重装/升级/降级；静默同版本原位修复、静默升级先移除旧载荷保留应用数据；默认禁止降级，可显式允许（与 Tauri 默认 `true` 不同，是有意的更安全决策）。
- 旧 MSI 迁移要求准确 ProductCode/UpgradeCode，不按显示名猜测；`LegacyMsiAutoDetect`（默认关）显式启用后按卸载注册项 DisplayName+Publisher 匹配且要求卸载命令含 `msiexec`，枚举 HKCU/HKLM 双视图，与显式 GUID 列表合并去重。
- 旧 MSI 卸载返回码：`0`/`1605` 继续，`1641`/`3010` 置重启标记，`1602` 按用户取消退出，其余非零视为迁移失败回滚新状态。
- 同一 UpgradeCode 多版本并存取最高版本判定，逐个移除全部匹配产品。
- 迁移次序：先卸载全部精确匹配的旧产品，再写入 NSIS 载荷（保留上游先卸载顺序，检测条件更严格）。

### 4.5 生命周期 Hook

- 项目可提供 `.nsh` 宏介入安装前/后、卸载前/后四点；入口保持稳定，Hook 不能绕过目录、所有权与卸载安全规则。

### 4.6 品牌、许可证与元数据

- 安装器/卸载器图标、Header/卸载 Header/Sidebar 图片、License 页（`.txt`/`.rtf`）、Publisher/Description/Homepage/Copyright、产品/文件版本、"应用和功能"卸载元数据。

### 4.7 文件关联与深链接

- 文件扩展名用应用专属 ProgID 并注册"打开方式"与默认应用 Capabilities；不静默抢占用户当前默认应用。
- 自定义 URL 协议可启动应用；卸载只在协议仍指向本次安装目录时删除注册。

### 4.8 Authenticode 签名

- 独立 `DotNet.Bundler.Signing.Windows` 公共 API；支持 PFX/P12 与 Windows `My` 证书指纹；PFX 密码走环境变量名；默认 SHA-256 固定（不为旧算法加开关），支持 RFC 3161 时间戳。
- 签名顺序：临时副本主 EXE → 显式 `SigningFiles` → Bundler 原生插件 → 临时卸载器 → 最终安装器；在工作区副本完成，不改调用方输入或共享缓存；任一步失败删除最终安装器。
- `WindowsExternalCommandSigner` 为 HSM/USB Token/远程 provider 提供入口：可执行文件加独立参数数组（不经 shell），至少一个参数含 `{path}`/`%1`，支持 `{artifactKind}`、`{target}`、`{productName}` 占位；provider 参数与输出不进异常消息。
- 内置实现不依赖 Windows SDK/`signtool.exe`，但要求 Windows 宿主；外部 provider 宿主范围由其命令决定。
- 未配置 signer 时显式签名文件会失败而非静默忽略；`SigningFiles` 只选输入目录内文件，不自动重签第三方件。
- 生产证书链、HSM 与公共时间戳服务验收见 `docs/special-acceptance.md` SA-P-01。

### 4.9 安装器自动化协议

| 参数 | 语义 |
| --- | --- |
| `/S` | 静默模式 |
| `/P` | 被动安装/卸载，只显示进度跳过输入页 |
| `/UPDATE` | 自动更新；无 `/S` 时隐含 `/P`，保留应用数据与已有快捷方式状态 |
| `/NS` | 不创建桌面/开始菜单快捷方式 |
| `/R` | 自动模式成功后以桌面用户令牌启动应用 |
| `/ARGS=<参数行>` | 与 `/R` 配合向应用传参（也兼容无等号形式），不经 shell 拼接 |
| `/D=<目录>` | 安装目录，必须是最后一个参数 |
| `/RECOVERONLY` | 只恢复旧状态（配合原安装器与原 `/D=`） |
| `/DELETEAPPDATA` | 卸载时删除应用数据和完整安装目录 |

稳定退出码：`0` 成功、`1` 用户取消、`2` 一般失败、`3` 参数/目录无效、`4` 版本策略阻止、`5` 无法关闭运行中应用、`6` 清单不同需 `/RECOVERONLY`、`3010` 成功但需重启。

`/R` 实现：非提权用当前令牌 `CreateProcessW`；提权用桌面 Shell 令牌 + `CreateEnvironmentBlock` + `CreateProcessWithTokenW`。
成功回调把 NSIS reboot flag 映射为 `3010`；事务在回调前已提交，`/R` 在需重启时不启动应用。

### 4.10 快捷方式完整性

- 公共 `NsisShortcutConfiguration`：Desktop/StartMenu、Arguments、WorkingDirectory、Icon、AppUserModelId、StartMenuFolder、LegacyProductNames、LegacyMainExecutables。
- 工作目录与图标必须是安装目录内安全相对路径且存在于最终载荷。
- `/UPDATE` 只刷新仍存在且仍由本产品拥有的快捷方式；卸载只删本产品拥有的 `.lnk`，被其他程序接管的保留；改名只能走显式旧名列表迁移。
- Windows 固定项（开始菜单/任务栏 pin）随系统版本与策略变化，不保证自动取消固定，见 `docs/special-acceptance.md`。

### 4.11 安装事务与失败恢复

- 修改旧状态前快照完整安装目录、产品相关注册表项、共享注册表值与新旧名快捷方式；journal 按范围存于 `%LOCALAPPDATA%\DotNetBundler\transactions` 或 `%ProgramData%\DotNetBundler\transactions`，快照完成后才标 active。
- 恢复前逐项比对 journal 快照目标与安装器编译时固定清单（路径、注册表 root/view/subkey/name 任一不符即失败）；恢复只用清单内目标，journal 不能单独授权清单外修改。
- 清单不同返回 `6` 保留 active journal；须用原安装器加原 `/D=` 跑 `/S /RECOVERONLY` 先恢复。更早不支持该开关的原包可按原范围/目录重试让其自动恢复；缺失可信原包时不猜测目标，需人工排障。
- active journal 静态快照树激活时计算 SHA-256 并存入 HKCU/HKLM 独立锚点，恢复前核对内容与结构；校验失败返回 `2` 不执行部分恢复。同用户可同时改 HKCU 锚点与 currentUser journal——这不是对同用户恶意进程的隔离。
- Hook `SetErrors`/`Abort` 触发失败时立即回滚；进程被直接终止保留 active journal，下次同产品安装器启动先恢复再装。
- journal 绑定原安装目录；首次自定义 `/D` 在注册目录前中断，重试须继续提供同一 `/D`。
- 提交点：active journal 原子重命名为 `.committed`；清理失败不回滚已提交安装，下次启动先重试清理 `.committed`。
- 升级路径显式复制旧卸载器到唯一临时文件并同步等待，避免 `Uninstall.exe` 自锁与竞态。
- 载荷提取 `SetOverwrite try` + 检查 error flag：锁定非主程序载荷返回 `2`；交互模式提示关程序重试，静默/被动不弹窗；锁也阻止回滚时保留 journal，释放后下次启动先恢复。
- 快捷方式目录/偏好、卸载信息、文件关联、URL 协议注册均在提交前检查 error flag，失败进入同一回滚路径。
- 快照需接近现有安装目录大小的临时空间；空间不足或遇不可安全复制的重解析点在修改旧状态前失败。

### 4.12 卸载前向恢复

- 卸载用独立 `${PRODUCT_ID}.uninstall` journal，不复用安装回滚快照，不宣称能撤销已删除或 `/REBOOTOK` 排队删除的文件。
- 修改持久状态前保存原安装目录、`/DELETEAPPDATA` 选择与恢复卸载器；恢复卸载器从已安装 `Uninstall.exe` 复制，复制前后与注册项 `BundlerRecoverySha256` 对照，执行前再校验 journal 副本哈希。
- 卸载注册项保留到 finalizing，为下次安装器提供受保护的 `InstallLocation`；恢复插件只接受与该目录一致的 journal。
- 下次安装启动以 `_?=` 直接模式同步运行恢复卸载器，幂等重做所有权安全的删除；返回 `3010` 时新安装停止，避免待删除队列重启后删掉新载荷。
- 卸载 Hook 在 active 恢复时可能重复执行，必须幂等。
- 安装目录内直接启动的 `Uninstall.exe` 外层自复制 launcher 不可靠传播退出码；升级/恢复/重启验收脚本须显式复制卸载器 `_?=` 同步等待，不得把外层 `0` 当完成证据。

### 4.13 锁定载荷策略

锁定载荷采用"安全失败并恢复"，不排入 `MoveFileEx` 的 `PendingFileRenameOperations`，原因：

1. 延迟替换需管理员/LocalSystem 上下文，无法为 `currentUser` 提供一致能力。
2. 调用成功只表示请求写入注册表，不保证重启时操作成功。
3. 共享队列无可安全纳入本事务撤销的机制，直写会竞态破坏其他安装器。
4. 排入后本事务的立即回滚与下次启动恢复无法保证重启后最终状态。

重新设计需同时满足：产品接受提权差异或停掉不提权安装；重启前向恢复有独立持久化状态机；可抛弃 VM 覆盖成功/部分排队/排队失败/崩溃/重启失败/共存等场景。
不影响卸载器已有 `/REBOOTOK`（删除目标无需版本提交语义）。

### 4.14 权限边界

- perMachine 产品目录、事务目录与 journal 树要求所有者与允许 ACE 只属 Administrators/SYSTEM，拒绝预建宽权限目录；最终 ACL 在真实提权安装下的形态仍属 `docs/special-acceptance.md` 待验。
- 目标清单约束独立于 ACL；快照内容与恢复卸载器的提权完整性依赖受保护锚点及目录边界。

### 4.15 压缩与杂项

- `Compression`/`BundlerNsisCompression`：`lzma`（默认）、`zlib`、`bzip2`、`none`，四种均经内嵌 makensis 实际编译验证。
- JSON `BundleConfigurationLoader` 保留文件关联、URL 协议及元数据——CLI 共享入口不静默丢能力。
- `IBundleSigner` 请求携带产物类别、产品名、目标 RID；类别区分主程序/显式 payload/Bundler 原生组件/卸载器/安装器。
- `ToolCacheDirectory`：默认用户级内容寻址缓存可显式覆盖；信任锚为固定 ZIP SHA-256 与逐文件 manifest 哈希，覆盖篡改、额外文件、并发、Unicode 与长路径。

## 5. 外部待验收（详见 `docs/special-acceptance.md`）

- 真实 UAC、perMachine ACL 落位、真实重启 `3010` 链路、退出码 `5` 场景。
- 生产 Authenticode 链（SA-P-01）；真实已发布产品的历史 MSI 迁移输入。
- 22 语言译文母语审校；固定项（pin）差异；锁定载荷在真实重启下的端到端。

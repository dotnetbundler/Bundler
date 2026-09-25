# MSI 能力矩阵（alpha.41；WIN-MSI-7..9 计划）

本表仅描述通用桌面工具的 MSI 用户能力。
“已实现并本机自动验证”只覆盖写明的当前主机和操作；“已实现但外部待验收”表示实现存在，但该能力的某些环境或发布条件尚无实际证据。
WIN-MSI-1..3 的阶段证据见 `docs/msi-roadmap.md` 第 6..8 节，WIN-MSI-4 的复核记录见第 9 节，WIN-MSI-5/6 的 x86/版本生命周期与目录/UI/Feature 证据见第 10 节。
**WIN-MSI-7..9 仍是计划，不能按已支持宣传**；来源和选择见 `docs/msi-tauri-capability-audit.md`。
生产证书、交互 UI、真实重启/锁定文件、干净 Windows/ARM64 宿主及提权安装不能由本机测试推断为通过。

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 独立后端包与直接 API | 已实现并本机自动验证 | 适用，MSI-1；WIN-MSI-2 补齐包消费验证 | 仓库外普通项目只引用打出的 `DotNet.Bundler.Wix` NuGet 包，使用包内 WiX 工具直接生成真实 MSI；不能靠本仓库 `ProjectReference` 或 MSBuild 元包。`tests/Msi.Api.PackageFixture` 已在本机通过，其他构建宿主仍按支持矩阵验收。 |
| 最小安装/卸载 | 已实现并本机自动验证 | 适用，MSI-1；本机 x64 fixture 已验证 | 独立 current-user fixture 静默安装、检查载荷和产品注册，再卸载并检查残留/用户文件；不能仅生成 MSI。其他 Windows/架构及干净宿主待外部验收。 |
| 名称、发布者、描述、`.ico` 图标、默认安装目录 | 已实现并本机自动验证 | 适用，MSI-1 | API/MSBuild 同义映射、数据库和系统显示正确；默认目录按 scope 固定，可选择目录见下表。其他图标输入尚未实现。 |
| 产品身份、版本、组件 | 已实现并本机自动验证 | 适用，MSI-1；MSI-3 增加语言产品线，MSI-4 修正 PackageCode 生成 | 英文历史身份稳定；简体中文使用隔离的 UpgradeCode、ProductCode、组件、安装目录和输出名。PackageCode 由 WiX 每次构建生成，独立构建已验证不同；同一产品版本只发布一份同语言内容，显式 UpgradeCode 由发行方负责跨语言唯一性。 |
| 目标架构 | 已实现但外部待验收 | x64、ARM64，MSI-1/4；x86，WIN-MSI-5 | x86 在当前 Windows 11 x64 宿主经直接 API 与 MSBuild 构建，静态检查 Intel 模板、32 位组件/目录/注册表视图，并真实安装、升级、降级、卸载；x64/ARM64 身份保持不变。其他宿主/目标环境仍待验收。 |
| major upgrade 与降级 | 已实现并本机自动验证 | MSI-2 默认策略；WIN-MSI-5 可选降级 | x64 默认 v1→v2 与降级/同版异包拒绝继续通过；x86 显式允许降级时真实 v2→v1 成功，旧受管资源更新且未知用户文件保留。默认仍拒绝；跨机器同版本唯一性由发行方负责。 |
| 显式 MSI 版本映射 | 已实现并本机自动验证 | WIN-MSI-5 | 仅显式三段有效数值可映射预发布应用版本；第四字段、无定义自动映射和越界值被拒绝。x86 真实映射升级、同 MSI 版本碰撞拒绝已通过；发行方负责跨发布单调分配。 |
| 安装范围与权限 | 已实现但外部待验收 | current user 已实测；per machine 在 MSI-2 已编译/检查数据库 | 两种身份家族、HKCU/HKLM 与目录已静态验证；per-machine UAC/真实安装外部待验收。单包动态切换范围、跨范围自动迁移不支持。 |
| 快捷方式 | 已实现并本机自动验证 | MSI-2 编译期开关；WIN-MSI-6 改为 Feature 内非 advertised 组件 | 显式启用的桌面/开始菜单链接创建、升级、卸载通过；组件化后可在 Feature 级别观察；同一路径被用户替换时 MSI 原生删除可能移除该链接，不承诺内容级所有权；系统固定项不自动取消固定。 |
| 范围内可选安装目录 | 已实现并本机自动验证 | WIN-MSI-6 | `InstallDirectorySelection` 开启 InstallDir UI；静默 `INSTALLFOLDER=` 与交互共用 execute-sequence 范围校验——current-user 限 `%LOCALAPPDATA%` 内子目录、per-machine 限 Program Files 内子目录（x64/x86 根按架构），根本身与范围外/越界值实测返回 1603；升级经 `RegistrySearch` 恢复已选目录。安装时 junction/重解析点检测未实现（原生条件无法判定），属已记录边界。 |
| MSI 交互 UI 与品牌位图 | 已实现但外部待验收 | WIN-MSI-6 | 自定义 dialog 序列（Welcome→[License]→[InstallDir]→VerifyReady→Exit+Browse/InvalidDir/维护）覆盖无许可与有许可两种路由；位图构建时校验 BMP 头与 493x58/503x314 尺寸并拒绝重解析点。真实交互流转、勾选行为、缩放/辅助功能尚未人工执行。 |
| 可选 Feature（PATH、卸载入口） | 已实现并本机自动验证 | WIN-MSI-6 | `AddToPath` 用 `Environment` 表 `[~];[INSTALLFOLDER]` 只增删本产品条目，实测原 PATH 各值保留、卸载后恢复原值；`UninstallShortcut` 在开始菜单生成 `msiexec /x [ProductCode]` 链接。feature 级别选择（`ADDLOCAL`）未实测交互设置。 |
| 安装后启动勾选 | 已实现但外部待验收 | WIN-MSI-6 | ExitDialog Finish 的 `WIXUI_EXITDIALOGOPTIONALCHECKBOX` 条件 DoAction（FileKey、asyncNoWait、Impersonate）才启动主程序；条件排除 `Installed`/`WIX_UPGRADE_DETECTED`，`/qn`/`/passive`/修复/升级无 UI 不触发。真实勾选/未勾选交互行为待人工验收。 |
| 卸载界面元数据（ARP） | 已实现并本机自动验证 | WIN-MSI-6 补齐 | `ARPNOMODIFY`/`ARPCONTACT`/`ARPCOMMENTS`/`ARPURLINFOABOUT`/`ARPPRODUCTICON`/`ARPINSTALLLOCATION`（Type 51 CA 写解析后安装路径）；"更改"按钮被禁用（本 UI 无功能修改流程）。 |
| 文件关联 | 已实现但外部待验收 | MSI-2，本机注册表已实测 | 自身 ProgID、OpenWithProgids、文件及可选 MIME Capabilities 声明与卸载通过；保留既有默认项。默认应用 UI/选择体验外部待验收。 |
| URL 协议 | 已实现但外部待验收 | MSI-2，本机注册表已实测 | 自身 ProgID 与 UrlAssociations 声明/清理通过，不写公共 scheme 根；用户选择默认处理程序及真实协议唤起外部待验收。 |
| payload/最终 MSI 签名 | 已实现但外部待验收 | MSI-3，本机测试证书已验证 | 对主 PE 与显式 SigningFiles 的隔离副本签名，然后签最终 MSI；真实 Authenticode 证书、签名顺序、原件不变和失败清理均有自动测试。生产证书、时间戳与信任链外部待验收。同版本已签包不静默复用。 |
| 语言与交互 UI | 已实现但外部待验收 | MSI-3：`en-US`、`zh-CN` 单语言包 | ProductLanguage/代码页/升级身份隔离及中文包与英文包并存安装/卸载已验证；应用提供 RTF 许可时启用 WiX 最小 UI，两种语言的数据库资源已检查。未提供许可时使用基础 Windows Installer UI。真实交互显示、母语审校和辅助功能外部待验收；不承诺 NSIS 全语言等价。 |
| 静默/被动、退出码 | 已实现但外部待验收 | MSI-3，本机已验证常规路径 | 原生 msiexec `/qn` 安装/修复、`/passive` 安装/卸载返回 0；损坏包返回原生 1619/1620，延迟故障返回 1603。重启所需 3010 未在本机触发，留待 VM；不复刻 NSIS 码。调用方显式使用 `/norestart` 控制系统重启。 |
| 失败、回滚、重启 | 已实现但外部待验收 | MSI-3，本机验证受限故障 | 仅在测试包副本加入延迟失败动作，文件复制后故障返回 1603，原生事务清除产品注册和托管文件；生产包没有自定义动作。损坏包无注册残留。锁定文件、磁盘故障、真实重启与重启后状态留在专用 VM。 |
| 修复/维护模式 | 已实现但外部待验收 | MSI-3，本机静默修复已验证 | 删除受管理文件后 `msiexec /fomus` 从可用源恢复，未知用户文件保留；缺失源和交互维护界面外部待验收。 |
| 离线构建与工具缓存 | 已实现并本机自动验证 | 适用，MSI-1；MSI-4 复核实际包 | 官方归档 SHA-256/逐文件哈希、许可证、缓存污染、本地包源与隔离缓存及 `--no-restore` 构建已验证；物理断网的干净宿主仍待外部验收。 |
| 任意应用运行时自动部署 | 明确不支持 | 不适用，明确不支持 | Bundler 不下载或安装 WebView2、VC Runtime、.NET Runtime 等第三方运行时；未来专家模式中的用户自备逻辑不构成本产品内建能力。 |
| 受管模式的任意脚本/目录全删 | 明确不支持 | 明确不支持 | 不为表面对齐引入未定义所有权或破坏 MSI 回滚的动作。WIN-MSI-8 的显式专家模式若启用原始 WiX，则用户自备逻辑不享有本行的受管安装保证。 |
| CLI | 未实现（MSI 路线外） | MSI 路线之外 | 所有计划格式完成后再按 `CLI-C1` 产品化。 |

## 尚未实现的 Tauri 通用能力补齐计划

下表是**尚未实现的计划**；每阶段每项功能须有新增自动化、独立后端包消费、MSBuild 映射及当前机器可安全执行的真实 MSI 生命周期测试。
缺少其他宿主/提权/生产证书不阻塞本机可完成的开发，也不增加对这些环境的支持声明。

| 用户能力 | 当前差距 | 计划阶段和完成条件 |
| --- | --- | --- |
| 更多单语言 MSI 与调用方翻译 | 仅 `en-US`/`zh-CN` | WIN-MSI-7：官方 WiX 3.14.1 可供资源核查后确定内置集；每 locale 独立身份、产物和生命周期，自定义翻译严格校验，现有语言 GUID 不变。 |
| 图标输入与 FIPS 构建 | MSI 图标仅 `.ico`；无 FIPS 选项 | WIN-MSI-7：安全转换实际支持的图标格式并测试；WiX 编译和链接都按所选 FIPS 选项执行，不宣称未经验证的系统级认证。 |
| 声明式 WiX 扩展 | 无 fragments、引用或 merge 支持 | WIN-MSI-8：常规受控 fragments/引用经白名单、身份和所有权检查；原始 merge 与完整模板只在显式专家模式，测试产物和失败清理，区分用户自备逻辑的责任。 |
| Tauri 通用 MSI 再审计 | WIN-MSI-4 仅审当时已承诺能力 | WIN-MSI-9：固定上游快照逐项分类、本机测试/包供应/文档一致性完成后再冻结；不把 Tauri 专属 updater 或应用运行时部署混入 MSI。 |

## 已验证的平台范围

| 构建宿主/安装目标 | x86 current-user MSI | x86 per-machine MSI | x64 current-user MSI | x64 per-machine MSI | ARM64 MSI |
| --- | --- | --- | --- | --- | --- |
| 本机 Windows 11 Pro build 26200 x64，非干净环境 | 独立包 API/MSBuild 构建与真实安装、映射版本升级、降级拒绝/允许、同版碰撞拒绝、卸载通过 | 仅生成并检查 32 位数据库；未执行 UAC/提权安装 | 直接 API 与 MSBuild 构建、安装、升级、修复、失败回滚、卸载通过 | 仅构建并检查数据库；未执行 UAC/提权安装 | 仅在 x64 宿主生成并检查 ARM64 包；未在 ARM64 用户端安装 |
| 干净 Windows 10/11 x86、x64 或 ARM64 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 |

Windows 7 上 WiX 3.14.1 自身的 Framework 零环境边界见 `docs/msi-roadmap.md` 第 2 节。
上表是实测证据，不把单台开发机的结果推广到所有 Windows 11 设备。
外部待验收的组合继续按 `docs/msi-manual-testing.md` 和 `docs/msi-open-items.md` 保存；`alpha.37` 的 x64/ARM64 身份基线继续受固定测试向量保护。

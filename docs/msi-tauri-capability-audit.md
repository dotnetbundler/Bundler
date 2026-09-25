# Tauri 通用 MSI 能力审计与补齐边界（2026-09-25 快照；WIN-MSI-6 已实施）

本文件记录上游参照、当前实现事实和已确认的后续产品选择。
WIN-MSI-5/6/7 已完成当前 Windows 11 x64 本机范围；WIN-MSI-8..9 尚未实现。
2026-09-26 当前分支为 `msi-development`，包版本为 `0.1.0-alpha.41`。
原 WIN-MSI-4 是既有 MSI 身份基线，不代表与 Tauri 通用 MSI 能力等价。
应用运行时依赖的自动发现、下载和安装继续不做。
无现成测试环境的验收项沿用 MSI 专用人工/外部清单，不充当本轮开发阻塞或已通过证据。

## 上游快照与方法

2026-09-25 用 `git -c http.sslBackend=openssl ls-remote https://github.com/tauri-apps/tauri.git refs/heads/dev` 取得 Tauri `dev` HEAD `7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf`。
以下以该提交的 [WiX 配置定义](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-utils/src/config.rs)和 [MSI 模板](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/windows/msi/main.wxs)为固定比较点；
用户文档参见 [Tauri WiX 配置](https://v2.tauri.app/reference/config/#wixconfig)及 [Windows 安装器](https://v2.tauri.app/distribute/windows-installer/)。
`dev` 后续会变化，实施某阶段前可重新核对并记录新快照，不能把旧表当成永远最新。
只借鉴用户可观察能力，独立设计和编写本项目代码与文案，不复制 Tauri 模板或翻译。

分类中的“已有”依据 `docs/msi-capability-matrix.md` 与仓库代码；“计划”是未实现；“另立产品路线”不属于本轮 MSI 后端能力。
Tauri 的通用配置字段不一定对 MSI 有独立含义，优先比较用户结果而非字段名。

| Tauri 用户能力或配置 | 当前 Bundler MSI 事实 | 选择与阶段 |
| --- | --- | --- |
| 名称、发布者、描述、主页、图标、许可、普通资源与额外可执行文件 | 名称/发布者/描述/主页、`.ico`、调用方预备的文件树/资源、RTF 许可及额外签名文件已有；不按 Rust target triple 自动寻找 sidecar | 保留通用目录输入；WIN-MSI-6 已补齐有意义的卸载信息字段（`ARPNOMODIFY`/`ARPCONTACT`/`ARPINSTALLLOCATION` 等）；WIN-MSI-7 已完成 `.ico` 严格校验并把图标接到全部受管快捷方式。`category` 等没有明确 MSI 用户结果的字段不伪造映射。 |
| 文件关联、URL 协议、快捷方式 | 自身候选注册及开始菜单/桌面快捷方式已有；不抢占系统默认 | 保留所有权边界；WIN-MSI-6 已实现 Feature 级可选快捷方式与受管卸载入口（`UninstallShortcut`），生命周期测试通过。 |
| x86、x64、ARM64 安装目标 | x64/ARM64 既有包和 x86 alpha.40 均可生成；x86 在当前 x64 Windows 已真实安装 | WIN-MSI-5 已完成 x86 独立身份、32 位数据库/目录/注册表视图和 x86 API/MSBuild 生命周期；构建宿主仍限定 Windows，其他宿主/原生目标端待验收。 |
| 稳定 UpgradeCode、版本覆盖、允许降级 | 显式三段 MSI 版本映射和显式降级已实现；默认仍阻止降级/同版本异包 | WIN-MSI-5 已通过固定 identity vector、版本边界、同 MSI 版本碰撞、默认拒绝和显式允许降级；Windows Installer 忽略第四版本字段，故仍不接受四段版本或无定义自动映射。[微软 ProductVersion 规则](https://learn.microsoft.com/en-us/windows/win32/msi/productversion)、[WiX MajorUpgrade](https://docs.firegiant.com/wix3/xsd/wix/majorupgrade/)。 |
| 安装目录选择、功能选择、安装后启动 | 范围内目录选择、Feature 级可选快捷方式/PATH、仅交互勾选启动均已实现 | WIN-MSI-6 已实现：current-user 限 `%LOCALAPPDATA%` 子目录、per-machine 限 Program Files 子目录，静默 `INSTALLFOLDER=` 同校验；勾选后用户会话启动，静默/被动/修复/升级不启动，不在提权服务上下文执行。 |
| 安装 UI 横幅和对话框图、许可页面 | 提供 RTF 时使用 WiX 最小 UI 或自定义含许可页序列；自定义位图已实现 | WIN-MSI-6 已实现：构建时校验 BMP 头与 `493x58`/`503x314` 尺寸、拒绝重解析点，经 `WixVariable` 注入；无许可时走无许可页的自有 dialog 序列。真实显示/缩放待人工验收。 |
| PATH 环境集成 | `AddToPath` 已实现（显式启用） | WIN-MSI-6 已实现：按安装范围用 Windows Installer Environment 表附加/卸载本产品条目（`[~];[INSTALLFOLDER]`），实测不覆写既有 PATH 值。[微软 Environment 表](https://learn.microsoft.com/en-us/windows/win32/msi/environment-table)。 |
| 多语言分别生成及调用方自定义翻译 | 38 语言静态表（WiX 3.14.1 内嵌 40 项中实测可编译者）、一次构建每语言一个独立 MSI、调用方 `.wxl` 按键覆盖 | WIN-MSI-7 已实现；每种语言独立稳定身份、输出名和安装目录，旧英语/中文测试向量不变；母语审校外列。 |
| FIPS 构建选项 | `FipsCompliant`/`BundlerWixFipsCompliant` 仅向 `candle` 透传 `-fips` | WIN-MSI-7 已实现参数透传；选项通过不等于环境或产品取得合规认证，FIPS 策略宿主实测外部待验收（MSI-OI-13）。 |
| WiX fragments、组件/功能引用、merge module、完整模板 | 无；当前产品由声明式后端生成 WiX | WIN-MSI-8：常规模式支持受控声明式 fragments/引用；原始 merge module 和完整模板替换只进入**显式专家模式**。详见下节。 |
| 证书选择、签名命令、时间戳、摘要配置 | 现有 PFX/证书存储指纹、外部签名命令、SHA-256 与 RFC 3161 时间戳路径 | 保留已验证的签名顺序与失败清理；WIN-MSI-9 对照用户结果审计。不为了字段对齐提供弱摘要算法。生产证书验收仍按 MSI 专用外部清单。 |
| WebView2、VC++、.NET 等应用运行时部署 | 无 | 产品边界不变：不自动发现、下载、安装、升级或卸载任意应用运行时。用户可将**已准备好的普通文件**作为载荷，但 Bundler 不管理其先决条件。[Tauri WebView2 模式](https://v2.tauri.app/distribute/windows-installer/#webview2-installation-options)。 |
| Tauri elevated update task、updater 专属签名包 | 无 | 与 Tauri 更新协议/运行时绑定，不通过 MSI 模板字段伪装成通用更新器；如要支持应用更新，另立跨格式协议、密钥与回滚路线，不列入 WIN-MSI-5..9。 |

## WiX 扩展的已确认选择

1. **常规模式**：只接收可检查所有权、身份、作用范围与回滚语义的声明式扩展。
   解析来源文件、拒绝重解析点和越界路径；
   禁止更改 Bundler 管理的 ProductCode/UpgradeCode/PackageCode、既有组件 key path 与产品目录；
   拒绝自定义动作、任意执行脚本、全目录删除及未受控外部工具下载。
   允许的元素和引用在 WIN-MSI-8 前写成精确白名单。
   不是所有合法 WiX XML 都会在常规模式获准。
2. **显式专家模式**：允许用户自备整份 `.wxs` 或原始 merge module。
   此时扩展内容可能创建服务、执行自定义动作或改写系统状态；Bundler 只对自身工具供应、输入路径、编译结果、约定的产品身份/版本/范围、签名和失败清理作可验证保证，**不为用户自备安装逻辑保证原生事务、所有权或运行时依赖边界**。
   须在 API、MSBuild 属性、示例与文档中显著区分，默认关闭。
   专家内容不得被描述成 Bundler 内建的运行时依赖部署功能。
3. 两种模式均保留 `-wx` 与适用 ICE 验证。
   用户内容及引用文件的哈希进入构建指纹；重复标识、未解析引用、身份冲突和未生成 MSI 要失败且不发布半成品。
   新增 WiX 官方文件先重新核对来源、MS-RL 对应源码、哈希和 NuGet 体积；不使用付费扩展或服务。

## 与实施路线的关系

阶段目标、逐层测试和退出条件见 `docs/msi-roadmap.md` 第 10 节。
计划状态不得写成已支持；公开示例必须在能力实现阶段同步更新。
每轮包内容变化迭代 `BundlerPackageVersion`，示例应用版本保持稳定。
WIN-MSI-5/6 的实施证据见 MSI 路线第 10 节、`VerifyWinMsi5.ps1` 与 `VerifyWinMsi6.ps1`；WIN-MSI-9 才执行新的完整 Tauri 通用 MSI 对照审计与再冻结。

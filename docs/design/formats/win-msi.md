# MSI 安装器（Windows，WiX 3.14.1）

本文件是 MSI 后端的唯一文档：选型与封存裁决、工具链事实、MSI 语义契约、能力状态与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project/project-rules.md`。

## 1. 状态与选型

- MSI alpha 基线冻结于 `0.1.0-alpha.43`；后续只接受缺陷修复或有明确条件的增强。
- 选型 **WiX 3.14.1**，仅 Windows 宿主构建 MSI，不要求非 Windows 宿主产出。
- **2026-10-05 封存裁决：v3 维持现状不再复议**——v4/v5 需 .NET 8+ 运行时（Windows 预载仅 .NET Framework，自包含体积不合"工具随包"定位），纯托管写出器工程量过大，捆绑 v3；上游出具体安全/兼容事故时由用户另行指示。
- 产品只打包调用方已准备的文件；不内建 WebView2/VC Runtime/.NET Runtime 的发现、下载或安装；不在 MSI 上复刻 NSIS journal 或任意脚本自定义操作。
- `WixToolset.Sdk/5.0.2` 只出现在 NSIS 迁移测试的旧 MSI fixture 中，不构成选型。

## 2. 工具供应链与"零环境"边界

- 只从 WiX 官方 `wix3141rtm` release 取二进制归档，固定精确版本与文件清单；归档 SHA-256、内文件哈希与来源固定在仓库（`third_party/wix/`），不在运行时下载。
- 许可证为 Microsoft Reciprocal License：分发含 WiX 代码的文件须向接收者提供对应源码并保留版权/专利/商标归属；捆绑清单变更必须重审（`third_party/wix/msi-wix-provenance.md`）。
- 包体积约 14.4 MB（15 个随包文件）已获接受；明显变化需重新测量记录。
- `candle.exe`/`light.exe`/`wix.dll` 目标框架 `.NETFramework,Version=v4.5`，两个 EXE 为 32 位进程；该需求不传给 MSI 最终用户。
- 工具自身零环境边界（只评价 WiX 编译器，不含调用方依赖）：
  - Windows 7 SP1 未预装 Fx4.5，不满足严格零安装；
  - Windows 10+ 预装 Fx4.6+/4.8.x，推断可解压即用，干净 VM 实测见 `docs/project/special-acceptance.md`；
  - WiX v6 起的 Open Source Maintenance Fee 不适用于 3.14.1。

## 3. MSI 语义契约

1. **身份**：`Identifier` + scope + 架构构成独立产品线；UUIDv5 由规范化 `Identifier+scope+arch` 推导稳定 `UpgradeCode`，由产品线+三段 MSI 版本+`ProductLanguage` 推导 `ProductCode`。major upgrade 换 ProductCode；同版同语言拒绝不同载荷重发；每次内容不同生成新 `PackageCode`。显式 UpgradeCode 仅限有证据的历史产品迁移，须查 GUID 冲突与所有权。
2. **版本**：MSI `ProductVersion` 只比较前三段；含第四段、预发布或 build metadata 的版本明确拒绝（显式三段映射除外，第四字段/越界/无定义自动映射均拒）；稳定版本须单调增加；默认 major upgrade 且阻止降级；同版本重打包不作升级路径。
3. **范围**：current user 与 per machine 是**两个独立产物配置**，默认 current user；不承诺同包安装时切换范围；跨范围升级不自动迁移。x64/ARM64/x86 各自独立产物与身份。
4. **目录/组件**：默认根为 `%LOCALAPPDATA%\Programs\<产品>` 与对应架构 Program Files；可选目录经范围、绝对路径、所有权校验（current-user 限 `%LOCALAPPDATA%` 内子目录、per-machine 限 Program Files 内子目录，越界实测 1603）；组件稳定 GUID/key path/归属，卸载只删本产品资源，默认保留用户数据和未知文件。
5. **事务**：用 Windows Installer 原生安装/回滚/修复/卸载；优先声明式 WiX 表与标准动作，自定义操作仅用于确有必要且无原生替代者；损坏包、锁文件、重启、修复按 MSI 原生语义，不套用 NSIS 退出码。

## 4. 能力状态

已实现并本机自动验证：

- 独立 `DotNet.Bundler.Wix` 包消费（仓外 Standalone fixture 真实产包）；最小安装/卸载（current-user x64/x86 实测）。
- 元数据/图标/默认目录；产品身份、版本、组件（简体中文为隔离产品线；PackageCode 逐次构建不同；同一版本只发布一份同语言内容）。
- major upgrade 与可选降级（显式允许时 x86 v2→v1 实测）；显式三段版本映射。
- 可选安装目录（`InstallDirectorySelection` + 静默 `INSTALLFOLDER=`，升级经 `RegistrySearch` 恢复）；junction 不经解引用、写入透到真实目标、卸载保留 reparse point——属已记录边界不再扩展。
- 可选 Feature：`AddToPath`（`Environment` 表 `[~];[INSTALLFOLDER]` 只增删本产品条目，实测卸载恢复原 PATH）、`UninstallShortcut`（`msiexec /x` 链接）。
- ARP 元数据（`ARPNOMODIFY`/`ARPCONTACT`/`ARPCOMMENTS`/`ARPURLINFOABOUT`/`ARPPRODUCTICON`/`ARPINSTALLLOCATION`；"更改"禁用）。
- FIPS 选项：仅向 `candle.exe` 透传 `-fips`；已在 FIPS 策略宿主实证（4688 捕获实参、关闭时 CNDL0308 拒构）——不构成 FIPS 认证声明。
- 前 NSIS 安装目录延续：`RegistrySearch` 读旧 NSIS 卸载键 `InstallRoot`（双视图），范围内且 Bundler 未注册自己 `InstallDir` 时设为默认 `INSTALLFOLDER`；三段真实场景已验。
- 离线构建与工具缓存：官方归档 SHA-256/逐文件哈希、许可证、缓存污染、`--no-restore` 全验。
- 声明式 WiX 扩展（`ExtensionFragmentFiles` 白名单 `.wxs` fragment：Component/Directory/File/RegistryKey/Environment/Condition/Shortcut/Property 等核心元素 + `ExtensionIdPrefix` + 显式 Feature 引用注入；禁自定义动作/序列表/Binary/身份/UI/升级元素）与专家模式（`ExpertTemplateFile` 整份替换 + `ExpertMergeModuleFiles` 直传 + `MsiIdentityProbe` 回读强制 Bundler 身份；专家产物不享有受管安装保证）。
- CLI：`bundler bundle --formats msi`，仅 Windows 宿主可产。

已实现但外部待验收（详见 `docs/project/special-acceptance.md`）：

- x64/ARM64/x86 在干净宿主与其他 Windows 版本的组合；per-machine 真实 UAC 安装。
- 自定义交互 UI/位图（构建期校验 BMP 头与尺寸、拒重解析点已实现；真实交互流转/缩放/辅助功能未验）。
- 安装后启动勾选（`WIXUI_EXITDIALOGOPTIONALCHECKBOX` 条件 DoAction；真实勾选行为未验）。
- 文件关联与 URL 协议（注册表声明已验；默认应用选择体验与真实唤起未验）。
- 签名（隔离副本签主 PE/显式 SigningFiles/最终 MSI 全链已验；生产证书与信任链未验，同版本已签包不静默复用）。
- 语言：38 语言静态表（`WixUIExtension.dll` 内嵌 40 culture 经逐个 `light` 实编译筛出，排除 `hi-IN`/`kk-KZ`）、每语言独立 MSI 产物、调用方 `.wxl` 覆盖、ja-JP 并存实测；母语审校/交互未验，不承诺 NSIS 全语言等价。
- 静默/被动/退出码（`/qn`、`/passive`、1619/1620/1603 已验；3010 重启场景未触发；`/norestart` 由调用方控制）。
- 失败回滚（延迟故障 1603 原生事务清注册/受管文件已验；锁定文件/磁盘故障/真实重启在专用 VM）。
- 修复/维护（`msiexec /fomus` 静默修复已验；缺失源与交互维护未验）。
- 明确不支持：任意运行时自动部署；受管模式的任意脚本/目录全删（专家模式自备逻辑不享有受管保证）。

## 5. 已验证平台范围

| 宿主/目标 | 实测范围 |
| --- | --- |
| 本机 Windows 11 x64（非干净） | x86/x64 current-user 构建、真实安装、映射升级、降级拒/许、同版碰撞拒、卸载；x86/x64 per-machine 仅生成并检查数据库（未提权安装） |
| `windows-11-arm` CI | ARM64 MSI 真机安装/覆盖装/修复/卸载（verify 绿） |
| 干净 Windows 10/11 各架构 | 未执行，属外部待验收 |

不把单台开发机结果推广到所有 Windows 11 设备；`alpha.37` x64/ARM64 身份基线受固定测试向量保护。

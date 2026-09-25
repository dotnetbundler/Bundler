# NSIS 上游参考与取舍

DotNet.Bundler 不复制 Tauri 的实现或运行时体系。项目只对齐适用于通用桌面打包器的用户能力，通过自身的 Core、格式后端、MSBuild 适配层、直接 API 和未来正式 CLI 实现。产品边界、能力状态、阶段顺序和审计方法见 `docs/roadmap.md`；本文记录固定的上游证据与 NSIS 决策。

参考快照：`tauri-apps/tauri` 仓库 `dev` 分支，提交 `5d995ed35b029cecd780fdbe614dc6023a89b81b`，核对于 2026-09-21。相关上游路径：

- `crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi`
- `crates/tauri-bundler/src/bundle/windows/nsis/utils.nsh`
- `crates/tauri-bundler/src/bundle/windows/nsis/mod.rs`
- `crates/tauri-bundler/src/bundle/windows/sign.rs`
- `crates/tauri-bundler/src/bundle/settings.rs`
- `crates/tauri-utils/src/config.rs`

## 决策

### 对齐能力

参考 Tauri 配置时先区分三类：元数据、美术资源、安装范围、关联、语言、签名、自定义模板及钩子属于适用时应提供等价能力的通用用例；NSIS 压缩和 WiX 标识符只属于对应格式后端；WebView2 安装策略和 VC 运行时部署属于 Tauri 应用运行时行为，不自动纳入 Bundler。上游字段证明用例存在，不要求复刻名称、类型或实现。逐字段审计见 `docs/nsis-capability-matrix.md`。

### 压缩

独立 API 和 MSBuild 适配层支持 LZMA、ZLIB、BZIP2 和不压缩，默认 LZMA。生成脚本对三种算法使用 `SetCompressor /SOLID`，关闭压缩时使用 `SetCompress off`。

### 应用运行时依赖

当前路线不内置通用运行时管理。Bundler 不自动发现、下载、检查版本、安装、修复或卸载任意应用运行时及先决条件。Tauri 的 WebView2 控制与其应用体系相关，不能直接成为框架无关的契约。调用方仍可把准备好的运行时文件放入载荷，或使用已说明的生命周期钩子与自定义模板。生成安装包所需的编译器和工具集属于格式后端，与应用运行时部署分开。

### 签名

Authenticode 是打包与分发能力。现有流水线依次签署主程序及明确选定的载荷文件的暂存副本、Bundler 自有 NSIS 插件的私有副本、导出的卸载器以及最终安装器。它不修改调用方输入目录，也不自动重新签署任意第三方文件。内置 PFX／证书存储区提供程序继续可用；基于参数数组的外部提供程序支持 HSM、云端、令牌或远程签名，不在 NSIS 后端重复签名策略。

### 安装范围

公开模式为 `currentUser`、`perMachine` 和 `both`。`both` 使用标准 NSIS `MultiUser.nsh`；固定模式显式设置执行级别和 Shell 上下文。x64 与 arm64 目标安装器使用 64 位注册表视图。上游初始化顺序及 `MULTIUSER_USE_PROGRAMFILES64` 行为用于核实含糊的 NSIS 细节。Bundler 保留已有的 `%LOCALAPPDATA%\Programs` 当前用户默认目录，不复制 Tauri 的目录布局。

### 美术资源与元数据

对照 Tauri 字段检查用户能力是否遗漏，但 Bundler 仍将其表达为普通打包元数据和 NSIS 专用美术资源覆盖。卸载器头图默认沿用安装器头图；安装器与卸载器图标可分别设置。静态资源不复制 Tauri 的运行时行为。

### 生命周期钩子

使用上游模板中四个可选的 `NSIS_HOOK_*` 宏名，因为调用位置明确，且该约定已在 NSIS 打包生态中公开。只采用名称和生命周期边界；钩子内容由用户提供 NSIS 代码。

### 旧 MSI 迁移

已审查 Tauri 按 `DisplayName` 和 `Publisher` 扫描的方式，但不同产品可以合法共用这两个值，因此不采用。Bundler 要求显式给出旧 MSI 的 ProductCode 或 UpgradeCode GUID，并由随包原生插件调用 Windows Installer API 查询。迁移先卸载全部精确匹配，再写入 NSIS 载荷。保留上游先卸载的顺序，但检测条件更严格。

### 文件关联与深层链接

采用 Tauri 卸载时检查深层链接所有权的做法，防止删除后来由其他应用接管的协议。不直接指定扩展名的默认 ProgID：现代 Windows 要求应用注册为候选项，由用户决定有效默认值。Bundler 注册应用专有的版本化 ProgID、`OpenWithProgids`、Capabilities 和 `RegisteredApplications`，随后刷新 Shell 关联缓存。自定义 URL 协议另注册直接协议键，使新装协议立即可以启动。

### 本地化

固定提交中 `nsis/mod.rs` 的 `get_lang_data` 匹配表给出 Bundler 采用的 22 种语言快照：Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian、Vietnamese。这些是公开配置名，不代表复制 Tauri 文件布局或译文。

NSIS 3.12 将波斯语编译器语言称为 `Farsi`，并提供 `LANG_FARSI`；Bundler 保留面向用户的 `Persian`，内部作映射。每份内置或自定义语言文件必须恰好包含该语言的规范 Bundler 消息键。选中的自定义文件整体替换内置文件，不做部分合并。配置顺序保留，第一种语言在系统 UI 语言未被选中时作为 NSIS 回退语言。机器验证覆盖结构、编译及 Unicode 传输；母语审校与 UI 人工验收仍属 MT-11。

## 尚需外部验证

需要凭据、生产标识或仓库当前不具备的平台条件的测试见 `docs/nsis-open-items.md`。

# NSIS 与 Tauri bundler 能力矩阵

> 审计日期：2026-09-23
> Tauri 仓库：`tauri-apps/tauri`
> 固定 commit：`5d995ed35b029cecd780fdbe614dc6023a89b81b`
> 本项目 NSIS 冻结起点：`71a5c90 feat(nsis): 冻结安全打包基线`；当前提交以 Git HEAD 为准

本文档记录 `NSIS-R1` 的逐项审计结果。
目标是对齐适用于通用桌面打包器的用户能力，不复制 Tauri 字段、Rust 数据模型或 runtime 部署逻辑。
状态定义见 `docs/development-rules.md`，产品边界与格式顺序见 `docs/roadmap.md`，NSIS 阶段证据见 `docs/nsis-roadmap.md`。

上游证据：

- `crates/tauri-utils/src/config.rs`：`BundleConfig`、`WindowsConfig`、`NsisConfig`；
- `crates/tauri-bundler/src/bundle/windows/nsis/mod.rs`；
- `crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi`；
- `crates/tauri-bundler/src/bundle/windows/sign.rs`。

## 1. `NsisConfig` 逐项矩阵

| Tauri 能力 | DotNet.Bundler 等价入口 | 状态 | 决策与验证 |
| --- | --- | --- | --- |
| `template` | `NsisBundlerOptions.TemplatePath` / `BundlerNsisTemplate` | 已实现 | 直接 API 与 MSBuild 共用同一模板渲染器；未知模板变量会失败 |
| `headerFile` | `HeaderFile` / `BundlerNsisHeaderFile` | 已实现 | `.bmp` 校验和渲染契约测试 |
| `sidebarFile` | `SidebarFile` / `BundlerNsisSidebarFile` | 已实现 | 同时用于安装/卸载欢迎与完成页 |
| `installerIconFile` | `InstallerIconFile` / `BundlerNsisInstallerIconFile` | 已实现 | 可回退到通用图标列表中的首个 `.ico` |
| `uninstallerIconFile` | `UninstallerIconFile` / `BundlerNsisUninstallerIconFile` | 已实现 | 可独立设置；默认回退到安装器图标 |
| `uninstallerHeaderFile` | `UninstallerHeaderFile` / `BundlerNsisUninstallerHeaderFile` | 已实现 | 默认回退到安装器 Header |
| `installScope` | `InstallScope` / `BundlerNsisInstallScope` | 已实现，外部待验收 | `currentUser`、`perMachine`、`both`；真实 UAC 见 MT-01/02 |
| `languages` | `Languages` / `BundlerNsisLanguages` | 已实现 | 内置固定快照中的 22 种语言；第一项在系统语言不匹配时回退；`Persian` 内部映射 NSIS `Farsi` |
| `customLanguageFiles` | `CustomLanguageFiles` / `BundlerNsisLanguageFile` | 已实现 | 自定义文件完整替换同一已选语言；严格拒绝缺失、重复、未知键和错误语言常量 |
| `displayLanguageSelector` | `DisplayLanguageSelector` / `BundlerNsisDisplayLanguageSelector` | 已实现 | 仅在多语言且显式启用时显示；单语言非系统语言已完成 Windows 静默安装回归 |
| `compression` | `Compression` / `BundlerNsisCompression` | 已实现 | `lzma`、`zlib`、`bzip2`、`none`；脚本渲染和四种实际编译均有自动化测试 |
| `startMenuFolder` | `Shortcuts.StartMenuFolder` / `BundlerNsisShortcutStartMenuFolder` | 已实现 | 本项目还提供所有权安全的创建、更新、迁移与删除 |
| `installerHooksFile` | `InstallerHooksFile` / `BundlerNsisInstallerHooksFile` | 已实现 | 四个 `NSIS_HOOK_*` 生命周期点；Hook 失败接入事务/前向恢复 |
| `minimumWebview2Version` | 无 | 不适用 | Tauri runtime 专属；本项目不安装或升级任意应用运行时 |

结论：适用于通用 NSIS 后端的小型配置、签名和本地化能力均已收口。
翻译内容的母语/专业审校属于 MT-11，不把自动结构校验冒充内容审校。

## 2. `WindowsConfig` 逐项矩阵

| Tauri 能力 | DotNet.Bundler 等价入口 | 状态 | 决策与后续 |
| --- | --- | --- | --- |
| `digestAlgorithm` | 内置 Authenticode 固定安全默认 SHA-256；外部 provider 自行控制 | 已实现，方案不同 | 不为旧算法增加公共开关；特殊发布策略使用外部 provider |
| `certificateThumbprint` | `WindowsAuthenticodeSigningOptions.CertificateThumbprint` / MSBuild 同名能力 | 已实现 | 证书选择和完整 staged payload/插件/卸载器/安装器链路均有自动化 |
| `timestampUrl` | `TimestampUrl` / `BundlerWindowsSigningTimestampUrl` | 已实现，外部待验收 | 内置实现使用 RFC 3161；生产服务验证见 MT-04 |
| `tsp` | 内置实现固定 RFC 3161 | 已决策，方案不同 | 不增加旧 Authenticode timestamp 协议开关；确有遗留 provider 时走外部命令 |
| `webviewInstallMode` | 无 | 不适用 | Tauri runtime 专属运行时部署 |
| `allowDowngrades` | `AllowDowngrades` / `BundlerNsisAllowDowngrades` | 已实现、默认不同 | 本项目默认禁止降级；这是更安全的明确产品决策，不复制 Tauri 的 `true` 默认值 |
| `minimumWebview2Version` | 无 | 不适用 | 同上，不承担 WebView2 生命周期 |
| `wix` | `DotNet.Bundler.Wix` | 已实现 | 属于独立安装格式，不混入 NSIS 配置 |
| `nsis` | `NsisBundleConfiguration` | 已实现 | 本项目通过强类型后端配置而非嵌套 Tauri JSON 模型组织 |
| `signCommand` | `WindowsExternalCommandSigner` / `BundlerWindowsSigningCommand` | 已实现，外部待验收 | 参数 Item 支持安全占位符；Fixture 验证替换、退出码与错误脱敏，真实 HSM/云服务见 MT-04 |
| `bundleVCRuntime` | 无 | 不适用 | Tauri 构建/runtime 约定；调用方可把已准备文件放入输入目录或资源，不由 Bundler 发现/下载依赖 |

## 3. 通用 `BundleConfig` 中与 NSIS 有关的能力

| Tauri 能力 | DotNet.Bundler 方案 | 状态/决策 |
| --- | --- | --- |
| `active` | `BundlerEnabled` 属于 MSBuild 入口；直接 API 是否调用由调用方决定 | 已实现于入口层，不进入后端模型 |
| `targets` | `BundleTargetConfiguration.Formats` + Core planner/backend preflight | 已实现；当前只有 NSIS 后端完成 |
| `createUpdaterArtifacts` | 无 | 不适用当前通用安装器契约；Tauri updater 产物依赖其 updater 协议，未来若有跨框架真实需求需单独提案 |
| `publisher`、`homepage`、`copyright` | `BundleConfiguration` 同等元数据 | 已实现 |
| `icon` | `BundleConfiguration.Icons` + NSIS 专属覆盖 | 已实现 |
| `resources` | `BundleResourceConfiguration`，显式 source/target | 已实现；文件和目录展开、路径逃逸与冲突均校验 |
| `licenseFile` | `BundleConfiguration.LicenseFile` | 已实现；NSIS 支持 `.txt`/`.rtf` 许可证页 |
| `license` 标识 | 无 NSIS 输出 | 格式不适用；未来需要该元数据的后端再加入通用模型 |
| `category` | 无 NSIS 输出 | 格式不适用；未来 macOS/Linux 后端设计时评估 |
| `fileAssociations` | `BundleFileAssociationConfiguration` | 已实现；本阶段修复 JSON loader 丢失该配置的缺陷 |
| `shortDescription` / `longDescription` | 当前通用 `Description` | NSIS 等价能力已实现；需要区分长短描述的后端出现时再拆分模型 |
| `useLocalToolsDir` | `ToolCacheDirectory` / `BundlerToolCachePath` | 已实现；默认用户级内容寻址缓存，也允许显式覆盖；压缩包哈希、逐文件 manifest、跨进程锁和损坏恢复已有自动化 |
| `externalBin` | 完整输入目录 + `BundleResourceConfiguration` | 明确采用不同方案；调用方准备最终 payload，不复制 Tauri target-triple 自动发现约定 |
| URL/深链接协议 | `BundleUrlProtocolConfiguration` | 已实现；本阶段修复 JSON loader 丢失该配置的缺陷 |

JSON 配置加载器是 CLI 的共享配置入口。
本阶段验证它会保留文件关联、URL 协议及其元数据，避免 CLI 看似接受配置但生成包时静默丢失。

## 4. 用户可观察行为矩阵

| 行为 | 当前状态 | 差异/证据边界 |
| --- | --- | --- |
| 内嵌 NSIS 工具、无需系统预装 | 已实现 | 本项目使用独立 NsisToolset 和内容寻址共享缓存 |
| Windows/Linux/macOS 宿主生成 Windows NSIS | 已实现，外部待验收 | 解析和工具资源已实现；2026-09-29/30 联合测试已实测 Linux/macOS 产 nsis.exe 在 Windows 实装卸+运行；原生 runner 见 MT-08 |
| win-x86 目标产物 | 已实现 | `DesktopTargetMatrix` 的 x86 门禁已移除（PR #12）+ `TargetArchitectureName` 补 `X86=>"x86"`（模板 32 位分支既有）；x64 宿主产 x86 nsis.exe 经 WoW64 实装卸过，载荷运行需宿主有 32 位 .NET 运行时 |
| SemVer 重装、升级、降级策略 | 已实现 | 本项目默认禁止降级 |
| 旧 MSI 迁移 | 已实现，生产包外部待验收 | 本项目要求准确 ProductCode/UpgradeCode，不按显示名猜测 |
| 运行程序协调 | 已实现 | Restart Manager 按完整程序路径关闭，不影响其他目录同名进程 |
| 安装失败/进程中断恢复 | 已实现，提权 ACL 外部待验收 | 安装回滚 journal 和卸载前向恢复有 Windows E2E；清单不同返回 `6`，须原安装器 `/RECOVERONLY`；静态快照与恢复卸载器哈希校验，真实 perMachine ACL 见 MT-07 |
| 自动更新/被动/静默参数与稳定退出码 | 已实现 | `/UPDATE`、`/P`、`/S`、`/R`、`/ARGS`、`/RECOVERONLY` 等为本项目协议 |
| 快捷方式生命周期 | 已实现，固定项外部待验收 | 按 `.lnk` 实际目标判断所有权；MT-06 覆盖 OS 固定项差异 |
| 文件关联和 URL 协议 | 已实现 | 不强抢 Windows 默认应用；协议删除有所有权检查 |
| 完整内置语言集合 | 已实现，内容质量外部待验收 | 22 种语言均由真实 NSIS 编译；非拉丁 Windows E2E；MT-11 审校译文/RTL/截断 |
| payload、插件、卸载器、安装器完整签名 | 已实现，生产身份外部待验收 | 临时副本中签主 EXE和显式 payload，再签 Bundler 插件、卸载器和安装器；外部 provider 可替换内置实现 |
| 真实 UAC、重启、生产证书 | 外部待验收 | 统一见 `docs/nsis-manual-testing.md` |
| Windows ARM64 目标安装/卸载、非 Windows 宿主编译 | 部分已验证（2026-10 `windows-11-arm` 腿 + 六宿主 CI 矩阵跑绿） | MT-08/MT-09 |
| symlink、junction、重解析点 | 已实现，采用安全拒绝 | 构建输入、资源、安装快照/恢复、journal 和工具缓存均不跟随；Windows E2E 验证外部目录不被修改 |
| ACL、ADS 与任意文件系统元数据 | 明确不承诺完整保真 | 保证失败不越界且不把混合载荷报告为成功；产品专属元数据应由显式 Hook 迁移 |
| 工具缓存完整性和并发恢复 | 已实现 | 信任锚为固定 ZIP SHA-256 和 ZIP 内逐文件哈希；覆盖篡改、额外文件、manifest、链接、并发、Unicode 与长路径 |

## 5. 审计结论

1. `NSIS-R1` 没有遗留“未调查”的 Tauri NSIS/Windows 配置项。
2. 本阶段新增的通用能力是四种 NSIS 压缩模式；默认保持 LZMA。
3. 文件关联和 URL 协议原本在 API/MSBuild 路径可用，但 JSON loader 会丢失，已修复，CLI 保持同一通用模型。
4. 完整签名已在 `NSIS-R2` 收口；22 种内置语言、严格键校验、覆盖和回退已在 `NSIS-R3` 收口。
5. WebView2、VC Runtime 和 Tauri updater 产物不构成当前通用安装器能力；不会为字段对齐制造运行时部署系统。
6. `NSIS-R4` 已冻结安全边界：受管文件树拒绝重解析点，工具缓存以固定归档和逐文件哈希校验；ACL/ADS 不列为通用保真承诺。
   后续新增格式不得隐式放宽这些边界。

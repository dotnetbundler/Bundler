# macOS `.app` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
计划实现与外部待验收行绑定到 `docs/mac-app-roadmap.md`、`docs/mac-app-open-items.md`、`docs/mac-app-manual-testing.md` 中的明确阶段/ID。
上游参照：`docs/mac-tauri-capability-audit.md`；PKG 取舍：`docs/mac-format-decision.md`。
`MAC-APP-1`..`MAC-APP-5` 已完成（云 macOS VM 已验证，`2026-09-26`），`.app` 格式已冻结；表内“已实现”行为均附本机证据，冻结基线见 `docs/mac-app-roadmap.md` MAC-APP-5 验收记录。

## 结构与核心元数据

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.app` 目录骨架与 `<产品名>.app` 产物 | 已实现 | MAC-APP-1 | `Contents/{Info.plist,MacOS,Resources,Frameworks}` 标准布局；输出目录约定 `artifacts/<rid>/app` |
| `CFBundleIdentifier`/`CFBundleName`/`CFBundleDisplayName`/`CFBundleExecutable` | 已实现 | MAC-APP-1 | identifier 按 Apple 规则校验；`CFBundleName` 可独立配置 |
| `CFBundleShortVersionString`/`CFBundleVersion` | 已实现 | MAC-APP-1 | 格式校验，不静默截断；`CFBundleVersion` 默认取版本可独立覆盖 |
| `LSMinimumSystemVersion`、`LSApplicationCategoryType`、`NSHumanReadableCopyright` | 已实现 | MAC-APP-1 | 类别受限枚举；最低系统版本由调用方显式配置、未配置不写入 |
| `Info.plist` 固定键（`CFBundleInfoDictionaryVersion`/`CFBundlePackageType`/`NSHighResolutionCapable` 等） | 已实现 | MAC-APP-1 | 上游 `LSRequiresCarbon` 死键不复制 |
| 主可执行/资源/framework/任意 `Contents` 映射 | 已实现 | MAC-APP-1 | framework 仅显式路径；不做宿主标准目录隐式查找 |
| `osx-x64`/`osx-arm64`/`osx` 产物 | 已实现 | MAC-APP-1、4、`e7dfeae` | 独立产物+裸 `osx` 通用目标；osx-x64 结构与架构断言已实测；x64 运行依赖 Rosetta 属系统行为，实机启动外部待验收（MT-03） |
| universal/fat Mach-O 输入 | 已实现 | MAC-APP-2、`e7dfeae` | 后端托管解析 Mach-O 头（等价 `lipo -info`），按目标 RID 校验必需切片集——`osx` 要求**每个** Mach-O 同时含 x86_64+arm64；Bundler 不合成 fat binary |
| `osx` 通用载荷契约 | 已实现（契约） | `e7dfeae`、main@`3811005` 三配方实测 | `osx` 仅接收**已合并 universal 目录**——①每个 Mach-O 双切片（强制校验）；②非 Mach-O 资产须架构便携（契约声明，不可通用判定）；已实测可跑配方：`PublishSingleFile`×2+lipo、`PublishAot`×2+lipo、FDD（托管集**不带 `-r`** 发布保持中立+双 apphost lipo）；松散目录式自包含物理不可跑（运行时散件无中立形态），契约不承诺 |
| MSBuild `osx` universal 编排 | 已实现 | OSX-UNIV | MSBuild 入口 `.NET` 专属旋钮 `BundlerUniversalRuntimeIdentifiers`（复数 RID 如 `osx-x64;osx-arm64`）：targets 对每个 RID 内层 `dotnet publish` 至 `obj/.../BundlerUniversal/<rid>`，再由纯托管合并器产出 universal 目录喂 `osx` 打包；托管 lipo 等价合并（fat 头+切片拼接，宿主无关）；合并规则=Mach-O 逐件并片/非 Mach-O 全同取一/dSYM+pdb 取首份/其余不同即拒 |

## 图标与资源

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.icns` 直接采用 | 已实现 | MAC-APP-1 | `CFBundleIconFile` |
| 位图合成 `.icns`（PNG 等） | 已实现 | MAC-APP-1 | 密度/缩放规则与上游对齐 |
| `.icon`→`Assets.car`（`CFBundleIconName`） | 已实现 | MAC-APP-2 | 需 Xcode≥26 `actool`；缺失/失败降级并警告，不阻塞打包；`assetutil` 回读图标名 |
| `*.car` 直接采用 | 已实现 | MAC-APP-2 | 直接拷贝为 `Resources/Assets.car`，优先于 `.icon` |

## 桌面集成与分发行为

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `CFBundleDocumentTypes` 文件关联 | 已实现 | MAC-APP-2 | 扩展名/名称/role/rank/contentTypes；与共享 `FileAssociations` 按扩展名重叠合并 |
| `UTExportedTypeDeclarations` 导出 UTI | 已实现 | MAC-APP-2 | 含扩展名/MIME→UTI 推断表 |
| `CFBundleURLTypes` URL scheme | 已实现 | MAC-APP-2 | schemes/name/role；与共享 `UrlProtocols` 按 scheme 重叠合并 |
| universal links（`associated-domains`） | 外部待验收 | MAC-APP-3 后 | 需 provisioning profile 与开发者账号，走 expert 边界 |
| `NSAppTransportSecurity` 例外域 | 已实现 | MAC-APP-2 | 默认关闭，显式配置才放宽（HTTP+子域） |
| 调用方自备 Info.plist 合并 | 已实现 | MAC-APP-2 | 文件或内联 XML 二选一；身份键合并后回读强制一致，冲突即拒绝 |
| 安装/卸载/升级事务 | 不适用 | — | `.app` 无该语义；安装=拷贝、卸载=删除、升级=替换，见路线第 3 节 |
| per-user/per-machine 安装范围 | 不适用 | — | 同上；受管安装归 MAC-PKG 候选 |

## 签名与公证

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| ad-hoc 签名（`codesign -s -`） | 已实现 | MAC-APP-3 | 本机实跑：`codesign -s -`+`--verify --deep --strict`+启动落盘标记全绿 |
| Developer ID 应用签名（identity/临时钥匙串） | 已实现+外部待验收 | MAC-APP-3 / MAC-APP-OI-01 | identity 传入与临时钥匙串导入/销毁流程已代码化并桩断言；真实证书信任链外部待验收 |
| hardened runtime | 已实现 | MAC-APP-3 | `--options runtime` 作用于全部签名目标 |
| entitlements | 已实现 | MAC-APP-3 | 文件路径显式配置，施加于主可执行与整包签名 |
| inside-out 嵌套代码签名 | 已实现 | MAC-APP-3 | `MacOS`/`Frameworks`/`Plugins`/`Helpers`/`XPCServices`/`Libraries` 内全部常规文件先签（含非 Mach-O 托管 dll），后主可执行、再整包 |
| 公证（`xcrun notarytool submit`+`xcrun stapler staple`） | 已实现+外部待验收 | MAC-APP-3 / MAC-APP-OI-01 | 默认关闭显式开启；ditto→submit(--wait)→staple 已组装；宿主下限 Xcode 13+/macOS 11.3+；真实提交外部待验收 |
| `skipStapling` 等价开关 | 已实现 | MAC-APP-3 | `SkipStapling`/`NotaryWait` 语义同上；不等待不上钉 |
| 公证失败自动取 `notarytool log` | 已实现 | MAC-APP-3 | submit 非零退出即构建失败并附输出（notarytool 服务端日志在 `--wait` 输出内） |

## Bundler 通用横切

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 独立后端包与直接 API | 已实现 | MAC-APP-1 | `DotNet.Bundler.MacApp`（netstandard2.0） |
| 仓库外 NuGet 包消费 | 已实现 | MAC-APP-1 | `tests/MacApp.Api.PackageFixture` |
| MSBuild 集成映射 | 已实现 | MAC-APP-1 | 与直接 API 同 Core 一致 |
| 离线构建 | 已实现 | MAC-APP-1 起 | 公证外全部离线；无第三方工具内嵌 |
| 宿主工具探测与版本门槛 | 已实现 | MAC-APP-1 | 缺必需工具明确报错；可选工具降级警告 |
| 示例项目 `samples/HelloMacApp` | 已实现 | MAC-APP-1 | 真实 .NET 载荷 |
| Gatekeeper 首启/信任评估 | 已实现+外部待验收 | MAC-APP-4 / MT-01、02 | 未签名隔离包被拦截已实测（`com.apple.quarantine` + `open` 阻断断言）；已公证场景需真实证书，外部待验收 |
| 真实公证+上钉+撤销场景 | 外部待验收 | MAC-APP-MT-02、08 / OI-01 | 需 Apple Developer 凭证 |

## 有意排除

| 项 | 说明 |
| --- | --- |
| `.pkg` 系统安装/卸载/收据 | 已确认另立 `MAC-PKG`（`docs/mac-format-decision.md`），不混入 `.app` 语义 |
| `lipo` fat binary 合成、rpath 改写、依赖解析 | 调用方构建职责 |
| `providerShortName`、`LSRequiresCarbon` 等上游死键 | 上游无人消费/无效果，不复制 |
| `MACOSX_DEPLOYMENT_TARGET` 环境变量联动 | 调用方编译链职责 |
| iOS/Updater | 桌面打包器边界外/另立跨格式路线 |

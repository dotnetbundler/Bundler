# macOS `.app` 后端实施路线（MAC-APP）

> 状态：**路线草案已完成，待用户确认**；确认后按 `MAC-APP-1` 启动指令进入实施，未确认前不写实现代码。
> 上游审计见 [`docs/mac-tauri-capability-audit.md`](mac-tauri-capability-audit.md)；PKG 取舍见 [`docs/mac-format-decision.md`](mac-format-decision.md)；逐项能力状态见 [`docs/mac-app-capability-matrix.md`](mac-app-capability-matrix.md)；外部条件见 [`docs/mac-app-open-items.md`](mac-app-open-items.md)；人工步骤见 [`docs/mac-app-manual-testing.md`](mac-app-manual-testing.md)。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。

## 1. 已核实事实、选择及风险

2026-09-26 核对：分支 `mac-app-development`（基于 `main` `993b0ad`），包版本 `0.1.0-alpha.45`，工作区干净。

公共模型现状：`PackageFormat` 已含 `App`/`Dmg`；`BundlePlanner` 在请求 `Dmg` 时自动补 `App` 中间产物；`DesktopTargetMatrix` 对 `MacOS` 放行 `App`/`Dmg`；RID 校验支持 `osx-x64`/`osx-arm64`，无 universal 表达。
无任何 macOS 后端、MSBuild 映射或 macOS 集成测试入口。
Windows 侧 NSIS（冻结 `71a5c90`）与 MSI（冻结基线 `0.1.0-alpha.43`）的路径不直接复用：`.app` 没有安装事务、收据、卸载器概念，语义见第 3 节。

本机环境自检（云 macOS VM，2026-09-26 实测）：

| 项 | 结果 |
| --- | --- |
| 宿主 | macOS 26.5.2 arm64（Apple M4 Virtual），完整 Xcode 26.6（`/Applications/Xcode.app`） |
| .NET | `dotnet --version` = `10.0.401`，蓝图已就位 |
| 打包/签名工具 | `hdiutil`、`pkgbuild`、`productbuild`、`productsign`、`codesign`、`xcrun notarytool`、`xcrun stapler`、`plutil`、`ditto`、`security`、`osascript`、`xar`、`pkgutil`、`installer`、`spctl`、`lipo`、`iconutil` 均可用 |
| Xcode 专属 | `actool`、`assetutil`、`SetFile` 在 Xcode 开发者目录内可用 |
| 缺口 | 无任何 codesigning 身份（`security find-identity -v -p codesigning` 为 0）；Rosetta 未激活（`arch -x86_64` 报 Bad CPU type）；无 `pwsh` |

含义：本机足以完成 `.app` 结构生成、`plutil` 校验、ad-hoc 签名、`codesign --verify`、真实启动/文件关联/URL 唤起等自动化验证；
Developer ID 签名、真实公证、osx-x64 原生运行属外部待验收（`docs/mac-app-open-items.md`）。
集成脚本用例用 POSIX shell 书写（无 pwsh），测试组织细节见第 4 节。

## 2. 工具供应与宿主门槛

**关键差异：macOS 打包工具不可再分发，本格式不适用“工具随包供应”的字面条款，改为“宿主工具检测+版本下限+明确报错”策略——此为对 `docs/development-rules.md` 第 3 节的格式级偏差，待用户确认。**

理由：`codesign`/`notarytool`/`hdiutil`/`osascript`/`plutil`/`security` 等是 Apple 专有二进制，许可上只能随 macOS/Xcode 使用，且免费随系统/Xcode 提供，没有合法再分发渠道；
这与 NSIS/WiX 必须随包内嵌的情形不同，目标是免费、无付费必需服务、覆盖合规的 macOS 构建宿主。

| 工具 | 来源 | 用途与门槛 |
| --- | --- | --- |
| `plutil` | 系统 | Info.plist 生成后 `-lint` 校验；必需 |
| `ditto`、`xattr` | 系统 | 公证打包 zip、扩展属性清理；MAC-APP-3 必需 |
| `codesign` | 系统 | ad-hoc/正式签名；MAC-APP-3 必需 |
| `security` | 系统 | 临时钥匙串导入 CI 证书；MAC-APP-3 必需 |
| `xcrun notarytool`、`xcrun stapler` | Xcode（`xcrun` 查找） | 公证与上钉；MAC-APP-3 显式开启时必需 |
| `actool`、`assetutil` | Xcode ≥26 | `.icon`→`Assets.car` 图标管线；可选，缺失降级 `.icns` 并警告 |
| `lipo` | 系统/CLT | universal 载荷校验（`lipo -info`）；MAC-APP-2 |
| `hdiutil`、`osascript`、`sw_vers`、`SetFile`、`bless`、`file` | 系统/Xcode | DMG 阶段（MAC-DMG）才需要，不在本路线要求 |
| `pkgbuild`、`productbuild`、`productsign`、`xar`、`pkgutil`、`installer` | 系统 | PKG 阶段（MAC-PKG，待决策）才需要 |

规则：

1. 构建前探测每个必需工具（`xcrun -f`/PATH）并记录版本与路径；缺必需工具立即报清晰错误，不静默降级；缺可选工具降级并警告。
2. 不自动安装 Xcode/CLT，不替用户激活 Rosetta；宿主缺项写入构建日志与错误信息。
3. 无网络下载：公证之外的每一步离线可用；公证是显式开启的网络动作。
4. `.app` 目录结构本身不含机器码，理论上跨宿主可生成；但所有可验证手段（签名、`plutil` 行为、真实启动）都在 macOS，**建议第一阶段起限定 macOS 宿主构建，跨宿主无签名结构生成登记为候选增强——待用户确认。**
5. 后端包形态建议：`DotNet.Bundler.Mac` 单包承载 macOS 系列后端（`App`/`Dmg`/未来的 `Pkg` 共享签名、公证与工具探测基础设施），各格式仍按独立阶段冻结——待用户确认；若按格式分包则签名共享层另立 `Bundler.Signing.Mac`。

## 3. 第一阶段前必须固定的 `.app` 语义

这些是计划契约，`MAC-APP-1` 首次写代码前以 Apple 官方文档与本机可执行测试核实，并在公开 API 中固定；
若实测推翻，先改本文与能力矩阵，不产出带错误身份的包。

1. **身份**：`Identifier` 映射 `CFBundleIdentifier`，按 Apple 规则校验（字母数字、连字符、点分段，段首非数字，建议 reverse-DNS）；
   校验不通过即拒绝，不静默改写；一经发布的 bundle ID 不再变化，否则 LaunchServices 关联、升级与用户授权记录断裂。
   `CFBundleName` 可独立于 `CFBundleDisplayName`/`ProductName` 配置。
2. **版本**：`CFBundleShortVersionString` 取规范化用户可见版本；`CFBundleVersion` 为构建迭代号，默认取同一版本串，可显式配置；
   两者都按 Apple 格式校验（至多三段数字），不接受的输入明确报错，不截断。
3. **载荷映射**：`InputDirectory` 不默认拍平进 bundle；
   约定映射为：主可执行（`MainExecutable`，Mach-O 校验）→ `Contents/MacOS/`；
   `Resources` 项 → `Contents/Resources/` 保留目标相对路径；
   显式 `Contents` 相对路径映射表（`files` 等价物）覆盖其余位置；
   `Frameworks` 仅接显式 `.framework`/`.dylib` 路径 → `Contents/Frameworks/`，不在构建宿主标准目录隐式查找。
   符号链接、重解析点与越界路径（`..`/绝对逃逸）一律拒绝；`Contents/` 顶层保留名（`MacOS`/`Resources`/`Frameworks`/`Info.plist`/`PkgInfo`）不允许被用户映射覆写。
4. **安装/卸载语义**：`.app` 无安装事务、无收据、无卸载器。
   产物是可整体移动/拷贝的目录；“安装”=用户放入 `/Applications`（或任意位置），“卸载”=删除 `.app`，“升级”=整体替换。
   Bundler 不伪造 MSI 式事务/回滚/注册语义；损坏 zip 式交付、部分拷贝等用户侧情形不属于后端承诺。
5. **架构**：`osx-x64`/`osx-arm64` 各为独立产物；`osx-x64` 产物在 Apple Silicon 依赖 Rosetta 属系统行为，非后端能力。
   请求 universal 语义时要求调用方提供已合成的 fat Mach-O（构建期 `lipo -info` 校验）；
   Bundler 不做 `lipo` 合成。是否在公共模型增加 `osx-universal` 目标表达待用户确认（当前建议：不加，用 `osx-x64`/`osx-arm64` 双产物覆盖）。
6. **签名语义**：默认产物不签名，载荷既有签名（如 .NET apphost 的 linker ad-hoc 签名）原样保留；
   显式配置后才执行 `codesign`：identity 字符串、`"-"` ad-hoc、临时钥匙串证书导入三种提供器；
   hardened runtime 默认开启且只作用于可执行目标；entitlements 显式给定（文件路径或 plist 内容）；
   一律 inside-out 顺序并对整个 bundle 收尾；签名前 `xattr -crs`；签名失败不留下看似成功的产物。
7. **公证语义**：公证是显式开启的网络上传，默认关闭；
   凭证经环境变量/密钥提供器传入（Apple ID 或 App Store Connect API Key），不进仓库、项目文件、命令行回显或可回显日志；
   `skipStapling` 等价开关单独存在；`notarytool` 非 Accepted 结果即构建失败并自动附 `notarytool log`；
   上钉失败不把产物标成“已公证”。
8. **秘密处理**：证书/密码/API key 只允许经密钥提供器或约定环境变量；临时钥匙串用完即删；
   本机只允许 ad-hoc 签名断言，Developer ID/公证一律外部待验收。

依据：[Apple Bundle 结构](https://developer.apple.com/go/?id=bundle-structure)、[Info.plist 键](https://developer.apple.com/documentation/bundleresources/information-property-list)、[codesign](https://developer.apple.com/library/archive/technotes/tn2206/)、[Notarization](https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution)、[QA1940 扩展属性](https://developer.apple.com/library/archive/qa/qa1940/_index.html)、上游快照 `docs/mac-tauri-capability-audit.md`。

## 4. 阶段计划

通用测试与清理规则见 `docs/development-rules.md`；`.app` 专有要求如下。
所有阶段均需对每项新增/修改功能增加自动化断言；
本机（云 macOS VM）可安全执行 `.app` 生成、校验、ad-hoc 签名、`codesign --verify`、启动、LaunchServices 注册与文件/URL 唤起等真实测试，当阶段跑完；
提权（sudo）、系统域写入、真实重启、生产证书与公证不阻塞本机开发，记入外部待办。

测试组织（遵循仓库分层约定；macOS 无 `pwsh`，入口用 bash，脚本名写明用途）：

| 用途 | macOS `.app` 入口 |
| --- | --- |
| 快速单元/契约 | `tests/Bundler.Tests` |
| 仓库外直接后端 API 包消费 | `tests/MacApp.Api.PackageFixture` |
| MSBuild 包消费 fixture | `tests/MacOS.App.Integration/Fixture` |
| 真实 macOS 集成 | `tests/MacOS.App.Integration/Verify.sh`（独立身份、预检无碰撞、`finally` 只清理本轮产物） |
| 专用环境与人工 | `docs/mac-app-manual-testing.md`、`docs/mac-app-open-items.md` |

### MAC-APP-1：可用的最小 `.app`（结构/元数据）

- **前置**：本路线第 2、3 节的工具策略与语义决策经用户确认；`MAC-PKG` 决策结论已知（不阻塞本阶段）；核对当前 Git 与公共模型。
- **目标/交付**：`DotNet.Bundler.Mac` 后端（或直接 API 等价物）、MSBuild 映射、`.app` 骨架生成、Info.plist 核心键（身份/显示名/bundle name/可执行名/两版本键/最低系统版本/类别/版权/图标名）、`.icns`（透传 + 位图合成）、第 3 节载荷映射、产物命名契约（`<产品名>.app`，输出目录 `artifacts/<rid>/app`）、`plutil -lint` 校验、示例 `samples/HelloMacApp`。
- **新增自动化**：输入校验（identifier 规则、版本格式、路径拒绝、保留名冲突）、plist 键值断言（`plutil -p` 读回）、目录结构断言、`.icns` 生成断言、MSBuild/API 同 Core 结果、NuGet 包内容、重复构建指纹。
  本机真实测试：构建含真实 .NET 载荷的 `.app` → `plutil -lint` → `open`/`open -W` 启动退出码与进程断言 → 卸载语义（删除 bundle 后无残留登记）。
- **人工边界**：干净宿主（未装 Xcode/CLT 的新建用户）构建本机已产出的 `.app` 并首启；Intel 宿主。
- **不做**：正式签名/公证/entitlements、文件关联与 URL scheme、`Assets.car`、universal 校验、DMG。
- **退出**：新增测试与受影响回归通过；本机 `.app` 生成→校验→启动烟雾测试通过；未支持项明确报错；外部项逐条登记。

### MAC-APP-2：分发与桌面集成行为

- **前置**：MAC-APP-1 通过，`.app` 语义冻结。
- **目标/交付**：`CFBundleDocumentTypes`+`UTExportedTypeDeclarations`（文件关联：扩展名/名称/role/rank/contentTypes/UTI 推断）、`CFBundleURLTypes`（schemes/name/role）、`NSAppTransportSecurity` 例外域（显式配置才放宽）、调用方 Info.plist 合并（文件或内联，身份键回读强制一致）、`Assets.car` 管线（`.icon`/`*.car` 输入、`actool`≥26 探测降级）、universal/fat 载荷 `lipo -info` 校验。
- **新增自动化**：plist 合并与身份键冲突拒绝、关联/协议键结构断言、UTI 推断表断言、`actool` 版本门控与降级分支、fat/非 fat 输入判定。
  本机真实测试：`lsregister` 注册后 `open <文件>`/`open <scheme>://` 实际唤起断言；`.app` 拷入/移出 `/Applications` 的 LaunchServices 行为（仅用户域内操作，不提权）。
- **人工边界**：Gatekeeper 对未签名/ad-hoc 首启的实际对话框表现；最小系统版本宿主的实际拒绝/允许。
- **不做**：正式签名/公证、MDM/企业分发、universal links（依赖 provisioning profile，MAC-APP-3 后外部边界）。
- **退出**：新增测试与回归通过；本机 LaunchServices/唤起链路可复现；外部项保留待验收。

### MAC-APP-3：codesign 与 notarization

- **前置**：MAC-APP-2 通过；本阶段全部签名代码路径可在无真实证书下完成开发。
- **目标/交付**：签名提供器（identity/临时钥匙串证书导入/`codesign -s -` ad-hoc）、hardened runtime 开关（仅可执行目标）、entitlements、inside-out 嵌套签名顺序（`MacOS`/`Frameworks`/`Plugins`/`Helpers`/`XPCServices`/`Libraries` 约定）、`xattr -crs`、签后 `codesign --verify --deep --strict` 与 `spctl` 断言、显式公证（ditto zip→签→`notarytool submit`--wait/异步→`stapler staple`）、`skipStapling`、凭证环境变量与密钥提供器、失败清理不留伪成功产物。
- **新增自动化**：签名顺序、entitlements 传递、hardened runtime 目标筛选、临时钥匙串创建/销毁、失败路径无伪产物、公证参数组装（不上传的桩断言）；
  本机真实测试：全链 `codesign -s -` ad-hoc 签名 + `--verify` + 启动 + 证书缺失路径。
- **人工边界**：Developer ID Application 证书、真实公证提交/上钉/撤销、Gatekeeper 离线验票、`APPLE_*` 凭证链路——全部外部待验收（MAC-APP-OI-01 起）。
- **不做**：DMG 签名、Developer ID Installer（属 PKG）、生产证书入库、任何默认自动上传。
- **退出**：本机 ad-hoc 签名链可复现、失败清理断言通过；所有需凭证项登记外部待办。

### MAC-APP-4：原生 macOS E2E 与支持矩阵

- **前置**：MAC-APP-1..3 完成；可用专用/可抛弃宿主计划。
- **目标/交付**：干净宿主（无 Xcode/CLT）构建-启动链路复核、`osx-x64`（Rosetta 或 Intel 原生）与 `osx-arm64` 运行矩阵、`LSMinimumSystemVersion` 宿主实测、下载-quarantine-首启 E2E（Gatekeeper 实际行为）、版本替换升级语义（v1→v2 `.app` 替换后关联/LaunchServices/用户数据）、卸载残留检查、文档与示例收口。
- **新增自动化**：可自动化的矩阵格子转自动化；不可自动化的逐项人工记录（OS 版本、架构、Git SHA、产物 SHA-256、日志、清理）。
- **人工边界**：凡需真实 Apple 凭证、物理多样性宿主或破坏性场景的格子保持外部待验收。
- **不做**：MAC-DMG/MAC-PKG 功能、伪造支持声明。
- **退出**：矩阵实测格子有证据、未测格子限缩支持声明；示例与文档完整。

### MAC-APP-5：审计与格式冻结

- **前置**：MAC-APP-4 完成。
- **目标/交付**：Tauri macOS `.app` 能力审计复核（上游漂移重核）、`docs/mac-app-capability-matrix.md` 状态定稿、许可/供应链复核（本格式无第三方内嵌工具，复核点为宿主工具版本下限与凭证边界）、外部待办收口、冻结 `.app` 配置与行为基线。
- **新增自动化**：补齐矩阵缺口；冻结测试向量。
- **不做**：新增功能；冻结后仅缺陷修复附回归测试。
- **退出**：矩阵与文档一致；冻结基线写入本文件与 `PROJECT_CONTEXT.md`；`docs/roadmap.md` 默认下一阶段推进到 `MAC-DMG`。

## 5. 验证分层与交接

| 验证层 | 内容 |
| --- | --- |
| 本机自动（云 macOS VM 已验证可用） | `.app` 生成、`plutil` 校验、结构断言、ad-hoc 签名与 `codesign --verify`、真实启动、`lsregister`/文件与 URL 唤起、`lipo` 校验、缓存/离线构建、NuGet 包消费 fixture |
| 专用 macOS 环境 | 干净宿主、Intel/Rosetta、不同 macOS 版本、下载-quarantine-首启、per-user 之外需提权的位置试验（可抛弃 VM） |
| 外部待验收 | Developer ID 证书签名、真实公证/上钉/撤销、Gatekeeper 信任链评估、生产分发 |

接班者先读 `AGENTS.md`、`docs/development-rules.md`、`PROJECT_CONTEXT.md`、`docs/roadmap.md`、本文、`mac-tauri-capability-audit.md`、矩阵/待办/人工文档，再核 Git、代码与测试。
每阶段完成新增自动化和适用的真实 macOS 测试后报告结果；未经用户明确要求不开始后续阶段代码、不提交或推送（本路线文档轮次的提交推送已由用户明确授权）。

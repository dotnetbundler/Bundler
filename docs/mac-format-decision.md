# macOS 格式决策：PKG 是否纳入（待用户确认）

> 状态：**待用户确认**。本文只记录分析、建议与备选，不构成已批准方案；用户确认后按确认结论更新 `docs/roadmap.md`、`PROJECT_CONTEXT.md` 与本文件状态。
> 背景：`docs/roadmap.md` MAC 节要求在 `MAC-APP` 开始前用格式决策记录决定 PKG 是否加入公共 `PackageFormat`；若加入，顺序固定为 `MAC-DMG` 之后、Linux 之前。
> 上游审计见 [`docs/mac-tauri-capability-audit.md`](mac-tauri-capability-audit.md)；`.app` 阶段分解见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)。

## 1. 已核实事实

- 公共模型现状（`src/Bundler.Abstractions/DesktopPlatform.cs`）：`PackageFormat = { Nsis, Msi, App, Dmg, Deb, Rpm, AppImage }`，无 `Pkg`；`DesktopTargetMatrix` 中 `MacOS` 仅放行 `App`/`Dmg`；`BundlePlanner` 已表达 `Dmg`→`App` 的依赖。
- 上游参照：Tauri bundler 没有 `.pkg` 输出（`PackageType` 仅 `MacOsBundle`/`IosBundle`/`Dmg`/`Updater` 及 Windows/Linux 各项），PKG 能力不能靠上游审计覆盖。
- 工具链事实（2026-09-26 本机核对，macOS 26.5.2 arm64 + Xcode 26.6）：`pkgbuild`/`productbuild`/`productsign`/`xar`/`pkgutil`/`installer` 全部为系统自带，位于 `/usr/bin`、`/usr/sbin`；构建与安装不需要任何第三方工具。
- 语义事实：`.pkg` 由 `Installer.app`/`installer(8)` 驱动，走组件包（`pkgbuild`，`PackageInfo`+payload）+ 分发包（`productbuild`，`distribution.xml`）两层；安装记录在 `/var/db/receipts`（`pkgutil --pkgs`/`--files`/`--forget`）；公证直接接受 `.pkg` 并支持 `stapler` 上钉；签名使用 **Developer ID Installer**（与应用签名证书不同类型）。

## 2. `.pkg` 与 `.app`/`.dmg` 的用户可观察差异

| 维度 | `.app`（可含于 `.dmg` 分发） | `.pkg` |
| --- | --- | --- |
| 安装模型 | 拖放拷贝，无安装事务 | Installer 受管安装，写入收据库 |
| 目标位置 | 用户自选（通常 `/Applications` 或 `~/Applications`） | 可指向 `/Applications`、`/Library`、`/usr/local`、`$HOME` 等任意域，含系统域 |
| 权限 | 不需提权 | 系统域/受管载荷需管理员授权，可后台/静默安装（`installer -pkg`） |
| 生命周期 | 卸载=删除 `.app`，无收据；升级=整体替换 | `pkgutil` 可枚举/查询/forget；`distribution.xml` 支持需求检查、组件选择、欢迎/许可/结语页 |
| 自定义逻辑 | 无 | `preinstall`/`postinstall` 等脚本可在提权上下文执行（属高风险面） |
| 签名/公证 | Developer ID Application + 公证 `.app` | Developer ID Installer + 公证 `.pkg` 本体 |
| 典型场景 | 桌面 GUI 应用分发 | 命令行工具、守护进程、驱动、企业 MDM 批量部署 |
| 卸载 | 拖入废纸篓 | 无官方一键卸载：`pkgutil --forget` 只清收据，文件需按 BOM 清单自行清理 |

结论：`.pkg` 是 macOS 上**唯一**的原生受管安装格式，能力与 `.app`/`.dmg` 不重叠——它在本产品中的角色对应 Windows 侧 MSI（受管安装、收据、系统域、企业部署），而 `.app`/`.dmg` 对应的是免安装/拖拽分发面。

## 3. 建议与理由

**建议：纳入 `PackageFormat.Pkg`，顺序置于 `MAC-DMG` 之后、`LINUX-DEB` 之前（与 `docs/roadmap.md` 既定顺序一致）。**

理由：

1. 真实通用需求：CLI 工具、后台守护/代理、需要装入 `/usr/local` 或 `LaunchDaemons` 的应用、MDM 企业分发都无法用拖放 `.app` 表达；这与 MSI 在 Windows 上的定位对称。
2. 供应成本为零：`pkgbuild`/`productbuild`/`xar`/`installer`/`pkgutil` 均随 macOS 提供，不涉及第三方工具再分发；与本格式整体的“宿主工具检测”策略一致（见 `mac-app-roadmap.md` 第 2 节）。
3. 载荷可复用：`MAC-PKG` 的 payload 可以是 `MAC-APP` 产出的 `.app`（规划器沿用 `Dmg`→`App` 同款中间产物依赖），也可是一般文件树。
4. 提前纳入可在 `MAC-APP`/`MAC-DMG` 设计期就保留正确的公共模型形状（枚举值、矩阵、规划器依赖），避免后补造成的公开 API 变更。

## 4. 纳入时必须同时固定的边界（建议，随决策一并确认）

- **受管模式不开放任意 `preinstall`/`postinstall` 脚本**：提权执行的任意脚本等价于 MSI 自定义动作的风险面；确有需求时走显式专家模式并在文档中显著标注责任边界，与 `docs/development-rules.md` 第 3 节一致。
- **升级语义独立设计**：`.pkg` 无 MSI major upgrade 等价物，覆盖安装、降级、收据清理规则须在 MAC-PKG 第一阶段前写进其格式路线，不套用 `.app` 或 MSI 的规则。
- **签名证书类型独立**：Developer ID Installer 与 Application 证书不可互替；测试与示例不得混用。
- **构建宿主限定 macOS**：`pkgbuild`/`productbuild` 无跨宿主替代。
- **卸载语义写实**：提供 BOM 清单查询与 `--forget` 边界说明，不承诺 Windows 式一键卸载。

## 5. 备选方案（不推荐但已考虑）

- **不纳入**：macOS 只剩拖放分发，放弃受管/企业安装面；若采用须在本文件与 `roadmap.md` 写明理由。
- **延后到 Linux 之后**：可行但无收益；PKG 与 APP/DMG 共享签名/公证基础设施，紧跟 DMG 更省重复接入。
- **替代工具**：`packages`（第三方 GUI）等不提供收益且引入再分发问题，不采用。

## 6. 需要用户拍板

1. 是否将 `Pkg` 加入 `PackageFormat` 并接受第 4 节边界？（建议：是）
2. 确认实施顺序 `MAC-APP` → `MAC-DMG` → `MAC-PKG` → `LINUX-DEB` → …（建议：是，与现行文档一致）。

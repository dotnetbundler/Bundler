# macOS `.pkg` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
计划实现与外部待验收行绑定到 `docs/mac-pkg-roadmap.md`、`docs/mac-pkg-open-items.md`、`docs/mac-pkg-manual-testing.md` 中的明确阶段/ID。
`.pkg` 无上游参照（tauri 无 `.pkg` 输出），能力集由原生工具链语义与 `docs/mac-format-decision.md` 边界决定。
路线规划轮进行中（`2026-09-26`），决策待用户确认；状态列在决策确认前一律为"计划实现"。

## 组件包与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.pkg` 组件包产物（`pkgbuild`） | 计划实现 | MAC-PKG-1 | 产物 `OutputDirectory/<rid>/pkg/<产品名>.pkg` |
| `.app` 中间产物输入 | 计划实现 | MAC-PKG-1 | 规划器自动补 `App` 步骤；只读输入 |
| 任意文件树载荷映射 | 计划实现 | MAC-PKG-1 | `--root`/`--component` 映射，文件树不限于 `.app` |
| 安装位置可配（默认 `/Applications`） | 计划实现 | MAC-PKG-1 | `--install-location` |
| identifier/version 默认规则 | 计划实现 | MAC-PKG-1 | 单一 `.app` 载荷取 `CFBundleIdentifier`/`CFBundleShortVersionString`，否则显式 `BundlerPkgIdentifier` |
| 非 macOS 宿主拒绝 | 计划实现 | MAC-PKG-1 | `pkgbuild`/`productbuild` 不可跨宿主 |
| 失败清理（临时产物） | 计划实现 | MAC-PKG-1 | 不留伪 `.pkg` |

## 分发包与页面

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 分发包（`productbuild`+`distribution.xml`） | 计划实现 | MAC-PKG-2 | 配置分发特性时自动升级包结构 |
| welcome/license/conclusion 页面 | 计划实现 | MAC-PKG-2 | 富文本页；`license` 复用公共 `LicenseFile` |
| 分发标题 | 计划实现 | MAC-PKG-2 | `distribution.xml` `title` |
| 安装域名 `system`/`current-user-home` | 计划实现 | MAC-PKG-2 | per-user 域非管理员可装，本机可实测 |
| requirements/choices 任意 XML | 明确拒绝 | — | 首个版本不暴露；有真实需求再评估 |

## 签名、公证与脚本

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| Developer ID Installer 签名 | 计划实现 | MAC-PKG-3 | `pkgbuild --sign`/`productsign --sign`；无 ad-hoc 等价物 |
| 临时钥匙串证书导入 | 计划实现 | MAC-PKG-3 | 复用 `MacAppSigning.TemporaryKeychain` |
| `.pkg` 公证 + `stapler` | 计划实现 | MAC-PKG-3 | 默认关闭；复用 `.app` 公证凭证三模式 |
| `ScriptsDirectory` 专家脚本旋钮 | 计划实现 | MAC-PKG-3 | 默认关闭，显著责任边界标注（`mac-format-decision.md` §4） |
| `preinstall`/`postinstall` 任意脚本默认面 | 明确拒绝 | — | 提权任意脚本风险面等价 MSI 自定义动作，仅专家模式 |

## 生命周期

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 收据记录（`pkgutil --pkgs`/`--files`） | 计划实现 | MAC-PKG-2/4 | 安装后 `/var/db/receipts` 可枚举 |
| 覆盖安装升级 | 计划实现 | MAC-PKG-4 | 收据版本随装更新；无降级保护 |
| 一键卸载 | 明确拒绝 | — | `.pkg` 无此语义；`pkgutil --forget` 只清收据，文件按 BOM 清单清理 |
| `osx-x64` 产物 | 计划实现 | MAC-PKG-4 | 结构与 payload 架构断言；运行态属外部待验收 |
| `system` 域管理员安装 | 外部待验收 | MAC-PKG-4 | 本机无 root，登记 MAC-PKG-OI-01 |

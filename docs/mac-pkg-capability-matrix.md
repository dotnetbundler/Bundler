# macOS `.pkg` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
已实现行验收证据：`Bundler.Tests` 122 项 + `tests/MacOS.Pkg.Integration/Verify.sh`（macOS 26.5.2 arm64 全绿）；外部待验收行绑定到 `docs/mac-pkg-open-items.md`、`docs/mac-pkg-manual-testing.md` 中的明确 ID。
`.pkg` 无上游参照（tauri 无 `.pkg` 输出），能力集由原生工具链语义与 `docs/mac-format-decision.md` 边界决定。
决策已于 `2026-09-26` 全部确认；`MAC-PKG-1..5` 全部完成，本矩阵已于 MAC-PKG-5 定稿（冻结基线 `0.1.0-alpha.47`），状态列如实反映当前实现。

## 组件包与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.pkg` 组件包产物（`pkgbuild`） | 已实现 | MAC-PKG-1 实测 | `OutputDirectory/<rid>/pkg/<产品名>.pkg`；集成实测 expand-full/xar 结构断言 |
| `.app` 中间产物输入 | 已实现 | MAC-PKG-1 实测 | `BundlerFormats=pkg` 自动先产 `.app`；payload 根即 `.app` |
| 任意文件树载荷映射 | 已实现 | MAC-PKG-1 实测 | `BundlerPkgPayload` 项 `Destination` 元数据映射，落到 install-location 相对路径 |
| 安装位置可配（默认 `/Applications`） | 已实现 | MAC-PKG-1 实测 | `--install-location`；相对路径明确拒绝 |
| identifier/version 默认规则 | 已实现 | MAC-PKG-1 实测 | 默认取 `BundlerIdentifier`/`Version`，可经 `BundlerMacPkgIdentifier`/`BundlerMacPkgVersion` 覆盖 |
| 非 macOS 宿主拒绝 | 已实现 | MAC-PKG-1 单测 | `pkgbuild` 不可跨宿主 |
| 失败清理（临时产物） | 已实现 | MAC-PKG-1 单测+集成 | 不留伪 `.pkg`；非法 install-location 使 publish 失败且无产物 |

## 分发包与页面

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 分发包（`productbuild`+`distribution.xml`） | 已实现 | MAC-PKG-2 实测 | 配置分发特性时自动升级包结构 |
| welcome/license/conclusion 页面 | 已实现 | MAC-PKG-2 实测 | `.txt`/`.html`/`.rtf` 页；`license` 复用公共 `LicenseFile` |
| 分发标题 | 已实现 | MAC-PKG-2 实测 | `BundlerMacPkgTitle`，默认产品名 |
| 安装域名 `system`/`current-user-home` | 已实现 | MAC-PKG-2 实测 | `BundlerMacPkgDomain`；per-user 域非管理员真实安装已验（`~/Applications` 落位+收据断言） |
| requirements/choices 任意 XML | 明确拒绝 | — | 首个版本不暴露；有真实需求再评估 |

## 签名、公证与脚本

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| Developer ID Installer 签名 | 已实现 | MAC-PKG-3 实测 | 组件包 `pkgbuild --sign --timestamp`、分发包 `productsign --sign`；`"-"`（ad-hoc）明确拒绝；无效身份真实失败已验 |
| 临时钥匙串证书导入 | 已实现 | MAC-PKG-3 单测 | p12 → 一次性 keychain → `--keychain`；构建后销毁 |
| `.pkg` 公证 + `stapler` | 已实现（接线） | MAC-PKG-3 单测 | `xcrun notarytool submit` 直接收 `.pkg` + `stapler`；默认关闭；真实凭证属外部待验收 OI-03 |
| `ScriptsDirectory` 专家脚本旋钮 | 已实现 | MAC-PKG-3 实测 | `pkgbuild --scripts` 原样收目录；`postinstall` 在 per-user 真实安装中执行已验（标记文件断言）；责任边界已标注 |
| `preinstall`/`postinstall` 任意脚本默认面 | 明确拒绝 | — | 提权任意脚本风险面等价 MSI 自定义动作，仅专家模式 |

## 生命周期

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 收据记录（`pkgutil --pkgs`/`--files`） | 已实现（per-user 域） | MAC-PKG-2 实测 | per-user 收据写 `~/Library/Receipts`，`pkgutil --pkgs/--files/--forget --volume ~` 可枚举；system 域属外部待验收 |
| 覆盖安装升级 | 已实现 | MAC-PKG-4 实测 | 同 identifier v1→v2 覆盖安装实测：收据 `pkg-version` 更新到 2.0.0；无降级保护 |
| 同 bundle 标识既有副本时的安装重定位 | 已记录边界 | 2026-09-30 联合测试实测 | 宿主存在同 CFBundleIdentifier 的既有 `.app` 副本时，macOS installer 将新 pkg 载荷安装到既有副本位置（重定位）而非全新目标路径；移除旧副本后按默认目标安装——系统原生语义非缺陷 |
| 一键卸载 | 明确拒绝 | — | `.pkg` 无此语义；`pkgutil --forget` 只清收据，文件按 BOM 清单清理 |
| `osx-x64` 产物 | 已实现（结构） | MAC-PKG-4 实测 | `osx-x64/pkg/` 产出、expand-full payload 内 `file` 断言 x86_64 Mach-O；运行态需 Rosetta/Intel 宿主，属 OI-04；裸 `osx` 通用目标 `e7dfeae` 起亦通 |
| `system` 域管理员安装 | 外部待验收 | MAC-PKG-4 | 本机无 root，登记 MAC-PKG-OI-01 |

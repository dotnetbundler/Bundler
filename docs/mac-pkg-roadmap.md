# macOS `.pkg` 后端实施路线（MAC-PKG）

> 状态：**`.pkg` 已冻结**（`MAC-PKG-5` 于 2026-09-27 完成，分支 `mac-pkg-development`）。
> 冻结基线：`MacPkgBundleConfiguration`+`MacPkgSigningConfiguration` 配置面与本文档 §3 语义契约在 `0.1.0-alpha.47` 冻结；此后变更走变更控制（能力矩阵改行 + 新测试 + 文档同步）。
> 冻结测试向量：`Bundler.Tests` 122/122 全绿 + `MacPkgIntegrationTests` 全绿（组件包/分发包/页面/域名/per-user 真实安装+收据/签名拒绝路径/scripts 归档与执行/覆盖升级/macos-x86_64 产物/失败清理）。
> `.pkg` 无上游参照：tauri 无 `.pkg` 输出（`PackageType` 仅 `MacOsBundle`/`IosBundle`/`Dmg`/`Updater` + Linux/Windows 各项），决策依据为原生 macOS 工具链语义（PKG 纳入决策经用户确认，见 git 记录）。
> `.app`/`.dmg` 侧已冻结的契约见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)、[`docs/mac-dmg-roadmap.md`](mac-dmg-roadmap.md)。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/mac-pkg-capability-matrix.md`](mac-pkg-capability-matrix.md)；外部条件见 [`docs/mac-pkg-open-items.md`](mac-pkg-open-items.md)；人工步骤见 [`docs/mac-pkg-manual-testing.md`](mac-pkg-manual-testing.md)。

## 1. 已确认决策（2026-09-26，全部落定）

1. **实现方式**：C# 原生编排 `pkgbuild`/`productbuild`/`productsign`/`xar`/`pkgutil`/`installer`（全部系统自带，零再分发成本，与"工具走宿主检测不内嵌第三方"策略一致）。
2. **包结构策略**：默认纯组件包（`pkgbuild` 直出 `.pkg`）；配置分发特性（欢迎/许可/结语页、标题、域名选择）时自动升级为 `productbuild` + `distribution.xml` 分发包。调用方看到的是同一个 `.pkg` 输出路径。
3. **载荷与安装位置**：payload 默认取 `MAC-APP` 产出的 `.app`（规划器沿用 `Dmg`→`App` 同款中间产物依赖），也支持任意文件树映射；安装位置默认 `/Applications`，可配 `--install-location`。
4. **签名**：可选 Developer ID Installer 签名（组件包 `pkgbuild --sign` / 分发包 `productsign --sign`），默认不签；`.pkg` 无 ad-hoc 等价物——要么真实 Installer 证书要么不签，与 `.app` 的 `Identity="-"` 语义不同；临时钥匙串证书导入复用 `MacAppSigning`。
5. **公证**：`.pkg` 本体支持公证+`stapler` 上钉（installer 包是 Gatekeeper 的实际验签对象，与 `.dmg`"内部 .app 是验签对象"的结论相反）；默认关闭，复用 `.app` 公证凭证三模式（keychain profile / Apple ID / API key + `APPLE_*` 环境变量回退）。
6. **分发特性面**：`welcome`/`license`/`conclusion` 富文本页 + 分发标题；`license` 复用公共 `LicenseFile`；不暴露 requirements/choices 的任意 XML 编辑面（后续有真实需求再加）。
7. **安装域名**：`domains` 配置 `system`（默认，需管理员授权）/ `current-user-home`（非管理员可装到 `~/Applications` 等）；后者同时是本机无 root 环境下唯一可真实实测的装态。
8. **identifier 与版本**：单一 `.app` 载荷时默认取其 `CFBundleIdentifier`/`CFBundleShortVersionString`；非 `.app` 载荷或多组件时必须显式 `BundlerPkgIdentifier`；`BundlerPkgVersion` 可覆盖。
9. **脚本边界**：不开放任意 `preinstall`/`postinstall`（提权执行的任意脚本等价于 MSI 自定义动作的风险面）；仅保留显式专家旋钮 `ScriptsDirectory`（默认关闭，启用时在文档与日志显著标注责任边界）。
10. **升级/卸载语义**：`.pkg` 无 MSI major upgrade 等价物——升级=覆盖安装（收据版本随装更新），无降级保护，无自动卸载；`pkgutil --forget` 只清收据不删文件，文档写实。
11. **产物命名**：`OutputDirectory/<rid>/pkg/<产品名>.pkg`（与 `.app`/`.dmg` 输出契约一致）。

## 2. 打包工具下限（三层口径）

- **打包工具（能力）下限**：`pkgbuild`/`productbuild`/`productsign`/`xar`/`pkgutil`/`installer` 全部为系统自带（`/usr/bin`、`/usr/sbin`），macOS 10.7+ 起长期存在；无第三方工具、无 Xcode/CLT 必需依赖。
- **宿主边界**：`.pkg` 制作必须 macOS 宿主（无跨宿主替代）；非 macOS 宿主请求明确 `NotSupportedException`。
- **安装宿主边界**：`system` 域安装需管理员授权；`current-user-home` 域不需提权（本机 uid 501 无 sudo，系统域真实安装属外部待验收）。
- **后端下限**：netstandard2.0 库，同 `.app`/`.dmg` 口径。
- **入口下限**：MSBuild=macOS 14（.NET 10 SDK）；CLI=macOS 宿主（`pkgbuild`/`productbuild` 依赖；dotnet tool 需 .NET 10 运行时，或 macos-universal AOT 二进制）。

## 3. 语义契约

- 输入契约：`.pkg` 后端载荷可以是 `.app` 中间产物（请求 `Pkg` 时规划器自动补 `App`）或直接文件树；不改写 `.app` 内容、不重复签名 `.app` 内部。
- 失败语义：构建任何一步失败 → 清理临时分发/组件产物，不留伪 `.pkg`。
- 安装语义：安装记录进 `/var/db/receipts`（`pkgutil --pkgs`/`--files` 可查）；升级=覆盖安装；卸载=BOM 清单 + `pkgutil --forget`，不承诺一键卸载。
- 输出：`OutputDirectory/<rid>/pkg/<产品名>.pkg`。
- 形态：默认单组件组件包；启用分发特性时分发包（`distribution.xml` 引用内部组件包）。

与 `.app`/`.dmg` 的用户可观察差异（选型参照）：

| 维度 | `.app`（可含于 `.dmg` 分发） | `.pkg` |
| --- | --- | --- |
| 安装模型 | 拖放拷贝，无安装事务 | Installer 受管安装，写入收据库 |
| 目标位置 | 用户自选（通常 `/Applications` 或 `~/Applications`） | 可指向 `/Applications`、`/Library`、`/usr/local`、`$HOME` 等任意域，含系统域 |
| 权限 | 不需提权 | 系统域/受管载荷需管理员授权，可后台/静默安装（`installer -pkg`） |
| 生命周期 | 卸载=删除 `.app`，无收据；升级=整体替换 | `pkgutil` 可枚举/查询/forget；`distribution.xml` 支持需求检查、组件选择、欢迎/许可/结语页 |
| 签名/公证 | Developer ID Application + 公证 `.app` | Developer ID Installer + 公证 `.pkg` 本体 |
| 典型场景 | 桌面 GUI 应用分发 | 命令行工具、守护进程、驱动、企业 MDM 批量部署 |

定位：`.pkg` 是 macOS 上唯一的原生受管安装格式，角色对应 Windows 侧 MSI；`.app`/`.dmg` 对应免安装/拖拽分发面。

## 4. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | `pkgbuild`/`productbuild` 产出、`pkgutil --expand-full`/`xar`/`pkgutil --files` 结构断言、`installer -dominfo`、per-user 域真实安装与收据、失败清理 |
| 人工/外部 | `system` 域管理员安装、Developer ID Installer 真实签名与公证、GUI Installer.app 观感 |

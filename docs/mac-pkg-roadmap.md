# macOS `.pkg` 后端实施路线（MAC-PKG）

> 状态：**`.pkg` 已冻结**（`MAC-PKG-5` 于 2026-09-27 完成，分支 `mac-pkg-development`）。
> 冻结基线：`MacPkgBundleConfiguration`+`MacPkgSigningConfiguration` 配置面与本文档 §3 语义契约在 `0.1.0-alpha.47` 冻结；此后变更走变更控制（能力矩阵改行 + 新测试 + 文档同步）。
> 冻结测试向量：`Bundler.Tests` 122/122 全绿 + `MacPkgIntegrationTests` 全绿（组件包/分发包/页面/域名/per-user 真实安装+收据/签名拒绝路径/scripts 归档与执行/覆盖升级/macos-x86_64 产物/失败清理）。
> `.pkg` 无上游参照：tauri 无 `.pkg` 输出（`PackageType` 仅 `MacOsBundle`/`IosBundle`/`Dmg`/`Updater` + Linux/Windows 各项），决策依据为原生 macOS 工具链语义与 [`docs/mac-format-decision.md`](mac-format-decision.md) 已确认边界。
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
9. **脚本边界**：按 `mac-format-decision.md` §4 既定边界——不开放任意 `preinstall`/`postinstall`；仅保留显式专家旋钮 `ScriptsDirectory`（默认关闭，启用时在文档与日志显著标注责任边界）。
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

## 4. 阶段分解

### MAC-PKG-1：最小可用组件包

- **前置**：MAC-APP/MAC-DMG 已冻结（已完成）。
- **目标/交付**：`Bundler.MacDmg` 同构的 `Bundler.MacPkg` 后端（netstandard2.0，`DotNet.Bundler.MacPkg` 包）；`pkgbuild --root`/`--component` 全链（identifier 默认规则、version、install-location、`--ownership`）；非 macOS 宿主明确拒绝；MSBuild 映射（`BundlerMacPkg*`）与直接 API；`Bundler.Tests` 新用例 + `tests/MacOS.Pkg.Integration` bash 实测（`pkgutil --expand-full` 断言 payload/`PackageInfo`、`xar -tf` 结构、`installer -dominfo` 域名信息）+ 示例 `samples/HelloMacPkg`。
- **不做**：分发包、签名、公证、脚本。
- **退出**：本机真实产出结构可验 `.pkg`；失败路径无残留。
- **验收记录（2026-09-26，macOS 26.5.2 arm64）**：`Bundler.Tests` 109 项全绿（新增 10 条）；`Verify.sh` 全绿——fixture 产出 `macos-arm64/pkg/<产品名>.pkg`（规划器自动先产 `.app`），`pkgutil --expand-full` 断言 payload 含 `.app`+`BundlerPkgPayload` 显式项、`PackageInfo` identifier/version/install-location 默认值与覆盖值回读，`xar -tf` 断言 PackageInfo/Payload/Bom，`installer -dominfo -plist`/`-pkginfo` 解析通过，非法 install-location 使 publish 失败且无 `.pkg` 残留；`samples/HelloMacPkg` 全旋钮演示实测产出。踩坑修正：组件包无 domains 声明，`installer -dominfo` 断言口径改为"解析通过"（域名属 MAC-PKG-2 分发包）。

### MAC-PKG-2：分发包与页面

- **前置**：MAC-PKG-1 通过。
- **目标/交付**：`productbuild --distribution` 分发包；`distribution.xml` 生成（title、welcome/license/conclusion 页、domains）；`LicenseFile` 复用为 license 页；域名 `system`/`current-user-home`；`current-user-home` 域下非管理员真实安装实测（`installer -pkg -target CurrentUserHomeDirectory`）+ 收据断言。
- **退出**：分发包装载欢迎/许可页在本机可验；per-user 真实安装与 `pkgutil` 收据断言通过。
- **验收记录（2026-09-27，macOS 26.5.2 arm64）**：`Bundler.Tests` 114 项全绿（新增 5 条分发用例）；`Verify.sh` 全绿——分发包变体 `xar -tf` 断言 `Distribution`+`component.pkg`+三页 Resources、Distribution 文档 title/domains 回读、`installer -dominfo -plist` 报告 `currentUserHome`；**真实免提权安装**：`installer -pkg -target CurrentUserHomeDirectory` 装出 `~/Applications/<app>.app`+`support/helper.txt`、应用真实启动、`pkgutil --pkgs/--files --volume ~` 收据断言、`--forget` 清理；`samples/HelloMacPkg` 分发全旋钮实测产出。
  踩坑修正：分发文档必须声明 `<options hostArchitectures="arm64"/>`，否则 Apple Silicon 宿主误报 Rosetta 缺失（`pkg-ref` 无 `arch` 属性）；installer 对 `<relocate>` 标记 bundle 有重定位行为，同 id `.app` 残留会把 payload 装到旧位置，集成测试安装前清理中间副本；per-user 收据在 `~/Library/Receipts` 需 `--volume ~`。

### MAC-PKG-3：签名与公证 + 专家脚本

- **前置**：MAC-PKG-2 通过。
- **目标/交付**：Developer ID Installer 签名（`pkgbuild --sign`/`productsign --sign`，临时钥匙串复用）；`.pkg` 公证（`notarytool`+`stapler`，默认关闭）；`ScriptsDirectory` 专家旋钮。
- **退出**：签名链预检/拒绝路径单测；本机无 Developer ID Installer 证书，真实签名/公证登记外部待验收。

### MAC-PKG-4：原生 E2E 与支持矩阵

- **前置**：MAC-PKG-1..3 完成。
- **目标/交付**：macos-x86_64/macos-arm64 产物矩阵、升级覆盖安装实测、收据/BOM 断言、错误路径清理、文档示例收口。
- **退出**：矩阵实测格子有证据；未测格子限缩声明。

### MAC-PKG-5：审计与格式冻结

- **前置**：MAC-PKG-4 完成。
- **目标/交付**：能力矩阵定稿、外部待办收口、冻结基线；`docs/roadmap.md` 推进到 `LINUX`。
- **退出**：`.pkg` 冻结基线写入本路线与 `PROJECT_CONTEXT.md`。
- **验收记录（2026-09-27）**：上游复核——`.pkg` 无上游参照（tauri 不产 pkg），复核点收敛为宿主工具链口径重核：`pkgbuild`/`productbuild`/`productsign`/`xar`/`pkgutil`/`installer` 全为 macOS 系统自带、`xcrun notarytool`/`stapler` 属 Xcode；能力矩阵定稿无悬空"计划实现"行；OI-01..05 逐条复核维持登记口径；冻结基线写入本路线与 `PROJECT_CONTEXT.md`；`docs/roadmap.md` 推进 `LINUX`；包版本 `0.1.0-alpha.47`。

## 5. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | `pkgbuild`/`productbuild` 产出、`pkgutil --expand-full`/`xar`/`pkgutil --files` 结构断言、`installer -dominfo`、per-user 域真实安装与收据、失败清理 |
| 人工/外部 | `system` 域管理员安装、Developer ID Installer 真实签名与公证、GUI Installer.app 观感 |

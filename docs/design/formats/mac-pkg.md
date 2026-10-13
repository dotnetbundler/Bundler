# macOS `.pkg` 安装包

本文件是 `.pkg` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
`.pkg` 无上游参照（Tauri 无此输出），能力集由原生 macOS 工具链语义与已确认边界决定。

## 1. 状态与决策

- `.pkg` 已冻结（基线 `0.1.0-alpha.47`），此后变更走变更控制（改行 + 新测试 + 文档同步）。
- C# 原生编排 `pkgbuild`/`productbuild`/`productsign`/`xar`/`pkgutil`/`installer`（全系统自带，零再分发成本）。
- 包结构策略：默认纯组件包（`pkgbuild` 直出）；配置分发特性（欢迎/许可/结语页、标题、域名选择）时自动升级为 `productbuild`+`distribution.xml` 分发包；调用方看到同一个 `.pkg` 输出路径。
- 载荷默认取 `.app` 中间产物（`Pkg` 自动补 `App`），也支持任意文件树映射（`BundlerMacPkgFile` 项的 `Destination` 落到 install-location 相对路径）；安装位置默认 `/Applications`，可配 `--install-location`（相对路径拒绝）。
- identifier/version 默认取 `BundlerIdentifier`/`BundlerVersion`（单一 `.app` 载荷可源自其 `CFBundleIdentifier`/`CFBundleShortVersionString`）；`BundlerMacPkgPackageName`/`BundlerMacPkgVersion` 可覆盖。
- 定位：`.pkg` 是 macOS 唯一原生受管安装格式，角色对应 Windows 侧 MSI；`.app`/`.dmg` 对应免安装/拖拽分发面。

## 2. 宿主门槛

- `.pkg` 制作必须 macOS 宿主，非 macOS 明确 `NotSupportedException`；工具全部系统自带（`/usr/bin`/`/usr/sbin`），macOS 10.7+ 起长期存在。
- `system` 域安装需管理员授权；`current-user-home` 域非管理员可装（`~/Applications` 等）。
- 后端 `netstandard2.0`；MSBuild 入口需 .NET 10 SDK。

## 3. 语义契约

- 安装记录进 receipts 库（`pkgutil --pkgs`/`--files` 可查；per-user 写 `~/Library/Receipts`）。
- **升级=覆盖安装**（收据版本随装更新），无降级保护、无自动卸载；`pkgutil --forget` 只清收据不删文件，不承诺一键卸载。
- 已记录边界：宿主存在同 `CFBundleIdentifier` 既有 `.app` 副本时，installer 会将新载荷**重定位**到既有副本位置而非默认目标——系统原生语义。
- 失败清理临时产物，不留伪 `.pkg`。
- 产物命名 `名称-版本-架构.pkg`。

## 4. 能力状态

已实现并验证：

- 组件包/分发包全链（`expand-full`/`xar` 结构断言）；welcome/license/conclusion 页（`.txt`/`.html`/`.rtf`，license 复用公共 `LicenseFile`）；分发标题。
- `system`/`current-user-home` 双域；per-user 域非管理员真实安装 + 收据断言；system 域 sudo `installer` 真装/卸载已由集成腿实证。
- Developer ID Installer 签名：组件包 `pkgbuild --sign --timestamp`、分发包 `productsign --sign`；`"-"` ad-hoc **明确拒绝**（`.pkg` 无 ad-hoc 等价物）；临时钥匙串导入/销毁复用 `MacAppSigning`；无效身份真实失败已验。
- `.pkg` 公证接线（`notarytool submit` 直收 `.pkg` + `stapler`；默认关；凭证三模式与 `APPLE_*` 回退同 `.app`）——**installer 包是 Gatekeeper 实际验签对象**（与 `.dmg` 结论相反）。
- `ScriptsDirectory` 专家旋钮（默认关）：`pkgbuild --scripts` 原样收目录，`postinstall` 在 per-user 真装中执行已验。
- 覆盖升级实测（收据版本更新）；`macos-x86_64` 产物结构与 CI 运行态实测；universal 通用目标可产。

外部待验收（详见 `docs/project/special-acceptance.md`）：

- Developer ID Installer 真实签名与公证全链；GUI Installer.app 观感；干净宿主验证。

## 5. 明确拒绝

- `requirements`/`choices` 任意 XML 编辑面（有真实需求再评估）。
- 默认面的任意 `preinstall`/`postinstall`（提权任意脚本风险等价 MSI 自定义动作，仅专家模式且显著标注责任边界）。
- 一键卸载（`.pkg` 无此语义）。

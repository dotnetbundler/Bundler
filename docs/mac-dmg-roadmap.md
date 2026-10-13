# macOS `.dmg` 后端实施路线（MAC-DMG）

> 状态：**`MAC-DMG-1`..`MAC-DMG-5` 已完成，`.dmg` 格式已冻结（2026-09-26，冻结基线 `0.1.0-alpha.45`）**。
> 上游审计已完成（`.dmg` 节，Tauri 固定快照 `7dbfc1f`）；
> `.app` 侧已冻结的契约见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/mac-dmg-capability-matrix.md`](mac-dmg-capability-matrix.md)；外部条件与人工/特殊验收统一登记在 [`docs/special-acceptance.md`](special-acceptance.md)。

## 1. 已确认决策（2026-09-26，全部落定）

1. **实现方式**：C# 原生编排 `hdiutil`/`osascript`/`SetFile`/`sips`，不内嵌上游 create-dmg fork（与"工具走宿主检测不内嵌第三方"策略一致，参数全可控）。
2. **DMG 本体签名/公证**：DMG 支持 `codesign`（复用 `.app` 签名配置面，`Identity="-"` 跳过）；DMG 本体不做公证——Gatekeeper 核验的是内部 `.app` 的 stapled 票据（与上游一致）。
3. **Finder 布局降级**：探测无 GUI 会话时跳过布局+警告、仍产可挂载 DMG；显式开关 `BundlerDmgSkipWindowLayout`（`BundlerMacDmgSkipWindowLayout` MSBuild 属性）。
4. **EULA**：支持 `LicenseFile`（txt/rtf）经 `hdiutil udifrez` 注入 SLA 资源，挂载时弹同意/不同意；复用公共模型 `LicenseFile`。
5. **压缩格式**：可配置枚举 `Udzo`/`Ulmo`/`Udbz`，**默认 `Ulmo`**（LZFSE，更小更快，挂载侧需 macOS 10.12+，今天在支持期的 Mac 全覆盖）；目标史前宿主（<10.12）分发时显式改 `Udzo`。`bless`/`internet-enable` 遗留项不暴露。
6. **产物命名**：`OutputDirectory/<rid>/dmg/<产品名>.dmg`（不带版本/架构，与 `.app` 输出契约一致；不沿用上游 `名称_版本_架构` 写法，因输出目录已区分架构且文件名带版本破坏重建指纹）。
7. **窗口布局可配置面**：窗口尺寸/位置、`.app` 图标位、`/Applications` 图标位、图标大小、窗口背景图（png/jpg/gif）、卷图标 `.VolumeIcon.icns`，全部可选、默认对齐上游（660×400、app=180,170、Applications=480,170、图标 128）。

## 2. 打包工具下限（三层口径）

- **打包工具（能力）下限**：`hdiutil` 自 Mac OS X 10.0 起存在；`osascript`+Finder 布局、`udifrez`、`sips` 均为长期系统自带；`SetFile`（卷图标 `icnC`）属 Xcode/CLT 附带，**可降级**（缺失跳过卷图标不阻塞）。链路无实质版本下限（arm64 宿主天然 ≥11.0）
- **产物可挂载下限**（产出物侧，由调用方压缩选型决定）：默认 `Ulmo` 要求挂载宿主 macOS 10.12+；选 `Udzo` 可到 OS X 10.1。
- **宿主边界**：DMG 制作必须 macOS 宿主（`hdiutil`/`osascript` 不可跨宿主）——与未签名 `.app` 跨宿主构建不同；非 macOS 宿主请求 DMG 明确拒绝。
- **后端下限**：netstandard2.0 库，同 `.app` 口径（官方 macOS 14+）。
- **入口下限**：MSBuild=macOS 14（.NET 10 SDK）；CLI=macOS 宿主（`hdiutil` 依赖；dotnet tool 需 .NET 10 运行时，或 macos-universal AOT 二进制）。

## 3. 语义契约

- 输入契约：DMG 后端只接受已存在的 `.app` 目录（规划器在请求 `Dmg` 时自动补 `App` 中间产物）；不重复签名 `.app` 内部、不改写 `.app` 内容。
- 失败语义：构建任何一步失败 → 卸载残留卷、删临时 `.dmg`，不留伪产物。
- 安装/升级/卸载语义：`.dmg` 无安装事务，沿用 `.app` 拖放惯例（受管安装归 MAC-PKG）。
- 输出：`OutputDirectory/<rid>/dmg/<产品名>.dmg`。
- 卷内容标准形态：`.app` + `/Applications` 拖放符号链接 + 隐藏 `.app` 扩展名；可选窗口背景图、卷图标、EULA。

## 4. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | DMG 生成、`hdiutil attach/verify`、卷内容断言、布局写盘、签名验签、失败清理 |
| 人工/外部 | 真实 Finder 双击挂载观感截图、DMG 本体公证（如启用）、Intel/Rosetta 宿主挂载 |
EOF
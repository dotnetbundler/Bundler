# macOS `.dmg` 镜像

本文件是 `.dmg` 后端的唯一文档：决策、语义契约、能力状态与外部待验收。
`.app` 侧契约见 `docs/formats/mac-app.md`；当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准。

## 1. 状态与决策

- `.dmg` 格式已冻结（基线 `0.1.0-alpha.45`），仅缺陷修复附回归测试。
- C# 原生编排 `hdiutil`/`osascript`/`SetFile`/`sips`，不内嵌 create-dmg 类第三方 fork（参数全可控，与宿主检测策略一致）。
- DMG 本体可 `codesign`（复用 `.app` 签名配置面，`Identity="-"` 跳过）；**DMG 本体不做公证**——Gatekeeper 核验内部 `.app` 的 stapled 票据。
- EULA：`LicenseFile`（txt/rtf）经 `hdiutil udifrez` 注入 SLA 资源，挂载时弹同意面板。
- 压缩枚举 `Udzo`/`Ulmo`/`Udbz`，**默认 `Ulmo`**（lzma；挂载侧需 macOS 10.15+；史前宿主显式改 `Udzo`）。
- 产物命名 `名称-版本-架构.dmg`（契约以 `docs/targets.md` 为准）；`bless`/`internet-enable` 遗留开关不暴露。

## 2. 宿主门槛

- DMG 制作**必须 macOS 宿主**（`hdiutil`/`osascript` 不可跨宿主），非 macOS 请求明确 `NotSupportedException`——与未签名 `.app` 任意宿主可产不同。
- 打包工具侧无实质版本下限（arm64 宿主天然 ≥11.0）；`SetFile`（卷图标）属 Xcode/CLT 附带，缺失降级跳过不阻塞。
- 产物可挂载下限由压缩选型决定：`Ulmo` 需 macOS 10.15+（hdiutil(1) 明文），`Udzo` 可到 OS X 10.1。
- 后端 `netstandard2.0`；MSBuild 入口需 .NET 10 SDK。

## 3. 语义契约

- 只接受已存在 `.app` 目录输入（规划器请求 `Dmg` 时自动补 `App` 中间产物）；不重复签名 `.app` 内部、不改写其内容。
- 任何一步失败：卸载残留卷 + 删临时 `.dmg`，不留伪产物。
- 无安装事务，沿用 `.app` 拖放惯例（受管安装归 `.pkg`）。
- 卷标准形态：`.app` + `/Applications` 拖放符号链接 + 隐藏 `.app` 扩展名；可选窗口背景图、卷图标、EULA。

## 4. 能力状态

已实现并验证：

- `hdiutil create`→挂载→`convert` 全链；`/Applications` 符号链接；隐藏扩展名；失败清理。
- Finder 布局：窗口尺寸/位置、`.app` 与 `/Applications` 图标位、图标大小、背景图（png/jpg/gif）、卷图标 `.VolumeIcon.icns`+`SetFile -c icnC`；默认对齐上游（660×400、app=180,170、Applications=480,170、图标 128）。
- **无 GUI 会话降级**：探测无 GUI 跳过布局+警告仍产可挂载 DMG；显式开关 `BundlerMacDmgLayoutSkip`。
- DMG 本体 `codesign` ad-hoc 验签实测；`udifrez` SLA 注入 + `udifderez` 回读断言实测。
- x86_64/arm64/universal 三架构产物结构与挂载实测；x64 运行态启动 CI 两腿通过。
- quarantine 挂载+拷出传播断言实测。
- 横切：独立 `Bundler.MacDmg` 包、`BundlerMacDmg*` MSBuild 属性、全链离线、工具探测门槛。

外部待验收（详见 `docs/special-acceptance.md`）：

- 真实 Finder 双击挂载观感；EULA 弹窗观感；真实证书签名。
- Intel/Rosetta 宿主挂载、干净宿主验证。

## 5. 有意排除

- DMG 本体公证（内部 `.app` stapled 票据足够）。
- 内嵌 create-dmg fork（宿主检测策略冲突）。
- `bless`/`internet-enable` 遗留开关。

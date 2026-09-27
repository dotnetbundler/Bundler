# macOS `.dmg` 后端实施路线（MAC-DMG）

> 状态：**`MAC-DMG-1`..`MAC-DMG-4` 已完成（2026-09-26 云 macOS VM 实测），`MAC-DMG-5` 待启动指令**。
> 上游审计见 [`docs/mac-tauri-capability-audit.md`](mac-tauri-capability-audit.md) 的 `.dmg` 节（同一 `7dbfc1f` 快照基线）；
> `.app` 侧已冻结的契约见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/mac-dmg-capability-matrix.md`](mac-dmg-capability-matrix.md)；外部条件见 [`docs/mac-dmg-open-items.md`](mac-dmg-open-items.md)；人工步骤见 [`docs/mac-dmg-manual-testing.md`](mac-dmg-manual-testing.md)。

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
- **入口下限**：MSBuild=macOS 14（.NET 10 SDK）；CLI 待定。

## 3. 语义契约

- 输入契约：DMG 后端只接受已存在的 `.app` 目录（规划器在请求 `Dmg` 时自动补 `App` 中间产物）；不重复签名 `.app` 内部、不改写 `.app` 内容。
- 失败语义：构建任何一步失败 → 卸载残留卷、删临时 `.dmg`，不留伪产物。
- 安装/升级/卸载语义：`.dmg` 无安装事务，沿用 `.app` 拖放惯例（受管安装归 MAC-PKG）。
- 输出：`OutputDirectory/<rid>/dmg/<产品名>.dmg`。
- 卷内容标准形态：`.app` + `/Applications` 拖放符号链接 + 隐藏 `.app` 扩展名；可选窗口背景图、卷图标、EULA。

## 4. 阶段分解

### MAC-DMG-1：最小可用镜像

- **前置**：MAC-APP 已冻结（已完成）。
- **目标/交付**：`Bundler.MacDmg` 后端（netstandard2.0，`DotNet.Bundler.MacDmg` 包）；`hdiutil create -srcfolder`(UDRW)→resize→挂载→写入 `/Applications` 符号链接+隐藏 `.app` 扩展名→detach（EBUSY 指数退避）→`convert` 只读压缩（默认 `Ulmo`，可配 `Udzo`/`Udbz`）全链；非 macOS 宿主明确拒绝；MSBuild 映射与直接 API；`Bundler.Tests` 新用例 + `tests/MacOS.Dmg.Integration` bash 实测（构建→`hdiutil attach` 挂载→断言卷内容→detach→`hdiutil verify`）+ 示例 `samples/HelloMacDmg`（`BundlerFormats=dmg`，顺带演示 `.app` 中间产物自动产出）。
- **不做**：Finder 布局、背景/卷图标、EULA、DMG 签名。
- **退出**：本机真实产出可挂载/可校验 DMG；失败路径无残留卷与伪产物。
- **验收记录（2026-09-26，云 macOS VM 26.5.2 arm64）**：`Bundler.Tests` 90/90 全绿（新增 9 条 dmg 用例：格式/宿主/卷名校验、链序、压缩枚举映射、detach 重试与耗尽清理、convert 失败无产物、MSBuild 映射）；`tests/MacOS.Dmg.Integration/Verify.sh` 全绿（真实产出 `.dmg`→attach→断言 `.app`+`/Applications` 链接→卷内启动→detach→`hdiutil verify` VALID→Udzo 变体 `Format: UDZO`）；`samples/HelloMacDmg` 实测产出 `.app`+`.dmg` 且挂载校验通过。踩坑修正：集成 fixture 在只读卷内写标记文件会崩（改 try/catch）；`unzip -l | grep -q` 在 pipefail 下偶发 SIGPIPE（改先落盘再 grep）。文件名 sanitize 复用 `MacAppBundleBackend.SanitizeFileName`（改 internal + InternalsVisibleTo）。

### MAC-DMG-2：Finder 布局与品牌

- **前置**：MAC-DMG-1 通过。
- **目标/交付**：`osascript` 窗口布局（尺寸/位置/图标位/图标大小/隐藏扩展名/背景图拷贝+写 `.DS_Store`）；`.VolumeIcon.icns`+`SetFile -c icnC`（SetFile 缺失降级警告）；GUI 会话探测失败→跳过布局+警告；`BundlerDmgSkipWindowLayout` 显式开关。
- **退出**：本机实测布局写盘与降级分支均走通。
- **验收记录（2026-09-26，云 macOS VM 26.5.2 arm64）**：`Bundler.Tests` 95/95 全绿（新增 5 条：默认布局脚本断言/无 GUI 降级/显式跳过/品牌落盘/缺失背景拒绝）。`Verify.sh` 全绿：品牌文件 `.background/bg.png`+`.VolumeIcon.icns` 落卷断言通过，`.DS_Store` 断言在无 GUI 宿主按设计条件跳过（headless `osascript` 返回 -1728 `Can't get disk` 降级为警告），UDZO/SkipWindowLayout 变体照常验证。布局参数全可配（窗口 200,120+660×400、app=180,170、Applications=480,170、icon 128 默认对齐上游）。踩坑修正两条：(a) `hdiutil resize -size +64m` 相对增量在 `-srcfolder` 产出的大镜像上被报 `Invalid argument`，改走 `resize -limits` 取当前扇区数 +131072（64MiB）后用 `-sectors` 绝对值增长；(b) `attach -nobrowse` 会隐藏卷使 Finder `Can't get disk`，布局运行时去掉该参数。

### MAC-DMG-3：签名与 EULA

- **前置**：MAC-DMG-2 通过。
- **目标/交付**：DMG 本体 `codesign`（复用 `.app` 签名配置/identity/临时钥匙串，`--timestamp`）；`LicenseFile`→`hdiutil udifrez` SLA 注入；与已签名 `.app` 的组合验证。
- **退出**：ad-hoc 签名 DMG 本机验签通过；EULA 注入后挂载弹许可（或 `udifrez` 断言）。
- **验收记录（2026-09-26，云 macOS VM 26.5.2 arm64）**：`Bundler.Tests` 99/99 全绿（新增 4 条：SLA 注入+codesign 顺序、签名互斥拒绝、缺失许可拒绝、SLA plist 结构/LPic 字节/RTF 分支/非 ASCII 告警）。`Verify.sh` 全绿：EULA+ad-hoc 变体 `udifderez -xml` 回读 LPic/STR#/TEXT 三类资源、`codesign --verify` 通过、`codesign -dvvv` 报 `Signature=adhoc`。`samples/HelloMacDmg` 补 `BundlerLicenseFile`（默认 `Assets/eula.txt`，`HelloMacDmgLicenseFile=none` 关闭）与 `BundlerMacDmgSign*`（`HelloMacDmgDmgSignIdentity=-` 直接演示 ad-hoc），实测产出签名 DMG。踩坑修正三条（均实测确认）：(a) `udifrez` 镜像必须走 `-image` 旗标，位置参数报 "no image specified"；(b) `udifrez` 只认转换后 UDIF 镜像，UDRW 报 `Function not implemented (78)`，EULA 注入排在 convert 之后；(c) SLA `LPic`/`STR#` 字节格式按上游 `dmg-license` 源码实测订正（`resID−5000` 相对编号 + doubleByte 字段），`udifderez` 回读通过。`MacAppSigning.TemporaryKeychain` 提取为 internal 供 MacDmg 经 InternalsVisibleTo 复用；identity/临时证书互斥与临时证书路径存在性在预检拒绝。真实证书签名与挂载观感弹窗登记 MAC-DMG-OI-01/OI-02。

### MAC-DMG-4：原生 E2E 与支持矩阵

- **前置**：MAC-DMG-1..3 完成。
- **目标/交付**：osx-x64/osx-arm64 产物、quarantine/首次挂载行为、错误路径清理断言、文档与示例收口。
- **退出**：矩阵实测格子有证据；未测格子限缩声明。
- **验收记录（2026-09-26，云 macOS VM 26.5.2 arm64）**：`Verify.sh` 全绿扩展——(a) SLA 真实挂载门控：`hdiutil attach` stdin 关闭时被 EULA 取消（"attach canceled"），回 `Y` 即挂载且卷内容齐全；(b) quarantine 传播：对 DMG 写 `com.apple.quarantine` 后挂载，拷出的 `.app` 携带隔离属性（Gatekeeper 分发语义成立）；(c) `osx-x64` 变体：产物存在、可挂载、内部载荷 `file` 断言 x86_64 Mach-O（本机无 Rosetta，运行态启动属 MAC-DMG-OI-03）；(d) 失败路径：非法 `BundlerMacDmgCompression` 值 publish 失败、无 `.dmg` 产物、无残留挂载。`Bundler.Tests` 99/99 仍全绿。干净宿主复核：后端必需工具仅 `hdiutil`/`osascript`（品牌/布局可选 `SetFile`，签名可选 `codesign`/`security`），均无 Xcode/CLT 必需依赖；GUI 观感、Intel/Rosetta 宿主、干净宿主首启仍登记外部待验收（MAC-DMG-OI-01/03/04）。

### MAC-DMG-5：审计与格式冻结

- **前置**：MAC-DMG-4 完成。
- **目标/交付**：上游漂移复核、能力矩阵定稿、冻结基线；`docs/roadmap.md` 推进到 `MAC-PKG`。

## 5. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | DMG 生成、`hdiutil attach/verify`、卷内容断言、布局写盘、签名验签、失败清理 |
| 人工/外部 | 真实 Finder 双击挂载观感截图、DMG 本体公证（如启用）、Intel/Rosetta 宿主挂载 |
EOF
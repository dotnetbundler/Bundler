# macOS `.dmg` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
计划实现与外部待验收行绑定到 `docs/mac-dmg-roadmap.md`、`docs/mac-dmg-open-items.md`、`docs/mac-dmg-manual-testing.md` 中的明确阶段/ID。
上游参照：Tauri 审计 `.dmg` 节（快照 `7dbfc1f`）。
路线已确认（`2026-09-26`）；`MAC-DMG-1`..`MAC-DMG-5` 已完成，**`.dmg` 配置与行为基线冻结于 `0.1.0-alpha.45`**（2026-09-26，冻结测试向量 = `Bundler.Tests` 99/99 全绿 + `MacDmgIntegrationTests` 全绿）；冻结后仅缺陷修复附回归测试；外部待验收行维持登记口径不变。

## 镜像与内容

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.dmg` 产物（只读压缩镜像） | 已实现 | MAC-DMG-1 | `hdiutil create`→挂载→`convert`；产物 `OutputDirectory/<rid>/dmg/<产品名>.dmg` |
| `.app` 中间产物输入 | 已实现 | MAC-DMG-1 | 规划器自动补 `App` 步骤；只读输入，不改写 `.app` |
| `/Applications` 拖放符号链接 | 已实现 | MAC-DMG-1 | 卷内标准拖放安装形态 |
| 隐藏 `.app` 扩展名 | 已实现 | MAC-DMG-1 | Finder 侧不显示扩展名 |
| 非 macOS 宿主拒绝 | 已实现 | MAC-DMG-1 | `hdiutil`/`osascript` 不可跨宿主，明确 `NotSupportedException` |
| 压缩格式枚举（`Udzo`/`Ulmo`/`Udbz`，默认 `Ulmo`） | 已实现 | MAC-DMG-1 | Ulmo 挂载侧需 macOS 10.12+；史前宿主改 `Udzo` |
| 失败清理（残留卷/临时镜像） | 已实现 | MAC-DMG-1 | 卸载残留卷+删临时文件，不留伪产物 |

## Finder 布局与品牌

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `osascript` 窗口布局（尺寸/位置/图标位/图标大小） | 已实现 | MAC-DMG-2 | 默认 660×400、app=180,170、Applications=480,170、图标 128；写 `.DS_Store`；无 GUI 宿主实测降级 |
| 窗口背景图（png/jpg/gif） | 已实现 | MAC-DMG-2 | 拷入卷内 `.background/` 并由 `.DS_Store` 引用 |
| 卷图标 `.VolumeIcon.icns` + `SetFile -c icnC` | 已实现 | MAC-DMG-2 | `SetFile` 属 Xcode/CLT 附带，缺失降级警告跳过；本机实测落卷 |
| 无 GUI 会话降级 | 已实现 | MAC-DMG-2 | 跳过布局+警告仍产可挂载 DMG；`BundlerMacDmgSkipWindowLayout` 显式开关 |

## 签名与许可

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| DMG 本体 `codesign`（identity/临时钥匙串，`-` 跳过） | 已实现 | MAC-DMG-3 | ad-hoc 验签与 `Signature=adhoc` 本机实测；真实证书为外部待验收（MAC-DMG-OI-02） |
| EULA 许可面板（`hdiutil udifrez` SLA 注入） | 已实现 | MAC-DMG-3 | 复用 `LicenseFile`（txt/rtf）；`udifderez` 回读断言本机实测；挂载弹窗观感为外部待验收（MAC-DMG-OI-01） |
| DMG 本体公证 | 不适用 | — | 已确认不做：Gatekeeper 核验内部 `.app` 的 stapled 票据 |

## Bundler 通用横切

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 独立后端包与直接 API | 已实现 | MAC-DMG-1 | `DotNet.Bundler.MacDmg`（netstandard2.0） |
| MSBuild 集成映射 | 已实现 | MAC-DMG-1 | `BundlerMacDmg*` 属性 |
| 离线构建 | 已实现 | MAC-DMG-1 起 | 全链离线，无第三方内嵌工具 |
| 宿主工具探测与版本门槛 | 已实现 | MAC-DMG-1 | 缺必需工具明确报错；可选工具降级警告 |
| osx-x64/osx-arm64/osx 产物 | 已实现 | MAC-DMG-4、`e7dfeae` | 双产物+裸 `osx` 通用目标（fat 载荷前置）；结构+挂载实测；x64 运行态启动 2026-10 CI 两腿实跑通过；观感仍人工（MAC-DMG-OI-03） |
| quarantine/首挂载行为 | 已实现 | MAC-DMG-4 | 带 `com.apple.quarantine` 挂载+拷出传播断言实测；GUI 观感属 MAC-DMG-OI-01 |

## 有意排除

| 项 | 说明 |
| --- | --- |
| `bless`/`internet-enable` 遗留开关 | 上游亦未暴露；压缩格式已开放 `Udzo`/`Ulmo`/`Udbz` 三选一 |
| DMG 本体公证 | 已确认不做（内部 `.app` stapled 票据足够） |
| 内嵌 create-dmg fork | 与宿主检测策略冲突，C# 原生编排替代 |
| `名称_版本_架构` 命名 | 与输出目录契约冲突，不沿用 |

# macOS `.dmg` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
计划实现与外部待验收行绑定到 `docs/mac-dmg-roadmap.md`、`docs/mac-dmg-open-items.md`、`docs/mac-dmg-manual-testing.md` 中的明确阶段/ID。
上游参照：`docs/mac-tauri-capability-audit.md` 的 `.dmg` 节。
路线已确认（`2026-09-26`），各阶段未启动，表内全部为非冻结状态。

## 镜像与内容

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.dmg` 产物（UDZO 只读压缩镜像） | 计划实现 | MAC-DMG-1 | `hdiutil create`→挂载→`convert UDZO`；产物 `OutputDirectory/<rid>/dmg/<产品名>.dmg` |
| `.app` 中间产物输入 | 计划实现 | MAC-DMG-1 | 规划器自动补 `App` 步骤；只读输入，不改写 `.app` |
| `/Applications` 拖放符号链接 | 计划实现 | MAC-DMG-1 | 卷内标准拖放安装形态 |
| 隐藏 `.app` 扩展名 | 计划实现 | MAC-DMG-1 | Finder 侧不显示扩展名 |
| 非 macOS 宿主拒绝 | 计划实现 | MAC-DMG-1 | `hdiutil`/`osascript` 不可跨宿主，明确 `NotSupportedException` |
| 失败清理（残留卷/临时镜像） | 计划实现 | MAC-DMG-1 | 卸载残留卷+删临时文件，不留伪产物 |

## Finder 布局与品牌

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `osascript` 窗口布局（尺寸/位置/图标位/图标大小） | 计划实现 | MAC-DMG-2 | 默认 660×400、app=180,170、Applications=480,170、图标 128；写 `.DS_Store` |
| 窗口背景图（png/jpg/gif） | 计划实现 | MAC-DMG-2 | 拷入卷内 `.background/` |
| 卷图标 `.VolumeIcon.icns` + `SetFile -c icnC` | 计划实现 | MAC-DMG-2 | `SetFile` 属 Xcode/CLT 附带，缺失降级警告跳过 |
| 无 GUI 会话降级 | 计划实现 | MAC-DMG-2 | 跳过布局+警告仍产可挂载 DMG；`BundlerDmgSkipWindowLayout` 显式开关 |

## 签名与许可

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| DMG 本体 `codesign`（identity/临时钥匙串，`-` 跳过） | 计划实现 | MAC-DMG-3 | 复用 `.app` 签名配置面；`--timestamp` |
| EULA 许可面板（`hdiutil udifrez` SLA 注入） | 计划实现 | MAC-DMG-3 | 复用 `LicenseFile`（txt/rtf），挂载时弹同意/不同意 |
| DMG 本体公证 | 不适用 | — | 已确认不做：Gatekeeper 核验内部 `.app` 的 stapled 票据 |

## Bundler 通用横切

| 能力 | 冻结状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| 独立后端包与直接 API | 计划实现 | MAC-DMG-1 | `DotNet.Bundler.MacDmg`（netstandard2.0） |
| MSBuild 集成映射 | 计划实现 | MAC-DMG-1 | `BundlerMacDmg*` 属性 |
| 离线构建 | 计划实现 | MAC-DMG-1 起 | 全链离线，无第三方内嵌工具 |
| 宿主工具探测与版本门槛 | 计划实现 | MAC-DMG-1 | 缺必需工具明确报错；可选工具降级警告 |
| osx-x64/osx-arm64 产物 | 计划实现 | MAC-DMG-4 | 双产物矩阵实测 |
| quarantine/首挂载行为 | 计划实现 | MAC-DMG-4 | 带 `com.apple.quarantine` 的挂载观察 |

## 有意排除

| 项 | 说明 |
| --- | --- |
| `UDBZ`/`ULFO`/`ULMO`/`bless`/`internet-enable` 遗留压缩与开关 | 上游亦未暴露；需要时再议（非破坏扩展） |
| DMG 本体公证 | 已确认不做（内部 `.app` stapled 票据足够） |
| 内嵌 create-dmg fork | 与宿主检测策略冲突，C# 原生编排替代 |
| `名称_版本_架构` 命名 | 与输出目录契约冲突，不沿用 |

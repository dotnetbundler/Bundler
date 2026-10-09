# macOS `.dmg` 外部待办索引（MAC-DMG-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/mac-dmg-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/mac-dmg-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MAC-DMG-OI-01 | MAC-DMG-2 | ~~带 GUI Finder 会话的 macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64 GUI 会话——窗口尺寸/图标摆放/卷图标观感截图（MAC-DMG-MT-01）；此轮实测发现并修复了自定义 `-mountpoint` 下布局从未生效、`.DS_Store` 竞态、卷图标被 Finder 剥离三缺陷（PR #2 并入） |
| MAC-DMG-OI-02 | MAC-DMG-3 | Developer ID Application 证书（DMG 本体 codesign 真实验签） | 签名 DMG `codesign --verify`/`spctl` 记录 |
| MAC-DMG-OI-03 | MAC-DMG-4 | Intel 宿主或已激活 Rosetta 的 arm64 宿主 | **部分已验证**：2026-10 macos-26-intel 与 Rosetta CI 两腿 `.dmg` 实跑通过；观感仍人工 |
| MAC-DMG-OI-04 | MAC-DMG-4 | 干净 macOS 宿主（无 Xcode/CLT） | 挂载、EULA 弹窗、拖放安装首启记录 |

相关 `.app` 外部事项（Developer ID 凭证、公证等）登记在 `docs/mac-app-open-items.md`，DMG 继承其结论不重复登记。

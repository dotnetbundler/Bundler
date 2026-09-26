# macOS `.dmg` 外部待办索引（MAC-DMG-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/mac-dmg-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/mac-dmg-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MAC-DMG-OI-01 | MAC-DMG-2 | 带 GUI Finder 会话的 macOS 宿主（本机 headless/SSH 无法驱动 osascript 布局） | MAC-DMG-MT-01 记录：窗口布局观感截图 |
| MAC-DMG-OI-02 | MAC-DMG-3 | Developer ID Application 证书（DMG 本体 codesign 真实验签） | 签名 DMG `codesign --verify`/`spctl` 记录 |
| MAC-DMG-OI-03 | MAC-DMG-4 | Intel 宿主或已激活 Rosetta 的 arm64 宿主 | osx-x64 `.dmg` 挂载+内 `.app` 启动记录 |
| MAC-DMG-OI-04 | MAC-DMG-4 | 干净 macOS 宿主（无 Xcode/CLT） | 挂载、EULA 弹窗、拖放安装首启记录 |

相关 `.app` 外部事项（Developer ID 凭证、公证等）登记在 `docs/mac-app-open-items.md`，DMG 继承其结论不重复登记。

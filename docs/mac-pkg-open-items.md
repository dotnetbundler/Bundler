# macOS `.pkg` 外部待办索引（MAC-PKG-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/mac-pkg-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/mac-pkg-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MAC-PKG-OI-01 | MAC-PKG-2 | 管理员权限的 macOS 宿主（本机 uid 501 无 sudo，无法做 `system` 域真实安装） | `installer -pkg -target /` 安装记录 + `pkgutil` 收据 |
| MAC-PKG-OI-02 | MAC-PKG-3 | Developer ID Installer 证书（与 Application 证书不同型，不可互替） | 签名 `.pkg` `pkgutil --check-signature`/`spctl` 记录 |
| MAC-PKG-OI-03 | MAC-PKG-3 | Apple 公证凭证（`APPLE_*` 或 keychain profile） | `.pkg` 公证通过 + `stapler validate` 记录 |
| MAC-PKG-OI-04 | MAC-PKG-4 | Intel 宿主或已激活 Rosetta 的 arm64 宿主 | osx-x64 `.pkg` 安装+启动记录 |
| MAC-PKG-OI-05 | MAC-PKG-4 | GUI 会话 macOS 宿主 | Installer.app 双击安装观感（欢迎/许可页）截图 |

相关 `.app` 外部事项（Developer ID Application 凭证、`.app` 公证等）登记在 `docs/mac-app-open-items.md`，PKG 继承其结论不重复登记。

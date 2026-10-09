# macOS `.pkg` 外部待办索引（MAC-PKG-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/mac-pkg-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/mac-pkg-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MAC-PKG-OI-01 | MAC-PKG-2 | ~~管理员权限的 macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64（有 sudo）——`sudo installer -pkg -target /` system 域真实安装 + `pkgutil` 收据断言（MAC-PKG-MT-01） |
| MAC-PKG-OI-02 | MAC-PKG-3 | Developer ID Installer 证书（与 Application 证书不同型，不可互替） | 签名 `.pkg` `pkgutil --check-signature`/`spctl` 记录 |
| MAC-PKG-OI-03 | MAC-PKG-3 | Apple 公证凭证（`APPLE_*` 或 keychain profile） | `.pkg` 公证通过 + `stapler validate` 记录 |
| MAC-PKG-OI-04 | MAC-PKG-4 | Intel 宿主或已激活 Rosetta 的 arm64 宿主 | **部分已验证**：2026-10 macos-26-intel 与 Rosetta CI 两腿 `.pkg` 装/启动断言通过；观感仍人工 |
| MAC-PKG-OI-05 | MAC-PKG-4 | ~~GUI 会话 macOS 宿主~~ 已验证 | **已消解**：2026-09-28 GUI 会话——Installer.app 双击安装欢迎/许可/结语页观感截图（MAC-PKG-MT-02） |

相关 `.app` 外部事项（Developer ID Application 凭证、`.app` 公证等）登记在 `docs/mac-app-open-items.md`，PKG 继承其结论不重复登记。

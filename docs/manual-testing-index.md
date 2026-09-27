# 各格式人工验收入口

人工清单按后端/安装格式分开。
可自动化的行为仍需由对应阶段新增自动化测试；人工结果必须附 OS build、架构、Git SHA、产物 SHA-256、操作、日志、结果和清理记录。

| 格式 | 专用路线 | 人工文档 | 能力矩阵与上游审计 | 外部待办 | 用例 ID |
| --- | --- | --- | --- | --- | --- |
| Windows NSIS | [nsis-roadmap.md](nsis-roadmap.md) | [nsis-manual-testing.md](nsis-manual-testing.md) | [能力矩阵](nsis-capability-matrix.md)；[上游参考](nsis-upstream-reference.md) | [nsis-open-items.md](nsis-open-items.md) | 历史 `MT-01..MT-11` 原样保留；新增用 `NSIS-MT-xx`，不得重编号历史引用 |
| Windows MSI | [msi-roadmap.md](msi-roadmap.md) | [msi-manual-testing.md](msi-manual-testing.md) | [能力矩阵](msi-capability-matrix.md)；[Tauri 通用能力审计](msi-tauri-capability-audit.md) | [msi-open-items.md](msi-open-items.md) | `MSI-MT-xx` |
| macOS `.app` | [mac-app-roadmap.md](mac-app-roadmap.md) | [mac-app-manual-testing.md](mac-app-manual-testing.md) | [能力矩阵](mac-app-capability-matrix.md)；[Tauri 通用能力审计](mac-tauri-capability-audit.md)；[PKG 格式决策](mac-format-decision.md) | [mac-app-open-items.md](mac-app-open-items.md) | `MAC-APP-MT-xx` / `MAC-APP-OI-xx` |
| macOS `.dmg` | [mac-dmg-roadmap.md](mac-dmg-roadmap.md) | [mac-dmg-manual-testing.md](mac-dmg-manual-testing.md) | [能力矩阵](mac-dmg-capability-matrix.md)；[Tauri 通用能力审计](mac-tauri-capability-audit.md) | [mac-dmg-open-items.md](mac-dmg-open-items.md) | `MAC-DMG-MT-xx` / `MAC-DMG-OI-xx` |
| macOS `.pkg` | [mac-pkg-roadmap.md](mac-pkg-roadmap.md) | [mac-pkg-manual-testing.md](mac-pkg-manual-testing.md) | [能力矩阵](mac-pkg-capability-matrix.md)；[PKG 格式决策](mac-format-decision.md) | [mac-pkg-open-items.md](mac-pkg-open-items.md) | `MAC-PKG-MT-xx` / `MAC-PKG-OI-xx` |
| Linux `.deb` | [linux-deb-roadmap.md](linux-deb-roadmap.md) | [linux-deb-manual-testing.md](linux-deb-manual-testing.md) | [能力矩阵](linux-deb-capability-matrix.md)；[Tauri 通用能力审计](linux-tauri-capability-audit.md) | [linux-deb-open-items.md](linux-deb-open-items.md) | `LINUX-DEB-MT-xx` / `LINUX-DEB-OI-xx` |
| Linux `.rpm` | [linux-rpm-roadmap.md](linux-rpm-roadmap.md) | [linux-rpm-manual-testing.md](linux-rpm-manual-testing.md) | [能力矩阵](linux-rpm-capability-matrix.md)；[Tauri 通用能力审计](linux-tauri-capability-audit.md) | [linux-rpm-open-items.md](linux-rpm-open-items.md) | `LINUX-RPM-MT-xx` / `LINUX-RPM-OI-xx` |
| Linux `.AppImage` | [linux-appimage-roadmap.md](linux-appimage-roadmap.md) | [linux-appimage-manual-testing.md](linux-appimage-manual-testing.md) | [能力矩阵](linux-appimage-capability-matrix.md)；[Tauri 通用能力审计](linux-tauri-capability-audit.md) | [linux-appimage-open-items.md](linux-appimage-open-items.md) | `LINUX-APPIMAGE-MT-xx` / `LINUX-APPIMAGE-OI-xx` |
| ARCHIVE（zip/tar.gz） | [archive-roadmap.md](archive-roadmap.md) | [archive-manual-testing.md](archive-manual-testing.md) | [能力矩阵](archive-capability-matrix.md) | [archive-open-items.md](archive-open-items.md) | `ARCHIVE-MT-xx` / `ARCHIVE-OI-xx` |

Linux RPM/AppImage 在各自规划轮建立自己的 `<format>-roadmap.md`、`<format>-manual-testing.md`、`<format>-capability-matrix.md`、`<format>-open-items.md`，各用独立格式前缀；
总索引只列入口，不混合测试步骤或结论。

# 各格式人工验收入口

人工清单按后端/安装格式分开。
可自动化的行为仍需由对应阶段新增自动化测试；人工结果必须附 OS build、架构、Git SHA、产物 SHA-256、操作、日志、结果和清理记录。

| 格式 | 专用路线 | 人工文档 | 能力矩阵与上游审计 | 外部待办 | 用例 ID |
| --- | --- | --- | --- | --- | --- |
| Windows NSIS | [nsis-roadmap.md](nsis-roadmap.md) | [nsis-manual-testing.md](nsis-manual-testing.md) | [能力矩阵](nsis-capability-matrix.md)；[上游参考](nsis-upstream-reference.md) | [nsis-open-items.md](nsis-open-items.md) | 历史 `MT-01..MT-11` 原样保留；新增用 `NSIS-MT-xx`，不得重编号历史引用 |
| Windows MSI | [msi-roadmap.md](msi-roadmap.md) | [msi-manual-testing.md](msi-manual-testing.md) | [能力矩阵](msi-capability-matrix.md)；[Tauri 通用能力审计](msi-tauri-capability-audit.md) | [msi-open-items.md](msi-open-items.md) | `MSI-MT-xx` |
| macOS `.app` | [mac-app-roadmap.md](mac-app-roadmap.md) | [mac-app-manual-testing.md](mac-app-manual-testing.md) | [能力矩阵](mac-app-capability-matrix.md)；[Tauri 通用能力审计](mac-tauri-capability-audit.md)；[PKG 格式决策](mac-format-decision.md) | [mac-app-open-items.md](mac-app-open-items.md) | `MAC-APP-MT-xx` / `MAC-APP-OI-xx` |

未来 macOS DMG/PKG（若纳入）和 Linux DEB/RPM/AppImage 各建立自己的 `<format>-roadmap.md`、`<format>-manual-testing.md`、`<format>-capability-matrix.md`、`<format>-open-items.md`，各用独立格式前缀；
总索引只列入口，不混合测试步骤或结论。

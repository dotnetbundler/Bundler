# 各格式人工验收入口

人工清单按后端/安装格式分开。可自动化的行为仍需由对应阶段新增自动化测试；人工结果必须附 OS build、架构、Git SHA、产物 SHA-256、操作、日志、结果和清理记录。

| 格式 | 专用人工文档 | 能力矩阵 | 外部待办 | 用例 ID |
| --- | --- | --- | --- | --- |
| Windows NSIS | [nsis-manual-testing.md](nsis-manual-testing.md) | [nsis-capability-matrix.md](nsis-capability-matrix.md) | [nsis-open-items.md](nsis-open-items.md) | 历史 `MT-01..MT-11` 原样保留；新增用 `NSIS-MT-xx`，不得重编号历史引用 |
| Windows MSI | [msi-manual-testing.md](msi-manual-testing.md) | [msi-capability-matrix.md](msi-capability-matrix.md)；[Tauri 通用能力审计](msi-tauri-capability-audit.md) | [msi-open-items.md](msi-open-items.md) | `MSI-MT-xx` |

未来 macOS `.app`/DMG 和 Linux DEB/RPM/AppImage 各建立自己的 `<format>-manual-testing.md`、`<format>-capability-matrix.md`、`<format>-open-items.md`，各用独立格式前缀；总索引只列入口，不混合测试步骤或结论。

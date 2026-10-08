# Special 证据文件约定

每条特殊验收脚本在结束时把证据写到 `tests/Special/evidence/<验收ID>-<宿主>-<日期>.md`。
回填 `docs/special-acceptance.md` 或 `<format>-open-items.md` 时贴文件内容；文件本身不入库（`evidence/` 已 gitignore）。

## 结构

```markdown
# <验收ID> <标题>
- 日期: 2026-10-08 UTC
- 宿主: Windows 11 23H2 x64 / macOS 15 arm64 / Ubuntu 24.04 x64 ...
- Bundler: 0.1.0-alpha.84 (commit abc1234)
- 凭证/环境: Developer ID Application 证书 / 可丢弃 VM / 公网 feed URL ...
- 人工介入点: 无 / UAC 批准 / 弹窗观察

## 步骤与结果
| 步骤 | 命令/动作 | 结果 | 证据摘录 |
| --- | --- | --- | --- |
| 1 | signtool sign ... | PASS | thumbprint=..., timestamp RFC3161 |

## 产物
- installer.exe sha256=...
- 日志片段: ...

## 结论
PASS / FAIL（失败时附原因与建议处置）
```

## 规则

- 结果一栏只写 `PASS`/`FAIL`/`UNTESTED`，未跑成的步骤也记录（附原因）。
- 凭证只记录拇指印/公钥指纹/证书 Subject，绝不写私钥、密码、token。
- 人工观察项单独成行，写明"观察到 X"与原始证据（截图名、对话框文字）。

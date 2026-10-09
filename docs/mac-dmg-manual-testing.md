# macOS `.dmg` 人工验收清单（MAC-DMG-MT）

以下项目本机无法自动化（需 GUI/专用宿主/真实凭证），逐项人工记录：OS 版本、架构、Git SHA、产物 SHA-256、日志、清理方式。

## 验收步骤（草拟，随阶段推进落实）

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| MAC-DMG-MT-01 | MAC-DMG-2 | GUI Finder 下双击挂载 DMG：窗口尺寸/图标摆放/背景图/卷图标观感核对，拖 `.app` 到 `/Applications` 链接完成安装 | **已验证**（2026-09-28，macOS 26.5.2 arm64 GUI）：观感截图 + `.DS_Store` 读回；伴随修复布局寻址/落盘竞态/卷图标三缺陷（PR #2） |
| MAC-DMG-MT-02 | MAC-DMG-3 | 带 EULA 的 DMG 挂载时弹许可面板（同意继续/不同意中止）；headless 门控（stdin 关闭取消、`Y` 挂载）已自动化，人工剩余 GUI 观感核对 | **已验证**（2026-09-28，GUI 会话）：SLA 许可面板弹出/同意/不同意行为截图与记录 |
| MAC-DMG-MT-03 | MAC-DMG-3 | 真实 Developer ID 签名的 DMG：`codesign --verify` + `spctl` 验签、quarantine 首挂载 | 验签输出 |
| MAC-DMG-MT-04 | MAC-DMG-4 | `osx-x64` DMG 在 Intel/Rosetta 宿主挂载并拖放安装启动 | **部分已验证**（2026-10 CI 两腿挂载/启动断言通过）；观感证据仍人工 |
| MAC-DMG-MT-05 | MAC-DMG-4 | 干净宿主（无 Xcode/CLT）挂载本机产出 DMG、拖放安装、首启 | 宿主环境记录、首启日志 |

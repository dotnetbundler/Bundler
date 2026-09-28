# macOS `.pkg` 人工验收清单（MAC-PKG-MT）

以下项目本机无法自动化（需管理员权限/GUI/专用宿主/真实凭证），逐项人工记录：OS 版本、架构、Git SHA、产物 SHA-256、日志、清理方式。

## 验收步骤（草拟，随阶段推进落实）

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| MAC-PKG-MT-01 | MAC-PKG-2 | `system` 域 `.pkg`：`sudo installer -pkg -target /` 真实安装，载荷落入 `/Applications`，`pkgutil --pkgs`/`--files` 收据断言 | **已验证**（2026-09-28，macOS 26.5.2 arm64 有 sudo）：system 域安装日志 + `pkgutil` 收据 |
| MAC-PKG-MT-02 | MAC-PKG-2 | GUI 双击分发包：Installer.app 欢迎/许可/结语页观感，许可拒绝中止安装 | **已验证**（2026-09-28，GUI 会话）：Installer.app 欢迎/许可页截图 |
| MAC-PKG-MT-03 | MAC-PKG-3 | Developer ID Installer 签名 `.pkg`：`pkgutil --check-signature`、`spctl -a -v -t install`；Gatekeeper 隔离场景首装 | 验签输出 |
| MAC-PKG-MT-04 | MAC-PKG-3 | 公证 `.pkg`：`stapler validate`、quarantine 场景 Gatekeeper 放行记录 | 公证票据与放行日志 |
| MAC-PKG-MT-05 | MAC-PKG-4 | `osx-x64` `.pkg` 在 Intel/Rosetta 宿主安装并启动 | 安装与启动日志 |
| MAC-PKG-MT-06 | MAC-PKG-4 | 干净宿主（无 Xcode/CLT）安装与首启；升级覆盖安装收据版本递增 | 宿主环境记录、收据对比 |

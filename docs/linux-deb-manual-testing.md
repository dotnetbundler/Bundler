# Linux `.deb` 人工验收清单（LINUX-DEB-MT）

以下项目本机无法自动化（需 GUI 桌面环境/专用宿主/真实发行版矩阵），逐项人工记录：OS 版本、架构、Git SHA、产物 SHA-256、日志、清理方式。
本机已可自动化的真实断言（`dpkg -i/-r/-P`、`lintian`、`desktop-file-validate`、docker 容器装卸）进 `tests/Linux.Deb.Integration/Verify.sh`，不在本清单重复。

## 验收步骤（草拟，随阶段推进落实）

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| LINUX-DEB-MT-01 | LINUX-DEB-2 | GUI 桌面宿主安装 `.deb`：应用菜单出现条目与图标、点击启动、`.desktop` 关联的文件/协议双击唤起 | **部分已验证**（2026-09-28，KDE）：菜单/图标/启动截图已记；关联唤起未验（fixture 未声明关联对象） |
| LINUX-DEB-MT-02 | LINUX-DEB-4 | ARM64 宿主（如 ARM 设备/qemu 整机）`dpkg -i` 安装 linux-arm64 产物并启动 | 安装与启动日志 |
| LINUX-DEB-MT-03 | LINUX-DEB-4 | Debian oldstable / 旧 LTS / 非 systemd 发行版宿主装卸；记录 dpkg 版本差异下的行为 | 各宿主安装日志 |
| LINUX-DEB-MT-04 | LINUX-DEB-3 | systemd 宿主：unit 安装后 `daemon-reload`、手动 `systemctl enable --now` 启动应用服务、卸载清 unit | **已验证**（2026-09-28，Ubuntu 宿主）：`daemon-reload`/`enable --now` 真实启动记录 |
| LINUX-DEB-MT-05 | LINUX-DEB-4 | `apt install ./name.deb`（本地文件安装解析依赖路径）与最小 apt 仓库工作流验证 | **已验证**（2026-09-28）：`apt install ./name.deb` 与最小仓库工作流记录 |
| LINUX-DEB-MT-06 | LINUX-DEB-1 | Windows/macOS 宿主构建 `.deb`，在 Linux 宿主安装验证跨宿主产物可用性 | 双宿主构建/安装记录 |

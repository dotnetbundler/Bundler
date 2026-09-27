# Linux `.rpm` 人工验收清单（LINUX-RPM-MT）

> 本清单收编"本机自动断言无法覆盖、需要人工或特定宿主"的验收项。
> 执行时把证据（截图/日志）回填到对应行或 `docs/linux-rpm-open-items.md` 的外部待办。
> ID 前缀 `LINUX-RPM-MT-xx`；登记索引见 `docs/manual-testing-index.md`。

| ID | 建议阶段 | 用例 | 验收证据 |
| --- | --- | --- | --- |
| LINUX-RPM-MT-01 | LINUX-RPM-2 | GUI 桌面宿主 `dnf install`/`rpm -i` 安装 `.rpm`：应用菜单出现条目与图标、点击启动、关联文件/协议双击唤起 | 截图与唤起记录 |
| LINUX-RPM-MT-02 | LINUX-RPM-4 | ARM64 宿主（ARM 设备/qemu 整机）`rpm -i` 安装 aarch64 产物并启动 | 安装与启动日志 |
| LINUX-RPM-MT-03 | LINUX-RPM-4 | 更广发行版宿主（RHEL 8/CentOS Stream/Alma/SUSE 旧版）装卸；记录 rpm 版本差异下的行为 | 各宿主安装日志 |
| LINUX-RPM-MT-04 | LINUX-RPM-3 | systemd 宿主：unit 安装后 `daemon-reload`、`systemctl enable --now` 启动应用服务、卸载清 unit | systemctl 输出 |
| LINUX-RPM-MT-05 | LINUX-RPM-4 | `dnf install ./name.rpm` 本地文件安装解析依赖路径与最小仓库工作流 | dnf 输出日志 |
| LINUX-RPM-MT-06 | LINUX-RPM-4 | Windows/macOS 宿主构建 `.rpm`，在 rpm 宿主安装验证跨宿主产物可用性 | 双宿主构建/安装记录 |
| LINUX-RPM-MT-07 | 后置（若引入签名） | `rpm --import` 公钥 + `rpm -K`/`rpm -v --checksig` 签名验证 | 验证输出 |

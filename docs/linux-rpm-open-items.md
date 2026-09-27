# Linux `.rpm` 外部待办清单（LINUX-RPM-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵/路线图中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `LINUX-RPM-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| LINUX-RPM-OI-01 | LINUX-RPM-4 | ARM64 Linux 宿主（本机 x86_64，无 qemu/ARM 设备） | `linux-arm64` 产物 `rpm -i` 安装与启动记录 |
| LINUX-RPM-OI-02 | LINUX-RPM-4 | GUI 桌面环境宿主（本机无桌面） | 菜单项/图标渲染、关联双击打开观感验收（对应 MT-01） |
| LINUX-RPM-OI-03 | LINUX-RPM-3 | 启用 systemd 的真实宿主（容器内 systemd 不充分） | unit 落位、`systemctl status`/`daemon-reload`/`enable --now` 真实记录（对应 MT-04） |
| LINUX-RPM-OI-04 | LINUX-RPM-4 | 更广发行版宿主矩阵（RHEL 8/CentOS Stream/Alma/SUSE 旧版） | 各宿主 `rpm -i/-e` 行为差异记录（对应 MT-03） |
| LINUX-RPM-OI-05 | LINUX-RPM-1 | Windows/macOS 构建宿主 | 跨宿主产出 `.rpm` 并在 rpm 宿主安装的记录（托管写入器使跨宿主成为设计目标，对应 MT-06） |
| LINUX-RPM-OI-06 | LINUX-RPM-4 | `dnf`/`zypper` 仓库环境 | 本地文件安装与最小仓库工作流验证（对应 MT-05） |
| LINUX-RPM-OI-07 | 后置 | GPG 密钥与签名验证流程 | 若引入包级签名：`rpm --import`/`rpm -K` 验证记录（对应 MT-07） |

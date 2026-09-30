# Linux `.rpm` 外部待办清单（LINUX-RPM-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵/路线图中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `LINUX-RPM-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| LINUX-RPM-OI-01 | LINUX-RPM-4 | ARM64 Linux 宿主（本机 x86_64，无原生 ARM 设备） | **部分已验证**：2026-09-29/30 联合测试——qemu binfmt 仿真 arm64 容器内四产方 `.rpm` 真实装卸+运行输出断言通过；真机 ARM64 宿主仍待验 |
| LINUX-RPM-OI-02 | LINUX-RPM-4 | ~~GUI 桌面环境宿主~~ 已验证 | **已消解**：2026-09-28 KDE 真桌面——菜单项/图标渲染与点击启动观感记录（对应 MT-01）；关联双击唤起未验（fixture 未声明关联对象） |
| LINUX-RPM-OI-03 | LINUX-RPM-3 | ~~启用 systemd 的真实宿主~~ 已验证 | **已消解**：2026-09-28 Ubuntu 宿主——unit 落位、`systemctl status`/`daemon-reload`/`enable --now` 真实记录（对应 MT-04） |
| LINUX-RPM-OI-04 | LINUX-RPM-4 | 更广发行版宿主矩阵（RHEL 8/CentOS Stream/Alma/SUSE 旧版；fedora/rockylinux:9/opensuse-leap 已由 docker 矩阵覆盖） | 各宿主 `rpm -i/-e` 行为差异记录（对应 MT-03） |
| LINUX-RPM-OI-05 | LINUX-RPM-1 | ~~Windows/macOS 构建宿主~~ 已验证 | **已消解**：2026-09-29/30 联合测试——Windows/macOS 宿主产 x86_64/aarch64 `.rpm` 在 fedora/rocky 容器 `rpm -i` 真实装卸+运行全过 |
| LINUX-RPM-OI-06 | LINUX-RPM-4 | ~~`dnf`/`zypper` 仓库环境~~ 已验证 | **已消解**：2026-09-28 fedora/opensuse 容器——`dnf`/`zypper` 本地文件安装 + createrepo_c 最小仓库工作流验证（对应 MT-05） |
| LINUX-RPM-OI-07 | 已消解 | GPG 密钥与签名验证流程 | `SIGN-1` 已实现可选包级签名（`BundlerRpmSigningKeyFile`/`Passphrase` → 双标签 `RPMSIGTAG_RSA`(268)+`RPMSIGTAG_PGP`(1002)），`rpm --import`+`rpm -K` 已实测验签（`digests signatures OK`）；生产密钥的保管与分发仍属发布者自身流程 |

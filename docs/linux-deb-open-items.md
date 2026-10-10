# Linux `.deb` 外部待办索引（LINUX-DEB-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/linux-deb-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/linux-deb-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| LINUX-DEB-OI-01 | LINUX-DEB-4 | ARM64 Linux 宿主（本机 x86_64，无原生 ARM 设备） | **部分已验证**：2026-09-29/30 联合测试——qemu binfmt 仿真 arm64 容器内四产方 `.deb` 真实装卸+运行输出断言通过；真机 ARM64 宿主已验（2026-10 `ubuntu-24.04-arm` CI 腿 `arm64-real-hw.sh` 真实装卸跑绿） |
| LINUX-DEB-OI-02 | LINUX-DEB-4 | 更多发行版宿主/容器（Debian oldstable、非 systemd 发行版、旧 dpkg 版本宿主） | 各发行版 `dpkg -i`/`dpkg -r` 记录与 `lintian` 差异核对 |
| LINUX-DEB-OI-03 | LINUX-DEB-2 | 有桌面环境的 Linux 宿主（GNOME/KDE 任一） | **部分已验证**（2026-09-28，KDE 真桌面）：菜单项、图标、点击启动观感截图已记录；文件关联/URL scheme 双击唤起未验——验收 fixture 未声明任何关联对象，无唤起目标可测 |
| LINUX-DEB-OI-04 | LINUX-DEB-3 | ~~启用 systemd 的真实宿主~~ 已验证 | **已消解**：2026-09-28 Ubuntu 宿主 unit 落位、`systemctl daemon-reload`/`enable --now` 真实启动记录 |
| LINUX-DEB-OI-05 | LINUX-DEB-1 | ~~Windows/macOS 构建宿主~~ 已验证 | **已消解**：2026-09-29/30 联合测试——Windows/macOS 宿主产 amd64/arm64 `.deb` 在 debian/ubuntu 容器 `dpkg -i` 真实装卸+运行全过 |
| LINUX-DEB-OI-06 | LINUX-DEB-4 | ~~apt 仓库与 `apt install ./pkg.deb` 工作流环境~~ 已验证 | **已消解**：2026-09-28 `apt install ./pkg.deb` 本地文件安装与最小仓库工作流记录；"单包可装、仓库管理在外"边界已明 |
| LINUX-DEB-OI-07 | 已收口（DEB-4） | lintian 基线已转硬断言 | DEB-4 消解全部可修发现（`changelog.Debian.gz` 补发、扩展描述默认行、Maintainer 邮箱、Section、copyright 年份）；残余 5 项豁免登记在 `tests/Bundler.IntegrationTests/Fixtures/Deb/lintian-exemptions.txt`（自包含载荷/工具链固有）；更高发行版 lintian 版本复跑属矩阵扩展 |
| LINUX-DEB-OI-08 | LINUX-DEB-3 已登记拒绝 | 若需要 xz/zstd：第三方托管压缩依赖裁决 + 宿主 dpkg 版本矩阵 | `BundlerDebCompression` 放开 xz/zstd 的实现记录；zstd 需安装宿主 dpkg≥1.21.18 |

相关 Linux `.rpm`/AppImage 事项在各自格式的 `linux-rpm-open-items.md`/`linux-appimage-open-items.md` 建立后登记，不混入本文件。

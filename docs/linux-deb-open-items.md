# Linux `.deb` 外部待办索引（LINUX-DEB-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/linux-deb-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/linux-deb-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| LINUX-DEB-OI-01 | LINUX-DEB-4 | ARM64 Linux 宿主（本机 x86_64，无 qemu/ARM 设备） | `linux-arm64` 产物 `dpkg -i` 安装与启动记录 |
| LINUX-DEB-OI-02 | LINUX-DEB-4 | 更多发行版宿主/容器（Debian oldstable、非 systemd 发行版、旧 dpkg 版本宿主） | 各发行版 `dpkg -i`/`dpkg -r` 记录与 `lintian` 差异核对 |
| LINUX-DEB-OI-03 | LINUX-DEB-2 | 有桌面环境的 Linux 宿主（GNOME/KDE 任一） | 菜单项、图标、`.desktop` 验证器通过之外的观感截图；文件关联/URL scheme 真实双击唤起记录 |
| LINUX-DEB-OI-04 | LINUX-DEB-3 | 启用 systemd 的真实宿主（容器内 systemd 不充分） | unit 落位、`systemctl status`/`daemon-reload` 真实记录 |
| LINUX-DEB-OI-05 | LINUX-DEB-1 | Windows/macOS 构建宿主 | 跨宿主产出 `.deb` 并在 Linux 宿主 `dpkg -i` 安装的记录（托管写入器使跨宿主成为设计目标） |
| LINUX-DEB-OI-06 | LINUX-DEB-4 | apt 仓库与 `apt install ./pkg.deb` 工作流环境 | apt 元数据/本地安装记录；明确"单包可装但仓库管理在外"的边界证据 |
| LINUX-DEB-OI-07 | LINUX-DEB-4 | lintian 复核（本机 lintian 2.x 已装，DEB-2 起信息级运行） | DEB-3 已消解 `file-in-etc-not-marked-as-conffile`（/etc DebFile 自动登记 conffile）与 `package-contains-ancient-file`（mtime 1980 固定值）；残余：`debian-changelog-file-missing-or-wrong-name`（仅上游 `changelog.gz`，缺 `changelog.Debian.gz`——DEB-4 裁决是否补发）、`copyright-without-copyright-notice`/`extended-description-is-empty`/`recommended-field Section`/`malformed-contact`（调用方内容侧）、`changelog-not-compressed-with-max-compression`（netstandard2.0 `GZipStream` 无 SmallestSize）、`undeclared-elf-prerequisites`/`embedded-library`/`no-manual-page`（自包含载荷固有或决策 6 不探测） |
| LINUX-DEB-OI-08 | LINUX-DEB-3 已登记拒绝 | 若需要 xz/zstd：第三方托管压缩依赖裁决 + 宿主 dpkg 版本矩阵 | `BundlerDebCompression` 放开 xz/zstd 的实现记录；zstd 需安装宿主 dpkg≥1.21.18 |

相关 Linux `.rpm`/AppImage 事项在各自格式的 `linux-rpm-open-items.md`/`linux-appimage-open-items.md` 建立后登记，不混入本文件。

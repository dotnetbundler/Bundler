# Linux `.AppImage` 人工验收清单（LINUX-APPIMAGE-MT）

> 本清单只登记"需要人工/外部宿主条件"的验收用例；可自动化的内容一律进 `tests/`。
> ID 前缀 `LINUX-APPIMAGE-MT-xx`；登记索引见 `docs/manual-testing-index.md`。

| ID | 关联阶段 | 用例 | 验收证据 |
| --- | --- | --- | --- |
| LINUX-APPIMAGE-MT-01 | LINUX-APPIMAGE-3 | GUI 桌面宿主：双击 `.AppImage`（或 `chmod +x` 后运行）启动应用，桌面集成工具（可选 appimaged/gear lever）识别图标与菜单项 | 截图与启动记录 |
| LINUX-APPIMAGE-MT-02 | LINUX-APPIMAGE-3 | ARM64 宿主：aarch64 产物在 ARM 设备上 `--appimage-extract-and-run` 或 FUSE 运行 | 安装与启动日志 |
| LINUX-APPIMAGE-MT-03 | LINUX-APPIMAGE-3 | 旧 glibc 发行版宿主（如 CentOS 7/Ubuntu 18.04）：runtime 兼容性实测，记录最低 glibc 要求 | 各宿主运行日志 |
| LINUX-APPIMAGE-MT-04 | LINUX-APPIMAGE-3 | 无网络/受限环境：产物运行不依赖网络（契约复核） | 断网运行记录 |
| LINUX-APPIMAGE-MT-05 | 后置（若引入签名） | `appimagetool --sign` 产物在目标宿主 `gpg --verify`/`--validate` 校验 | 验证输出 |

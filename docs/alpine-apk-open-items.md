# Alpine `.apk` 外部待办索引（APK-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/alpine-apk-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/alpine-apk-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| APK-OI-01 | APK-1 | Windows/macOS 构建宿主（跨宿主产出矩阵） | 其他宿主产出的 `.apk` 在本宿主 `alpine` 容器 `apk add` 安装记录；产物确定性 sha 比对（本后端纯托管写入器，结构上应逐字节确定——跨宿主确定性属交叉观察项） |
| APK-OI-02 | APK-4 | ARM64 Alpine 真机或真实 ARM64 宿主（本机 x86_64，已有 qemu binfmt 覆盖） | **部分已验证**：2026-09-30 `alpine:latest` aarch64 容器经 qemu binfmt 实装+运行断言通过；真机 ARM64 宿主仍待验 |
| APK-OI-03 | APK-2 | 真实升级工作流环境（旧版本已装的系统升级到新版本） | `.pre-upgrade`/`.post-upgrade` 脚本在 `apk upgrade` 时真实执行（验证侧只断言了控制段成员存在，未实测升级路径） |
| APK-OI-04 | 可选扩展 | apk 仓库/索引工作流环境（abuild/abuild-index/`apk add --repository` 网络场景） | 需要 `abuild-index` 或同型工具工作流的真实索引服务验证；本后端仅产单包——仓库级能力是格式边界外的可选扩展 |
| APK-OI-05 | APK-1 | 其他 Alpine 版本宿主/容器（旧版 alpine、edge） | 各版本 `apk add`/`apk del` 行为差异核对（当前仅 `alpine:latest`） |

相关 deb/rpm 生态事项在 `docs/linux-deb-open-items.md`/`docs/linux-rpm-open-items.md` 登记，不混入本文件。

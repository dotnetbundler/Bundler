# Alpine `.apk` 外部待办索引（APK-OI）

本文件只登记**需要外部输入或专用环境**的事项；可本机完成的内容见 `docs/alpine-apk-roadmap.md` 阶段分解。
逐项用例步骤见 `docs/alpine-apk-manual-testing.md`。

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| APK-OI-01 | APK-1 | ~~Windows/macOS 构建宿主（跨宿主产出矩阵）~~ 已验证 | **已消解**：2026-10-01 联合测试——win/mac/linux 三产方产出 `.apk` 经 alpine-docker 消费侧 `apk add` 真实装卸 + sha256 逐行核对全过；同日 alpine 容器内直接产出 `.apk` 并实装运行通过（musl 生产宿主实测） |
| APK-OI-02 | APK-4 | ARM64 Alpine 真机或真实 ARM64 宿主（本机 x86_64，已有 qemu binfmt 覆盖） | **部分已验证**：2026-09-30 `alpine:latest` aarch64 容器经 qemu binfmt 实装+运行断言通过；2026-10-05 自动化腿 `AlpineApkIntegrationTests.DockerArm64InstallRunRemoveUnderBinfmt` 在 `tonistiigi/binfmt --install arm64` 注册后真跑通过（1/1/0，76s）——该腿随宿主 binfmt 注册态自动从 SKIP 转 PASS；真机 ARM64 宿主仍待验 |
| ~~APK-OI-03~~ | APK-2 | ~~真实升级工作流环境（旧版本已装的系统升级到新版本）~~ 已验证 | **已消解**：2026-10-02 `tests/Alpine.Apk.Integration/Verify.sh` 升级腿——v1 `-r0` `apk add` 后 v2 `-r1` `apk add` 升级，`.pre-upgrade`/`.post-upgrade` 标记断言真实执行通过；v2 `.post-install` 标记未出现（升级路径只跑升级脚本不跑安装脚本），`apk list` 证实装到 `1.0.0-r1` |
| APK-OI-04 | 可选扩展 | apk 仓库/索引工作流环境（abuild/abuild-index/`apk add --repository` 网络场景） | 需要 `abuild-index` 或同型工具工作流的真实索引服务验证；本后端仅产单包——仓库级能力是格式边界外的可选扩展 |
| APK-OI-05 | APK-1 | 其他 Alpine 版本宿主/容器（旧版 alpine、edge） | 各版本 `apk add`/`apk del` 行为差异核对（当前仅 `alpine:latest`） |

相关 deb/rpm 生态事项在 `docs/linux-deb-open-items.md`/`docs/linux-rpm-open-items.md` 登记，不混入本文件。

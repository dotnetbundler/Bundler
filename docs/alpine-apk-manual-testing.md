# Alpine `.apk` 人工验收清单（APK-MT）

以下项目本机无法自动化（需真机 ARM64、跨宿主产出矩阵、真实升级工作流等），逐项人工记录：OS 版本、架构、Git SHA、产物 SHA-256、日志、清理方式。
本机已可自动化的真实断言（`apk add --allow-untrusted`、可信公钥安装、`apk del`、结构与确定性检查）进 `AlpineApkIntegrationTests`，不在本清单重复。

## 验收步骤（草拟，随阶段推进落实）

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| APK-MT-01 | APK-4 | ARM64 真机宿主（如 Raspberry Pi、ARM64 VPS、真实 aarch64 Alpine）上 `apk add --allow-untrusted` 安装 aarch64 产物并启动主程序 | 安装与启动日志、版本字符串、卸载清理记录 |
| APK-MT-02 | APK-1 | Windows/macOS 构建宿主经 `BundlerFormats=alpineapk` 产出 `.apk`，在 `alpine` 容器或真机 `apk add` 实装 | 宿主版本 + 产物 sha + 安装日志；顺带核对产物确定性（同输入下跨宿主是否逐字节一致） |
| APK-MT-03 | APK-2 | 旧版 alpine（如 `alpine:3.18`、`alpine:edge`）容器中安装/卸载；记录 `apk` 版本差异下的行为（含 pax 扩展头、签名段兼容性） | 各版本安装日志、报错文本 |
| APK-MT-04 | APK-2 | 已装 1.0.0 的系统升级 `apk upgrade` 到 1.0.1：断言 `.pre-upgrade`/`.post-upgrade` 脚本真实执行顺序 + 数据段路径覆盖语义 | 升级脚本标记文件、版本号更新断言 |
| APK-MT-05 | 可选扩展 | `abuild-index` 或等价工作流把 `.apk` 纳入 apk 仓库后用 `apk add --repository` 网络安装 | 仓库索引建立与安装日志；属格式边界外能力，评估后再决定是否扩展 |
| APK-MT-06 | APK-3 | 目标机 `/etc/apk/keys/` 真实分发公钥后 `apk add`（不带 `--allow-untrusted`）验证信任链端到端 | 公钥 sha256、目标机 `apk` 信任链识别日志（可信签名路径可自动覆盖，分发属运维） |

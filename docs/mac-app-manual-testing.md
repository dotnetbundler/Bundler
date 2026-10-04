# macOS `.app` 手动与专用环境验收

与 `docs/mac-app-open-items.md` 绑定；未满足外部条件的能力保持外部待验收。
总入口与索引见 `docs/manual-testing-index.md`；自动化归 `tests/`，本文件只覆盖跨机器、需真实凭证或物理交互的步骤。
每条记录：OS 版本、架构、Git SHA、产物路径/SHA-256、日志位置、清理方式。

## 执行前约定

- 基线命令在 `docs/development-rules.md` 第 5 节与 `docs/mac-app-roadmap.md` 第 4 节测试组织表。
- macOS 集成测试：`MacAppIntegrationTests`（`dotnet test --filter-class "*MacAppIntegrationTests*"`）。
- 除非条目明确说 sudo，不提权；可抛弃/专用宿主优先。
- 记录产物 SHA-256，便于与代码签名状态区分（签名会改变文件字节）。
- osx 产物**运行判定一律以直接执行二进制的 exit code 与输出为准**——`open -W` 返回码与真实运行结果可不一致（2026-09-30 连续三轮：`open -W` 报 0 而实际进程崩溃 exit134/0x8007000B），只作辅助信号。

## 验收步骤（草拟，随阶段推进落实）

| ID | 阶段 | 操作与预期 | 主要证据 |
| --- | --- | --- | --- |
| MAC-APP-MT-01 | MAC-APP-1..2 | 干净宿主：未装 Xcode/CLT 的 macOS 用户环境下，拷贝本机产出的 `.app` 至 `/Applications`（或任意目录），双击/`open` 首启；未签名与 ad-hoc 各测一次，记录 Gatekeeper 实际对话框与放行路径 | **部分已验证**（2026-09-28）：quarantine 首启 Gatekeeper 阻断对话框截图已记；干净宿主（无 Xcode/CLT）段未验 |
| MAC-APP-MT-02 | MAC-APP-3 | 真实 Developer ID Application 证书签名 → `notarytool submit` → `stapler staple` → 打包分发下载（带 quarantine）→ `spctl -a -vvv`/`stapler validate` 断言 → 首启无警告 | notarytool 输出、stapler validate、spctl 记录 |
| MAC-APP-MT-03 | MAC-APP-4 | `osx-x64` `.app` 在 Intel 宿主或已激活 Rosetta 的 arm64 宿主实际启动 | 架构标记、启动日志 |
| MAC-APP-MT-04 | MAC-APP-2 | 文件关联与 URL scheme 实际唤起：`lsregister` 后 `open` 文件/`open scheme://`，切换处理器与多 handler 共存 | LaunchServices 记录、唤起日志 |
| MAC-APP-MT-05 | MAC-APP-4 | `LSMinimumSystemVersion` 边界：低于下限宿主拒绝运行、等于/高于可运行；**高于下限已自动化**（`MacAppIntegrationTests` 以 `BundlerMacAppMinimumSystemVersion=99.0` 实测 LaunchServices 拒绝）；剩余=低于下限的旧宿主真实拒跑 | 系统提示与版本号 |
| MAC-APP-MT-06 | MAC-APP-4 | 升级替换：**已自动化**（`MacAppIntegrationTests` v1→v2 `~/Applications` 原地替换+重新注册+启动+identifier 保持）；剩余人工项=用户数据保留（沙盒/非沙盒偏好与容器目录）核对 | **已验证**（2026-09-28）：v1→v2 升级用户数据/偏好保留核对记录 |
| MAC-APP-MT-07 | MAC-APP-2 | universal/fat `.app` 在 `x86_64` 与 `arm64` 宿主各自原生启动 | `lipo -info`、两种启动日志 |
| MAC-APP-MT-08 | MAC-APP-3 | 公证异常路径：凭证缺失/错误、非 Accepted、撤销后的离线首启；`skipStapling` 产品行为 | 错误日志、`notarytool log` |
| MAC-APP-MT-09 | MAC-APP-1..2 | 访达展示核对：图标（Retina/暗黑变体若有）、`CFBundleDisplayName` 显示、`Get Info` 面板版本/版权字段 | **已验证**（2026-09-28）：Get Info 面板版本/版权字段与 plist 读回对照截图 |
| MAC-APP-MT-10 | MAC-APP-4 | 时间戳/证书轮换边界：签名时间戳存在性断言、证书到期/撤销后已签产物的验签行为（评估用） | `codesign -dvvv`、验签输出 |

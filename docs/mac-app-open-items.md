# macOS `.app` 外部待办与依赖

用于列出仓库测试之外需要环境、凭证或生态条件的项。
`最早阶段` 指可开始满足它的最早路线阶段；完成证据绑定到 `docs/mac-app-manual-testing.md` 或路线验收。
未满足前相关能力保持外部待验收。

## 当前缺口（2026-09-26 本机实测）

| 项 | 本机状态 |
| --- | --- |
| codesigning 身份 | `security find-identity -v -p codesigning` 返回 0 个身份；只能 ad-hoc 签名 |
| Rosetta | 未激活（`arch -x86_64` → Bad CPU type）；`osx-x64` 原生运行不可测 |
| `pwsh` | 不存在；macOS 集成脚本改用 bash |
| Apple Developer 凭证 | 无；`APPLE_ID`/`APPLE_PASSWORD`/`APPLE_TEAM_ID` 或 `APPLE_API_KEY`/`APPLE_API_ISSUER` 均缺 |

## 待办清单

| ID | 最早阶段 | 所需输入/环境 | 完成证据 |
| --- | --- | --- | --- |
| MAC-APP-OI-01 | MAC-APP-3 | Apple Developer Program 成员资格、Developer ID Application 证书、公证凭证（`APPLE_ID`+app-specific password+`APPLE_TEAM_ID`，或 `APPLE_API_KEY`+`APPLE_API_ISSUER`+`AuthKey_*.p8`）、可导入证书的钥匙串 | MAC-APP-MT-02 记录：签名→公证→staple→`spctl` 全通 |
| MAC-APP-OI-02 | MAC-APP-4 | 已激活 Rosetta 的 arm64 宿主或 Intel 宿主 | **部分已验证**：2026-10 macos-26-intel CI 腿真 Intel 硬件跑绿 + macos-26 Rosetta 腿 `osx-x64` `.app` 启动断言通过；剩启动观感人工（MAC-APP-MT-03） |
| MAC-APP-OI-03 | MAC-APP-4 | 干净 macOS 宿主矩阵：无 Xcode/CLT 环境、不同 macOS 主版本（含 `LSMinimumSystemVersion` 边界宿主） | MAC-APP-MT-01、05 记录 |
| MAC-APP-OI-04 | MAC-APP-4 | 真实分发渠道（公网/内网下载路径），产生 quarantine 首启场景 | MAC-APP-MT-02 的下载-首启部分 |
| MAC-APP-OI-05 | MAC-APP-4 | 证书撤销/到期/时间戳边界试验环境（可抛弃证书与宿主） | MAC-APP-MT-08、10 记录 |
| MAC-APP-OI-06 | MAC-APP-2 | `.icon`/`*.car` 管线验收环境：Xcode≥26 `actool`（本机已具备，列此以覆盖“无 Xcode 宿主”降级分支的反向验证） | MAC-APP-2 降级分支测试记录 + MT-09 访达图标核对 |
| MAC-APP-OI-07 | MAC-APP-3 | provisioning profile（universal links/受管 entitlement 场景需要 `embedded.provisionprofile`） | 如需启用 universal links 的验收记录；普通 deep link 不受阻 |
| MAC-APP-OI-08 | 待定 | osx 通用载荷"预合并旁路"防呆评估 | 结论与实现取舍记录；属开发者侧防呆、非环境缺口 |
> MAC-APP-OI-08 详情：单目录输入走 `bundle --rid osx` 时，`MacAppBundleBackend.InspectPayload` 只查 Mach-O 切片，非 Mach-O 文件不验架构——可手工 lipo 一个 fat apphost 夹带按架构编的散件（如 R2R 程序集）绕过，产出"fat 头可过、实跑 0x8007000B"的坏件（2026-10-01 联合测试实见）。
> 候选方案：①通用签名表探测（ELF/native PE/thin Mach-O/PE+RTR），Abstractions 放嗅探、Core 管线 rid=osx 统一调用，FDD 纯 IL 放行；②osx 通用只收合并器产出目录、拒收预合并输入（砍掉契约允许的自产 fat+FDD 用法）。
> 倾向 ①；与使用方契约口径一并后期裁决。

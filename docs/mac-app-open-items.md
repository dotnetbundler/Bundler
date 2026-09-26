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
| MAC-APP-OI-02 | MAC-APP-4 | 已激活 Rosetta 的 arm64 宿主或 Intel 宿主 | MAC-APP-MT-03：`osx-x64` `.app` 启动记录 |
| MAC-APP-OI-03 | MAC-APP-4 | 干净 macOS 宿主矩阵：无 Xcode/CLT 环境、不同 macOS 主版本（含 `LSMinimumSystemVersion` 边界宿主） | MAC-APP-MT-01、05 记录 |
| MAC-APP-OI-04 | MAC-APP-4 | 真实分发渠道（公网/内网下载路径），产生 quarantine 首启场景 | MAC-APP-MT-02 的下载-首启部分 |
| MAC-APP-OI-05 | MAC-APP-4 | 证书撤销/到期/时间戳边界试验环境（可抛弃证书与宿主） | MAC-APP-MT-08、10 记录 |
| MAC-APP-OI-06 | MAC-APP-2 | `.icon`/`*.car` 管线验收环境：Xcode≥26 `actool`（本机已具备，列此以覆盖“无 Xcode 宿主”降级分支的反向验证） | MAC-APP-2 降级分支测试记录 + MT-09 访达图标核对 |
| MAC-APP-OI-07 | MAC-APP-3 | provisioning profile（universal links/受管 entitlement 场景需要 `embedded.provisionprofile`） | 如需启用 universal links 的验收记录；普通 deep link 不受阻 |

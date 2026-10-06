# 特殊验收清单

> 本文件登记只能由外部付费条件或用户本人手动完成的验收项。
> 每项自包含：要做什么、所需条件、完成证据、当前卡点、平替方案。
> 能由自动化测试或宿主子会话完成的验收不在此列；完成证据以用户执行后的记录回填。

## 一、付费凭证类

### SA-P-01 Authenticode 生产代码签名（NSIS 生产签名全链 + MSI 生产证书链 + SmartScreen 声誉）

- **要做什么**：用生产级 Authenticode 证书对隔离载荷、插件、卸载器、安装器逐件签名并挂 RFC 3161 时间戳，验签通过并在真实 Windows 上观察 SmartScreen 行为。
- **所需条件**：OV 证书约 ¥700–3000/年或 EV 证书约 ¥2000–5000/年；时间戳服务器免费。
- **完成证据**：`signtool verify /pa` 通过受信链；SmartScreen 拦截/放行观察记录；证书过期后已签件凭时间戳仍被信任的证据。
- **当前卡点**：签名管线（顺序、隔离、PE 签名、提供方调用、失败清理、秘密隐藏）已全验；RFC 3161 时间戳嵌入已用自签证书+公共服务器实证（见末节）；缺 CA 信任链与 SmartScreen 声誉证据。
- **平替方案**：① Azure Trusted Signing 约 ¥70/月起按量计费、含时间戳，最低成本云签名；② SignPath 对合格开源项目免费；③ EV 证书立即获 SmartScreen 声誉，OV 需装机量自然积累；④ 接受警告期如实声明。

### SA-P-02 `.app` 签名+公证全链（Apple Developer ID）

- **要做什么**：Developer ID Application 证书签名 `.app` → Apple 公证 → `stapler` 钉票 → `spctl` 评估通过；无 Xcode 宿主首启不弹"无法验证开发者"。
- **所需条件**：Apple Developer Program $99/年（公证服务免费但绑该会员）+ `APPLE_ID`+app-specific password+`APPLE_TEAM_ID` 或 `APPLE_API_KEY`+`APPLE_API_ISSUER`+`AuthKey_*.p8` + 可导入证书的钥匙串。
- **完成证据**：签名→公证→staple→`spctl` 全通记录。
- **当前卡点**：无 codesigning 身份（只能 ad-hoc 签名）、无 Apple 凭证；管线侧 ad-hoc/自签名腿已验，缺的仅 Apple 信任链。
- **平替方案**：无真实平替——Developer ID 证书只发付费账号；未签名分发可用 `xattr -d com.apple.quarantine`/右键打开的文档级绕行。

### SA-P-03 DMG 本体 codesign 真实验签

- **要做什么**：Developer ID Application 证书对 `.dmg` 本体签名并验签。
- **所需条件**：同 SA-P-02 一份会员下的 Application 型证书。
- **完成证据**：签名 DMG `codesign --verify`/`spctl` 记录。
- **当前卡点**：同 SA-P-02。
- **平替方案**：同 SA-P-02。

### SA-P-04 `.pkg` 签名+公证

- **要做什么**：Developer ID Installer 证书（与 Application 证书不同型、不可互替）签名 `.pkg` 并公证。
- **所需条件**：同一份 $99 会员（两种证书型都发）+ 公证凭证。
- **完成证据**：`pkgutil --check-signature`/`spctl` 记录 + 公证通过 + `stapler validate` 记录。
- **当前卡点**：同 SA-P-02。
- **平替方案**：同 SA-P-02。

### SA-P-05 provisioning profile（universal links/受管 entitlement）

- **要做什么**：如需启用 universal links，`embedded.provisionprofile` 嵌入 `.app` 并验签。
- **所需条件**：付费 Apple Developer Program 账号签发 provisioning profile。
- **完成证据**：启用 universal links 的验收记录；普通 deep link 不受阻、本项不阻塞。
- **平替方案**：无平替——profile 只在付费账号下签发。

### SA-P-06 证书撤销/到期/时间戳边界试验（macOS）

- **要做什么**：用可抛弃证书与宿主验证撤销/过期场景下的签名判定。
- **所需条件**：$99 会员发证后再做；免费侧自签链可模拟过期但非真实 Apple 链。
- **完成证据**：撤销/到期场景试验记录。

### SA-P-07 签名 `.app` 更新链（UPDATE）

- **要做什么**：mac 自更新换包前新旧 `.app` codesign 签名身份一致性腿的实证（防更新通道劫持）。
- **所需条件**：SA-P-02 的凭证就位后一次验收。
- **完成证据**：签名应用更新链验证记录；现实现无生产证书只能跑未签名腿。

## 二、只能用户手动处理类

### A. 用户账号/材料/真人审校类

| 项 | 要做什么 | 完成证据 | 当前卡点/说明 |
| --- | --- | --- | --- |
| 真实旧 WiX/MSI 产品迁移 | 用真实历史 ProductCode/UpgradeCode 或已发布 MSI 验证迁移与 `LegacyMsiAutoDetect` 自动检测 | 真实已发布产品的识别、per-machine 提权枚举、迁移行为记录 | 通用机制与自动检测已由一次性 fixture 验证；需用户侧真实旧标识/已发布包 |
| 生产 GPG 密钥流程 | 真实开发者 GPG 密钥给 rpm/AppImage 签名 + 公钥分发/吊销流程演练 | 生产密钥签名+分发/吊销记录 | 签名实现与 `rpm --import`+`rpm -K`/`gpgv` 验签已自动化；密钥生成免费，流程属发布者自身 |
| UPDATE 真实发布管线 | 真实通道 `BundlerUpdateFeed` 指向公网静态托管，HelloBundlerApp 走 pack→安装→Check/Download/Apply 全链 | 端到端狗食记录 | 静态托管免费（GitHub Pages 等）；需用户决定托管并保管 ECDSA 私钥 |
| UPDATE 公网/CDN Range 行为 | 差分在真实公网/CDN 上的 HTTP Range 行为记录 | 公网腿 Range 响应与复用率记录 | 回环 206 已实证；部分 CDN 对 Range 有缓存语义差异 |
| mac quarantine 首启 | `.app` 经真实分发渠道（公网/内网下载）产生 quarantine 首启场景 | 下载-首启观察记录 | 需真实下载路径；更新期剥 quarantine 逻辑已实现 |
| NSIS 22 语言母语审校 | 内置 22 语言文案母语/专业审校，阿拉伯语/波斯语重点 RTL 布局，代表性缩放下截图 | 母语审校意见+RTL/DPI 截图 | 机械层已全净（键集逐位一致、零条与英文同）；语言准确性、地区术语、截断只能真人 |
| MSI 本地化审校+辅助功能 | 支持语言集 UI、缩放和可访问性检查 | 审校与可访问性记录 | 同上需真人 |
| MSI 对话框交互腿 | Browse 对话框、范围外路径触发 `InvalidDirDlg`、`ADDLOCAL` Feature 选择交互、启动勾选生效、位图显示、缩放/辅助功能、per-machine UAC 交互安装 | 真实交互 UI 环境记录 | 需真实 Windows 桌面交互；许可/InstallDir/Install/Finish 流转已自动化 |

### B. 借设备/虚拟机类（按裁决由用户借设备或虚拟机验收）

| 项 | 要做什么 | 完成证据 | 当前卡点/已有覆盖 |
| --- | --- | --- | --- |
| ARM64 真机组（deb/rpm/AppImage/apk） | 真机 ARM64 宿主装卸+运行各格式件 | 真机装卸与运行记录 | qemu binfmt 仿真 arm64 容器已实证装卸+运行通过，缺真机 |
| UPDATE arm64 AOT 引导件 | linux-arm64/win-arm64 构建宿主产 arm64 引导件并入包，重跑 arm64 换包腿 | arm64 引导件构建+换包记录 | osx-arm64 件已由 mac 宿主真实产出入库；qemu 下 ilc SIGABRT 属环境边界；GitHub ARM runner/云 ARM 实例可租 |
| Windows ARM64 矩阵 | MSI/NSIS 在 Windows ARM64 的构建、安装、升级、修复、卸载逐组合 | 各组合 verbose log/注册表/OS build/清理证据 | 单台 x64 开发机不能代表；Azure Windows ARM64 VM/Parallels 可租 |
| Intel Mac / Rosetta | osx-x64 `.app`/`.dmg`/`.pkg` 实跑、挂载、安装、启动 | osx-x64 各格式启动记录 | arm64 宿主 `arch -x86_64` Bad CPU type；装 Rosetta（免费需人工授权）或租 Intel Mac |
| 干净 macOS 宿主��阵 | 无 Xcode/CLT 干净宿主各 macOS 主版本首装 `.app`/`.dmg` | 挂载/EULA 弹窗/拖放安装首启记录 | 现宿主有开发工具链；云 mac 实例按天可租 |
| 干净 Windows 10/11 | WiX 自身依赖边界（无 .NET SDK 仅 candle/light 编译）、独立 VM 复跑安装/卸载、Win7 记 Framework 缺失边界 | 各宿主构建/安装/卸载记录 | 非干净 Windows 11 x64 已有证据；干净宿主未测 |
| 提权/UAC Windows | NSIS per-machine 安装/卸载与 journal ACL 取证、预建父目录所有权/继承；MSI current-user/per-machine 权限差异；UPDATE 引导件在 `Program Files` 提权目录的写权限与 UAC 交互 | `icacls`/HKLM 权限/提权边界记录 | 普通本机不得静默触发 UAC；当前实证均 per-user 目录 |
| 可丢弃故障 VM | NSIS 真实重启+锁定文件删除（重启后复核）、真实 ACL 拒绝+物理磁盘/配额耗尽；MSI 重启/锁定/磁盘故障/缺源 | 重启后状态、回滚日志、`icacls` 记录 | 确定性故障注入已覆盖事务检查点；真实系统故障只能可丢弃 VM 注入 |
| 跨 Windows 版本固定项 | Win10/11 各 build 上升级前/卸载前已固定开始菜单/任务栏项检查 | 固定项清理行为记录 | 固定项存储与接口随系统版本/策略变化 |
| appimaged 类桌面集成 | appimaged/gear lever 工具对 AppImage 的识别与观感 | 集成工具识别观感记录 | 双击启动=FUSE 同路径已验；工具识别段待桌面环境+工具 |
| apk 仓库/索引工作流（可选扩展） | `abuild-index`/`apk add --repository` 真实索引服务验证 | 索引服务验证记录 | 后端仅产单包，仓库级能力属格式边界外可选扩展 |

## 三、已消解样板：RFC 3161 时间戳链路（免费项实证记录）

RFC 3161 时间戳副签名协议不需要付费凭证。
用自签名证书 + 公共时间戳服务器即可真实跑通签名→取时间戳→嵌入副签名全链。
该腿证明的是时间戳管线与嵌入正确性，不证明 CA 信任链——信任链证据仍归 SA-P-01。

**2026-10-06 已实证消解（win-x64 宿主 `b7dd840`）**：自签代码签名证书 + `signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256` exit 0；`Get-AuthenticodeSignature.TimeStamperCertificate` 非空（Issuer=DigiCert Trusted G4 TimeStamping RSA4096 SHA256 2025 CA1，有效期至 2037-11-04）；不带 `/tr` 对照组 TimeStamperCertificate 为空，证明时间戳确来自 RFC 3161 服务器而非残留；`timestamp.invalid.example` 负例 exit 1。
教训：对已签名 PE 做此验证会读旧签名造成假阳性，须用无签名 PE。

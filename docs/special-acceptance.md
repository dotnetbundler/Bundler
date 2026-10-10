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
| 生产 GPG 密钥流程 | 真实开发者 GPG 密钥给 rpm/AppImage 签名 + 公钥分发/吊销流程演练 | 生产密钥签名+分发/吊销记录 | `gpg-production.sh --self-contained`（临时密钥全链签名+验签）收编进 `release-verify` ubuntu 腿跑绿；真实开发者密钥分发/吊销仍挂本表 |
| UPDATE 真实发布管线 | 真实通道 `BundlerUpdateFeed` 指向公网静态托管，HelloBundlerApp 走 pack→安装→Check/Download/Apply 全链 | 端到端狗食记录 | 静态托管免费（GitHub Pages 等）；需用户决定托管并保管 ECDSA 私钥 |
| UPDATE 公网/CDN Range 行为 | 差分在真实公网/CDN 上的 HTTP Range 行为记录 | 公网腿 Range 响应与复用率记录 | 回环 206 已实证；部分 CDN 对 Range 有缓存语义差异 |
| mac quarantine 首启 | `.app` 经真实分发渠道（公网/内网下载）产生 quarantine 首启场景 | 下载-首启观察记录 | quarantine xattr 标记落位模拟腿已一次性 CI 跑绿（run 37892544275）；真实下载首启弹窗观察仍挂本表 |
| NSIS 22 语言母语审校 | 内置 22 语言文案母语/专业审校，阿拉伯语/波斯语重点 RTL 布局，代表性缩放下截图 | 母语审校意见+RTL/DPI 截图 | 机械层已全净（键集逐位一致、零条与英文同）；语言准确性、地区术语、截断只能真人 |
| MSI 本地化审校+辅助功能 | 支持语言集 UI、缩放和可访问性检查 | 审校与可访问性记录 | 同上需真人 |
| MSI 对话框交互腿 | Browse 对话框、范围外路径触发 `InvalidDirDlg`、`ADDLOCAL` Feature 选择交互、启动勾选生效、位图显示、缩放/辅助功能、per-machine UAC 交互安装 | 真实交互 UI 环境记录 | 需真实 Windows 桌面交互；许可/InstallDir/Install/Finish 流转已自动化 |

### B. 借设备/虚拟机类（按裁决由用户借设备或虚拟机验收）

| 项 | 要做什么 | 完成证据 | 当前卡点/已有覆盖 |
| --- | --- | --- | --- |
| ~~ARM64 真机组（deb/rpm/AppImage/apk）~~ | ~~真机 ARM64 宿主装卸+运行各格式件~~ | **已完成**（2026-10-09） | `arm64-real-hw.sh` 收编进 `release-verify`，在 GitHub `ubuntu-26.04-arm` 真 arm64 硬件腿跑绿（deb/rpm/apk/AppImage 装卸+运行）；Windows 侧 `arm64-matrix.ps1` 在 `windows-11-arm` 跑绿 |
| ~~UPDATE arm64 AOT 引导件~~ | ~~linux-aarch64/windows-arm64 构建宿主产 arm64 引导件并入包，重跑 arm64 换包腿~~ | **已完成**（2026-10-09） | linux-aarch64/linux-musl-aarch64 由 GitHub `ubuntu-26.04-arm` runner、windows-arm64 由 `windows-11-arm` runner 真宿主产出入库（`tools/<target>/`），冒烟 `apply` 过；arm64 换包腿收编进 `release-verify`（docker tmpfs/VHD 小卷） |
| ~~Windows ARM64 矩阵~~ | ~~MSI/NSIS 在 Windows ARM64 的构建、安装、升级、修复、卸载逐组合~~ | **已完成**（2026-10-09） | `arm64-matrix.ps1` 收编进 `release-verify` 的 `windows-11-arm` 腿跑绿（装+NSIS/MSI 覆盖装+MSI 修复+卸逐组合，rc=0） |
| ~~Intel Mac / Rosetta~~ | ~~macos-x86_64 `.app`/`.dmg`/`.pkg` 实跑、挂载、安装、启动~~ | **部分收编**（2026-10-09） | `macos-26-intel` CI 腿在真 Intel 硬件跑绿（macos-x86_64 `.app`/`.dmg`/`.pkg` 产包+挂载+装卸+全量单测）；`macos-26` arm64+Rosetta 腿跑绿（macos-x86_64 `.app`/`.dmg` 实跑）；剩启动观感仍挂本表 |
| 干净 macOS 宿主矩阵 | 无 Xcode/CLT 干净宿主各 macOS 主版本首装 `.app`/`.dmg` | 挂载/EULA 弹窗/拖放安装首启记录 | 现宿主有开发工具链；云 mac 实例按天可租 |
| 干净 Windows 10/11 | WiX 自身依赖边界（无 .NET SDK 仅 candle/light 编译）、独立 VM 复跑安装/卸载、Win7 记 Framework 缺失边界 | 各宿主构建/安装/卸载记录 | 非干净 Windows 11 x64 已有证据；干净宿主未测 |
| 提权/UAC Windows | NSIS per-machine 安装/卸载与 journal ACL 取证、预建父目录所有权/继承；MSI current-user/per-machine 权限差异；UPDATE 引导件在 `Program Files` 提权目录的写权限与 UAC 交互 | `icacls`/HKLM 权限/提权边界记录 | 普通本机不得静默触发 UAC；当前实证均 per-user 目录 |
| 可丢弃故障 VM | NSIS 真实重启+锁定文件删除（重启后复核）、真实 ACL 拒绝+物理磁盘/配额耗尽；MSI 重启/锁定/磁盘故障/缺源 | **部分收编**（2026-10-09） | `disposable-vm-faults.ps1` 收编进 `release-verify`（windows-2025 与 windows-11-arm 跑绿：真 ACL 拒装 rc=2+真 VHD 盘满 ENOSPC rc=2+无半途树）；真实重启+锁定文件删除与 MSI 故障仍挂本表 |
| 跨 Windows 版本固定项 | Win10/11 各 build 上升级前/卸载前已固定开始菜单/任务栏项检查 | **部分收编**（2026-10-09） | `pinned-items.ps1` 收编进 `release-verify`（windows-2025 b26100 x64 与 windows-11-arm b26200 跑绿：装→`.lnk` 发现→pin 动词枚举→卸→lnk 消；另 Devin VM b20348 实证）；Win10 桌面 SKU 与任务栏 pin 落点仍挂本表——win11 已移除程序化 pin 通道（shell verb 枚举得到但 DoIt 返 E_ACCESSDENIED），落点只能人工 |
| appimaged 类桌面集成 | appimaged/gear lever 工具对 AppImage 的识别与观感 | **部分收编**（2026-10-09） | `.desktop`/图标产出与 `desktop-file-validate` 校验收编为一次性 CI 腿跑绿（run 37892544275）；双击启动=FUSE 同路径已验；工具识别观感仍挂本表 |
| ~~apk 仓库/索引工作流（可选扩展）~~ | ~~`abuild-index`/`apk add --repository` 真实索引服务验证~~ | **已完成**（2026-10-09） | `apk-index.sh` 收编进 `release-verify` ubuntu 腿跑绿——`apk index` 生成架构子目录索引 + `apk add --repository` 真装+卸（含 run 37892544275 实证） |

## 三、已消解样板：RFC 3161 时间戳链路（免费项实证记录）

RFC 3161 时间戳副签名协议不需要付费凭证。
用自签名证书 + 公共时间戳服务器即可真实跑通签名→取时间戳→嵌入副签名全链。
该腿证明的是时间戳管线与嵌入正确性，不证明 CA 信任链——信任链证据仍归 SA-P-01。

**2026-10-06 已实证消解（windows-x86_64 宿主 `b7dd840`）**：自签代码签名证书 + `signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256` exit 0；`Get-AuthenticodeSignature.TimeStamperCertificate` 非空（Issuer=DigiCert Trusted G4 TimeStamping RSA4096 SHA256 2025 CA1，有效期至 2037-11-04）；不带 `/tr` 对照组 TimeStamperCertificate 为空，证明时间戳确来自 RFC 3161 服务器而非残留；`timestamp.invalid.example` 负例 exit 1。
教训：对已签名 PE 做此验证会读旧签名造成假阳性，须用无签名 PE。

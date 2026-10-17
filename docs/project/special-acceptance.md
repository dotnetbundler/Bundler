# 特殊验收清单

> 本文件登记**只能由外部付费条件或用户本人手动完成**的验收项。
> 每项自包含：要做什么、所需条件、完成证据、当前卡点、平替方案。
> 能由自动化测试或宿主子会话完成的验收不在此列；完成证据以用户执行后的记录回填。

## 执行约定

- 需要重启、UAC、系统级待处理删除、磁盘耗尽或故意修改 ACL 的验收，只能在可抛弃虚拟机或专用测试机执行，先创建快照。
- 每轮记录：Git SHA、包版本、OS build/架构、账户权限、产物 SHA-256、日志路径、结果与清理方式。
- 生产私钥与密码只经环境变量/秘密库传递，不进仓库、命令行或记录。
- 结论只写"通过/失败/未执行/条件不适用"，未执行不得写成通过。
- 半自动验收脚本集中在 `tests/Manual/`（索引见其 README），需人工动作处暂停等待。

## 一、付费凭证类

### SA-P-01 Authenticode 生产代码签名（NSIS 生产签名全链 + MSI 生产证书链 + SmartScreen 声誉）

- **要做什么**：用生产级 Authenticode 证书对隔离载荷、插件、卸载器、安装器逐件签名并挂 RFC 3161 时间戳，验签通过并在真实 Windows 上观察 SmartScreen 行为。
- **所需条件**：OV 证书约 ¥700–3000/年或 EV 证书约 ¥2000–5000/年；时间戳服务器免费。
- **完成证据**：`signtool verify /pa` 通过受信链；SmartScreen 拦截/放行观察记录；证书过期后已签件凭时间戳仍被信任的证据。
- **当前卡点**：签名管线（顺序、隔离、PE 签名、提供方调用、失败清理、秘密隐藏）已全验；RFC 3161 时间戳嵌入已用自签证书+公共服务器实证；缺 CA 信任链与 SmartScreen 声誉证据。
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
- **完成证据**：签名应用更新链验证记录；自动化层已覆盖 BundleID 拒换/quarantine 剥离/未签名与自签（含 OU→TeamIdentifier 尽力腿，CI macOS 宿主取不到签名即诚实 Skip），剩余缺口仅真 Apple Developer ID 身份链实证。

### SA-P-08 mac 公证异常路径

- **要做什么**：真实凭证下跑公证异常分支：凭证缺失/错误、非 Accepted、撤销后离线首启、`skipStapling` 产品行为。
- **所需条件**：同 SA-P-02 的凭证与可联网宿主。
- **完成证据**：`notarytool log`、各异常路径断言与离线首启记录。
- **当前卡点**：正常路径（SA-P-02）与异常路径共用凭证；无凭证时两路均不可验。

## 二、只能用户手动处理类

### A. 用户账号/材料/真人审校类

| 项 | 要做什么 | 完成证据 | 当前卡点/说明 |
| --- | --- | --- | --- |
| 真实旧 WiX/MSI 产品迁移 | 用真实历史 ProductCode/UpgradeCode 或已发布 MSI 验证迁移与 `LegacyMsiAutoDetect` 自动检测 | 真实已发布产品的识别、per-machine 提权枚举、迁移行为记录 | 通用机制与自动检测已由一次性 fixture 验证；需用户侧真实旧标识/已发布包 |
| 生产 GPG 密钥流程 | 真实开发者 GPG 密钥给 rpm/AppImage 签名 + 公钥分发/吊销流程演练 | 生产密钥签名+分发/吊销记录 | `gpg-production.sh --self-contained`（临时密钥全链签名+验签）收编进 `verify` ubuntu 腿跑绿；真实开发者密钥分发/吊销仍挂本表 |
| UPDATE 真实发布管线 | 真实通道 `BundlerUpdateFeed` 指向公网静态托管，HelloBundlerApp 走 pack→安装→Check/Download/Apply 全链 | 端到端狗食记录 | 静态托管免费（GitHub Pages 等）；需用户决定托管并保管 ECDSA 私钥 |
| UPDATE 公网/CDN Range 行为 | 差分在真实公网/CDN 上的 HTTP Range 行为记录 | 公网腿 Range 响应与复用率记录 | 回环 206 已实证；部分 CDN 对 Range 有缓存语义差异 |
| mac quarantine 首启 | `.app` 经真实分发渠道（公网/内网下载）产生 quarantine 首启场景 | 下载-首启观察记录 | quarantine xattr 标记落位模拟腿已一次性 CI 跑绿（run 37892544275）；macos-26 起模拟打标不再持久（见 B 表"macOS 26+ quarantine 可写宿主"）；真实下载首启弹窗观察仍挂本表 |
| NSIS 22 语言母语审校 | 内置 22 语言文案母语/专业审校，阿拉伯语/波斯语重点 RTL 布局，代表性缩放下截图 | 母语审校意见+RTL/DPI 截图 | 机械层已全净（键集逐位一致、零条与英文同）；语言准确性、地区术语、截断只能真人 |
| MSI 本地化审校+辅助功能 | 支持语言集 UI、缩放和可访问性检查 | 审校与可访问性记录 | 同上需真人 |
| MSI 对话框交互腿 | Browse 对话框、范围外路径触发 `InvalidDirDlg`、`ADDLOCAL` Feature 选择交互、启动勾选生效、取消与修复（含缺源修复提示）、位图显示、缩放/辅助功能、per-machine UAC 交互安装 | 真实交互 UI 环境记录 | 需真实 Windows 桌面交互；许可/InstallDir/Install/Finish 流转已自动化 |
| MSI 默认处理程序与同路径接管 | 在 Windows 默认应用界面选择处理程序、实际打开文件/协议；观察同路径被其他程序接管时 Windows Installer 的原生删除行为 | 默认应用界面截图、唤起结果、接管行为记录 | 原生 MSI 快捷方式不保证保留被替换的同路径链接；需真实交互桌面 |
| MSI 第三方 WiX 专家模板 | 真实第三方 WiX 项目材料（自定义动作/扩展命名空间/merge module）接入专家模式逐项核对：回滚/所有权语义、第三方二进制许可归属、安装失败行为；常规模式白名单拒绝清单抽验 | 模板/fragment 清单与来源、许可核对记录、构建/安装/失败场景日志 | 需用户提供真实第三方 WiX 项目材料 |

### B. 借设备/虚拟机类（按裁决由用户借设备或虚拟机验收）

| 项 | 要做什么 | 完成证据 | 当前卡点/已有覆盖 |
| --- | --- | --- | --- |
| Intel Mac / Rosetta 启动观感 | `macos-x86_64` `.app`/`.dmg`/`.pkg` 在 Intel/Rosetta 宿主的启动观感核对 | 观感记录 | `macos-26-intel` CI 腿与 `macos-26` arm64+Rosetta 腿的挂载/安装/启动断言已全绿；剩观感人工 |
| macOS 26+ quarantine 可写宿主 | 在 `com.apple.quarantine` 可写回的 macOS 宿主（macOS ≤15 或未被限制的环境）复跑五条打标腿：MacAppGate 剥标（unsigned-both、same-team 两腿）、`.app` 更新链 e2e 剥标断言（全链照跑、仅打标面条件化）、DMG quarantine 跨卷传播、Gatekeeper 观察腿 | 五条腿在可写宿主的跑绿记录 | macos-26 CI 实证受保护 xattr 对测试进程全写入面不持久化（`xattr -w` CLI 与裸 setxattr 同样 rc=0 但 getxattr 读不回，verify run 38914231566）；各腿已接 `TrySetQuarantineTree`/`TrySetQuarantine` 写+读回探测，写失败或读不回即诚实 Skip（e2e 腿全链照跑、仅剥标断言按探测结果条件化）——macos-26 上为尽力腿，真实剥标/传播断言待可写宿主验收 |
| 干净 macOS 宿主矩阵 | 无 Xcode/CLT 干净宿主各 macOS 主版本首装 `.app`/`.dmg`/`.pkg`；含低于 `LSMinimumSystemVersion` 的旧版本宿主拒跑断言；顺带在无 `actool` 宿主构建 `.app` 验证降级分支（警告不阻塞） | 挂载/EULA 弹窗/拖放安装首启记录、拒跑提示、降级警告记录 | 现宿主有开发工具链；云 mac 实例按天可租 |
| 干净 Windows 10/11 | WiX 自身依赖边界（无 .NET SDK 仅 candle/light 编译）、独立 VM 复跑安装/卸载、Win7 记 Framework 缺失边界 | 各宿主构建/安装/卸载记录 | 非干净 Windows 11 x64 已有证据；干净宿主未测 |
| 提权/UAC Windows | NSIS per-machine 安装/卸载与 journal ACL 取证、预建父目录所有权/继承；MSI current-user/per-machine 权限差异；UPDATE 引导件在 `Program Files` 提权目录的写权限与 UAC 交互 | `icacls`/HKLM 权限/提权边界记录 | 普通本机不得静默触发 UAC；当前实证均 per-user 目录 |
| 可丢弃故障 VM | NSIS 真实重启+锁定文件删除（重启后复核）、真实 ACL 拒绝+物理磁盘/配额耗尽；MSI 重启/锁定/磁盘故障/缺源 | 前后快照、退出码、verbose log、重启后文件/注册表 | `disposable-vm-faults.ps1` 收编进 `verify`（windows-2025 与 windows-11-arm 跑绿：真 ACL 拒装 rc=2+真 VHD 盘满 ENOSPC rc=2+无半途树）；真实重启+锁定文件删除（`tests/Manual/win/nsis-reboot/Verify.ps1`）与 MSI 故障仍挂本表 |
| 跨 Windows 版本固定项 | Win10/11 各 build 上升级前/卸载前已固定开始菜单/任务栏项检查 | 各 build 固定项行为记录 | `pinned-items.ps1` 收编进 `verify`（windows-2025 b26100 x64 与 windows-11-arm b26200 跑绿；另 Devin VM b20348 实证）；Win10 桌面 SKU 与任务栏 pin 落点仍挂本表——win11 已移除程序化 pin 通道（shell verb 枚举得到但 DoIt 返 E_ACCESSDENIED），落点只能人工 |
| appimaged 类桌面集成 | appimaged/gear lever 工具对 AppImage 的识别与观感 | 工具识别观感记录 | `.desktop`/图标产出与 `desktop-file-validate` 校验收编为一次性 CI 腿跑绿（run 37892544275）；双击启动=FUSE 同路径已验；工具识别观感仍挂本表 |
| NSIS `/R` 降权启动 | per-machine 安装后 `setup.exe /S /R` 启动已安装应用：进程 Integrity Level=Medium、属桌面用户不继承 High 令牌、参数传递到达 | Process Explorer 等 IL 观察 + 应用收到参数的断言 | 需交互桌面（UAC 弹窗手动批准）+ IL 观察工具；`uac-assisted.ps1` 辅助就位 |
| NSIS Restart Manager 无法关闭进程 | 另一用户会话/受控 fixture 中运行安装目录主程序，静默升级应返回 `5`、不动旧安装、不影响同名他处进程 | 退出码、旧安装哈希对比、journal 记录 | 需可交互 Windows VM 双会话或不可终止 fixture；不得用系统关键进程 |
| deb/rpm 文件/协议关联双击唤起 | GUI 桌面宿主双击 `.desktop` 声明关联的文件/协议，应用被唤起 | 唤起记录 | 菜单/图标/点击启动已验（KDE 截图）；验收 fixture 已声明 `text/plain`+`x-scheme-handler:bundler-fixture` 关联对象且装后断言自动化；GUI 双击唤起观感仍需人工 |
| `.app` 默认处理器切换/多 handler | 文件/URL scheme 在默认应用界面切换处理器、多 handler 共存场景的实际唤起观察 | LaunchServices 记录、唤起日志 | `lsregister`+`open` 唤起已自动化；切换处理器属 GUI 用户动作只能人工 |
| UPDATE `open -n` 桌面重启语义 | 真实 macOS 桌面会话下 `open -n` 重启应用的 Dock 图标/聚焦/路径观察 | 桌面会话观察记录 | 现有断言止于 `open` 返回码与进程存活（无头环境） |
| deb systemd 生命周期补验 | 真实 systemd 宿主补验 `systemctl status`/journal、`-r` 后 unit 清理 | status/journal/清理记录 | 容器腿无 systemd；unit 落位、postinst `daemon-reload` 合成与真实宿主 `enable --now` 已验（2026-09-28 Ubuntu 记录，原 LINUX-DEB-OI-04） |
| `pkg-apply` 图形提权入口链 | 桌面宿主上经 `pkexec`→`zenity`/`kdialog`+`sudo -S`→裸 `sudo` 三级退化链拉起 `bundler-updater pkg-apply` 的图形交互面 | 图形提权交互到装包落账本、`--app` 降权重启的端到端记录 | **提权后语境容器内已实证**（`syspkg-verify` CI 腿 run 38936728961：debian/fedora/alpine 容器内 `apt-get`/`dpkg -i`、`dnf`/`rpm -U --oldpackage`、`apk add`/`--allow-untrusted` 退化链+降权重启+env 回灌+账本断言全绿，含 busybox `setpriv` 在场拒参→`su` 承接实态）；**客户端编排（SyspkgReplay：预检三件套/留存/三级提权退化链/env 件一次性清理/回滚重放）已实现**，unit 探针族+integration POSIX 垫片 e2e（仅 Linux 宿主执行）双侧实证；`pkexec` 在场但会话无 polkit agent 的拉起即败由有界退出观察退化下一级兜底；**`pkexec` 弹窗在场被用户拒绝授权（pkexec rc 126/127 鉴权失败面）属用户裁决面**——拒绝后的退化/终止形态与桌面观感一并归本项验收；容器无 pkexec/polkit 图形交互通道，三级提权入口链需桌面宿主 |
| `pkg-apply` Windows MSYS sh 腿 | Windows 宿主装 MSYS2/Git-Bash，以 `sh` 跑 integration pkg-apply 奇偶族的 sh 支路（stub 垫片非特权全链：退化链/降权构造/env 回灌/退出码同断言集） | MSYS 宿主 sh 腿全链通过记录 | sh 支路自动化只在 POSIX 宿主跑（`SkipWhen(IsWindows)`，CI 无 MSYS 腿），Windows MSYS 全链缺自动化证据 |
| s390x/ppc64le UnixFileTypes PAL 可达性 | 在 s390x/ppc64le 宿主（真机或 QEMU 整机模拟）跑 `UnixFileTypes`：`libSystem.Native` PAL 可解析且 `FileStatus.Mode` 判型正确 | 对应架构宿主上类型读取断言记录 | PAL 化后内核 stat 偏移表不存在，剩"PAL 在该 ABI 可解析+判型"单点未验；amd64/arm64 宿主已断言 |
| Windows zip 符号链接还原 | Windows 宿主（开开发者模式或管理员）解含符号链接的 zip，断言链接还原语义 | Windows 侧链接还原记录 | POSIX 侧 `ArchiveExtractor`/xattr 腿已验；`WindowsLinks` 宿主差件需 Windows 符号链接权限，普通 CI 宿主不开开发者模式时创建被拒属平台预期 |
| UPDATE zypper 降级语义 | openSUSE 宿主实测 `zypper install -y` 同 argv 重放旧版本地件的非交互降级形态（实现不带 `--force`/`--oldpackage`，放行/解题确认/拒装皆待实测） | 降级命令序列与结果记录 | **apk 降级已实证**：alpine 容器 `apk add` 签名旧版本地件原生放行降级（rc=0，账本翻回旧版，`syspkg-verify` run 38936728961 K2 相位）；deb `apt-get`/`dpkg -i`、rpm `dnf`/`rpm -U --oldpackage` 已明；**zypper 非交互降级形态仍挂本表** |
| UPDATE repack 拒绝场景 | 旁车在场但包非本包管理器账本登记（repack/手动铺文件）时，预检确定性拒绝"不参与更新" | 拒绝路径与退出码记录 | 防 Tauri #3595 类漂移更新；客户端预检拒绝已实现并经垫片断言（账本归属非零即拒），真机 repack 现场未验 |
| UPDATE 依赖变更场景 | 更新包相对已装版本新增/变更依赖声明（deb `Depends`/rpm `Requires`/apk `depends`）时更新链全行为验收，含 `dpkg -i` 退化腿缺依赖留 unconfigured 态的恢复路径 | 依赖变更包更新记录、退出码与账本状态 | 设计约束更新包不得新增依赖（`docs/design/update.md` §2.3）；确需放开先经本项验收再改文档边界 |

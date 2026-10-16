# UPDATE 自更新模块

本文件是 UPDATE 的唯一文档：决策、协议、能力状态与边界。
这是跨格式产品面而非新安装格式：产物随既有格式旁车而生，更新语义分两式复用既有安装器能力。
已冻结（基线 `0.1.0-alpha.74`）。
deb/rpm/alpineapk 的 installer-replay 扩展为冻结后设计定稿项（§2）：发射与引导件 `pkg-apply` 动词已实现，客户端 replay 编排仍为**计划实现**。

## 1. 核心决策

- **覆盖格式**：nsis/msi/app/appimage/zip/targz 六件已实现。
  deb/rpm/alpineapk 归 installer-replay 语义族按 §2 受约束实现——发射与 `pkg-apply` 动词已落地，客户端 replay 编排计划实现。
  dmg 是运输件、pkg 是提权安装器，非更新载体。
- **清单形态**：静态 JSON 按通道分文件（`bundler-update-feed.{channel}.json`，文件名=通道，零服务端分流）：`{channel, version, notes, publishedAt, artifacts:[{rid, format, file, url, size, sha256, sig, blockmap?}]}`。
  `url` 是相对清单所在目录的输出根路径（byFormat 布局含 `<format>/` 前缀），`file` 为裸文件名。
  GitHub Releases/静态站直接可用。
- **签名**：ECDSA P-256（.NET BCL 内置，零依赖）。
  `.sig` 旁车（64B P1363 r‖s，与 `.sha256` 同位部署）——签名防伪、sha256 防传输损坏、HTTPS 防中间人，三层各职。
- **安装身份**：`bundler-update.json` 旁车写入安装目录/载荷顶层（`format/rid/channel/feedUrl/publicKey` 五元，deb/rpm/alpineapk 另加 `pkg` 包名字段——旧读端未知字段忽略，v1 兼容），不改应用任何文件。
  兜底链：旁车→ARP 注册表/Info.plist/receipt/包管理器账本→确定性报"更新不可用"，绝不乱猜。
- **应用语义两式分工**：安装器格式走重跑安装器（nsis `/S /UPDATE`、msi `msiexec` major upgrade——复用既有升级语义零新造）。
  deb/rpm/alpineapk 同属 installer-replay 族，由引导件 `pkg-apply` 提权编排包管理器安装命令（§2）。
  非安装器格式走 Sparkle 式三段换包（stage→验签→备份→原子替换→重启）。
  **同格式约束**（zip 覆盖安装器装的程序会使 ARP/注册表/快捷方式/卸载所有权全线失联，系统包格式同样绝不跨格式换装——`format` 严格相等 + 同 RID 约束不动）。
- **引导程序**：外置 Native AOT per-RID 二进制 `bundler-updater[.exe]`——运行中应用无法自换文件后重启（win 文件锁死、posix "退出后谁拉起新版"需外置）。
  极老宿主降级 POSIX shell 脚本（清单 `bootstrap` 字段声明）。
  linux 用 musl 静态一件通吃。
- **差分**：block-map（无状态方案）——64KiB 固定块 sha256 表写 `artifacts[].blockmap`。
  客户端比对新旧 blockmap，命中块本地复用、缺失块合并 HTTP Range（需 206）拉取后按偏移重组。
  **仅边界对齐的未变块可复用**，前部插入的漂移块按缺失处理。
  任一环节失败回退全量。
  `EnableDelta` 默认开。
  CDC/rolling hash 若实测漂移损失过大再评。
- **回滚**：换包期瞬备 `<install>.bundler-backup` 必建（原子性+断电恢复载体），成功后默认删除。
  `KeepRollbackBackup`+`RollbackBackupDirectory` 显式保留→落数据区 `DotNet.Bundler/backups`（per-user=`%LOCALAPPDATA%`/`~/Library/Application Support`/`$XDG_DATA_HOME`，per-machine=`%ProgramData%`/`/Library`/`/var/lib`）。
  `--rollback` 显式回滚。
  不做孤儿清理。
  安装器式由安装器自身回滚语义承接。
  deb/rpm/alpineapk 回滚=同路径提权重放留存旧包（§2）。
- **macOS**：新 `.app` 签名/公证由 MacApp 管线打包期完成。
  更新器职责=换包前 `codesign --verify` 且签名身份与旧包一致（防劫持）+剥 `com.apple.quarantine`+`open -n` 重启。
- **feedUrl 多形态**：http(s)/`file://`/本地路径/UNC——离线更新与企业内网分发是静态清单模型的免费特性。

## 2. deb/rpm/alpineapk：受约束 installer-replay（计划实现）

状态：**部分实现**——§2.3 引导件动词与 §2.5 身份/发射已落地，§2.2 客户端编排与 §2.4 回滚重放的客户端侧计划实现。
"包管理器领地"论证的真实内核是**不绕过账本**——此前据此整体拒绝，现把防线从"不碰"收紧为"不绕账本"：replay 让包管理器自己登记、卸旧、跑 scriptlet，恰恰尊重账本，与拒绝理由不冲突。

### 2.1 语义归属与分界

- deb/rpm/alpineapk 与 nsis/msi 同属 **installer-replay 语义族**：换包执行体是各生态包管理器（apt/dpkg、dnf/zypper/rpm、apk），引导件只编排安装命令。
- **不走 file-swap 三段式**：暂存搬移、`.bundler-swap` marker、崩溃恢复件全不引入此路径——包管理器自带事务账本（中断归 `dpkg --configure -a` 等原生机制管），重复记账即 desync 风险源。
- 绕过账本直换 `/usr` 文件仍**明确拒绝**（§5）：无先例，重复记账/孤儿文件/scriptlet 失序/卸载账实不符/完整性校验误报五重 desync 致命伤。

### 2.2 客户端侧（普通权限）

流程：下载包 → ECDSA `.sig` 验签 → 预检 → 留存回滚件 → 提权拉起 `bundler-updater pkg-apply` → 客户端退出。

- **验签**：与现状同一 ECDSA 链。
  rpm GPG/apk RSA 等包管理器自带签名层是**正交层**，不参与更新链信任（信任根仍是烙入身份的 `publicKey`）。
- **预检三件套**：① `bundler-update.json` 旁车在场。
  ② `command -v` 探得对应包管理器。
  ③ 账本归属双检——`dpkg -s <pkg>`/`rpm -q <pkg>`/`apk info -e <pkg>` 确认 `pkg` 名确由该包管理器登记。
  缺任一→确定性拒绝"不参与更新"，防 repack/手动铺文件造成的漂移更新（Tauri #3595 类坑）。
- **回滚留存**：包管理器账本只记安装事实、不保留包文件，留存责任在客户端——Apply 前把本次下载到的新包与（若可得）当前安装版本对应工件存入用户空间 retention 目录，供 `--rollback` 重放。
  非 Bundler 链首装或 retention 无留存旧包时，`--rollback` 确定性拒绝报"无留存包可回滚"，不空转。
- **提权三级退化链**：`pkexec` → `zenity`/`kdialog` 询问密码 + `sudo -S` → 裸 `sudo`。
  任一级拉起 `bundler-updater pkg-apply` 后客户端退出。

### 2.3 引导件侧（root，新动词 `pkg-apply`）

只做两件有意义的事：

- **装本地包**：命令前端优先退化链——deb：`apt-get install -y ./x.deb` → `dpkg -i`。
  rpm：`dnf install -y ./x.rpm` → `zypper install` → `rpm -U`。
  apk：`apk add ./x.apk`（公钥未预置 `/etc/apk/keys/` 时 `--allow-untrusted`，标 TOFU 边界——信任已由 ECDSA 验签建立，包管理器签名层仅作运输完整性）。
  dpkg 腿恒带 `DEBIAN_FRONTEND=noninteractive --force-confdef --force-confold`——职责仅 conffile 静默（防无 tty 环境交互挂死）。
  `dpkg -i` 本就不解析依赖，不存在"防依赖不解析"的旗标功效。
  依赖解析归退化链首腿 `apt-get install` 管。
  裸 `dpkg -i` 退化腿遇缺依赖会留 unconfigured 态（归 `dpkg --configure -a`/`apt-get -f install` 原生机制修），故更新包**不得新增 Depends**——确需新增属依赖变更场景，登记外部验收/文档边界（`docs/project/special-acceptance.md`）。
- **降权重启**：装完由 `PKEXEC_UID`/`SUDO_UID` 取回原始用户 → `setpriv`/`sudo -u`/`runuser`/`su` 退化链降权 + 回灌客户端导出的会话 env 件（`DISPLAY`/`XAUTHORITY`/`DBUS_SESSION_BUS_ADDRESS` 等）→ 以原权限拉起 `--app`。
  不以 root 重启应用。
  降权器"在场"不等于"能用"：每级先同步预检最小 argv（`工具 选项 /bin/sh -c :`，sudo 加 `-n` 免密闸），rc≠0 即降级下一级——分离 spawn 只证 `nohup` 分叉成，证不了降权器收下 argv（alpine busybox `setpriv` 在场却拒 `--reuid/--regid/--clear-groups` 是实证过的真案，无预检会把重启静默吞成 rc0）。
  预检同步执行不设超时——最小 argv 形（`sh -c :` 即刻返回）+ stdin 钉死零交互，挂死无现实入口，超时+强制终结路径的复杂度不抵收益。
- 引导件 musl 静态件注入 `/usr/lib/<pkg>/`（与 zip/app 注入同构）。
  nsis/msi"不注入引导件"先例不适用此处——installer-replay 族的执行体仍是引导件编排安装命令。
  动词协议签名：`bundler-updater pkg-apply --pkg <本地包> --format <deb|rpm|apk> --app <重启目标> [--env-file <会话env件>] [--rollback] [--log <件>]`。
  每级安装命令先 `command -v` 预检再调用；`--env-file` 会话 env 降权后经内层 `env K=V` 回灌（`sudo` env_reset 剥外层环境，缺省按最小集 HOME+DISPLAY）；外层 `nohup` 分离。
  退出码独立面：0 成功 / 3 参数错 / 4 安装失败 / 5 包管理器缺席 / 6 重启失败（安装已提交，客户端据此报"已装待手动重启"）。
  AOT `PkgApplyPlan` 与 POSIX `bundler-updater.sh` pkg-apply 支路逐字同协议同退出码；双侧同名测试缝 `BUNDLER_UPDATER_CMD_<TOOL>`/`BUNDLER_UPDATER_PASSWD_FILE`/`BUNDLER_UPDATER_APK_KEYS_DIR` 供 stub 垫片非特权跑全链。

### 2.4 回滚

`--rollback` = 与更新同路径提权、同退化链重放留存旧包，逐腿按各包管理器降级语义放行。
deb 退化链与更新同 argv（`apt-get install -y` 不带 `--allow-downgrades`）——debian 容器实证 apt 拒旧版降级、自动退化 `dpkg -i` 原生放行（rc=0、账本翻回旧版——`syspkg-verify` D3 相位）。
rpm `dnf install -y` 与更新同 argv（非 `dnf downgrade`）——fedora 容器实证 dnf5 `install` 对本地旧版件原地放行降级（rc=0、账本翻回旧版——`syspkg-verify` R3 相位）。
`zypper install -y` 同 argv 重放——非交互降级形态仍外部待验收（`docs/project/special-acceptance.md`）。
退化腿 `rpm -U` 在 `--rollback` 下加 `--oldpackage`（`syspkg-verify` R3b 相位实证账本翻回旧版）。
apk `apk add` 旧版本地件原生放行降级（alpine 容器实证：rc=0、账本翻回旧版——`syspkg-verify` K2 相位）。

### 2.5 身份与清单

- `bundler-update.json` 加 `pkg` 字段（包管理器包名）。
  旧读端忽略未知字段，v1 身份兼容。
- `CoveredFormats` 收 `Deb`/`Rpm`/`AlpineApk`。
  清单 `format` 严格相等 + 同 RID 约束不动——绝不跨格式换装（deb 装的不用 rpm 盖）。

## 3. 密钥轮换边界

公钥钉在安装身份旁车——**更换私钥即旧客户端永拒新清单**。
现阶段轮换路径是重发全量安装包。
多钥 sidecar/逐钥过渡属未来规格，有真实需求再立项。
首次正式发布时密钥仪式不可省。

## 4. 能力状态

已实现并验证：

- `UpdateManifestEmitter` 清单 + `.sig` 旁车 + `bundler-update.json` 身份烙记。
  `BundlerUpdate*` MSBuild 旋钮族。
- AOT 引导件全 RID 内嵌（win x86_64/aarch64/i686、linux musl×2 静态件、mac x86_64/arm64/universal fat，arm64 件真宿主产出，macos-universal 为 x86_64+arm64 lipo 双切片 fat 件，windows-i686 为 x64 宿主产的独立 thin 件，PR #104 起全 8 件均经 produce-bootstrap CI 工件入库、统一 provenance）。
  linux glibc 目标经 musl 姊妹件解析（static-pie 双宿主通吃）。
- POSIX `bundler-updater.sh` 降级件（bash/dash 双壳实证，`.app`/AppImage/zip/targz 换件+回滚同语义）。
- 三段式换包 `<install>.bundler-swap` 崩溃恢复。
  kill -9 断电无砖化实证。
- 并发互斥 `<install>.bundler-lock`（owner pid 存活判夺锁，`--lock-timeout` 默认 30s 超时 rc=3，AOT 与 shell 件跨实现互斥）。
- 磁盘空间预检（下载前按精确 size 比对 + apply 前跨卷两段暂存/复制按树估到目标卷，估不出 WARN 不放行）。
- symlink/exec 位保留。
  install 叶链解析穿透（双侧同语义：`UpdateClient` 持字面拼写、引导件 apply 时 `CanonicalPath` 逐段物理化——客户端不代解链、引导件不信未规范化输入）。
- 安装器重跑：win 宿主 NSIS `/UPDATE` 链与 MSI major upgrade 实证。
- `Bundler.Updater` 应用内库：`UpdateClient` 四动词 Check→Download→Verify→Apply/Rollback（netstandard2.0+net10.0 双目标零依赖）。
  `BUNDLER_UPDATER_LINK` 源文件双栖编译。
- 差分双路实证：本地命中、脏缓存回退、回环 HTTP Range-206。
  `bundler-cache/artifact.bin` 源件缓存。
- 离线/内网本地路径 feed 端到端。
- `pkg-apply` 动词 AOT/POSIX 双实现同协议（deb/rpm/apk 前端退化链、降权重启链、env 回灌、`--rollback`、退出码 0/3/4/5/6）——stub 垫片非特权全链实证：unit 探针族（跨宿主断言）+ integration 真跑奇偶族。
  奇偶族整体仅非 Windows 宿主执行（各腿首部 `SkipWhen(IsWindows)`），Windows 面由 unit 探针族覆盖。
  Windows MSYS sh 腿挂 `docs/project/special-acceptance.md` 外部验收。
- `pkg-apply` 容器真机全链：`syspkg-verify` CI 腿在 debian/fedora/alpine 三容器内以 root 实证——真包装/更新/`--rollback`、账本断言（`dpkg -s`/`rpm -q`/`apk list --installed`）、conffile 保留（deb confold / rpm `.rpmnew`）、apk unsigned `--allow-untrusted` 与签名闸、降权重启 uid 归属+env 回灌、busybox `setpriv` 拒参→`su` 承接、rc 0/4/5/6 全绿（run 38935978299）。
  降权器逐级最小 argv 预检（`spawn` 只证分叉不证收参）由此腿实证补入双实现。

部分实现/外部待验收：macOS 同签名身份比对腿（无 Apple 证书，挂 `docs/project/special-acceptance.md`）。

计划实现（UPDATE 系统包扩展阶段，设计定稿见 §2）：

- deb/rpm/alpineapk 客户端 replay 编排：预检三件套（旁车+`command -v`+账本归属双检）、三级提权退化链拉起 `pkg-apply`、回滚件留存与 `Apply`/`Rollback` 客户端分支。
- 容器提权全链/apk 降级/conffile 修改场景已由 `syspkg-verify` 实证；zypper 降级形态、图形提权入口链（pkexec→zenity/kdialog→`sudo -S`）、repack 拒绝场景、依赖变更场景仍为外部待验收，登记于 `docs/project/special-acceptance.md`。

## 5. 明确拒绝

- 绕过包管理器账本直换 `/usr` 文件式"deb/rpm/apk 自更新"——见 §2.1 五重 desync 致命伤。
  dmg/pkg 自更新（非安装形态，`.app` 本身可更新）。
- Omaha 式专用服务端（静态清单任意静态托管可服务）。
  服务端动态协议/遥测/灰度。
  应用内 UI 更新框架。
  ClickOnce。
  Velopack 式多版本目录。

## 6. 开放协议备忘

清单/签名是开放协议：不经本仓打包的产物按 schema 产签名件同样可更新。
本仓产物亦可被第三方更新器消费。

# UPDATE 自更新模块

本文件是 UPDATE 的唯一文档：决策、协议、能力状态与边界。
这是跨格式产品面而非新安装格式：产物随既有格式旁车而生，更新语义分两式复用既有安装器能力。
已冻结（基线 `0.1.0-alpha.74`）。
deb/rpm/alpineapk 的 installer-replay 扩展为冻结后设计定稿项（§2）：发射、引导件 `pkg-apply` 动词与客户端 replay 编排均已实现，容器内真 root 全链经 `syspkg-verify` 实证，图形提权入口链归外部待验收。

## 1. 核心决策

- **覆盖格式**：nsis/msi/app/appimage/zip/targz 六件已实现。
  deb/rpm/alpineapk 归 installer-replay 语义族按 §2 受约束实现——发射、`pkg-apply` 动词与客户端 replay 编排已落地。
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
  syspkg（deb/rpm/alpineapk）为 installer-replay 族的受约束子形——产品语义仍两式，实现层按派发路径计为三式。
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
  签名态探测 fail-closed：`codesign` 不可运行/输出不可判与"确证未签名"分流——判不出签名态一律拒换（已签旧件被未签件降级、跨 Team-ID 换装同样拒）；Info.plist 为二进制 plist 时经 `/usr/libexec/PlistBuddy` 兜底解 bundle id。
- **清单取信次序**：`<feed>.sig` 旁车先于 JSON 解析验签（签名防伪先于内容语义）；`artifact.file` 词法收口为单段文件名（分隔符/`..`/绝对路径一律拒——签名保真不保意图，落盘名不信远端词法）；
  http(s) `.part` 断点续传核对 `Content-Range`，断点态不一致即弃 `.part` 从头干净拉；
  抓取/IO/HTTP/畸形 JSON/坏公钥全归 `UpdateException` 确定性契约，用户取消（cancellation）原样穿透不包。
- **feedUrl 多形态**：http(s)/`file://`/本地路径/UNC——离线更新与企业内网分发是静态清单模型的免费特性。

## 2. deb/rpm/alpineapk：受约束 installer-replay

状态：**已实现**（自动化层全绿）——§2.2 客户端编排（`SyspkgReplay`）、§2.3 引导件动词、§2.4 客户端回滚重放、§2.5 身份/发射全落地；容器内真 root 全链（退化链/降权重启/env 回灌/账本/conffile/apk 降级/`--rollback`）经 `syspkg-verify` 三容器实证；图形提权入口链的停链语义（取消/拒绝/鉴权失败一律停、已启动腿不重放、对话框失败不触 sudo）已实现并自动化覆盖，真实桌面观感与 GUI 交互归外部待验收（`docs/project/special-acceptance.md`）。
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
- **回滚留存**：包管理器账本只记安装事实、不保留包文件，留存责任在客户端——Apply 前把本次下载到的新包存入用户空间 retention 目录，供 `--rollback` 重放；旧件=历轮更新留存件，无主动取回当前安装版本工件的机制。
  留存是 `<包件>` + `<包件>.sig` 成对落位（签名字节取自本次 feed 验签所用值）——retention 在用户可写区，重放前必须用身份 `publicKey` 对留存副本再验签；验不过/缺 `.sig` 的候选不被拣选（防止同用户写权篡改件借 sanctioned 提权装为 root）。
  非 Bundler 链首装或 retention 无留存旧包时，`--rollback` 确定性拒绝报"无留存包可回滚"，不空转。
  retention 不做容量剪枝——留存件只增不剪不设上限，登记为开放边界（清理由外部运维策略管）。
- **提权三级退化链**：`pkexec` → `zenity`/`kdialog` 询问密码 + `sudo -S` → 裸 `sudo`。
  退化判据只剩可用性探测（`command -v`）与 spawn 前失败：`Process.Start` 未起成
  （探测后工具被删/执行位丢失）才依序补下一腿；一旦 spawn 返回有效句柄该腿即终局——
  观察窗内任意退出码（0/3/4/5/6/126/127/其他）或窗外退出都只留痕+清 env 件，
  句柄原样交还调用方观察退出码，绝不重放已执行安装器。
  授权取消/拒绝/未知鉴权失败一律停（pkexec 126 取消、127 鉴权失败、问密码腿
  对话框失败归一 126）——用户需其他方式时显式重试，不自动换腿。
  问密码腿为串行 sh 脚本：先 `unset password` 清掉可能继承的同名 export 属性，
  再把对话框 stdout/rc 收进 sh 变量——密码不进子进程环境；
  非零即清变量 exit 126 不触 sudo（sudo 缓存票据不得越过取消放行），
  成功才 `command printf` 管道喂 `sudo -S`；密码不进 argv/环境变量/磁盘/日志。
  问密码腿有图形会话门槛：`DISPLAY`/`WAYLAND_DISPLAY` 全缺席（headless）时
  zenity/kdialog 必败直接跳过，直落裸 `sudo`（tty 密码提示/NOPASSWD 场景存活）。
  `Apply`/`Rollback` 返回已拉起 `Process` 供观察退出码——已启动不表示安装成功；
  `applied` 标记/留存件是"最近交付工件"面（诊断与回滚用），非零 rc 不还原。
- 实现落点（B3 `SyspkgReplay`）：客户端身份 `alpineapk` 在动词面映射 `--format apk`。
  会话 env 件写 `<staging>/pkg-<guid>/session.env`（0600，含 CR/LF 的值跳过，零有效对或写败退为不带 `--env-file`）。
  件是一次性输入：引导件读毕即删件与 `pkg-*` 暂存目录，客户端在拉起腿退出时尽力删余（观察窗内退出任意 rc 均内联执行、仍存活腿挂 `Exited` 钩——已终结进程不保证派发 `Exited`）——
  客户端可能先于引导件退出使钩不触发，残件由下一次 apply 的 24h stale staging 清扫兜底。
  新包复制进用户空间 retention 目录并写 `applied` 标记；留存副本在拉起前再过一遍公钥验签（apply 与 rollback 同口径），验不过即拒绝并还原现场。
  验签同趟算出的 sha256 随 verb argv 以 `--sha256` 携给引导件——留存件在用户可写目录，验签到 root 执行之间仍有窗；root 侧把包暂存进私有目录并对暂存副本复核该哈希，验后替换窗收口到"暂存目录属 root"（详见 §2.3）。
  rollback 拣 `applied` 之外最新留存件（须 pkg+sig 成对且过签，否则跳选下一个）同链重放、applied 翻回旧包名。
  提权腿全部 spawn 失败（逐腿 `Process.Start` 均未起成）时还原 `applied` 标记并删除本轮 retained 件——rollback 侧只还原标记、绝不删既有留存件；已启动腿非零退出不属于此还原面。
  测试缝与引导件同名同源：`BUNDLER_UPDATER_CMD_<TOOL>` env 覆盖 + `CommandProbe`/`EnvProbe`/`ExecProbe`/`ChmodProbe`/`SpawnProbe` 进程内探针。

### 2.3 引导件侧（root，新动词 `pkg-apply`）

只做两件有意义的事：

- **装本地包**：命令前端优先退化链——deb：`apt-get install -y ./x.deb` → `dpkg -i`。
  rpm：`dnf install -y ./x.rpm` → `zypper install -y` → `rpm -U`。
  apk：`apk add ./x.apk`（公钥未预置 `/etc/apk/keys/` 时 `--allow-untrusted`，标 TOFU 边界——信任已由 ECDSA 验签建立，包管理器签名层仅作运输完整性）。
  deb 两级恒带 `DEBIAN_FRONTEND=noninteractive --force-confdef --force-confold`（`apt-get` 经 `-o Dpkg::Options` 透传、`dpkg` 直带旗标）——职责仅 conffile 静默（防无 tty 环境交互挂死）。
  `dpkg -i` 本就不解析依赖，不存在"防依赖不解析"的旗标功效。
  依赖解析归退化链首腿 `apt-get install` 管。
  裸 `dpkg -i` 退化腿遇缺依赖会留 unconfigured 态（归 `dpkg --configure -a`/`apt-get -f install` 原生机制修），故更新包**不得新增 Depends**——确需新增属依赖变更场景，登记外部验收/文档边界（`docs/project/special-acceptance.md`）。
- **降权重启**：装完由 `PKEXEC_UID`/`SUDO_UID` 取回原始用户 → `setpriv`/`sudo -u`/`runuser`/`su` 退化链降权 + 回灌客户端导出的会话 env 件（`DISPLAY`/`XAUTHORITY`/`DBUS_SESSION_BUS_ADDRESS` 等）→ 以原权限拉起 `--app`。
  提权语境下不以 root 重启应用；`PKEXEC_UID`/`SUDO_UID` 均缺席（su/root 直跑、无 `PKEXEC_UID` 的 polkit 会话）时按当前权限 spawn——直接 root 拉起场景即以 root 起 `--app`。
  降权器"在场"不等于"能用"：每级先同步预检最小 argv（`工具 选项 /bin/sh -c :`，sudo 加 `-n` 免密闸），rc≠0 即降级下一级——分离 spawn 只证 `nohup` 分叉成，证不了降权器收下 argv（alpine busybox `setpriv` 在场却拒 `--reuid/--regid/--clear-groups` 是实证过的真案，无预检会把重启静默吞成 rc0）。
  预检同步执行不设超时——最小 argv 形（`sh -c :` 即刻返回）+ stdin 钉死零交互，挂死无现实入口，超时+强制终结路径的复杂度不抵收益。
- `--env-file` 是一次性输入件：读毕即删件与父级 `pkg-*` 暂存目录（删失败不挡退出码；
  装失败不消费也删——AOT `PkgApplyPlan` 与 sh 双侧同口径）。
  显式给出的 env 件须过完整性三重（普通文件 + 非符号链接 + 属主=调用 uid，uid 缺席取进程 euid）——判存到 open 的 TOCTOU 窗内 fifo 可挂死 root 进程、symlink 可把 root 私有件灌进降权 app 的 env；任一步不过按 rc3 拒绝，不静默降级"件缺席"。
- 引导件 musl 静态件注入 `/usr/lib/<pkg>/`（与 zip/app 注入同构）。
  nsis/msi"不注入引导件"先例不适用此处——installer-replay 族的执行体仍是引导件编排安装命令。
  动词协议签名：`bundler-updater pkg-apply --pkg <本地包> --format <deb|rpm|apk> --app <重启目标> [--sha256 <hex>] [--env-file <会话env件>] [--rollback] [--log <件>]`。
- **暂存核验（留存包 TOCTOU 收口）**：包先 `cp`/`File.Copy` 进 root 私有临时目录（`bundler-pkg.*`/`bundler-pkg-*`，umask-077/0700），装了暂存副本——装期间原件被改写不影响本轮回放；
  `--sha256`（严格 64 位十六进制，畸形归用法错 rc3）对暂存副本逐位复核，不匹配/无哈希工具一律 rc4 拒绝，不降级装原件；
  暂存根随进程全出口回收（finally/EXIT trap）。
- 每级安装命令先 `command -v` 预检再调用；安装腿有界——AOT `WaitForExit` 超时杀进程返 124、sh 侧 `timeout(1)` 包裹同码，默认 30min，`BUNDLER_UPDATER_PKG_TIMEOUT` 秒数双侧同名覆盖；
  apt-get 另带 `-o DPkg::Lock::Timeout=120` 把 dpkg 锁等待钉在包管理器自身界内。
  `--env-file` 会话 env 降权后经内层 `env K=V` 回灌（`sudo` env_reset 剥外层环境，缺省按最小集 HOME+DISPLAY）；外层 `nohup` 分离——nohup 走与包管理器同一 `ResolveCommand`/`_pa_cmd` 解析序（`BUNDLER_UPDATER_CMD_NOHUP` 覆盖 → PATH），spawn 包三向钉 `/dev/null`。
  `BUNDLER_UPDATER_CMD_<TOOL>` 覆盖值须过存在+普通文件+执行位三重复核才权威，不可用值按未设置退化——覆盖缝只给测试垫片用，流浪 env 不许把探测结果对调用方会话造假。
  退出码独立面：0 成功 / 3 参数错 / 4 安装失败 / 5 包管理器缺席 / 6 重启失败（安装已提交，客户端据此报"已装待手动重启"）。
  AOT `PkgApplyPlan` 与 POSIX `bundler-updater.sh` pkg-apply 支路 argv/退出码同协议、诊断输出同口径；双侧同名测试缝 `BUNDLER_UPDATER_CMD_<TOOL>`/`BUNDLER_UPDATER_PASSWD_FILE`/`BUNDLER_UPDATER_APK_KEYS_DIR` 供 stub 垫片非特权跑全链。

### 2.4 回滚

`--rollback` = 与更新同路径提权、同退化链重放留存旧包，逐腿按各包管理器降级语义放行。
留存旧包重放前同样过身份 `publicKey` 再验签——孤立无 `.sig` 或验签失败的候选件被跳过拣选（按留存 mtime 试下一个），全数不合则确定性拒绝。
deb 退化链与更新同 argv（`apt-get install -y` 不带 `--allow-downgrades`）——debian 容器实证 apt 拒旧版降级、自动退化 `dpkg -i` 原生放行（rc=0、账本翻回旧版——`syspkg-verify` D3 相位）。
rpm `dnf install -y` 与更新同 argv（非 `dnf downgrade`）——fedora 容器实证 dnf5 `install` 对本地旧版件原地放行降级（rc=0、账本翻回旧版——`syspkg-verify` R3 相位）。
`zypper install -y` 同 argv 重放——非交互降级形态仍外部待验收（`docs/project/special-acceptance.md`）。
退化腿 `rpm -U` 在 `--rollback` 下加 `--oldpackage`（`syspkg-verify` R3B 相位实证账本翻回旧版）。
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
  存活判定 fail-closed：进程确定不存在才算死锁可回收；异属主查看受限/判定异常一律按"活"处理——宁等超时也不误杀活着的持锁者（sh `kill -0` EPERM 复核 `/proc` 存在性、AOT `GetProcessById` 全异常保守按活）。
- 磁盘空间预检（下载前按精确 size 比对 + apply 前跨卷两段暂存/复制按树估到目标卷，估不出 WARN 不放行）。
- symlink/exec 位保留。
  install 叶链解析穿透（双侧同语义：`UpdateClient` 持字面拼写、引导件 apply 时 `CanonicalPath` 逐段物理化——客户端不代解链、引导件不信未规范化输入）。
- 安装器重跑：win 宿主 NSIS `/UPDATE` 链与 MSI major upgrade 实证。
- `Bundler.Updater` 应用内库：`UpdateClient` 四动词 Check→Download→Verify→Apply/Rollback（netstandard2.0+net10.0 双目标零依赖）。
  `BUNDLER_UPDATER_LINK` 源文件双栖编译。
- 差分双路实证：本地命中、脏缓存回退、回环 HTTP Range-206。
  `bundler-cache/artifact.bin` 源件缓存。
- 离线/内网本地路径 feed 端到端。
- `pkg-apply` 动词 AOT/POSIX 双实现同协议（deb/rpm/apk 前端退化链、降权重启链、env 回灌、`--rollback`、`--sha256` root 侧暂存复核、退出码 0/3/4/5/6）——stub 垫片非特权全链实证：unit 探针族（跨宿主断言）+ integration 真跑奇偶族。
  argv 拼写单源 `UpdateArgv`（`src/Bundler.Core/Update/UpdateArgv.cs` 同源链接编译）——逐元双引号 CommandLineToArgvW 转义（ProcessStartInfo.Arguments 面）与 POSIX 单引号（su -c/问密码管线面）双侧共用。
  奇偶族整体仅非 Windows 宿主执行（各腿首部 `SkipWhen(IsWindows)`），Windows 面由 unit 探针族覆盖。
  Windows MSYS sh 腿挂 `docs/project/special-acceptance.md` 外部验收。
- `pkg-apply` 容器真机全链：`syspkg-verify` CI 腿在 debian/fedora/alpine 三容器内以 root 实证——真包装/更新/`--rollback`、账本断言（`dpkg -s`/`rpm -q`/`apk list --installed`）、conffile 保留（deb confold / rpm `.rpmnew`）、apk unsigned `--allow-untrusted` 与签名闸、降权重启 uid 归属+env 回灌、busybox `setpriv` 拒参→`su` 承接、rc 0/4/5/6 全绿（run 38936728961）。
  降权器逐级最小 argv 预检（`spawn` 只证分叉不证收参）由此腿实证补入双实现。
- deb/rpm/alpineapk 客户端 replay 编排（`SyspkgReplay`，UPDATE 系统包扩展 B3）：预检三件套（构造闸 `pkg` 名 + 前端退化链 `command -v` + 账本归属双检）、留存目录 + `applied` 标记、三级提权退化链拉起安装位 `bundler-updater pkg-apply`（`alpineapk`→`apk` 映射、拉起即退；退化判据=工具缺席/spawn 前失败，已启动腿任意 rc 不重放、取消/拒绝/鉴权失败停链、问密码取消不触 sudo）、rollback 拣留存旧包同链重放——unit 探针族跨宿主断言 + integration 真进程层道具（rc 原样/窗外清理/spawn 换腿）+ POSIX 垫片 e2e 仅 Linux 宿主执行（非 Linux 腿 skip）全绿。

部分实现/外部待验收：macOS 同签名身份比对腿（无 Apple 证书，挂 `docs/project/special-acceptance.md`）。

外部待验收（UPDATE 系统包扩展阶段，设计定稿见 §2）：

- 容器提权全链/apk 降级/conffile 修改场景已由 `syspkg-verify` 实证；zypper 降级形态、repack 拒绝场景、依赖变更场景仍为外部待验收；图形提权入口链的真实桌面观感（pkexec 弹窗/zenity/kdialog 对话框实机交互）亦归外部验收——停链/取消语义已自动化覆盖，登记口径见 `docs/project/special-acceptance.md`。

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

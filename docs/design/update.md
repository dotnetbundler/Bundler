# UPDATE 自更新模块

本文件是 UPDATE 的唯一文档：决策、协议、能力状态与边界。
这是跨格式产品面而非新安装格式：产物随既有格式旁车而生，更新语义分两式复用既有安装器能力。
已冻结（基线 `0.1.0-alpha.74`）。

## 1. 核心决策

- **覆盖格式**：nsis/msi/app/appimage/zip/targz 六件走本模块清单化流程；deb/rpm/alpineapk 属包管理器领地——清单/差分/验签编排不归本模块，但引导件提供 `pkg-apply` 特权执行体供系统包自更新调用（协议见 §1 末项）；dmg 是运输件、pkg 是提权安装器，非更新载体。
- **清单形态**：静态 JSON 按通道分文件（`bundler-update-feed.{channel}.json`，文件名=通道，零服务端分流）：`{channel, version, notes, publishedAt, artifacts:[{rid, format, file, url, size, sha256, sig, blockmap?}]}`；`url` 是相对清单所在目录的输出根路径（byFormat 布局含 `<format>/` 前缀），`file` 为裸文件名；GitHub Releases/静态站直接可用。
- **签名**：ECDSA P-256（.NET BCL 内置，零依赖）；`.sig` 旁车（64B P1363 r‖s，与 `.sha256` 同位部署）——签名防伪、sha256 防传输损坏、HTTPS 防中间人，三层各职。
- **安装身份**：`bundler-update.json` 旁车写入安装目录/载荷顶层（`format/rid/channel/feedUrl/publicKey` 五元），不改应用任何文件；兜底链：旁车→ARP 注册表/Info.plist/receipt→确定性报"更新不可用"，绝不乱猜。
- **应用语义两式分工**：安装器格式走重跑安装器（nsis `/S /UPDATE`、msi `msiexec` major upgrade——复用既有升级语义零新造）；非安装器格式走 Sparkle 式三段换包（stage→验签→备份→原子替换→重启）；**同格式约束**（zip 覆盖安装器装的程序会使 ARP/注册表/快捷方式/卸载所有权全线失联）。
- **引导程序**：外置 Native AOT per-RID 二进制 `bundler-updater[.exe]`（运行中应用无法自换文件后重启；win 文件锁死、posix "退出后谁拉起新版"需外置）；极老宿主降级 POSIX shell 脚本（清单 `bootstrap` 字段声明）；linux 用 musl 静态一件通吃。
- **差分**：block-map（无状态方案）——64KiB 固定块 sha256 表写 `artifacts[].blockmap`；客户端比对新旧 blockmap，命中块本地复用、缺失块合并 HTTP Range（需 206）拉取后按偏移重组；**仅边界对齐的未变块可复用**，前部插入的漂移块按缺失处理；任一环节失败回退全量；`EnableDelta` 默认开；CDC/rolling hash 若实测漂移损失过大再评。
- **回滚**：换包期瞬备 `<install>.bundler-backup` 必建（原子性+断电恢复载体），成功后默认删除；`KeepRollbackBackup`+`RollbackBackupDirectory` 显式保留→落数据区 `DotNet.Bundler/backups`（per-user=`%LOCALAPPDATA%`/`~/Library/Application Support`/`$XDG_DATA_HOME`，per-machine=`%ProgramData%`/`/Library`/`/var/lib`）；`--rollback` 显式回滚；不做孤儿清理；安装器式由安装器自身回滚语义承接。
- **macOS**：新 `.app` 签名/公证由 MacApp 管线打包期完成；更新器职责=换包前 `codesign --verify` 且签名身份与旧包一致（防劫持）+剥 `com.apple.quarantine`+`open -n` 重启。
- **feedUrl 多形态**：http(s)/`file://`/本地路径/UNC——离线更新与企业内网分发是静态清单模型的免费特性。
- **pkg-apply 特权动词**：引导件第二动词 `bundler-updater pkg-apply --pkg <本地包> --format <deb|rpm|apk> --app <重启目标> [--env-file <会话env件>] [--rollback] [--log <件>]`，deb/rpm/alpineapk 自更新的特权执行体。
  客户端经 pkexec/sudo 以 root 拉起，动词本体只做两件事：① 前端退化链装本地包——deb `apt-get→dpkg` 两级恒带 `DEBIAN_FRONTEND=noninteractive` 与 confdef/confold（apt 侧经 `-o Dpkg::Options::=` 透传，dpkg 侧裸旗标）；rpm `dnf→zypper→rpm`，`--rollback` 子形态末位换 `rpm -U --oldpackage`；apk `add`，`/etc/apk/keys` 无预置公钥时自动 `--allow-untrusted`；每级先 `command -v` 预检再调用，全缺席=5、在场全败=4。
  ② 降权重启——`PKEXEC_UID`→`SUDO_UID` 取原用户（都不在=本来就 root 直跑），`setpriv→sudo→runuser→su` 退化链降权拉起 `--app`（runuser/su 只收用户名，经 passwd 查名；gid 按 `SUDO_GID`→passwd→uid 兜底）；`--env-file` 会话 env 经内层 `env K=V …` 回灌（sudo env_reset 剥外层环境，回灌必须在降权后重建），缺省按最小集 HOME+DISPLAY 并记日志；外层 `nohup` 分离。
  包管理器账本即状态——不引入三段搬移/marker/锁/崩溃恢复；不验签（客户端已验）。
  退出码独立面：0 成功 / 3 参数错 / 4 安装失败 / 5 包管理器缺席 / 6 重启失败（安装已提交，客户端据此报"已装待手动重启"）。
  AOT `PkgApplyPlan` 与 `tools/posix/bundler-updater.sh` 的 pkg-apply 支路逐字同协议同退出码；双侧同名测试缝 `BUNDLER_UPDATER_CMD_<TOOL>`（命令路径覆盖，设了即视为存在）、`BUNDLER_UPDATER_PASSWD_FILE`、`BUNDLER_UPDATER_APK_KEYS_DIR` 供 stub 垫片非特权跑全链。

## 2. 密钥轮换边界

公钥钉在安装身份旁车——**更换私钥即旧客户端永拒新清单**；现阶段轮换路径是重发全量安装包；多钥 sidecar/逐钥过渡属未来规格，有真实需求再立项。首次正式发布时密钥仪式不可省。

## 3. 能力状态

已实现并验证：

- `UpdateManifestEmitter` 清单 + `.sig` 旁车 + `bundler-update.json` 身份烙记；`BundlerUpdate*` MSBuild 旋钮族。
- AOT 引导件全 RID 内嵌（win x86_64/aarch64/i686、linux musl×2 静态件、mac x86_64/arm64/universal fat；arm64 件真宿主产出，macos-universal 为 x86_64+arm64 lipo 双切片 fat 件，windows-i686 为 x64 宿主产的独立 thin 件；PR #104 起全 8 件均经 produce-bootstrap CI 工件入库、统一 provenance）；linux glibc 目标经 musl 姊妹件解析（static-pie 双宿主通吃）。
- POSIX `bundler-updater.sh` 降级件（bash/dash 双壳实证；`.app`/AppImage/zip/targz 换件+回滚同语义）。
- 三段式换包 `<install>.bundler-swap` 崩溃恢复；kill -9 断电无砖化实证。
- 并发互斥 `<install>.bundler-lock`（owner pid 存活判夺锁；`--lock-timeout` 默认 30s 超时 rc=3；AOT 与 shell 件跨实现互斥）。
- 磁盘空间预检（下载前按精确 size 比对 + apply 前跨卷两段暂存/复制按树估到目标卷，估不出 WARN 不放行）。
- symlink/exec 位保留；install 叶链解析穿透（双侧同语义；`UpdateClient` 持字面拼写、引导件 apply 时 `CanonicalPath` 逐段物理化——客户端不代解链、引导件不信未规范化输入）。
- 安装器重跑：win 宿主 NSIS `/UPDATE` 链与 MSI major upgrade 实证。
- `Bundler.Updater` 应用内库：`UpdateClient` 四动词 Check→Download→Verify→Apply/Rollback（netstandard2.0+net10.0 双目标零依赖）；`BUNDLER_UPDATER_LINK` 源文件双栖编译。
- 差分双路实证：本地命中、脏缓存回退、回环 HTTP Range-206；`bundler-cache/artifact.bin` 源件缓存。
- 离线/内网本地路径 feed 端到端。
- `pkg-apply` 动词 AOT/POSIX 双实现同协议（deb/rpm/apk 前端退化链、降权重启链、env 回灌、`--rollback`、退出码 0/3/4/5/6）——stub 垫片非特权全链实证：unit 探针族 + integration 真跑奇偶族（POSIX 宿主 AOT/sh 双腿，Windows 宿主仅 AOT 腿；Windows MSYS sh 腿挂 `docs/project/special-acceptance.md` 外部验收）。

部分实现/外部待验收：macOS 同签名身份比对腿（无 Apple 证书，挂 `docs/project/special-acceptance.md`）。

## 4. 明确拒绝

- deb/rpm/apk 的清单化自更新编排（包管理器领地——`pkg-apply` 只提供特权安装执行体，验签/降权前编排/旧包留存归调用方）；dmg/pkg 自更新（非安装形态，`.app` 本身可更新）。
- Omaha 式专用服务端（静态清单任意静态托管可服务）；服务端动态协议/遥测/灰度；应用内 UI 更新框架；ClickOnce；Velopack 式多版本目录。

## 5. 开放协议备忘

清单/签名是开放协议：不经本仓打包的产物按 schema 产签名件同样可更新；本仓产物亦可被第三方更新器消费。

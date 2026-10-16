# UPDATE 自更新模块

本文件是 UPDATE 的唯一文档：决策、协议、能力状态与边界。
这是跨格式产品面而非新安装格式：产物随既有格式旁车而生，更新语义分两式复用既有安装器能力。
已冻结（基线 `0.1.0-alpha.74`）。

## 1. 核心决策

- **覆盖格式**：nsis/msi/app/appimage/zip/targz 六件；deb/rpm/alpineapk 属包管理器领地明确排除；dmg 是运输件、pkg 是提权安装器，非更新载体。
- **清单形态**：静态 JSON 按通道分文件（`bundler-update-feed.{channel}.json`，文件名=通道，零服务端分流）：`{channel, version, notes, publishedAt, artifacts:[{rid, format, file, url, size, sha256, sig, blockmap?}]}`；`url` 是相对清单所在目录的输出根路径（byFormat 布局含 `<format>/` 前缀），`file` 为裸文件名；GitHub Releases/静态站直接可用。
- **签名**：ECDSA P-256（.NET BCL 内置，零依赖）；`.sig` 旁车（64B P1363 r‖s，与 `.sha256` 同位部署）——签名防伪、sha256 防传输损坏、HTTPS 防中间人，三层各职。
- **安装身份**：`bundler-update.json` 旁车写入安装目录/载荷顶层（`format/rid/channel/feedUrl/publicKey` 五元），不改应用任何文件；兜底链：旁车→ARP 注册表/Info.plist/receipt→确定性报"更新不可用"，绝不乱猜。
- **应用语义两式分工**：安装器格式走重跑安装器（nsis `/S /UPDATE`、msi `msiexec` major upgrade——复用既有升级语义零新造）；非安装器格式走 Sparkle 式三段换包（stage→验签→备份→原子替换→重启）；**同格式约束**（zip 覆盖安装器装的程序会使 ARP/注册表/快捷方式/卸载所有权全线失联）。
- **引导程序**：外置 Native AOT per-RID 二进制 `bundler-updater[.exe]`（运行中应用无法自换文件后重启；win 文件锁死、posix "退出后谁拉起新版"需外置）；极老宿主降级 POSIX shell 脚本（清单 `bootstrap` 字段声明）；linux 用 musl 静态一件通吃。
- **差分**：block-map（无状态方案）——64KiB 固定块 sha256 表写 `artifacts[].blockmap`；客户端比对新旧 blockmap，命中块本地复用、缺失块合并 HTTP Range（需 206）拉取后按偏移重组；**仅边界对齐的未变块可复用**，前部插入的漂移块按缺失处理；任一环节失败回退全量；`EnableDelta` 默认开；CDC/rolling hash 若实测漂移损失过大再评。
- **回滚**：换包期瞬备 `<install>.bundler-backup` 必建（原子性+断电恢复载体），成功后默认删除；`KeepRollbackBackup`+`RollbackBackupDirectory` 显式保留→落数据区 `DotNet.Bundler/backups`（per-user=`%LOCALAPPDATA%`/`~/Library/Application Support`/`$XDG_DATA_HOME`，per-machine=`%ProgramData%`/`/Library`/`/var/lib`）；`--rollback` 显式回滚；不做孤儿清理；安装器式由安装器自身回滚语义承接。
- **macOS**：新 `.app` 签名/公证由 MacApp 管线打包期完成；更新器职责=换包前 `codesign --verify` 且签名身份与旧包一致（防劫持）+剥 `com.apple.quarantine`+`open -n` 重启。
- **feedUrl 多形态**：http(s)/`file://`/本地路径/UNC——离线更新与企业内网分发是静态清单模型的免费特性。

## 2. 密钥轮换边界

公钥钉在安装身份旁车——**更换私钥即旧客户端永拒新清单**；现阶段轮换路径是重发全量安装包；多钥 sidecar/逐钥过渡属未来规格，有真实需求再立项。首次正式发布时密钥仪式不可省。

## 3. 能力状态

已实现并验证：

- `UpdateManifestEmitter` 清单 + `.sig` 旁车 + `bundler-update.json` 身份烙记；`BundlerUpdate*` MSBuild 旋钮族。
- AOT 引导件全 RID 内嵌（win x86_64/aarch64、linux x86_64/aarch64/musl×2、mac x86_64/arm64；arm64 件真宿主产出；windows-i686 经 aot CI 工件）。
- POSIX `bundler-updater.sh` 降级件（bash/dash 双壳实证；`.app`/AppImage/zip/targz 换件+回滚同语义）。
- 三段式换包 `<install>.bundler-swap` 崩溃恢复；kill -9 断电无砖化实证。
- 并发互斥 `<install>.bundler-lock`（owner pid 存活判夺锁；`--lock-timeout` 默认 30s 超时 rc=3；AOT 与 shell 件跨实现互斥）。
- 磁盘空间预检（下载前按精确 size 比对 + apply 前跨卷两段暂存/复制按树估到目标卷，估不出 WARN 不放行）。
- symlink/exec 位保留；install 叶链解析穿透（双侧同语义；`UpdateClient` 持字面拼写、引导件 apply 时 `CanonicalPath` 逐段物理化——客户端不代解链、引导件不信未规范化输入）。
- 安装器重跑：win 宿主 NSIS `/UPDATE` 链与 MSI major upgrade 实证。
- `Bundler.Updater` 应用内库：`UpdateClient` 四动词 Check→Download→Verify→Apply/Rollback（netstandard2.0+net10.0 双目标零依赖）；`BUNDLER_UPDATER_LINK` 源文件双栖编译。
- 差分双路实证：本地命中、脏缓存回退、回环 HTTP Range-206；`bundler-cache/artifact.bin` 源件缓存。
- 离线/内网本地路径 feed 端到端。

部分实现/外部待验收：macOS 同签名身份比对腿（无 Apple 证书，挂 `docs/project/special-acceptance.md`）。

## 4. 明确拒绝

- deb/rpm/apk 自更新（包管理器领地）；dmg/pkg 自更新（非安装形态，`.app` 本身可更新）。
- Omaha 式专用服务端（静态清单任意静态托管可服务）；服务端动态协议/遥测/灰度；应用内 UI 更新框架；ClickOnce；Velopack 式多版本目录。

## 5. 开放协议备忘

清单/签名是开放协议：不经本仓打包的产物按 schema 产签名件同样可更新；本仓产物亦可被第三方更新器消费。

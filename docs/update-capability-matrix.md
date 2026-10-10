# UPDATE 能力矩阵

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 外部待验收项的明细在 `docs/update-open-items.md`。
> 本矩阵覆盖"应用自更新"模块：打包侧清单/签名/身份烙记、引导程序、应用内更新库、差分下载。
> 决策与阶段对应关系见 `docs/update-roadmap.md`。

| 能力 | 状态 | 阶段 | 备注 |
| --- | --- | --- | --- |
| 静态 JSON 清单（通道化 `bundler-update-feed.{channel}.json`） | 已实现 | UPDATE-1 | `UpdateManifestEmitter` 产 `{version,notes,artifacts:[{rid,format,url,size,sha256,signature,blockMap}]}`；`url` 为裸文件名按清单目录解析 |
| ECDSA P-256 `.sig` 旁车 | 已实现 | UPDATE-1 | 64B P1363（r‖s）签名，`UpdateSignatureVerifier` 单源双栖（Core 产/Updater 验）；BCL 内置、零依赖 |
| 身份烙记旁车 `bundler-update.json` | 已实现 | UPDATE-1 | `{format,rid,channel,feedUrl,publicKey}` 写进安装目录/载荷；权威同款旁车模式，不改应用本体 |
| feedUrl 多形态（http(s) / file:// / 本地路径 / UNC） | 已实现 | UPDATE-4 | `ResolveArtifactLocation`：http feed→URI 相对解析；本地→目录拼接；Onova `LocalPackageResolver` 同型 |
| `BundlerUpdate*` MSBuild 旋钮 | 已实现 | UPDATE-1 | `BundlerUpdateFeed`/`BundlerUpdateChannel`/`BundlerUpdatePublicKey`/`BundlerUpdateFormats`/`BundlerUpdateNotes` 等（见 README） |
| Native AOT 引导件 `bundler-updater[.exe]` | 已实现 | UPDATE-2 | per-RID 内嵌包内：windows-x86_64、windows-arm64、linux-x86_64、linux-aarch64、linux-musl-x86_64、linux-musl-aarch64、macos-x86_64、macos-arm64（arm64 件由 GitHub arm64 runner 真宿主产出，见 UPDATE-OI-01）；windows-i686 仍无件 |
| POSIX `bundler-updater.sh` 脚本引导 | 已实现 | UPDATE-2 | AOT 缺失/极老宿主降级；bash/dash 双壳实证；`.app`/`AppImage`/zip/targz 换件+回滚同语义 |
| 三段式换包（stage→备份→原子替换→重启） | 已实现 | UPDATE-3 | 标记 `<install>.bundler-swap` 崩溃恢复；换包期瞬备 `<install>.bundler-backup` 必建（原子性+崩溃恢复载体），成功后默认删除；`ApplyOptions.KeepRollbackBackup`（显式 `RollbackBackupDirectory` 覆盖）保留回滚点→落数据区 `DotNet.Bundler/backups`（per-user=`%LOCALAPPDATA%`/`~/Library/Application Support`/`$XDG_DATA_HOME`，per-machine=`%ProgramData%`/`/Library`/`/var/lib`）；`--rollback` 显式回滚（保留备份可重试） |
| 断电/中断无砖化 | 已实现 | UPDATE-3 | linux 宿主 kill -9 于 701MB CopyTree 中途→marker 恢复实证；换包要么完整要么自动还原 |
| 并发换包互斥 | 已实现 | UPDATE-3 | `<install>.bundler-lock` 锁件（owner pid）；第二实例等锁至 `--lock-timeout`（默认 30s）超时拒 rc=3；崩溃残留按 owner pid 存活判夺锁；AOT 与 `bundler-updater.sh` 同形互斥（跨实现实例互斥） |
| 磁盘空间预检 | 已实现 | UPDATE-3/4 | UpdateClient 下载前按 feed 工件精确 size 对比目标卷剩余即拒；引导件 apply 前把跨卷两段式暂存/`--keep-payload` 复制/回滚复制按树体积估到目标槽所在卷，估不出只 WARN 不放行拒错——同卷原子 rename 不占空间不计 |
| symlink/exec 位保留 | 已实现 | UPDATE-3 | `CopyTree` CreateSymbolicLink+GetUnixFileMode 透传；AppRun symlink+exec 位断言 |
| install 路径叶链解析穿透 | 已实现 | UPDATE-3 | install 叶段为 junction/符号链接时双侧（AOT/`bundler-updater.sh`）解析穿透作用于目标（POSIX `norm_path` 同语义、win mountvol/junction 腿实证）——穿透是设计契约，不做"拒绝叶链"语义；指称分工：`UpdateClient` 持字面拼写（仅 `GetFullPath` 绝对化不解链，锁/备份/缓存等兄弟位按字面锚定），引导件 apply 时 `CanonicalPath` 逐段物理化再算派生路径——两侧指向同一安装，客户端不代引导件解链、引导件不信任未规范化输入 |
| macOS 三项（codesign 验身份/剥 quarantine/`open -n` 重启） | 部分实现 | UPDATE-3 | quarantine 剥离+`open -n`+posix darwin 腿实证；同签名身份比对腿因无 Apple 证书挂 UPDATE-OI-02 |
| 安装器重跑语义（nsis `/UPDATE` / msi major upgrade） | 已实现 | UPDATE-3 | win 宿主实证：NSIS 安装器链 6/6、MSI major upgrade 链路通过；不走换文件语义 |
| `Bundler.Updater` 应用内库 | 已实现 | UPDATE-4 | `UpdateClient` 四动词：Check（版本比较+rid/format 槽位匹配）→Download→Verify→Apply/Rollback；netstandard2.0+net10.0 双目标，零依赖 |
| 协议件链接编译（`BUNDLER_UPDATER_LINK`） | 已实现 | UPDATE-4 | 5 个 Core/Update 源文件按条件编译命名空间双栖，库与 Core 单源不引程序集 |
| 离线/内网分发 | 已实现 | UPDATE-4/5 | 本地路径 feed 端到端实证；UNC 同路径语义 |
| block-map 差分下载 | 已实现 | UPDATE-5 | 64KiB 固定块 sha256 表（`<file>.blockmap`）；命中块本地复用+缺失块合并 Range（http 需 206）；`<install>.bundler-cache/artifact.bin` 源件缓存；任环节失败回退全量；`EnableDelta` 默认开 |
| 差分双路实证 | 已实现 | UPDATE-5 | 本地差分命中腿（`delta applied`/`B reused`）、脏缓存回退腿、回环 HTTP 真 Range-206 腿 |
| deb/rpm/apk 自更新 | 明确拒绝 | — | 包管理器领地（apt/dnf/apk 通道），权威一致不自更新 |
| dmg/pkg 自更新 | 明确拒绝 | — | 分发载体非安装形态；`.app` 本身可更新 |
| Omaha 式专用服务端 | 明确拒绝 | — | 静态清单模型任意静态托管可服务 |
| 内容定义分块（CDC/rolling hash）差分 | 计划实现 | — | 边界对齐漂移损失若实测过大再评（zsync 同型），登记于路线 §1.9 协议注 |

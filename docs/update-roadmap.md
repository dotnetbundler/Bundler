# UPDATE 路线（应用自更新）

> 状态：**已冻结**（2026-10-05；UPDATE-1..6 全阶段实现并实证，文档族收口完成），冻结基线 `0.1.0-alpha.74`。
> 范围依据：`docs/roadmap.md` §7.2「应用自更新/升级包」登记项——为打包产物增加升级/更新能力。
> 上游参照：Tauri updater（清单形态）、Sparkle（appcast/EdDSA/三段式换包/quarantine）、Velopack（外置引导/通道）、electron-updater（latest.yml/block-map）、Omaha（安装器重跑语义）、Onova（便携件原位覆盖）、AppImageUpdate（zsync 参照）；调研报告《更新模块全面调研报告》2026-10-05。
> 本路线是**跨格式产品面**而非新安装格式：产出物随既有格式旁车而生，应用语义分两式复用既有安装器能力。

## 1. 决策清单（已裁决——按推荐项放行）

| # | 决策点 | 候选 | 推荐与理由 |
| --- | --- | --- | --- |
| 1 | 覆盖格式范围 | 全格式 / 自更新适配格式 | **nsis/msi/app/appimage/zip/targz 六件**——`deb`/`rpm`/`alpineapk` 属包管理器领地（权威实践：apt/dnf/apk 仓库统一更新，自更新器与包管数据库撕裂）明确排除；`dmg` 是运输件、`pkg` 是提权安装器，均非更新载体 |
| 2 | 清单形态 | 静态文件 / 动态服务端协议 | **静态 JSON/通道**（Tauri `latest.json` 式）：`{version, notes, publishedAt, artifacts:[{rid, format, url, size, sha256, sig, bootstrap?}]}`，与产物同目录部署——GitHub Releases/静态站直接可用，零服务端依赖；砍 Omaha 式 XML 协议/遥测 |
| 3 | 签名算法 | Ed25519 / ECDSA P-256 / 无 | **ECDSA P-256**——.NET 10 BCL 内置（实测无公开 Ed25519 API，Ed25519 需 BouncyCastle）；与 EdDSA 同构信任模型（公钥进应用+私钥构建期签产物），零外部依赖 |
| 4 | 信任模型 | 仅 HTTPS / sha256 / 分离签名 | **分离签名 `.sig` 旁车 + 清单内嵌**——签名防伪（源头被攻破/第三方镜像仍可验）、sha256 防传输损坏、HTTPS 防中间人，三层各职不替代；私钥经秘密纪律入库构建，公钥打包期写入安装身份旁车 |
| 5 | 安装身份定位 | 修改应用二进制 / 旁车文件 | **`bundler-update.json` 旁车**（electron-builder `app-update.yml` 同型）——不改应用任何文件；写入安装目录或载荷顶层，记 `format/rid/channel/feedUrl/publicKey`；兜底：win 读 ARP 注册表、mac 读 `Info.plist` 键重建，全丢→确定性报"更新不可用"绝不乱猜 |
| 6 | 应用语义 | 原位覆盖 / 重跑安装器 / 多版本目录 | **两式精确分工**：安装器格式走「重跑」——nsis `setup.exe /S /UPDATE`（已有原位覆盖语义）、msi `msiexec` major upgrade（UpgradeCode 链已有）；非安装器格式走「Sparkle 三段式」——stage 解压→验签→mv 旧件备份→替换→重启。不学 Velopack 多版本目录（与安装器语义太重） |
| 7 | 同格式约束 | 允许跨格式更新 / 同格式 | **同格式**——nsis→nsis、msi→msi；zip 覆盖安装器装的程序会使 ARP 清单/注册表/快捷方式/卸载所有权全线失联；跨格式迁移是既有独立产品面（NSIS→MSI legacy 链），不混进更新 |
| 8 | 引导程序形态 | 无外置 / 外置二进制 / 脚本 | **外置 Native AOT per-RID 二进制**——运行中应用无法自换文件后重启（win 文件锁死、posix 侧虽可换但"退出后谁拉起新版"仍需外置）；权威全用外置（Sparkle `Autoupdate.app`/Velopack `Update.exe`/Onova 临时 updater）。极老宿主降级 POSIX shell 脚本（清单 `bootstrap` 字段声明类型）；linux 用 musl 静态 AOT 一件通吃 |
| 9 | 差分 | 不做 / block-map | **block-map**（electron-updater 式无状态方案）——打包期产 `.blockmap`（块哈希表），客户端对新旧两份 blockmap 比对后对产物发 HTTP Range 只拉变化块；服务端零配对产物、静态托管兼容，全量兜底天然存在；排最后阶段实现。协议定死：固定块大小默认 64KiB（实际值写入 blockmap 头）、块以 sha256 标识并按序记录偏移表、比对按哈希匹配不依赖位置（**仅边界对齐的未变块可复用**——前部插入导致对齐漂移的后续块按缺失处理，全量兜底）、缺失块经 HTTP Range 拉取后按偏移重组；若实测漂移损失过大，UPDATE-5 设计期再评内容定义分块（CDC/rolling hash，zsync 同型）替代定宽 |
| 10 | 通道模型 | 清单分通道名 / 动态分流 | **清单 URL=通道**——`latest.json`/`beta.json` 文件名即通道（electron `latest-beta.yml`/Velopack channel 同型）；服务端零语义，灰度/遥测不做；feed URL 同时接受 `file://`/本地路径/UNC 共享（产物 URL 相对清单所在目录解析）——离线更新与企业内网分发是静态清单模型的免费特性（Onova `LocalPackageResolver` 同型先例） |
| 11 | 回滚 | 无 / 备份自动还原 | **file-swap 天然免费**——mv 旧件到备份位，新版启动失败自动还原（Sparkle 式）；安装器式由安装器自身回滚语义承接（nsis journal/msi rollback 已有）。**回滚点默认不保留**（对齐 Sparkle/Firefox）：换包期瞬备 `<install>.bundler-backup` 必建（原子性+断电恢复），成功后删除；`ApplyOptions.KeepRollbackBackup`+`RollbackBackupDirectory` 显式开启→备份迁数据区 `DotNet.Bundler/backups/<名>-<哈希>`（per-user 装→`%LOCALAPPDATA%`/`~/Library/Application Support`/`$XDG_DATA_HOME`，per-machine 装→`%ProgramData%`/`/Library`/`/var/lib`）；不做孤儿清理 |
| 12 | 交付边界 | 仅打包侧 / 打包侧+应用内更新库 | **全做，分两阶段**：先打包侧（清单/签名/旁车/引导模板），后应用内 `Bundler.Updater` 库（检查→下载→验签→调引导→回滚）——库是路线内正式阶段而非另立产品 |
| 13 | macOS 代码签名与隔离 | 更新器签名 / 构建期归属 | **构建期归属**——新 `.app` 的 Developer ID 签名/公证由既有 MacApp 管线在打包期完成（Apple 凭证是既有外部挂账项）；更新器三职责：换包前 `codesign --verify` 且签名身份与旧包一致（防劫持）、剥 `com.apple.quarantine`（程序内下载必被贴标）、ECDSA 验签照旧；无凭证场景新旧同为未签名、行为不劣于现状 |

## 2. 语义对应表（权威→本仓）

| 权威概念 | 本仓对应 | 说明 |
| --- | --- | --- |
| Tauri `latest.json` `platforms` 映射 | `bundler-update-feed.json` `artifacts[]` | `{rid,format}` 双键定位；`portable` 槽兜底 RID-less 载荷 |
| `.sig` 旁车（minisign） | `.sig` 旁车（ECDSA P-256 DER） | 与 `.sha256` 同位部署 |
| `app-update.yml`（electron） | `bundler-update.json` | 格式/RID/通道/feed/公钥五元 |
| Sparkle `Autoupdate.app` | `bundler-updater` AOT 二进制（+shell 降级件） | 随包供应，宿主侧临时复制再跑 |
| Velopack channel | feed 文件名（`latest.json`/`beta.json`） | 零服务端分流 |
| Omaha `updatecheck`→run installer | nsis `/S /UPDATE`、msi `msiexec /i` | 复用既有安装器升级语义，零新造 |
| electron block-map | `artifacts[].blockmap` 字段 | UPDATE-5 实现 |
| Sparkle quarantine 剥离 | file-swap 内 `xattr -d` 步骤 | UPDATE-3 |

## 3. 阶段表

| 阶段 | 范围 | 退出条件 |
| --- | --- | --- |
| `UPDATE-1` | `src/Bundler.Update` 打包侧：`bundler-update-feed.json` schema + 清单生成器 + `EcdsaSigner`（`.sig` 旁车） + `bundler-update.json` 身份旁车 + `BundlerUpdate*` MSBuild/CLI 旋钮 + 单元测试 + 集成断言 | 打包产出清单+sig+身份旁车可互验；签名负例（错钥/改件）拒绝；旋钮面覆盖审计 |
| `UPDATE-2` | 引导程序：`bundler-updater` Native AOT per-RID 模板（win-x64/arm64、osx-x64/arm64、linux-x64/arm64、musl）+ shell 降级件 + 引导协议（等退出/备份/原子换/重启/回报） | 各宿主引导件产出、自测干跑通过；脚本降级件在裸 posix 环境可跑 |
| `UPDATE-3` | file-swap 三段式执行 + mac 三项（codesign 身份一致/quarantine 剥离/失败还原）+ installer-replay 接线（nsis `/UPDATE`、msiexec major upgrade） | win/mac/linux/musl 真机：zip/targz/appimage/.app 全链换包+回滚实证；nsis/msi 更新链实证；断电/验签失败无砖化 |
| `UPDATE-4` | `src/Bundler.Updater` 应用内库：`UpdateChecker`（拉清单/比版本/选件）→ `UpdateDownloader`（http(s) 下载/本地路径解析/sha256 校验/断点续传）→ `SigVerifier`（ECDSA）→ `UpdateApplier`（两式分派调引导）→ 回滚钩子；开放协议文档 | 库 API 面冻结级评审；端到端 demo（打包→发清单→应用检查更新→换包成功）五宿主实证；离线腿（`file://`/本地目录 feed→同目录产物解析→更新成功）纳入验收 |
| `UPDATE-5` ✅ | block-map 差分：打包期 `.blockmap` 产物 + 下载器 Range 栈（新旧 blockmap 比对→变化块请求→本地重组）+ 全量回落 | 差分与全量双路实证；清单 `blockmap` 字段启用 |
| `UPDATE-6` | 收口：能力矩阵/open-items/manual-testing 文档族 + 冻结基线写入 + 主文档同步 | 冻结文档齐备；外部待验收项如实挂账 |

## 4. 阶段实施证据

### 2026-10-07 归档保真修复（xattr+符号链接全链路，三轮宿主测试 D3 项）

- 病理：macOS 签名驻两处——Mach-O `LC_CODE_SIGNATURE`+`_CodeSignature/` 普通文件（任何归档保得住）与非 Mach-O 件的 `com.apple.cs.*` xattr（普通 zip/tar 丢）；`UpdateApplier` 走 `System.IO.Compression` 解包还把 zip 内 S_IFLNK 条目落成普通文件——`.app` Framework 软链断裂+签名件更新后失效（R3 mac 腿实证 ad-hoc 应用更新后完全未签名）。
- 产出侧：`ArchiveBundleBackend` 在 macOS 宿主检测到载荷树含扩展属性（`listxattr` 探测）时，`zip` 改走 `ditto -c -k --sequesterRsrc`（Sparkle/electron-updater 同款，AppleDouble 入 `__MACOSX/`）、`targz` 改走系统 bsdtar（xattr 自动编码 `._*` 条目）；`UpdateManifestEmitter` 的 `.app` 运输 zip 同策略（`ditto --keepParent`）。无 xattr 载荷与非 mac 宿主照旧 managed 写出器，格式不变。
- 解包侧：新增 `ArchiveExtractor`——zip 在 macOS 走 `ditto -x -k`、targz 走系统 `tar`（xattr+软链一并还原）；其余宿主 managed 提取并把 `__MACOSX/` 清掉；`ExtractZip` 补 S_IFLNK 还原（读中央目录 external attributes，netstandard2.0 无 `ExternalAttributes` 属性面），顺带补齐 Linux 载荷 zip 更新的断链潜伏洞。
- 工具链缺席/失败自动回退 managed 写出器并告警（宿主探测降级惯例）；dmg/pkg 通道本零损失不动。
- 已知限制：`ditto -x` 不还原软链自身的 xattr（Apple 工具语义上限，Sparkle 同款）——zip 通道下链级 xattr 落地即丢；targz 通道文件/目录/链接三态全保真。`.app` 签名不含链级 xattr，主场景不受影响；确需链级 xattr 保真的载荷走 targz。
- 证据：`ExtractZip_RestoresSymlinkEntries`（自产 zip 符号链接还原）与 `ExtractZip_DropsAppleDoubleTree` 断言；`Detects_ExtendedAttributes_OnPayload` POSIX 探测断言；`Zip_XattrPayload_UsesHostToolPreservingMetadata`（mac 宿主 `__MACOSX/` 条目区分断言）；`Zip_XattrPayload_RoundTrips_ThroughUpdateExtraction`（targz 路文件/目录/软链三态 xattr + zip 路文件/目录 xattr 与软链本体还原断言）；mac 腿签名 .app zip 往返 `codesign --verify --deep` 实证。

### 2026-10-06 完整测试轮缺陷修复（四宿主全量测试暴露两族真实缺陷，均已修）

- `Bundler.Updater.csproj` 补 `<TargetFramework></TargetFramework>` 复位（与 `Bundler.Core.csproj` 同款）——`src/Directory.Build.props` 单数 `netstandard2.0` 压制复数双目标，净树 build 只产 netstandard2.0，`dotnet pack` 按复数求 net10.0 dll 报 NU5026，级联 98 条集成腿（linux/musl/mac 三宿主同样复现）。
- UPDATE 序列化迁 STJ 源生成：新增 `UpdateJson`（netstandard2.0 保持 `DataContractJsonSerializer`、net10.0 走 `System.Text.Json` 源生成上下文 `UpdateJsonContext`+`UpdateJsonNamingPolicy` 自定义线路名 rid/sig/blockmap），8 处序列化调用点统一收编——CLI AOT 发布 IL2026/IL3050 清零（`DataContractJsonSerializer` 需动态代码，AOT 下不可用）。
- `bundler-updater` usage 补 `--rollback` 命令行；五件 per-RID 引导件全部重建入库（linux/musl 本机、osx/win 宿主子会话）。
- 布局判定定案：`bundler-updater`/`bundler-update.json` 落载荷包根目录是设计契约（`updater/<rid>/` 仅 Bundler.Core 内嵌资源路径）。
- 第三轮缺陷（win 宿主增量复测暴露）：内嵌引导件 `LogicalName` 的 `%(RecursiveDir)` 在 Windows 展开为反斜杠——Windows 构建的 Core.dll 资源名成 `updater/win-x64\bundler-updater.exe`，按正斜杠查询失败 → zip 缺引导件 + 2 用例败；改 `Replace('\','/')` 归一，资源名与构建机无关。
- 证据：净树 pack rc=0 全量 nupkg（net10.0+netstandard2.0 双 lib 入包）；CLI AOT publish rc=0；本机 Bundler.Tests 333/294P/0F/39S、IntegrationTests 176/111P/0F/65S（原 101 败清零）；linux/musl/mac 三宿主复测全绿、win 宿主 pack+集成 0 败。

### 2026-10-06 复审修复第三轮（Devin Review 2 属实处置 + 3 旧案重报回线）

- `SignatureUrl` 修复：feed 带 query/fragment 时 `.sig` 改插路径段（`feed.json.sig?v=3`），末位拼接会被 http 当查询串一部分打到错地址；4 断言单测覆盖。
- `Apply` 侧车前移：文件级安装身份旁车 `WriteSidecar` 移到派生引导件之前——侧车写失败停在换包之前，不再留"换包在跑而身份未持久"的半态（侧车内容不含版本，先写语义不变）。
- 三条旧案重报回线：清单 url 转义（`OriginalString`+转义断言用例）、Apply 未验门禁（`_verifiedArtifact`+拒装用例）、提取根预测性（per-uid 0700 已隔异用户，同 uid 属同主体威胁模型外）。
- 本机 Bundler.Tests update 腿 44/44。
- 复审第四轮 1 属实补修：`SignatureUrl` 本地路径分支——目录名含 `#`/`?` 的本地 feed 是合法文件名字符不是分隔符，改按 scheme 分派（http(s)→UriBuilder 插路径段；本地/file URI→末位追加），补 2 断言；1 旧案（提取根预测性）维持前轮处置回线。

### 2026-10-06 复审修复第二轮（Devin Review 7 属实处置 + 2 误报回线）

- 清单 URL 转义保留：`RelativeUrl` 改 `MakeRelativeUri().OriginalString`（取转义形态），制品名含 `#`/`?`/空格不再把产物地址截断成 fragment/query。
- 清单本体签名：发射器写 feed JSON 后随即 `feed.sig` 旁车签名；`FetchFeedAsync` 先取 `<feed>.sig` 并 ECDSA 验签（取不到/验不过确定性拒绝），堵住"换源降级喂未签清单"面。
- 文件级安装身份旁车：`WriteSidecar` 写 `<file>.bundler-update.json`，`TryRead` 在目录/`Contents/` 分支前先探文件路径——AppImage 单件安装换包后身份不丢（Apply 成功后对文件级安装目录落旁车）。
- 差分缓存收紧：`UpdateDeltaCache` 由 `DownloadAsync` 后移到 `Verify` 成功后才刷新——未过签验的字节永不进缓存。
- `Apply` 门禁：`Verify` 成功记录 `_verifiedArtifact` 绝对路径，未验件（含重命名的他件）Apply 确定性拒绝。
- 引导件提取根按用户隔离：`Path.GetTempPath()/bundler-updater-<uid|UserName>` 且 POSIX chmod 0700——多用户同机不再共享可写提取目录。
- POSIX 重启去 `/bin/sh -c`：`ProcessStartInfo(appPath, UseShellExecute=false)` 直接 exec，消掉路径经 shell 展开面；`.sh` 降级脚本 `nohup "$APP_PATH"` 属变量值非再展开，保持原样。
- 误报回线两条：`.app` 清单条目已实现（`Emitter_AppDirectory_ProducesZipTransport` 证）；`+build` 元数据上轮已整串剥离（`UpdateVersion` 测试证）。
- 测试 +6 全绿：feed 缺签拒/feed 篡改拒/未验 Apply 拒+缓存未沉/文件旁车往返/AppImage 侧车持久断言/清单 url 转义断言；feed.sig 产出断言挂进既有发射器用例；本机 332/293P/0F/39S。
- linux-x64/linux-musl-x64 AOT 件随本轮 `BootstrapPlan` 变更重建入库；win-x64/osx-x64/osx-arm64 由宿主子会话第三轮重建（同法）。

### 2026-10-06 复审修复轮（Devin Review 8 bug+5 flag 全部属实处置）

- `.app` 目录件修复：清单发射前经自家 `ZipWriter` 产 `<name>.app.zip` 运输件（exec 位/软链/确定性全保），签名/块表/尺寸/哈希全对运输件，清单记 `format=app`——此前目录件不进清单致更新完全不可达。
- 清单 `url` 改相对清单所在目录路径（`file` 恒为裸名）：制品按 `<rid>/<format>/` 分目录落盘时裸名解析已错。
- 引导件工具改 Bundler.Core 内嵌资源供应：`updater/<rid>/` 与 `updater/posix/` 清单资源解出兜底，`BootstrapperDirectory` 旋钮保留开发覆盖——MSBuild 包/CLI/项目直引/NuGet 全形态可达，删 tasks/updater 打包与 CLI updater/ 复制双侧通道。
- Windows 宿主禁降级 POSIX 脚本：per-RID 二进制缺件时确定性拒绝，不再把 `.sh` 改名 `.exe` 注入。
- `nsis`/`msi` installer-replay 格式载荷免引导件（旁车照写）；归档注入前先剔除输入树自带同名 `bundler-update.json`/引导件条目（同构去重）。
- `BootstrapPlan` 崩溃恢复先于安装存在性检查（上轮死在备份与换包之间时 install 缺失也可自愈）；新增文件级语义——安装目标为文件（AppImage 单件）时走 `<file>.bundler-{swap,backup}` 单件备份/换入/回滚，宿主目录无关文件原样保留；`tools/posix/bundler-updater.sh` 同构改造并删除根 `tools/` 陈旧副本。
- 身份旁车 `TryRead` 增 `.app/Contents/` 探测；`UpdateVersion` 整串剥离 `+` 元数据并改 semver 数值段比较（`beta.10>beta.2`）；下载 `.part`→已存在目标先删再移。
- 测试 +11 全绿：win 禁降级/内嵌兜底/.app zip+相对 url/marker 缺 install 恢复/文件级换包+回滚/归档去重/semver 段比较/Contents 探测/重复下载/AppImage 文件级真 E2E（linux 宿主真 AOT 引导件）。
- per-RID 引导件随新 `BootstrapPlan` 重建：linux-x64/linux-musl-x64 本机已产入库；win-x64/osx-x64/osx-arm64 待宿主子会话重建（UPDATE-3 同法）。

### 2026-10-05 整模块真实端到端功能测试
MSBuild 真消费链全通：`update-keygen` 产钥→`HelloBundlerApp` 经 `BundlerUpdate*` 真旋钮 publish 出 zip+清单+`.sig`+`.blockmap`（zip 载荷内含侧车+本 RID 引导件）→解包装 v1→feed 发 v1.1.0→`UpdateClient` 全量下载+验签+引导换包→`--rollback` 逐字节还原 v1（备份保留）→feed 发 v1.2.0→**差分实腿 83% 复用**（38.2M 件：31.5M 本地复用/6.7M Range 拉取）→换包后安装目录与 v3 zip 逐文件 sha256 一致。
功能测试暴露两缺陷并当场修复：MSBuild 项目引用模式下任务程序集旁缺 `updater/` 工具树（改 AfterBuild Copy 落 `updater/`，None CopyTo* 会经项目引用流进消费方载荷故弃用）；`UpdateBootstrapper` 的约定目录探测误用 `AppContext.BaseDirectory`（MSBuild 节点里它是 SDK 宿主目录）改 `Assembly.Location`；`UpdateClient` 对相对 `installDirectory` 入库即 `GetFullPath` 绝对化（引导件以自身目录为 cwd）。

### 2026-10-05 UPDATE-6 文档族收口+冻结
文档族齐套：`update-capability-matrix.md`（逐项能力×验收状态）、`update-open-items.md`（UPDATE-OI-01..06：arm64 AOT 件/Apple 凭证腿/桌面会话观察/真实发布链狗食/公网 Range/per-machine UAC）、`update-manual-testing.md`（UPDATE-MT-01..06 一一对应 OI），`manual-testing-index.md` 登记 UPDATE 行（`UPDATE-MT-xx`/`UPDATE-OI-xx` 前缀）；README 增"应用自更新（UPDATE）"节（旋钮表+keygen+UpdateClient 用法+格式边界）；`docs/roadmap.md` §7.2 需求项改"已实现"；AGENTS 一句话现状与 CONTEXT 同步冻结基线 `0.1.0-alpha.74`。同轮补强：HTTP 差分真腿——回环微型服务器实证 feed/块表/Range-206 全走 http 通道。

### 2026-10-05 UPDATE-5 block-map 差分
块表模型 `UpdateBlockMap`（v1：64KiB 固定块+有序 sha256 base64 列表）落 `Core/Update` 并经 `BUNDLER_UPDATER_LINK` 链接进 `Bundler.Updater`，单源双栖。
发射器为每个制品多产 `<file>.blockmap` 并把 `blockmap` 字段写入清单（`url` 仍为裸文件名，块表随制品同目录解析）。
客户端 `UpdateClientOptions.EnableDelta`（默认开）接 `TryDownloadDeltaAsync`：块表合法→对 `<install>.bundler-cache/artifact.bin` 算块表→按哈希不依赖位置比对→命中块本地复制、缺失块合并为连续区间（http 走 `Range` 且硬性要求 206、本地/UNC 走定位读）→产物 size+sha256 全量校验；块表缺失/过期/非法、Range 被拒、校验不过等任一环节失败都回退全量下载。缓存随每次成功下载刷新，供下次差分。
测试 +3：块表确定性（块数/尺寸/有序哈希/分块敏感度）、差分命中腿（三块中两块哈希命中→日志断言 `delta applied`/`B reused`）、脏缓存回退腿（全不匹配仍经拉取重组出正确产物并通过验签）。
本机 Bundler.Tests 314/275P/0F/39S。

### 2026-10-05 UPDATE-4 应用内更新库

- 交付：`src/Bundler.Updater`（netstandard2.0+net10.0，程序集 `DotNet.Bundler.Updater`，零外部依赖）：`UpdateClient` 门面（`FromInstallDirectory`/`FromIdentity`→`CheckForUpdateAsync`→`DownloadAsync`→`Verify`→`Apply`/`Rollback`/`UpdateAsync`）；`UpdateDownloader`（http(s) `.part` Range 断点续传+`file://`/本地/UNC 解析+size/sha256 必验拒放）；`UpdateApplier`（nsis `/UPDATE`、msiexec major upgrade、zip/app/targz 解包换包、appimage 单件）；`UstarReader`（ustar+pax+GNU 长名+软链+逃逸防护）；`UpdateVersion`（数字段+预发布比较）。
- 共享源链接编译（`<Compile Include>` 复用 Core Update 四个文件），`BUNDLER_UPDATER_LINK` 条件把协议类型落 `DotNet.Bundler.Updater.Protocol` 命名空间——同仓双装不撞名。
- feed 语义定稿：`feedUrl`=清单文件地址（http/file/本地路径均可），产物 `url` 恒裸文件名按清单同目录解析（Tauri latest.json 同型）；emitter 相应改裸文件名。
- 回滚协议：`--rollback` 引导模式——备份**复制**回安装目录且保留可重试（原"备份当载荷重走 swap"会先被备份步骤覆盖，属协议缺陷已修）；sh/AOT 双侧同步。
- 修复：sh `--keep-payload` 原用 mv 消耗载荷与 AOT copy 分叉 → 改 `cp -a`；`CopyTree` 跨卷把软链解引用+丢 exec 位 → 软链重建+unix mode 复制（alpine/linux 子会话实证上报）；`BootstrapperPath` 指向 `.sh` 时被二进制分支错抢 → 加扩展名判流。
- 证据：`UpdateTests` 15 + `UpdaterClientTests` 7 = 22/22 全绿；linux 真机端到端（真 AOT 引导件：装 v1→查→下→验→换 v2→回滚）与 posix 降级件换包均 rc=0；全量 Bundler.Tests 310 用例零失败。
- API 语义：无匹配件（远端新版不含本 RID/格式）返回 null 而非抛错——增量滚动发布下是常态。

### 2026-10-05 UPDATE-3 宿主实证

- 四宿主子会话真机全链实证，per-RID 引导件全部入库（分支 `devin/1791228441-update-module` 远端头 `2ca2e62`）：
  - **win-x64**：`bundler-updater.exe` 1.67MB 构建入包；file-swap rc=0 换包+备份；marker 断电恢复（半成品 install+backup→还原再换）；NSIS `v1 /S /D` → `v2 /UPDATE /S` 更新链（FileVersion 2.0.0.0、ARP 恰一条 v2、InstallLocation 复用）；MSI major upgrade（`WIX_UPGRADE_DETECTED`+`MIGRATE`、新 ProductCode、ARP 单条）。
  - **linux-x64**：kill -9 于跨卷 CopyTree 中段（701MB 实载荷 tmpfs→ext4）复跑日志 `interrupted swap detected`→还原→换包完成不砖；软链/可执行位载荷同卷 rename 全保留；posix 脚本 bash+dash 双壳 swap/恢复同协议；签名负例链（真 feed+`.sig`，篡改一字节 `VERIFY_REJECT`）。
  - **macOS-arm64**：osx-arm64 引导件 2.9MB 实证全腿——未签→未签换包 rc=0、quarantine 剥离后全树 xattr=0、bundle-id 不符 rc=4 零变更拒、`open -n` 经 LaunchServices 真重启、posix 脚本 darwin 分支同协议、file-swap+marker 恢复；osx-x64 件构建级验证（宿主无 Rosetta）。
  - **alpine/musl**：linux-musl-x64 static-pie 静态件 2.4MB 入库实证（busybox sh + AOT 三场景协议一致）；linux-arm64 AOT 因 qemu 仿真 ilc SIGABRT 不可产（环境边界非产品问题）。
- 遗留边界：mac 真实签名身份腿需 Apple Developer ID（外部待验收——无证书宿主 adhoc/self-signed 的 TeamIdentifier='not set' 归入 unsigned 分支已验证）；跨卷 CopyTree 软链/执行位丢失已修（UPDATE-4 内）。

### 2026-10-05 UPDATE-2 引导程序

- 交付：`src/Bundler.Updater.Bootstrap`（net10.0 `PublishAot`，程序集名 `bundler-updater`）：`BootstrapPlan` 等退出→备份→原子换→重启→回报，rc 0/2/3/4 契约；`tools/posix/bundler-updater.sh` 同协议 shell 降级件；`tools/<rid>/` 构建产物随包目录。
- `UpdateBootstrapper.Inject/TryResolve`：`BootstrapperDirectory` 旋钮+`AppContext.BaseDirectory/updater` 约定目录；per-RID 件优先、缺件降级 posix 脚本、目录缺位安全跳过。
- 五后端引导件注入：nsis/wix 经 `UpdatePayloadStaging`、macapp→Contents/MacOS（随 codesign）、appimage→payloadRoot、archive→0755 归档条目；MSBuild 包 tasks/updater/ 与 CLI 输出 updater/ 双侧随包。
- 证据：`UpdateTests` 13/13（TryResolve 选件序、Apply 换包+备份+keep-payload、嵌套/缺目录拒绝）；linux-x64 AOT 实件（2.3MB）真跑换包 rc=0；posix 脚本 wait/超时 rc=3/用法 rc=2；CLI zip 冒烟包内含 `bundler-updater`（0755）且解出后可执行换包。
- 修复：`--keep-payload` 原语义死（MoveTree 后判存恒假）→ 改复制换入；`CopyTree` 不建目标根（跨盘 fallback 同缺陷）→ 补 `CreateDirectory`。
- 边界：win/osx/arm64/musl per-RID 件不在本机交叉产出（Native AOT 不跨编译）——UPDATE-3 各宿主子会话本地产件+实证。

### 2026-10-05 UPDATE-1 打包侧

- 交付：`src/Bundler.Update`（`EcdsaSigner`：`.sig` 统一 64B P1363、验签 DER/P1363 双试；`UpdateManifestEmitter`：`.sig` 旁车+`bundler-update-feed.{channel}.json`）+ `src/Bundler.Core/Update/`（`UpdateKeyMaterial`/`UpdateIdentitySidecar`/`UpdatePayloadStaging`）。
- 五后端身份旁车注入（nsis/wix/macapp/appimage/archive）；用户发布目录零触碰（staging 内就地或复制注入）。
- 双入口旋钮：MSBuild `BundlerUpdate*` 六项（任务+buildTransitive+样品 props 覆盖）；CLI `update` 分节+`--update.<knob>`+`update-keygen`。
- 证据：`UpdateTests` 10/10 含 ArchiveBundler 真产 zip 内旁车断言、签名负例（错钥/改件/乱码）拒绝；CLI 冒烟 keygen→zip→`.sig`+feed+包内旁车闭环；全量 298 用例零失败。

## 5. 边界备忘

- 更新引擎消费的清单/签名是开放协议：不经本仓打包的产物按 schema 产签名件同样可更新；反之本仓产物亦可被第三方更新器消费。
- `bundler-update.json` 删除降级链：旁车→ARP/Info.plist/receipt 兜底→"更新不可用"确定性错误，永不乱更新。
- AOT 兼容面：linux=musl 静态一件通吃；win/osx 按 deployment target 编译；老宿主天然走安装器路径不需要引导；无 AOT 覆盖的裸露便携件场景有 shell 降级件。
- 不做清单：服务端动态协议/遥测/灰度、应用内 UI 更新框架（引擎职责之外）、ClickOnce、Velopack 式多版本目录、包管理器内自更新。

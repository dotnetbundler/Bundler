# UPDATE 路线（应用自更新）

> 状态：**路线已立，待 `UPDATE-1` 启动指令**（2026-10-05 立项）。
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
| 11 | 回滚 | 无 / 备份自动还原 | **file-swap 天然免费**——mv 旧件到备份位，新版启动失败自动还原（Sparkle 式）；安装器式由安装器自身回滚语义承接（nsis journal/msi rollback 已有） |
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
| `UPDATE-4` | `src/Bundler.Updater` 应用内库：`UpdateChecker`（拉清单/比版本/选件）→ `UpdateDownloader`（HttpClient 下载/sha256 校验/断点续传）→ `SigVerifier`（ECDSA）→ `UpdateApplier`（两式分派调引导）→ 回滚钩子；开放协议文档 | 库 API 面冻结级评审；端到端 demo（打包→发清单→应用检查更新→换包成功）五宿主实证 |
| `UPDATE-5` | block-map 差分：打包期 `.blockmap` 产物 + 下载器 Range 栈（新旧 blockmap 比对→变化块请求→本地重组）+ 全量回落 | 差分与全量双路实证；清单 `blockmap` 字段启用 |
| `UPDATE-6` | 收口：能力矩阵/open-items/manual-testing 文档族 + 冻结基线写入 + 主文档同步 | 冻结文档齐备；外部待验收项如实挂账 |

## 4. 阶段实施证据

（空——各阶段完成后按日期条目归档于此。）

## 5. 边界备忘

- 更新引擎消费的清单/签名是开放协议：不经本仓打包的产物按 schema 产签名件同样可更新；反之本仓产物亦可被第三方更新器消费。
- `bundler-update.json` 删除降级链：旁车→ARP/Info.plist/receipt 兜底→"更新不可用"确定性错误，永不乱更新。
- AOT 兼容面：linux=musl 静态一件通吃；win/osx 按 deployment target 编译；老宿主天然走安装器路径不需要引导；无 AOT 覆盖的裸露便携件场景有 shell 降级件。
- 不做清单：服务端动态协议/遥测/灰度、应用内 UI 更新框架（引擎职责之外）、ClickOnce、Velopack 式多版本目录、包管理器内自更新。

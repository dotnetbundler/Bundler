# DotNet.Bundler 项目上下文

> 最后更新：2026-10-17
> 当前分支：`main`
> UPDATE 系统包扩展 B0–B5 全链完成并入 `main`（PR #107 设计、#108 发射+`pkg` 身份、#109 引导件 `pkg-apply` 双实现、#106/#110 macOS quarantine 能力探测、#111 客户端 `SyspkgReplay`、#112 syspkg-verify 三容器验收+DropProbe 修复、#113/#115 冻件重冻、#114 收尾：syspkg-verify 经 `workflow_call` 收编 verify 发布门、usage 文档、`pkg` 字段 v1 兼容断言）。
> 测试确定性修复 #116（奇偶腿宿主 `DEBIAN_FRONTEND` env 泄漏+uidedge rec 竞态）与 #117（NSIS 卸载分离删除断言改 WaitFor）已并入。
> 发布门实证：verify 于 `0e4a015` 基线**三连全绿**（run `38975789470`、`38980755696`、`38985327721`——六宿主测试矩阵 + syspkg 三容器腿 + produce/consume/manual/universal 全链）。
> 引导件冻件口径：现嵌 8 件为 produce-bootstrap run `39030858362`@`e0174dc` 产出（PR #129 入库，含 #128 PAL 化——env 件校验与 `UnixFileTypes` 改走 `libSystem.Native`、gate 竞态修复），SHA-256 与字节数留账于该 squash 正文。
> 图形提权入口链等剩余边界仍挂 `docs/project/special-acceptance.md`。
> 当前包版本：`0.1.0-alpha.103`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全部 11 个格式、CLI 与 UPDATE 均已冻结，无进行中的格式阶段；目标模型已定型——`TargetOperatingSystem{Windows,MacOS,Linux}` + `LibC` 旁维度 + `{os}[-{libc}]-{arch}` canonical 词（别称仅输入归一，产物名用各生态词），契约与全产物名目录见 `docs/design/targets.md`**
> 实施证据以 git 提交与 PR 记录为准，各格式 `docs/design/formats/` 单文档只留设计决策、语义契约与能力状态
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 项目不变量在 `docs/project/project-rules.md`，AI 协作规则在 `AGENTS.md`；实现原理在 `docs/design/architecture.md`；产品顺序在 `docs/project/roadmap.md`；各格式契约细节在 `docs/design/formats/` 下对应文档；用户面使用文档在 `docs/usage/`。
> 本文内容须与 Git、代码和测试一致；不一致时以后三者为准并先修正本文。

## 1. 项目结构（现况）

| 项目 | 角色 | 目标框架 |
| --- | --- | --- |
| `src/Bundler.Abstractions` | 跨包稳定契约 | `netstandard2.0` |
| `src/Bundler.Core` | 格式无关的校验、规划、编排、工作目录生命周期、统一工具缓存（`ToolCache`/`ZipToolCache`）与后端共享件（`ToolRunner`/`Hashes`/`Gzip`/`PayloadSource`/`FileTree`） | `netstandard2.0;net10.0` |
| `src/Bundler.Nsis` | Windows + NSIS 后端（内嵌 NsisToolset 3.12-r1 多宿主工具与 win-x86 Native AOT 插件） | `netstandard2.0` |
| `src/Bundler.Wix` | Windows + WiX 3.14.1/MSI 后端（内嵌固定 WiX 工具子集） | `netstandard2.0` |
| `src/Bundler.Signing.Windows` | Windows Authenticode 签名 API（NSIS/MSI 共用） | `netstandard2.0` |
| `src/Bundler.MacApp` | macOS `.app` 后端（骨架/Info.plist/`.icns`/Contents 载荷映射） | `netstandard2.0` |
| `src/Bundler.MacDmg` | macOS `.dmg` 后端（`hdiutil` 全链、拖放卷、`Ulmo`/`Udzo`/`Udbz`） | `netstandard2.0` |
| `src/Bundler.MacPkg` | macOS `.pkg` 后端（`pkgbuild`/`productbuild` 组件与分发包、签名/公证/专家脚本） | `netstandard2.0` |
| `src/Bundler.Deb` | Debian `.deb` 后端（纯托管 ar/tar/gzip 写入器，无原生工具依赖，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.Rpm` | RPM `.rpm` 后端（纯托管 lead/header/cpio/gzip 写入器，无原生工具依赖，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.AppImage` | Linux `.AppImage` 后端（内嵌 appimagetool+type2 runtime，仅 Linux 宿主构建） | `netstandard2.0` |
| `src/Bundler.Archive` | `.zip`/`.tar.gz` 归档后端（纯托管写入器，任意构建宿主） | `netstandard2.0` |
| `src/Bundler.AlpineApk` | Alpine `.apk` 后端（纯托管三段 gzip 写入器，任意构建宿主；可选 RSA 签名经 BouncyCastle） | `netstandard2.0` |
| `src/Bundler.Update` | UPDATE 打包侧：清单发射器 + ECDSA P-256 `.sig`/`.blockmap` 旁车生成 | `netstandard2.0` |
| `src/Bundler.Updater` | UPDATE 应用内库：`UpdateClient` 查/下/验/换四动词 + block-map 差分（`BUNDLER_UPDATER_LINK` 单源双栖 Core 协议层） | `net10.0;netstandard2.0` |
| `src/Bundler.Updater.Bootstrap` | UPDATE 引导件：Native AOT per-target `bundler-updater` + `tools/posix/bundler-updater.sh` 降级件（经 Bundler.Core 内嵌资源供应） | `net10.0` |
| `src/Bundler.MSBuild` | MSBuild Task 适配层（`buildTransitive` 导入） | `netstandard2.0` |
| `src/Bundler.Cli` | CLI 适配层（dotnet tool nupkg + `PublishAot` 原生二进制双分发） | `net10.0` |
| `src/Bundler.Package` | 便利元包 `DotNet.Bundler`（聚合后端与 MSBuild 支持） | `netstandard2.0` |
| `tests/Unit` | `Bundler.UnitTests`：单元层（纯进程内、零子进程 spawn） | `net10.0` |
| `tests/Api` | `Bundler.ApiTests`：公共 API functional 层（无 IVT，真跑内嵌工具产真物） | `net10.0` |
| `tests/Integration` | `Bundler.IntegrationTests`：真环境层（fixture 发布链/真装真卸/docker/更新链） | `net10.0` |
| `tests/Package` | `Bundler.PackageTests`：本地 nupkg 消费契约层 | `net10.0` |
| `tests/Shared` | 四测试工程链接共享的基建源码目录 | `net10.0` |
| `tests/Fixtures` | 被测 fixture 工程与载荷集中地 | — |
| `tests/Manual` | 特殊验收脚本层（验收登记在 `docs/project/special-acceptance.md`） | sh/ps1 |
| `samples/HelloBundlerApp` | 全后端统一公开示例（应用版本 `1.0.0`，主工程公共旋钮 + `formats/*.props` 各后端专属旋钮，100% 旋钮覆盖） | `net10.0` |
| `tools/Bundler.Nsis.Plugin` | NSIS 原生插件源码（有意在 slnx 之外，重建需 .NET 10 + Windows 原生链） | `net10.0` |
| `third_party/` | 第三方归档、许可证、逐文件 SHA-256 与 provenance 文档 | — |

根 `Directory.Build.props` 的默认 `TargetFramework` 为 `net10.0`；
MSBuild 任务链保留 `netstandard2.0`（双宿主加载契约，见 `docs/project/project-rules.md` §2）。
src/ 各工程共享元数据统一在 `src/Directory.Build.props`（链式导入根 props），Cli/Core 按项目名条件豁免；
外部依赖版本集中在 `Directory.Packages.props`（中央包管理），`PackageReference` 不带 `Version=`，自仓 `DotNet.Bundler*` 包引用用 `VersionOverride="$(BundlerPackageVersion)"`。

## 2. 各格式当前状态

| 格式 | 冻结基线 | 文档 |
| --- | --- | --- |
| NSIS | `0.1.0-alpha.31`（alpha.32/33 journal 加固） | `docs/design/formats/win-nsis.md` |
| MSI | `0.1.0-alpha.43` | `docs/design/formats/win-msi.md` |
| `.app` | `0.1.0-alpha.45` | `docs/design/formats/mac-app.md` |
| `.dmg` | `0.1.0-alpha.45` | `docs/design/formats/mac-dmg.md` |
| `.pkg` | `0.1.0-alpha.47` | `docs/design/formats/mac-pkg.md` |
| `.deb` | `0.1.0-alpha.51` | `docs/design/formats/linux-deb.md` |
| `.rpm` | `0.1.0-alpha.55` | `docs/design/formats/linux-rpm.md` |
| `.AppImage` | `0.1.0-alpha.58` | `docs/design/formats/linux-appimage.md` |
| `.zip`/`.tar.gz` | `0.1.0-alpha.59` | `docs/design/formats/archive.md` |
| CLI | `0.1.0-alpha.62` | `docs/usage/cli.md` |
| Alpine `.apk` | `0.1.0-alpha.63` | `docs/design/formats/linux-alpine-apk.md` |
| UPDATE | `0.1.0-alpha.74` | `docs/design/update.md` |

签名能力（SIGN 已收官）：rpm/AppImage 可选 OpenPGP/GPG、apk 可选 RSA、NSIS/MSI 托管 Authenticode、app/dmg codesign、pkg productsign——总览见 `docs/design/signing.md`。
各格式契约与能力状态详见对应文档；外部事项统一登记在 `docs/project/special-acceptance.md`。

## 3. 最近验证

当前基线：`main@cb76455`（`0.1.0-alpha.103`）——全项目重构链 PR #82–#88、测试重写链 #90–#97、收口修复链 #98–#104 已全部合入：`ToolRunner` 统一进程执行、`Hashes`/`Gzip` 收编、`PayloadSource` 三包枚举共享、`ToolCache` 泛化并入 AppImage（默认缓存根统一为 `DotNetBundler/tools`，弃 `XDG_CACHE_HOME`）、`FileTree` 树复制收敛、文档同步与 Tauri/上游产品参照框架移除。
**测试体系重写链 PR #90–#97 已全部合入 `main`**（原 stacked 基：#91→main、#92→skeleton、#93→unit、#94→api、#95→integration、#96→package、#97→ci），链顶 `test-rewrite/docs` 分支头（verify 实证内容 `7909ffe`，其后仅 docs 提交）。
**verify 收口实证（判据经裁决收敛为"链顶同内容一次全绿"）**：run `38847618076`（2026-10-16，跑在临时探针分支 `dispatch-probe-test@44af979`——内容为 `7909ffe` + 逐字拷贝 `verify.yml` 的 `probe2.yml` shim，为免污染主 workflow 的 dispatch 通道，探针分支用后已删）**30 腿全绿**——tests 六宿主矩阵×6 + produce×6 + consume 九配对（自消×6 + WOW64 `win-arm←win-x64` + Rosetta `mac-arm←mac-intel` + universal x64 切片 `mac-intel←mac-arm`）+ manual-auto×6 + universal 产/消×3（该轮尚无跨 OS 归档交换腿；现 verify consume 矩阵已扩为 15 配对）。
更早证据轮 `38825129500`（同拓扑前身）30/30 绿、首轮收口 run `38833688512` 抓出 windows-11-arm runner 网络劣化（NuGet restore 连接挂死 10min×2 + artifact 上传步死寂 31min 后自愈）——据此 `RestoreWithRetry` 改 3min×4 短超时换枪（7909ffe）、fixture 统一 `NuGetAudit=false` 消可选网络往返。
verify 再早 run `38678818534`/`38687565528`/`38692708521`/`38697374256` 的宿主瞬态处置（where.exe 探测超时误判、ctor 内 Skip 误报、NuGet 挂死有界重试、AppImage 双腿各验、dmg EXIT trap 防滞留）均已收编；HelloBundlerApp 字面样品链经裁决不收编（fixture 链等价覆盖，留 Manual 验收面）。
收口期增补：`aot.yml` 更名 `produce-bootstrap.yml` 并扩为全 RID 产件流（linux/musl x86_64+aarch64、macos 双架构+fat universal 工件、win 三架构；五腿 dispatch `38754749689` 实证全绿）；musl 件按实证改 `-static-pie` 静态产（`StaticExecutable=true`+`LinkerFlavor=lld`，产物 glibc/musl 通吃；bfd 链接 static-pie 会崩故 lld 必需；产件走 alpine SDK 容器——glibc 宿主直产 musl 缺 musl target 的 GCC crt 探测路径，arm 镜像实证两次失败，x64 镜像碰巧可产但不可赌）；
shell 变量邻接守卫进 test.yml select 前置门（`$VAR`+非 ASCII 在部分 locale 下被拼进变量名炸 set-u——`consume-macos.sh` LEG-SKIP 首踩实证，lint 扫 .sh/shebang/workflow run 内联，bash 词法注释判定）；
verify consume 腿按"每台消费宿主吃所有能跑的产件"裁决改 (host×src) 配对矩阵：windows-arm64 吃 x64 件（WOW64，旁装 x64 运行时经 `DOTNET_ROOT_X64` 指给 apphost）、macos-26 吃 intel 产件（Rosetta）、macos-intel 吃 arm 产件内 x64 切片（`consume-macos.sh` 新增 `腿=子目录` 映射），linux 无原生异构机制保持对角线；Windows 腿标签钉显式 `windows-2025-vs2026`/`windows-11-vs2026-arm`。
本机 Windows x64 Release（PR #105 链上 `b270741` 实测）：Unit 1319 测（1285 过/34 宿主门控跳）、Api 84 测、Integration 244 测、Package 18 测，全零失败。
收口期修复的真缺陷：`produce.sh` `cat|head` SIGPIPE（92fe129）、NSIS 卸载注册表异步清理竞态（33f0472）、NSIS `/D` 正斜杠粘连+PKG relocation+artifact 丢执行位（b39d9af）、dmg 腿 EXIT trap 毒化（2190df7）、`hdiutil convert` 持续 EAGAIN 踢 diskimages-helper（7a8d2bd）。
verify run `38881497256`（main@`cb76455`，#103/#104 合入后）4 条 macOS consume 腿红：`app-empty`/`dmg-empty` 误报——根因为 `set -o pipefail` 域内 `find|grep -q` 的 SIGPIPE 竞态（grep 早退、find 收 141 连带管道判败，日志实证 `find: stdout: Broken pipe`），非产件真空；修复在 PR #105（`a1b5004` 外层 14 处改 `find|head -1` 形态，`87b2169` universal 脚本改 `-print -quit`）。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/project/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- 各格式外部事项等待对应环境输入，统一登记在 `docs/project/special-acceptance.md`。
- 仓外独立包消费 fixture（原 `tests/Bundler.LocalPackagesTests/Fixtures/Msi/`，随测试重写迁至 `tests/Fixtures/MsiPackage/`）的 `Fixture` 与 `Standalone/` 在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源（PR #41）；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。
- **测试重写收口期登记的功能侧待办**（分批推进中）：
  - ~~静态兼容修复~~——已完成（PR #98）：`PosixRename` 已删，POSIX 同卷判定改 `/proc/self/mountinfo` 挂载点最长前缀比对（`DriveInfo.GetDrives()` 回退），ENOSPC 预检在静态件恢复；
  - ~~`tools/macos-universal` fat 件与 `tools/windows-i686` 引导件入库~~——已完成（PR #99，工件取 produce-bootstrap run `38865619194`；`macos-universal` 命中原生 fat 件，`windows-i686` 引导件从落空变有件）；
  - ~~linux 引导件收敛 musl 静态单件~~——已完成（PR #100：glibc 目标经 musl 姊妹件解析，两枚 glibc 件删除、produce-bootstrap glibc 产腿同步下线；静态兼容修复 PR #98 已落地）；
  - ~~既有冻件可选经 `produce-bootstrap` 全量重产统一 provenance~~——升级为必须：旧件基线（run `38865619194`@`5c22c8c`）不含 PR #98 代码，`linux-musl-*` 静态件带 `dlopen("libc")` 实为破损件；已重产（run `38879370229`）全量换冻 8 件统一 provenance（PR #104）；#105 审计再改 `BootstrapPlan` 语义（回滚 marker、异型槽选腿、锁分流、nohup 重启、恢复向已提交容错），随包引导件最终换冻至 run `38903696939`@`58b1ba8` 基线（8 件，魔数/架构/执行位核验）。
- **spec §3 终验对账遗留**（审计结论：63 项零真缺口；弱覆盖中可自动化项已补断言，余下按性质归位）：
  - ~~测试侧待做：`MacAppGate` 未签名 `.app` 拒绝腿 + update e2e 非 zip 格式腿 + zip/tar.gz 跨宿主同件交换接线~~——已实现（PR #101：`MacAppGateTests` BundleID 拒换/quarantine 剥离恒自动化 + 自签尽力腿四件，TeamIdentifier 面探针驱动取不到即诚实 Skip 留 SA-P-07；`UpdateClientChainTests` 补 tar.gz 与 macOS 未签 .app e2e；PR #102：consume 三脚本归档格式级交换模式 + verify 矩阵 6 行轮转 + 产侧 SHA256SUMS LF 修复）；
  - 环境受限已登记 `special-acceptance.md`：s390x/ppc64le `UnixFileTypes` PAL 可达性（stat 已改走 `libSystem.Native`，ABI 偏移表不存在）、Windows zip 符号链接还原、提权目录换装（原已登记）；
  - 能力缺口（功能面）：`DebPackageReader` 不解 GNU 长名/PAX——真实第三方 deb 样本解析非当前目标，规格已对齐；


## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，功能面无进行中阶段。
**测试体系重写已收口**（规格 `docs/design/testing.md`；Unit/Api/Integration/Package 四层 + `tests/Manual` 验收脚本层全量实现，CI 拓扑为 test/verify/produce-bootstrap 三 workflow，verify 发布门已全绿；PR 链 #90–#97 与静态兼容 PR #98 均已合入 `main`）。
剩余工作：

- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`，见 §5）；
- UPDATE 系统包扩展（deb/rpm/alpineapk installer-replay 自更新）B0–B5 全链已实现：发射（`pkg` 身份字段、`CoveredFormats` 扩收、三后端旁车+引导件注入）、`pkg-apply` 动词 AOT/POSIX 双实现同协议、客户端 replay 编排（`SyspkgReplay`：预检三件套/留存/三级提权退化链/回滚重放）、debian/fedora/alpine 容器验收腿（经 `workflow_call` 收编 verify 发布门 `syspkg` job）——契约见 `docs/design/update.md` §2，图形提权入口链等剩余边界登记 `docs/project/special-acceptance.md`；
- 发布链资产实际推送（NuGet/Release 为另一步）；
- 零星已登记增强按 `docs/project/roadmap.md` §4.2 候选标准排队。

# DotNet.Bundler 项目上下文

> 最后更新：2026-10-19
> 当前工作基线：`main@576e6f7`（含 PR #142 `744cfb6` Core+Abstractions 收口 squash 合入与 PR #143 CI 诊断增强合入）；
> 此前 `main@c319b8c` 含 #133/#134 诊断瘦身回净、#135 dmg 断言收窄、#136 dmg 挂载后失败注入腿、#137 文档对账、#138 签名文档修正；
> `110a2fe` 基线全矩阵 verify `39133258129` 已 completed/success 全绿；
> 联合 verify run `39142103135`（head=`0c18cf9`）未全绿：WindowsARM MSI Package 腿 attempt1 两测试 `dotnet restore` 3min 超时后 job 被取消、attempt2 restore 超时判败，其余已运行的 tests 矩阵腿通过、下游未执行——根因未证实；
> verify run `39177022111`（head=`a137268`）Windows2025 x64 NSIS 重安装腿安装器 rc=0xC000013A、UbuntuARM runner 失联无工件——根因未证实；
> `diagnostics/ci-failures-20261019` 分支只做定向复现入口与安全诊断插桩（不改产品语义、不放宽断言、非产品修复），已提交并合入 main 为 PR #143（验证实现基线 `a95468d`，`576e6f7`，Local Review 第 1 轮 clean）；当前阶段为取证/诊断就绪、本轮复验通过、根因未确认；
> 基于 `a95468d`（alpha.105）的四条定向/复验 CI 均 completed/success（Windows2025 `NsisIntegrationTests` 类 28/28、WindowsARM `MsiPackageContractTests` 10/10、UbuntuARM Integration 全类完成、Windows2025 suite=all 原调度四层 0 失败）——本轮未重现旧故障，不能证明根因已修；该基线不覆盖主线 PR #142/alpha.106 新代码；
> 已停止额外盲跑，旧三类故障（MSI restore 超时/NSIS 0xC000013A/UbuntuARM 失联）保留未决；完整 verify 仍无全矩阵绿证。
> UPDATE 系统包扩展 B0–B5 全链完成并入 `main`（PR #107 设计、#108 发射+`pkg` 身份、#109 引导件 `pkg-apply` 双实现、#106/#110 macOS quarantine 能力探测、#111 客户端 `SyspkgReplay`、#112 syspkg-verify 三容器验收+DropProbe 修复、#113/#115 冻件重冻、#114 收尾：syspkg-verify 经 `workflow_call` 收编 verify 发布门、usage 文档、`pkg` 字段 v1 兼容断言）。
> 测试确定性修复 #116（奇偶腿宿主 `DEBIAN_FRONTEND` env 泄漏+uidedge rec 竞态）与 #117（NSIS 卸载分离删除断言改 WaitFor）已并入。
> 发布门实证：verify 于 `9d51ba3` 基线**三连全绿**（run `39062962978`、`39066991823`、`39070871573`——六宿主测试矩阵 + syspkg 三容器腿 + produce/consume/manual/universal 全链）。
> 引导件冻件口径：现嵌 8 件为 produce-bootstrap run `39275440680`@`4330237` 产出（含 BundledSha256 musl libssl 修复），**已过期且不可沿用**——Unix spawn 面后改为 `NativeSpawnUnix`（libc posix_spawn）；且 musl static-pie 件的 `DllImport("libc")` 运行期不可解析（无 `.dynsym`），须带 csproj `DirectPInvoke` 直绑清单的新源码重冻，收口前 produce-bootstrap 按终头再产。
> 外部工具挂死加固：PR #130 给 `ToolRunner`/`ToolCache` 全链有界等待+spawn 追踪（`BUNDLER_TOOL_TIMEOUT_SECONDS`/`BUNDLER_TOOL_TRACE`），挂死收敛为带现场的有界失败；macOS 偶发 `hdiutil` 簿记滞后断言面由 PR #131 改有界轮询；PR #132 加 CI 进程快照采样（随后过滤瘦身至测试/工具面白名单）——加固后 intel 腿 suite=all 八连压测零复现，OS 层凶手未捕获；verify `39225055542`@`09d6a01` macos-intel 腿复现真挂死——取证（`ci-logs-macos-26-intel`）实证：`hdiutil`/`makensis` 已退而成 Z 态僵尸 ~3h 未被 reap、宿主 S 态 0%、另有一 100% CPU 同 argv `dotnet` 子件（fork 后未 exec 的空转件，340KB RSS）——对照 dotnet/runtime 源码钉死机制为 **`Process.Start` 内部 Unix spawn/reap 全局互斥死锁**：`ForkAndExecProcess` 持 `s_processStartLock` 读锁同步等 fork 子件回报 exec，子件卡死则读锁不释放→SIGCHLD reaper（`CheckChildren`）排写锁饿死→僵尸不 reap、新 spawn 排在写者后全线阻塞（dotnet/runtime #135294/PR #135323 同族，修进 .NET 12、10.x 无修复）；根治决案=**弃 Unix `System.Diagnostics.Process`**：`Bundler.Core/NativeSpawnUnix` 走 **libc `posix_spawn`**（dup2/addchdir 经 file_actions 全原生执行、SETPGROUP 自立组→kill(-pid) 树杀、spawn 调用放专线程 60s 有界弃件+Linux `/proc/self/task/<tid>/children` 精确扑孤——fork 子件跑托管 IL 的仓内 shim 路线经 WSL 实证否决：子件继承半死运行时态 SIGSEGV，shim 必须全原生）；上层统一 `ToolProcess`/`ToolProcessStartInfo`（Unix→NativeSpawnUnix、Windows 包装 Process——CreateProcess 无该失效类）、src 全部 spawn/等待点与测试侧 `TestProc`/`ProcessRunner` 收编同构件、`ProcessWait`/`TestProcessExit` 退役删除；残余如实登记：posix_spawn 内部仍 fork+shim，最深层机况下单次调用可堵——无共享锁不传染，60s 弃件后进程存活，比"有界失败但锁死面仍在"严格进一阶。
> 图形提权入口链等剩余边界仍挂 `docs/project/special-acceptance.md`。
> 当前包版本：`0.1.0-alpha.109`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：**全功能面体检收口进行中**——七域并行审查（Bugs/Security/Flags/Notes 四级全清零）产出约 95 项 findings，按用户授权分 6 个连贯 PR 处置：PR-1 Core+Abstractions 已合入（#142，4 轮 Local Review 清零）、PR-2 入口层收口完成（#144 待统一合并，2 轮 Local Review 清零）、PR-3 Linux 后端收口完成（#145 待统一合并，5 轮 Local Review 清零）、PR-4 Windows 后端收口完成（#146 待统一合并，6 轮 Local Review 清零）、PR-5 macOS 后端收口完成（#147 待统一合并，2 轮 Local Review 清零）、PR-6 UPDATE 收口完成（#148 待统一合并，Local Review 第 21 轮 5B/3F/6N 处置中——含 musl static-pie `DirectPInvoke` 直绑修复，引导件冻件须按终头重冻）——剩全矩阵 verify 统一 dispatch 连绿验收；**公开 API 破改登记**：`Bundler.Updater` 包面 `UpdateClient.Apply`/`Rollback` 返回类型由 `System.Diagnostics.Process` 换 `ToolProcess`（快开发期授权口径——不背旧版兼容债）；
> PR-5 macOS 当前事实：签名横切收敛为 `Bundler.MacApp/MacSigningSupport`（凭据解析/临时钥匙串/codesign·pkgbuild·productsign 参数/公证提交-等待-订钉单源）；临时钥匙串改 `codesign --keychain` 直给、不再变异用户搜索列表；`productsign` 补 `--timestamp`、pkg 公证 `--wait` 对齐 1h 超时；DMG detach 统一为 graceful→`-force` 递进+`hdiutil info` 复核单序列（成败两路共用）；`resize -limits` 字段序双序自校正；pkg `Destination` 撞名簿+`.app` 注入拒绝；`.rtfd` 移出声明；`xattr -crs` 提到 macOS 无条件段；Apple ID 口令进 argv 的运行期警告+文档推荐 keychain-profile/API key
> PR-6 UPDATE 当前事实：argv 拼写收敛单源 `Core/Update/UpdateArgv.cs`（三工程同源链接）；feed 取信=`.sig` 先验后解析+`artifact.file` 单段词法收口+`.part` Range 核对+全异常归 `UpdateException`（取消穿透）；`pkg-apply` 增 `--sha256`——root 侧把包暂存进私有 `bundler-pkg.*` 目录并对暂存副本复核哈希（留存包验签→root 执行 TOCTOU 收口），安装腿有界（默认 30min、`BUNDLER_UPDATER_PKG_TIMEOUT` 覆盖、apt-get `DPkg::Lock::Timeout=120`）；`BUNDLER_UPDATER_CMD_*` 覆盖须过存在+普通文件+执行位复核；`ProcessAlive`/`MacAppGate` fail-closed（签名态判不出一律拒）；AOT/POSIX parity 与 syspkg-verify 全链路已接线 `--sha256`
> 全部 11 个格式、CLI 与 UPDATE 均已冻结（冻结指功能面不扩展，质量收口仍在进行）；目标模型已定型——`TargetOperatingSystem{Windows,MacOS,Linux}` + `LibC` 旁维度 + `{os}[-{libc}]-{arch}` canonical 词，契约与全产物名目录见 `docs/design/targets.md`
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

当前基线：`main@744cfb6`（`0.1.0-alpha.106`，PR #142 Core+Abstractions 收口 squash 合入）。
PR-1（Core+Abstractions 收口）本机验证（Windows x64，.NET SDK 10.0.302）：Release build rc 0（0 警告 0 错误）；Unit 全量 1434 总/0 失败/37 跳过；Api 全量 84 总/0 失败/14 跳过；Integration `ToolRunnerIntegrationTests` 3/3。
PR-1 已落改动面：`ToolRunner.RunSync` 成功腿有界排空（对齐 TryRunAsync，消最后一条无界等待）；`UpdatePayloadStaging` 改走 `FileTree.CopyTree`+自复制防护+保留名（`bundler-update.json`/引导件）撞名显式拒绝；
`UpdateBootstrapper` 注入改 `InjectOrThrow` 直抛、target 词显式校验、提取根属主/0700/24h 清扫（含 squatting 降级根）；`UpdateKeyMaterial` P-256 坐标+在场私钥 d 校验、POSIX 0600；`.desktop` 全落文值 CR/LF 净化；
Validator 集中剥 null 元素+`MainExecutable` 空白拒绝；`BundlePipeline` producedPaths 归一化+缺失/碰撞分类异常+`.bundler-work` stale 清扫；
`ArtifactNaming` APK `-r<release>` 生态词修正+planner 默认 release 单源；`ToolCache` 清单定长读+段名拒 `*`/`?`/`:`（glob/ADS）；
新增 `IO/PathText` 统一宿主路径比较口径（Windows/macOS 不敏感、Linux 敏感，与 validator 同约定），收编 staging/pipeline/PayloadSource/planner/ToolCache/ZipToolCache 六处；
`BundleConfigurationLoader`（net10-only JSON）由 Core 挪至 `tests/Shared`（Core 留 netstandard-safe `BundleConfigurationPaths`）。
此前非文档变更包括 PR #139/#140 squash 合入：`a3ea637`/#139 UPDATE 提权停链修复（已启动提权腿即终局、取消/拒绝/鉴权失败一律停不重放、问密码取消 126 不触 sudo）；
`09c9646`/#140 DMG 挂载可观测增强（`hdiutil attach` 成功后落挂载事实 Information 日志（挂载点+镜像路径），attach 失败不落）。
两 PR 合并前均过本地评审（Local Review clean）；
合并后 CI 连续失败取证就绪（PR #143 已合入 `576e6f7`）、根因未证实（见下条），不声称已过全矩阵。
全量重构、测试重写、收口修复与 UPDATE 系统包扩展链（PR #82–#136）均已合入 `main`；逐轮验证过程与 run 记录以 git 提交和 PR 留痕为准，本文不留分轮过程，仅保留当前事实与仍有意义的结论。

- **verify 发布门**：`9d51ba3` 基线三连全绿（`39062962978`/`39066991823`/`39070871573`——六宿主测试矩阵 + syspkg 三容器 + produce/consume/manual/universal 全链）；
  `d51f1f6` 基线 verify `39098097192`/`39107053316` 全绿；
  `6141ab5`（#133 合入）verify `39118079751` 全绿；
  `110a2fe` 基线（#137 文档对账）全矩阵 verify `39133258129` 已 completed/success 全绿（六宿主 tests、produce/consume、manual/auto、universal、syspkg 三容器全 success）——该绿只覆盖 `110a2fe` 基线；`39142103135`/`39177022111` 均未全绿，当前无全矩阵绿证。
- **macOS intel 挂死面**：两轮三测试锁步挂死定性 runner 实例级间歇失响应，OS 层凶手未捕获；我方"全链无超时兜底"放大器已修——#130 有界等待/杀树/有界排空/spawn 追踪（`BUNDLER_TOOL_TIMEOUT_SECONDS`/`BUNDLER_TOOL_TRACE`）+ `ToolCache` 锁上限，#132–#134 进程快照采样瘦身至白名单并经 `ci-logs-*` 工件 `always()` 上传（不进 job 日志）；suite=all 八连压测 + api 十连 + verify 多轮累计零复现，插桩留作复发取证面。
- **dmg 失败清理断言面**：`hdiutil detach` 簿记滞后一次性断言误报由 #131 改 60s 有界轮询；`diskimages-helper` 邻座残影污染全局断言由 #135 收窄至失败域（retest `39124695555` macos 双腿绿）；#136 补挂载后失败真实注入腿（`BundlerMacDmgBackgroundFile` 缺件→attach 后 `ApplyBrandingAsync` 抛→catch 强制 detach）。
- **引导件冻件口径**：现嵌 8 件为 produce-bootstrap run `39275440680`@`4330237` 产出（`158d84a` 入库，SHA-256 账留 PR #148 评论 `6270901606`）；**已过期且不可沿用**——Unix spawn 面后改 `NativeSpawnUnix`（libc posix_spawn），且 musl static-pie 件 `DllImport("libc")` 运行期不可解析（无 `.dynsym`），须带 `DirectPInvoke` 直绑清单的终头重冻；musl 静态单件经 alpine SDK 容器产、双宿主通吃（`docs/design/update.md`）。
- 本机 Windows x64 Release 基准（Windows 10.0.26200 win-x64，.NET SDK 10.0.302 / MSBuild 18.6.11，`main@110a2fe` 接管时实测）：`dotnet build Bundler.slnx -c Release --no-restore` rc 0（0 警告 0 错误）；
  `dotnet test tests/Unit/Bundler.UnitTests.csproj -c Release --no-build --no-restore` rc 0（总 1409 / 通过 1372 / 跳过 37 / 失败 0）；
  `dotnet test tests/Api/Bundler.ApiTests.csproj -c Release --no-build --no-restore` rc 0（总 84 / 通过 70 / 跳过 14 / 失败 0）；
  本轮未执行 Integration/Package。
- **PR #139 窄验证**（`0.1.0-alpha.104`，本机 Windows + 既有 WSL Ubuntu（dotnet 10.0.112）直跑测试程序集）：
  Release build `--no-restore` rc 0（0 警告 0 错误）；
  Unit `*SyspkgReplayTests*` 36/36；
  Windows `SyspkgReplayLaunchIntegrationTests` 11/11、`UpdateClientChainTests` 全 skip（Linux 门禁）；
  WSL `SyspkgReplayLaunchIntegrationTests` 11/11、`UpdateClientChainTests` 20 通过 + 1 macOS 腿 skip；
  日志留临时目录 `bundler-verify-1792329808/elevation-stop/rework/`。
- **PR #140 窄验证**（`0.1.0-alpha.105`，本机 Windows x64 Release）：
  Release build `--no-restore` rc 0（0 警告 0 错误）；
  Unit `--filter-class '*MacDmgTests*'` 22 总/21 通过/1 跳过（新 Theory `MountedFactLoggedOnlyAfterSuccessfulAttach` 两分支实跑：attach 成功恰一条挂载事实消息+时序断言、attach 失败零消息零 detach）；
  `--filter-class '*MacDmgIntegrationTests*'` 12 总/1 通过/11 跳过（macOS 门禁，真实挂载腿由 macOS CI 执行）；
  最终基础门：Unit 全量 1409 总/1372 通过/37 跳过/0 失败、Api 84/70/14/0、`PackageContentContractTests` 8/8；
  日志留临时目录 `bundler-verify-1792329808/dmg-observability/`。
- **CI 连续失败三面事实**（根因未证实，证据目录 `bundler-ci-failures-65e61f0a8a4744318d37534977a69b40`）：
  verify `39142103135`@`0c18cf9` WindowsARM MSI Package 腿——attempt1 `FeatureSetScopePathAndCustomDirectory`/`PublicSampleMsiTableContract` 在 `dotnet restore` 3min 超时后 job 被取消；attempt2 `PerLanguageMsisInstallCoexistAndUninstallIndependently` 同样 restore 超时判败（该 attempt 为 Package 18 总 1 失败）；其余已运行的 tests 矩阵腿通过，下游 produce/consume/syspkg/manual 未执行；
  verify `39177022111`@`a137268` Windows2025 x64 `NsisIntegrationTests.ReinstallClosesOwnedProcessOnly` 安装器 rc=`-1073741510`=0xC000013A（退出码特征，原因待判）；UbuntuARM runner 失联、job logs 404 无工件；
  最新 WindowsARM 与 macOS 双腿均成功；
  `diagnostics/ci-failures-20261019` 分支只加定向复现入口（`test_class` 精确过滤）与安全诊断插桩（`BUNDLER_RESTORE_TRACE`/`BUNDLER_NSIS_DIAGNOSTICS`/快照资源字段），不放宽断言、不改产品语义，已提交并合入 PR #143（验证实现基线 `a95468d`，`576e6f7`，Local Review 第 1 轮 clean）。
- **PR #143 定向 CI 取证**（验证实现基线 `a95468d`，工件与原始日志留 `bundler-ci-failures-65e61f0a8a4744318d37534977a69b40`）：
  run `39183902219` Windows2025 `NsisIntegrationTests` 类 28/28 completed/success（含 `ReinstallClosesOwnedProcessOnly`；art `12157855300`：`nsis-shutdown.log` 记 owned 退出 0xC000013A、installer rc0、external 存活——本轮行为符合预期，旧 installer 中断原因未证实）；
  run `39183904434` WindowsARM `MsiPackageContractTests` 10/10 completed/success（art `12157349776`：`restore-progress.log` 20 BEGIN/20 SPAWN/20 EXIT 全 rc0、均 attempt1、无 TIMEOUT/RETRY）；
  run `39183906714` UbuntuARM Integration 全类运行完成 success（art `12158120423`）——本轮未失联，不能宣称失联已根治；
  run `39185300817` Windows2025 suite=all 原调度 completed/success（art `12158982147`：Unit 1409 总/37 跳/0 败、Api 84 总/14 跳/0 败、Integration 所有类完成（Nsis class END rc0）、Package 18/0 跳/0 败；`nsis-shutdown.log` installer rc0/owned 退 0xC000013A/external 存活，`restore-progress.log` 无 TIMEOUT/RETRY/非零 EXIT）——本轮未复现，不证明根因已修；
  四条均只覆盖各自平台/范围，不冒充完整六宿主 verify；旧失败根因未重现，诊断插桩非产品修复。
- **诊断分支窄验证**（`diagnostics/ci-failures-20261019`@`a137268`，本机 Windows x64 + WSL Ubuntu dotnet 10.0.112）：
  Release build `--no-restore` rc 0（0 警告 0 错误，restore 离线命中全局缓存）；
  Windows `CiTestDriverIntegrationTests` 11/11、`ToolingDiagnosticsIntegrationTests` 5/5；
  WSL `CiTestDriverIntegrationTests` 11/11、`ToolingDiagnosticsIntegrationTests` 5/5（经 `dotnet <dll> -class` 直接跑 Windows 产物程序集）；
  未跑真 NSIS/MSI 安装腿、未启本地安装同意闸；
  日志留临时目录 `bundler-ci-failures-verify/`。
- **同步 `main@744cfb6` 后窄回归**（合并进行中、alpha.106，本机 Windows x64 + WSL Ubuntu dotnet 10.0.112）：
  Release build `--no-restore` rc 0（0 警告 0 错误，restore 离线命中全局缓存）；
  Windows `CiTestDriverIntegrationTests` 11/11、`ToolingDiagnosticsIntegrationTests` 5/5；
  WSL `CiTestDriverIntegrationTests` 11/11、`ToolingDiagnosticsIntegrationTests` 5/5（经 `dotnet <dll> -class` 直接跑 Windows 产物程序集）；
  四条旧 CI 复验仍仅覆盖 `a95468d`（alpha.105），未重跑全矩阵；
  日志留临时目录 `bundler-ci-failures-verify/merge-main-744cfb6/`。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/project/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- 各格式外部事项等待对应环境输入，统一登记在 `docs/project/special-acceptance.md`。
- 仓外独立包消费 fixture（`tests/Fixtures/MsiPackage/`）的 `Fixture` 与 `Standalone/` 在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。
- 环境受限能力已登记 `docs/project/special-acceptance.md`：s390x/ppc64le `UnixFileTypes` PAL 可达性、Windows zip 符号链接还原、提权目录换装等。
- 能力边界（有意不实现）：`DebPackageReader` 不解 GNU 长名/PAX——真实第三方 deb 样本解析非当前目标，规格已对齐。


## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`。
**当前主线：全功能面体检收口（用户已授权"开始"）**——七域只读审查产出约 95 项 findings（Bugs ~22 / Security ~12 / Flags ~18 / Notes ~30 / 改进 ~25），按因果链分 6 个连贯 PR，各走 Local Review 闭环（`devin-reviewer` 子会话审 diff 自发评论、四类清零才合）：

1. **PR-1 Core+Abstractions**（`rework/core-abstractions`，**已合入 #142**，4 轮 Local Review 清零）：有界等待/FileTree 复用/validator 剥 null/桌面净化/命名与注入收口/秘密面/PathText 统一路径比较。
2. **PR-2 入口层**（`rework/entry-layer`，**完成待统一合并 #144**，2 轮清零）：CLI 混合 target 格式串台、POSIX `\` 路径统一 `/`、CLI ParseValue 类型感知+JSON→rc2、MSBuild 异常聚合、推断链下沉 Core、缺省格式表单源、签名秘密改 `*EnvironmentVariable` 间址、旋钮覆盖机械化测试。
3. **PR-3 Linux**（`rework/linux-backends`，**完成待统一合并 #145**，5 轮清零）：binLink 校验四后端收敛（`LinuxLinkName`）、PayloadSource 链接策略（in-tree 保留/外指拒绝/悬空拒绝/`link→.` 归一）、`PayloadPathBook` 共享簿记、Archive 父前缀拒绝+暂存件移出归档源、RPM 2GiB 长标签+Int64s、GNUPGHOME 出树进工作目录、四门面 `Validate` 接校验缝、apk 签名成员名对齐 abuild 且签名成员推导进校验缝、i686-AppImage 收窄为拒绝、AppImage 保留名前缀收口+异常口径分层（旋钮腿 ArgumentException/载荷腿 IOp）。
4. **PR-4 Windows**（`rework/windows-backends`，**完成待统一合并 #146**，6 轮清零）：`Uninstall.exe` 归属三层——un.onInit 全新卸载门（标记件→插件 `CheckUninstallDirectoryOwnership` 注册 InstallRoot 规范化比对兜底）+ `Begin` 绑定 + 恢复腿 `GetState`/`Recover` 经 `ValidateActiveTransaction` 绑注册 InstallRoot（finalizing 豁免），复制卸载器/伪造 journal 均 fail-closed；外部签名器同 `ToolRunner` 口径有界等待（独立包不引 Core，自带同语义实现+`Timeout` 显式旋钮 > env > 15min）；PFX `EphemeralKeySet`（netstandard2.0 引用面缺具名常量，显式 32，宿主需 FX≥4.7.2/Core）；`RmGetList` 先于 `RmShutdown`；LegacyMsiAutoDetect 收紧（`WindowsInstaller=1`+`MsiGetProductInfo` 复核名/发行者）；WiX 指纹缺失字面 Source 计 MISSING 哨兵、预处理引用按文档化命名空间收窄；21 语言恢复文案本地化；导出契约 37（字面 27+展开 10）；插件重建 SHA-256 `115BFC31…09A11`。本机验证：Build 0/0、Unit 1534/0/39、Api 84/0/14、Integration NSIS 真装腿 29/29+归属腿 5/0/1。
5. **PR-5 macOS**：`MacSigningSupport` 共享化（凭据/keychain/参数生成单源）、codesign `--keychain` 替代搜索列表变异、productsign `--timestamp`、detach 统一、撞名簿、`.rtfd` 移除声明、`resize -limits` 字段序（macOS 实机复核登记）。
6. **PR-6 UPDATE**：feed fetch/`TryRead`/argv `JoinArgv`/feed File 词法/pkg-apply 有界/ProcessAlive+MacAppGate fail-closed、**retained 包 root 侧重验签**（动 `Bundler.Updater.Bootstrap` 源码→必须 dispatch `produce-bootstrap` 重冻 8 件入库评审 + `syspkg-verify` 复验）。

收口后 dispatch 一次 verify 全矩阵（`39142103135` 已作废，覆盖 alpha.106+ 的 CI 证据）。
其余既有剩余工作：

- **CI 连续失败根治**：verify `39142103135`@`0c18cf9`（MSI Package restore 超时后取消/重跑判败）与 `39177022111`@`a137268`（NSIS rc=0xC000013A、UbuntuARM 失联）根因未证实、旧失败未重现；
  PR #143（`diagnostics/ci-failures-20261019`，验证实现基线 `a95468d`）四条定向/复验 run 均过、本轮未复现旧故障；已停止额外盲跑，旧三类故障保留未决；
  该 PR 已合入 `576e6f7`；四条取证基线为 alpha.105，不覆盖 PR #142/alpha.106；完整 verify 仍无全矩阵绿证；
- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`，见 §5）；
- UPDATE 系统包扩展（deb/rpm/alpineapk installer-replay 自更新）B0–B5 全链已实现：发射（`pkg` 身份字段、`CoveredFormats` 扩收、三后端旁车+引导件注入）、`pkg-apply` 动词 AOT/POSIX 双实现同协议、客户端 replay 编排（`SyspkgReplay`：预检三件套/留存/三级提权退化链/回滚重放）、debian/fedora/alpine 容器验收腿（经 `workflow_call` 收编 verify 发布门 `syspkg` job）——契约见 `docs/design/update.md` §2，图形提权真实桌面观感等剩余边界登记 `docs/project/special-acceptance.md`；
- 发布链资产实际推送（NuGet/Release 为另一步，仍需用户明确许可）；
- `docs/project/roadmap.md` §4.2 候选增强队列当前为空（DMG 挂载可观测已收口），新增强按候选标准登记。

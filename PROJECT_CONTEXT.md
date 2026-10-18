# DotNet.Bundler 项目上下文

> 最后更新：2026-10-18
> 当前分支：`fix/dmg-mounted-observability`@`0c18cf9`（叠加分支：父链 `fix/updater-elevation-stop`@`841484f` 已推送为 PR #139、未合并；
> 本分支已推送为 PR #140、base=PR #139、未合并；
> 基线 `main@c319b8c`——含 #133/#134 诊断瘦身回净、#135 dmg 断言收窄、#136 dmg 挂载后失败注入腿、#137 文档对账、#138 签名文档修正；
> `110a2fe` 基线全矩阵 verify `39133258129` 已 completed/success 全绿——不覆盖 alpha.104/105 新代码；
> 两阶段联合 verify run `39142103135`（head=`0c18cf9`）已触发、结果待确认）
> UPDATE 系统包扩展 B0–B5 全链完成并入 `main`（PR #107 设计、#108 发射+`pkg` 身份、#109 引导件 `pkg-apply` 双实现、#106/#110 macOS quarantine 能力探测、#111 客户端 `SyspkgReplay`、#112 syspkg-verify 三容器验收+DropProbe 修复、#113/#115 冻件重冻、#114 收尾：syspkg-verify 经 `workflow_call` 收编 verify 发布门、usage 文档、`pkg` 字段 v1 兼容断言）。
> 测试确定性修复 #116（奇偶腿宿主 `DEBIAN_FRONTEND` env 泄漏+uidedge rec 竞态）与 #117（NSIS 卸载分离删除断言改 WaitFor）已并入。
> 发布门实证：verify 于 `9d51ba3` 基线**三连全绿**（run `39062962978`、`39066991823`、`39070871573`——六宿主测试矩阵 + syspkg 三容器腿 + produce/consume/manual/universal 全链）。
> 引导件冻件口径：现嵌 8 件为 produce-bootstrap run `39030858362`@`e0174dc` 产出（PR #129 入库，含 #128 PAL 化——env 件校验与 `UnixFileTypes` 改走 `libSystem.Native`、gate 竞态修复），SHA-256 与字节数留账于该 squash 正文。
> 外部工具挂死加固：PR #130 给 `ToolRunner`/`ToolCache` 全链有界等待+spawn 追踪（`BUNDLER_TOOL_TIMEOUT_SECONDS`/`BUNDLER_TOOL_TRACE`），挂死收敛为带现场的有界失败；macOS 偶发 `hdiutil` 簿记滞后断言面由 PR #131 改有界轮询；PR #132 加 CI 进程快照采样（随后过滤瘦身至测试/工具面白名单）——加固后 intel 腿 suite=all 八连压测零复现，OS 层凶手未捕获。
> 图形提权入口链等剩余边界仍挂 `docs/project/special-acceptance.md`。
> 当前包版本：`0.1.0-alpha.105`（根 `Directory.Build.props` 的 `BundlerPackageVersion`；
> 本分支 DMG 挂载可观测增强进行中，未合并、联合 verify `39142103135` 已触发待结论）
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

当前基线：`main@c319b8c`（含 #137 文档对账、#138 签名文档修正；`0.1.0-alpha.103`）。
最近非文档变更为 `2453c25`/#136（挂载后失败集成测试）。
`fix/updater-elevation-stop` 分支在其上实施 UPDATE 提权停链修复（`0.1.0-alpha.104`）：已启动提权腿即终局、取消/拒绝/鉴权失败一律停不重放、问密码取消 126 不触 sudo——已推送为 PR #139（`841484f`），未合并、CI 证据由联合 run `39142103135` 覆盖；
`fix/dmg-mounted-observability` 叠加其上实施 DMG 挂载可观测增强（`0.1.0-alpha.105`）：`hdiutil attach` 成功后落挂载事实 Information 日志（挂载点+镜像路径），attach 失败不落——已推送为 PR #140（`0c18cf9`，base=PR #139），未合并；
两 PR 均已过本地首轮评审（Local Review clean）；
联合 verify run `39142103135`（head=`0c18cf9`）已触发、结果待确认——该 run 覆盖当前叠加分支运行时代码（含 #139 祖先），非 `841484f` 独立 CI 证据。
全量重构、测试重写、收口修复与 UPDATE 系统包扩展链（PR #82–#136）均已合入 `main`；逐轮验证过程与 run 记录以 git 提交和 PR 留痕为准，本文不留分轮过程，仅保留当前事实与仍有意义的结论。

- **verify 发布门**：`9d51ba3` 基线三连全绿（`39062962978`/`39066991823`/`39070871573`——六宿主测试矩阵 + syspkg 三容器 + produce/consume/manual/universal 全链）；
  `d51f1f6` 基线 verify `39098097192`/`39107053316` 全绿；
  `6141ab5`（#133 合入）verify `39118079751` 全绿；
  `110a2fe` 基线（#137 文档对账）全矩阵 verify `39133258129` 已 completed/success 全绿（六宿主 tests、produce/consume、manual/auto、universal、syspkg 三容器全 success）——该绿只覆盖 `110a2fe` 基线，alpha.104/105 分支新代码 CI 待跑。
- **macOS intel 挂死面**：两轮三测试锁步挂死定性 runner 实例级间歇失响应，OS 层凶手未捕获；我方"全链无超时兜底"放大器已修——#130 有界等待/杀树/有界排空/spawn 追踪（`BUNDLER_TOOL_TIMEOUT_SECONDS`/`BUNDLER_TOOL_TRACE`）+ `ToolCache` 锁上限，#132–#134 进程快照采样瘦身至白名单并经 `ci-logs-*` 工件 `always()` 上传（不进 job 日志）；suite=all 八连压测 + api 十连 + verify 多轮累计零复现，插桩留作复发取证面。
- **dmg 失败清理断言面**：`hdiutil detach` 簿记滞后一次性断言误报由 #131 改 60s 有界轮询；`diskimages-helper` 邻座残影污染全局断言由 #135 收窄至失败域（retest `39124695555` macos 双腿绿）；#136 补挂载后失败真实注入腿（`BundlerMacDmgBackgroundFile` 缺件→attach 后 `ApplyBrandingAsync` 抛→catch 强制 detach）。
- **引导件冻件口径**：现嵌 8 件为 produce-bootstrap run `39030858362`@`e0174dc` 产出（PR #129 入库），SHA-256 与字节数留账于该 squash 正文；musl 静态单件经 alpine SDK 容器产、双宿主通吃（`docs/design/update.md`）。
- 本机 Windows x64 Release 基准（Windows 10.0.26200 win-x64，.NET SDK 10.0.302 / MSBuild 18.6.11，`main@110a2fe` 接管时实测）：`dotnet build Bundler.slnx -c Release --no-restore` rc 0（0 警告 0 错误）；
  `dotnet test tests/Unit/Bundler.UnitTests.csproj -c Release --no-build --no-restore` rc 0（总 1409 / 通过 1372 / 跳过 37 / 失败 0）；
  `dotnet test tests/Api/Bundler.ApiTests.csproj -c Release --no-build --no-restore` rc 0（总 84 / 通过 70 / 跳过 14 / 失败 0）；
  本轮未执行 Integration/Package。
- **`fix/updater-elevation-stop` 分支窄验证**（`0.1.0-alpha.104`，本机 Windows + 既有 WSL Ubuntu（dotnet 10.0.112）直跑测试程序集）：
  Release build `--no-restore` rc 0（0 警告 0 错误）；
  Unit `*SyspkgReplayTests*` 36/36；
  Windows `SyspkgReplayLaunchIntegrationTests` 11/11；
  Windows `UpdateClientChainTests` 全 skip（Linux 门禁）；
  WSL `SyspkgReplayLaunchIntegrationTests` 11/11、`UpdateClientChainTests` 20 通过 + 1 macOS 腿 skip；
  日志留临时目录 `bundler-verify-1792329808/elevation-stop/rework/`；
  分支全矩阵 CI 待在 PR 分支执行（见 §6），不以合入 main 为前提。
- **`fix/dmg-mounted-observability` 分支窄验证**（`0.1.0-alpha.105`，叠加 `fix/updater-elevation-stop`@`841484f`/PR #139 未合并父链，本机 Windows x64 Release）：
  Release build `--no-restore` rc 0（0 警告 0 错误）；
  Unit `--filter-class '*MacDmgTests*'` 22 总/21 通过/1 跳过（新 Theory `MountedFactLoggedOnlyAfterSuccessfulAttach` 两分支实跑：attach 成功恰一条挂载事实消息+时序断言、attach 失败零消息零 detach）；
  `--filter-class '*MacDmgIntegrationTests*'` 12 总/1 通过/11 跳过（macOS 门禁，真实挂载腿由 macOS CI 执行）；
  最终基础门：Unit 全量 1409 总/1372 通过/37 跳过/0 失败、Api 84/70/14/0、`PackageContentContractTests` 8/8；
  日志留临时目录 `bundler-verify-1792329808/dmg-observability/`；
  `39133258129` 绿仅覆盖 `110a2fe` 基线，不含本分支新代码；
  本分支运行时代码 CI 证据待联合 verify `39142103135` 结论。

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

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，功能面无进行中阶段。
**测试体系重写已收口**（规格 `docs/design/testing.md`；Unit/Api/Integration/Package 四层 + `tests/Manual` 验收脚本层全量实现，CI 拓扑为 test/verify/produce-bootstrap/syspkg-verify 四 workflow，verify 发布门已全绿；PR 链 #90–#97 与静态兼容 PR #98 均已合入 `main`）。
剩余工作：

- **PR #139（`fix/updater-elevation-stop`@`841484f`，UPDATE 提权停链修复）与其叠加 PR #140（`fix/dmg-mounted-observability`@`0c18cf9`，DMG 挂载可观测增强，`0.1.0-alpha.105`）均未合并、均已过本地首轮评审（Local Review clean）**：
  联合 verify run `39142103135`（head=`0c18cf9`）已触发、结果待确认——覆盖当前叠加分支运行时代码（含 #139 祖先），非 `841484f` 独立 CI 证据；
  `110a2fe` 基线全矩阵 verify `39133258129` 已全绿（旧基线证据，不覆盖分支新代码）；
- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`，见 §5）；
- UPDATE 系统包扩展（deb/rpm/alpineapk installer-replay 自更新）B0–B5 全链已实现：发射（`pkg` 身份字段、`CoveredFormats` 扩收、三后端旁车+引导件注入）、`pkg-apply` 动词 AOT/POSIX 双实现同协议、客户端 replay 编排（`SyspkgReplay`：预检三件套/留存/三级提权退化链/回滚重放）、debian/fedora/alpine 容器验收腿（经 `workflow_call` 收编 verify 发布门 `syspkg` job）——契约见 `docs/design/update.md` §2，图形提权真实桌面观感等剩余边界登记 `docs/project/special-acceptance.md`；
- 发布链资产实际推送（NuGet/Release 为另一步）；
- 零星已登记增强按 `docs/project/roadmap.md` §4.2 候选标准排队。

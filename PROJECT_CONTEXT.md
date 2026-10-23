# DotNet.Bundler 项目上下文

> 最后更新：2026-10-23
> 当前基线：`main@fc3b6d2`——macOS 27 管道创建缺陷修复 PR #150 已 squash 合入，合并树与 CI 验证头 `0a76dfb` 逐文件零差异（同树 `18c55cb`）。
> 此前全功能面体检收口的 PR #142、#144–#149 均已 squash 合入；`9999e6e` 代码树与 verify 验证头 `dd64c9a` 逐文件零差异。
> 进行中：分支 `fix/mac-app-gate-evidence`（`.app` 签名门禁已签分支证据 + `sh` fail-closed，见 §5），待 CI 验证与用户授权合入 `main`。
> 当前包版本：`0.1.0-alpha.111`（该分支；`main` 为 `0.1.0-alpha.110`）。
> 全部 11 个格式、CLI 与 UPDATE 均已冻结（冻结指功能面不扩展，缺陷修复照走），全功能面体检收口已完成。
> 目标模型已定型——`TargetOperatingSystem{Windows,MacOS,Linux}` + `LibC` 旁维度 + `{os}[-{libc}]-{arch}` canonical 词，契约与全产物名目录见 `docs/design/targets.md`。
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 实施证据以 git 提交与 PR 记录为准，本文不留分轮过程。
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

UPDATE 系统包扩展（deb/rpm/alpineapk installer-replay 自更新）B0–B5 全链已实现并经 `syspkg-verify` 三容器腿收编进 verify 发布门，契约见 `docs/design/update.md` §2。

## 3. 最近验证

- **CI 发布门**：verify 终头 `dd64c9a` 五连全绿（`39653183541`/`39661978487`/`39670560817`/`39683074173`/`39690254249`，各 47/47 jobs success）。
  合入后 `main@94a1d3b`（代码同 `9999e6e`）verify 两连全绿（`39702694685`/`39775564388`，各 47/47 jobs success）。
  produce-bootstrap 终头五平台腿全绿（`39645817903` 等）。
  `verify` 六宿主矩阵的 macOS 宿主为 `macos-26` 与 `macos-26-intel`；macOS 27 由 `test.yml` 可选预览腿 `xcode-27` 覆盖（只经显式点名，不进 verify）。
- **本机 Windows**（收口 PR #149 头 `9e2d93e`，Windows x64，.NET SDK 10.0.302）：
  Release build rc 0（0 警告 0 错误）；
  Unit 1598 全绿；
  `ToolingDiagnosticsIntegrationTests` 6/6（含 75s 空闲看门狗与六 attempt 重试语义腿）。
- **本机 macOS 27**（2026-10-23，macOS 27.0.1 arm64，.NET SDK 10.0.402，Claude 桌面 App 拉起的 shell：C locale、新建文件带 `com.apple.provenance`）：
  修复前 `main@94a1d3b`：Unit 总 1629 / 失败 17（均为 `pipe/fcntl failed.`）；旧 `macos-aarch64` 冻件 `.app` apply rc=4。
  修复后（PR #150 头 `770454c`）：Release build rc 0（0 警告 0 错误）；
  Unit 总 1635 / 通过 1608 / 跳过 23 / 失败 4——4 个均为 `ArchiveTests`，系本机 provenance 环境所致（见 §5），在 `main` 基线同环境下 `Detects_ExtendedAttributes_OnPayload` 同样失败；
  Api 在 `LANG=en_US.UTF-8` 下总 84 / 失败 2（`ArchiveApiTests` 同属 provenance）；C locale 下另有 5 个 NSIS 腿因 `makensis` "Bad text encoding" 失败（见 §5）；
  Integration（`LANG=en_US.UTF-8`，未开本地安装同意闸）macOS/UPDATE 八类首跑总 135 / 失败 3：`.app` 端到端更新 rc=4（旧冻件，重冻后通过）、`EulaAndAdHocSignedVariant` `udifderez` EAGAIN(35)（单跑复跑通过，已登记 `diskimages-helper` 瞬态）、`FinderSlaAcceptanceMountsVolume` 90s 未见 Agree 按钮（本机会话无 GUI 辅助功能授权）；
  重冻后 `UpdateClientChainTests`/`UpdaterBootstrapParityTests`/`MacAppGateTests`/`UpdateIntegrationTests` 总 92 / 失败 0 / 跳过 28（含自签身份导入临时钥匙串后识别不到而 Skip 的 `MacAppGateTests` 腿）；
  新冻件 `macos-aarch64`/`macos-universal` `.app` apply rc=0 换包生效，`macos-x86_64` 本机无 Rosetta 未跑。
- **CI macOS 27**（`xcode-27` 预览镜像实测 macOS 27.0.1 26A434 arm64、导出 `pipe2`）：
  `test` run `39962038778`@`6f899d0` suite=unit：总 1635 / 失败 0 / 跳过 23。
  `test` run `39962825289`@`770454c` suite=all（含重冻引导件）：Unit 1635/0 失败/23 跳过、Api 84/0/35、Integration 324/0/168、Package 19/0/10。
- **verify（PR #150）**（run `39962835463`@`770454c`；其后 `0a76dfb` 只改本文）：
  attempt 1 失败——`windows-2025-vs2026` 测试腿 `NsisIntegrationTests.InterruptedInstallManifestMismatchAndTamperRejections` 300s 未见中断装置钩位标记（"did not reach its post-install hook"），其余测试腿通过、下游未执行；
  该腿此前于 verify `39368446045` 以 120s 窗口失败过一次并于 PR #148 放宽至 300s，本次 300s 仍未达——窗口内快照可见 `smartscreen.exe`，但快照白名单不含安装器进程，根因未证实；
  与本修复无关（Windows 不走 `NativeSpawnUnix`，NSIS 安装链不经 updater 引导件），首次失败现场存本机诊断目录；按失败留痕 `rerun --failed` 复跑。
  attempt 2 全绿 47/47（六宿主测试、produce/consume/manual/universal、`syspkg-verify` deb/rpm/apk 三容器——重冻 musl 引导件真装/更新/回滚通过）。
- **CI 故障归属结论**（仍有效）：
  MSI 类 restore 超时根因已证实并根治（签名包吊销检查走 HTTP/80，处置见 `docs/design/testing.md` §3.11），修复后 windows-arm 腿零 restore 超时；
  NSIS 安装器 rc=0xC000013A 与 UbuntuARM runner 失联归 runner 基建瞬断（未复现、无产品面证据）；
  macOS intel 挂死根因为 .NET Unix `Process.Start` spawn/reap 全局锁死锁（dotnet/runtime #135294 同族，10.x 无修复），已以 `NativeSpawnUnix`（libc `posix_spawn`）弃用 Unix `Process` 根治，原理见 `docs/design/architecture.md`。
  残余：`posix_spawn` 内部仍 fork+shim，最深层机况下单次调用可堵，但无共享锁不传染，60s 弃件后进程存活。
- **引导件冻件口径**：现嵌 8 件为 produce-bootstrap run `39962028724`@`6f899d0` 产出（五平台腿全绿），SHA-256 账留 PR #150 分支提交 `770454c` 消息。
  `NativeSpawnUnix`（含建管 `O_CLOEXEC`/`ENOSYS` 平台口径修复）+ musl static-pie `DirectPInvoke` 直绑 + `WorkingDirectory=""` PATH 基准修复 + `EnvironmentIsComplete` 完整环境块全链在冻件内。- **公开 API 破改登记**：`Bundler.Updater` 包面 `UpdateClient.Apply`/`Rollback` 返回类型由 `System.Diagnostics.Process` 换为 `ToolProcess`（快速开发期不背兼容债，见 `docs/design/update.md`）。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/project/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- **macOS 27 管道创建缺陷**（已修复，PR #150 合入 `fc3b6d2`；留此作根因记录，下轮对账可移除）：
  根因：`NativeSpawnUnix` 建管优先调 `pipe2(fds, O_CLOEXEC)`，`O_CLOEXEC` 写死 Linux 值 `0x80000`（Darwin 为 `0x1000000`），`ENOSYS` 也写死 Linux 值 38（Darwin 为 78）；
  macOS ≤26 无 `pipe2` 始终回落 `pipe+fcntl`，故 CI 一直通过；macOS 27 起 `pipe2` 可用（SDK 标 `macos(27.0)`），误传旗标得 `EINVAL`，被当真实失败抛 `pipe/fcntl failed.`。
  实测影响：Core 带重定向工具调用全失败；冻件引导件 `MacAppGate` 判不出签名态 fail-closed，`.app` 自更新 rc=4。
  修复：两常量分平台取值、`EINVAL` 同按缺席回落；新增 `NativeSpawnUnixTests`（反向变异验证：Darwin 旗标改回 Linux 值即 3 例失败）；重冻 8 件引导件；`test.yml` 增 `xcode-27` 可选预览腿。
  包未公开发布，无外部用户受影响。
- **macOS ≤26 ARM64 回落路径 `FD_CLOEXEC` 不落位**（既有已登记残余，本轮实测确认，未处置）：
  Apple ARM64 变参 ABI 下 `DllImport fcntl(int,int,int)` 第三参递不到，回落路径管端不带 `FD_CLOEXEC`；
  本进程经 `NativeSpawnUnix` 拉起的子件由 `POSIX_SPAWN_CLOEXEC_DEFAULT` 兜底，宿主进程其他代码（如嵌入 `Bundler.Updater` 的应用自行 spawn）拉起的子件可继承管端。
  macOS 27+ 走 `pipe2` 原子路径不受影响；可选处置为 Apple ARM64 专用 `fcntl` 签名（以 6 个占位参数填满 x2–x7 使第 9 参落栈），待用户裁决。
- **Archive 遇 `com.apple.provenance` 改走宿主工具**（本轮发现，未处置，涉产品语义待用户裁决）：
  macOS 对从下载来源 App（如 IDE、Claude 桌面 App）拉起的进程所建文件自动加 `com.apple.provenance`；
  `MacArchiveTools` 只要载荷有任何 xattr 即改走宿主 `ditto`/`tar` 保留 xattr，产物失去确定性、symlink 表示随之改变。
  本机 4 个 `ArchiveTests` 与 2 个 `ArchiveApiTests` 因此失败；CI runner 无该属性故不暴露。
  待裁决：是否把 `com.apple.provenance`（及同类系统管理属性）排除在 xattr 判定之外。
- **`.app` 签名门禁已签分支无自动化证据**（处置中：分支 `fix/mac-app-gate-evidence`）：
  更正：此前记为"仅 macOS 27 Skip、macos-26 仍覆盖"有误——`MacAppGateTests` 5 条自签身份腿自 #101 起在 macos-26/macos-26-intel/xcode-27 全部 Skip，从未实跑。
  根因：自签证书 `CSSMERR_TP_NOT_TRUSTED` 被 `find-identity -v` 排除；即便绕过，自签签出 `TeamIdentifier=not set`，门禁视同未签，已签分支（同队/跨队/篡改/降级）在自签下不可达。
  另：POSIX `sh` 门禁 `codesign` 不可执行时拿空 Team 按未签放行（fail-open），与 AOT fail-closed 不一致。
  处置：`MacAppGate` 增 `GateTool` 缝 + 共享决策表 12 例（AOT Unit / `sh` Integration）；`sh` 改 126/127 fail-closed；真证书腿改读 `BUNDLER_TEST_MAC_SIGNING_P12[_B]`；`update.md`/SA-P-07 措辞更正。
- **NSIS 中断装钩位等待超时复发**（既有偶发，未处置）：
  `NsisIntegrationTests.InterruptedInstallManifestMismatchAndTamperRejections` 在 windows-2025 腿两次未见钩位标记（`39368446045` 120s 窗口、`39962835463` attempt 1 的 300s 窗口），复跑均通过；
  300s 仍未达说明不只是负载慢，疑似安装器被拦截或挂起（窗口内见 `smartscreen.exe`，未证实）；
  取证缺口：进程快照白名单不含被测安装器进程。
  可选处置为把被测安装器纳入快照白名单或超时时落安装器进程树与 NSIS 日志，待用户裁决；不放宽断言。
- **NSIS 在 C locale 宿主打包失败**（本轮发现，未处置）：
  POSIX `makensis` 即使传 `-INPUTCHARSET UTF8`，在 C/POSIX locale 下遇非 ASCII 脚本仍报 "Bad text encoding"；
  本机 C locale 下 5 个 `NsisApiTests` 失败，`LANG=en_US.UTF-8` 下通过；Docker 容器默认 C locale，Linux/macOS 宿主交叉打 NSIS 的用户可能触发。
  可选处置为后端给 `makensis` 子进程注入 UTF-8 locale，待用户裁决。
- 各格式外部事项等待对应环境输入，统一登记在 `docs/project/special-acceptance.md`。
- 仓外独立包消费 fixture（`tests/Fixtures/MsiPackage/`）的 `Fixture` 与 `Standalone/` 在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。
- 环境受限能力已登记 `docs/project/special-acceptance.md`：s390x/ppc64le `UnixFileTypes` PAL 可达性、Windows zip 符号链接还原、提权目录换装等。
- 能力边界（有意不实现）：`DebPackageReader` 不解 GNU 长名/PAX——真实第三方 deb 样本解析非当前目标，规格已对齐。

## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，全功能面体检收口已完成。
macOS 27 管道创建缺陷修复已合入（PR #150），当前无进行中阶段。
候选工作（均需用户授权后启动）：

- §5 本轮新发现各项（ARM64 回落 `FD_CLOEXEC`、Archive provenance、macOS 27 自签身份腿 Skip、NSIS 中断装钩位超时、NSIS C locale）的处置裁决；
- 正式 `macos-27` runner 标签上线后把 macOS 27 并入 verify 六宿主矩阵；
- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`）；
- 发布链资产实际推送（NuGet/Release 为另一步，仍需用户明确许可）；
- `docs/project/roadmap.md` §4.2 候选增强队列当前为空，新增强按候选标准登记。

# DotNet.Bundler 项目上下文

> 最后更新：2026-10-23
> 当前基线：`main@94a1d3b`。
> 代码基线为 `9999e6e`，`94a1d3b` 只改本文。
> 全功能面体检收口的 PR #142、#144–#149 均已 squash 合入 `main`；`9999e6e` 代码树与 verify 验证头 `dd64c9a` 逐文件零差异。
> 当前包版本：`0.1.0-alpha.109`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）。
> 当前阶段：**无进行中阶段**。
> 全部 11 个格式、CLI 与 UPDATE 均已冻结（冻结指功能面不扩展，缺陷修复照走），全功能面体检收口已完成。
> 已知未修缺陷：macOS 27 宿主上 Unix 子进程管道创建失败（见 §5），修复待用户授权。
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
  CI 覆盖的 macOS 宿主只有 `macos-26` 与 `macos-26-intel`，未覆盖 macOS 27。
- **本机 Windows**（收口 PR #149 头 `9e2d93e`，Windows x64，.NET SDK 10.0.302）：
  Release build rc 0（0 警告 0 错误）；
  Unit 1598 全绿；
  `ToolingDiagnosticsIntegrationTests` 6/6（含 75s 空闲看门狗与六 attempt 重试语义腿）。
- **本机 macOS 27**（2026-10-23，`main@94a1d3b`，macOS 27.0.1 arm64，.NET SDK 10.0.402）：
  `dotnet build Bundler.slnx -c Release` rc 0（0 警告 0 错误）；
  `dotnet test tests/Unit/Bundler.UnitTests.csproj -c Release --no-build` rc 2（总 1629 / 通过 1589 / 跳过 23 / 失败 17）；
  17 个失败均为 `Win32Exception: pipe/fcntl failed.`，根因见 §5；
  未跑 Api/Integration/Package。
- **CI 故障归属结论**（仍有效）：
  MSI 类 restore 超时根因已证实并根治（签名包吊销检查走 HTTP/80，处置见 `docs/design/testing.md` §3.11），修复后 windows-arm 腿零 restore 超时；
  NSIS 安装器 rc=0xC000013A 与 UbuntuARM runner 失联归 runner 基建瞬断（未复现、无产品面证据）；
  macOS intel 挂死根因为 .NET Unix `Process.Start` spawn/reap 全局锁死锁（dotnet/runtime #135294 同族，10.x 无修复），已以 `NativeSpawnUnix`（libc `posix_spawn`）弃用 Unix `Process` 根治，原理见 `docs/design/architecture.md`。
  残余：`posix_spawn` 内部仍 fork+shim，最深层机况下单次调用可堵，但无共享锁不传染，60s 弃件后进程存活。
- **引导件冻件口径**：现嵌 8 件为 produce-bootstrap run `39341833602`@`0beb70a` 产出。
  `NativeSpawnUnix` + musl static-pie `DirectPInvoke` 直绑 + `WorkingDirectory=""` PATH 基准修复 + `EnvironmentIsComplete` 完整环境块全链在冻件内（产线 alpine 容器冒烟与本机直跑 apply 双实证）；
  SHA-256 账留 PR #148 处置记录评论。
- **公开 API 破改登记**：`Bundler.Updater` 包面 `UpdateClient.Apply`/`Rollback` 返回类型由 `System.Diagnostics.Process` 换为 `ToolProcess`（快速开发期不背兼容债，见 `docs/design/update.md`）。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/project/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- **macOS 27 管道创建缺陷**（已证实，未修复，修复待用户授权）：
  `src/Bundler.Core/NativeSpawnUnix.cs` 建管优先调 `pipe2(fds, O_CLOEXEC)`，`O_CLOEXEC` 写死为 Linux 值 `0x80000`；
  只在符号缺席或 `ENOSYS` 时回落 `pipe+fcntl`。
  macOS 26 及更早没有 `pipe2`，始终走回落路径，所以 CI 一直通过。
  macOS 27 SDK 头文件标注 `pipe2` 自 `macos(27.0)` 起可用，而 Darwin 的 `O_CLOEXEC` 为 `0x1000000`；
  `pipe2(0x80000)` 返回 `EINVAL`，被当作真实失败抛 `pipe/fcntl failed.`。
  本机实证：C 探针 `pipe2(0x80000)` 得 -1/EINVAL、`pipe2(O_CLOEXEC)` 得 0；Unit 中 `ToolProcessTests` 8 个、`AppImageTests` 9 个因此失败。
  推断影响面（未逐项实测）：macOS 27 上 Core 经 `ToolProcess` 的带重定向工具调用（`.app`/`.dmg`/`.pkg` 后端的 Apple 工具链）、应用内 `Bundler.Updater`，以及同源编译进该常量的 macOS 引导件冻件。
  修复须补 macOS 27 验证腿（CI 有对应 runner 时）。
- 各格式外部事项等待对应环境输入，统一登记在 `docs/project/special-acceptance.md`。
- 仓外独立包消费 fixture（`tests/Fixtures/MsiPackage/`）的 `Fixture` 与 `Standalone/` 在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。
- 环境受限能力已登记 `docs/project/special-acceptance.md`：s390x/ppc64le `UnixFileTypes` PAL 可达性、Windows zip 符号链接还原、提权目录换装等。
- 能力边界（有意不实现）：`DebPackageReader` 不解 GNU 长名/PAX——真实第三方 deb 样本解析非当前目标，规格已对齐。

## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，全功能面体检收口已完成，当前无进行中阶段。
候选工作（均需用户授权后启动）：

- 修复 macOS 27 管道创建缺陷（§5）；
- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`）；
- 发布链资产实际推送（NuGet/Release 为另一步，仍需用户明确许可）；
- `docs/project/roadmap.md` §4.2 候选增强队列当前为空，新增强按候选标准登记。

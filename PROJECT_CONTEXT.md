# DotNet.Bundler 项目上下文

> 最后更新：2026-10-15
> 当前分支：`main`（重写链顶 `test-rewrite/docs`，PR #90–#97 stacked 待合）
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

当前基线：`main@85b6f38`（`0.1.0-alpha.103`）——全项目重构链 PR #82–#88 已全部合入：`ToolRunner` 统一进程执行、`Hashes`/`Gzip` 收编、`PayloadSource` 三包枚举共享、`ToolCache` 泛化并入 AppImage（默认缓存根统一为 `DotNetBundler/tools`，弃 `XDG_CACHE_HOME`）、`FileTree` 树复制收敛、文档同步与 Tauri/上游产品参照框架移除。
**测试体系重写链 PR #90–#97 全部 open 未合**（stacked 基：#91→main、#92→skeleton、#93→unit、#94→api、#95→integration、#96→package、#97→ci），链顶 `test-rewrite/docs@7ea14e8`。
verify 发布门 run `38678818534`（2026-10-15）**33 作业全绿**：tests 六宿主矩阵 + produce×6 + consume×6 异机验签真装真跑 + manual-auto×6 + universal 产/消×3。
其后 run `38687565528` 再绿、`38692708521` 与 `38697374256` 各抓出一条宿主瞬态并修复：CLI 腿 `where.exe` 探测超时误判缺工具 + 夹具 ctor 内 Skip 误报整类失败（450d196/362cae2）、NuGet restore 连接层挂死 10min（7fe88d9 有界重试）。
起跑三连绿前又收编两处收口小项：consume-linux AppImage 双腿各跑各验（FUSE 直跑+extract 免挂，新版 type2-runtime 吃 fuse3）、consume-macos pkg 腿 EXIT trap 防 fixture 滞留 /Applications；HelloBundlerApp 字面样品链经裁决不收编（fixture 链已等价覆盖，保留为 Manual 验收面）。
稳定性判据：verify 需同内容**连续 3 次全绿**才收住——当前链顶内容尚未复跑验证（用户指令暂停 dispatch），前两次绿属于旧测试内容不计入新链顶连胜。
本机 Windows x64 Release：Unit 1222 测（1192 过/30 宿主门控跳）、Api 84 测、Integration 234 测、Package 18 测，全零失败。
收口期修复的真缺陷：`produce.sh` `cat|head` SIGPIPE（92fe129）、NSIS 卸载注册表异步清理竞态（33f0472）、NSIS `/D` 正斜杠粘连+PKG relocation+artifact 丢执行位（b39d9af）、dmg 腿 EXIT trap 毒化（2190df7）、`hdiutil convert` 持续 EAGAIN 踢 diskimages-helper（7a8d2bd）。

## 4. 外部验收边界（未执行，不视为完成）

只能人工/外部付费完成的验收统一登记在 `docs/project/special-acceptance.md`，**不得宣称已完成**；
覆盖：生产 Authenticode/Apple 证书与公证、真实 UAC/重启/锁定文件、干净宿主矩阵、母语审校、真实 GUI 交互、公网/真机腿等。

## 5. 未决问题（等待用户或外部输入）

- 各格式外部事项等待对应环境输入，统一登记在 `docs/project/special-acceptance.md`。
- 仓外独立包消费 fixture（原 `tests/Bundler.LocalPackagesTests/Fixtures/Msi/`，随测试重写迁至 `tests/Fixtures/MsiPackage/`）的 `Fixture` 与 `Standalone/` 在仓内改项目引用后仍保留 `PackageReference`+`Bundler.LocalPackages.props`，
  作为已发布 nupkg 的还原来源、buildTransitive 注入与任务程序集进包契约的验收腿；
  还原来源已由 packageSourceMapping 钉死本地源（PR #41）；
  是否换处理方式（如公开发布后改验公网源、或移入独立验收仓）留待后续裁决。

## 6. 默认下一步

全部格式、CLI 与 UPDATE 均已冻结并入 `main`，功能面无进行中阶段。
**测试体系重写已收口**（规格 `docs/design/testing.md`；Unit/Api/Integration/Package 四层 + `tests/Manual` 验收脚本层全量实现，CI 拓扑为 test/verify/aot 三 workflow，verify 发布门已全绿；PR 链 #90–#97 全部 open 待合入，基见 §3）。
剩余工作：

- 外部待验收项（统一登记在 `docs/project/special-acceptance.md`，见 §5）；
- 发布链资产实际推送（NuGet/Release 为另一步）；
- 零星已登记增强按 `docs/project/roadmap.md` §4.2 候选标准排队。

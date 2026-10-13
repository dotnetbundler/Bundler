# 项目规则

本文件是 **DotNet.Bundler 产品与工程不变量的唯一规范入口**：代码结构、NuGet 包内容与工程组织必须始终满足这些规则，任何改动（人或 AI 执行）都不得违反。
AI 的行为与协作规则在根 `AGENTS.md`；格式专属契约在 `docs/formats/` 对应文档；产品边界与格式顺序在 `docs/roadmap.md`。

## 1. 产品定位与分层

`DotNet.Bundler` 是通用桌面应用打包工具，不是只服务 .NET 应用的安装器生成器。
输入是调用方已准备好的普通应用目录，不限定被打包应用的语言或框架。

稳定分层：

```text
用户入口（MSBuild 适配器 / CLI 适配器 / 直接公共 API）
  ↓
格式无关 Core：验证、规划、编排、工作目录、工具缓存
  ↓
格式后端：NSIS、WiX/MSI、macOS、Linux 等
  ↓
格式所需编译器或平台工具
```

- `DotNet.Bundler.Abstractions` 定义公共契约；`DotNet.Bundler.Core` 负责格式无关的验证、规划、编排、工作目录与工具缓存；各 `DotNet.Bundler.<Format>` 后端各自实现格式语义。
- **入口只是适配层**：MSBuild Task 与 CLI 只负责输入映射、日志与输出呈现，调用同一 Core/后端公共 API，不得各自实现打包语义。
  MSBuild Task 在 MSBuild 进程内调用它们，不再启动额外的 .NET CLI 驱动；实际格式编译器可作为工具进程启动。
- 后端 API 不依赖 `.csproj`、`dotnet publish`、MSBuild 类型或应用使用 .NET。
- `DotNet.Bundler` 是便利元包（聚合后端与 MSBuild 支持）。
- 共享抽象须有真实跨格式需求（已有两个真实消费者，或第二个格式明确提出需求）才提取，不为假想复用扩大公共模型。

## 2. 包即产品

- **后端 NuGet 包本身必须可直接使用**：仓库外普通项目只引用相应后端包、没有本仓库源码或 `ProjectReference`、不引用 `DotNet.Bundler.MSBuild`，就能通过公共 API 生成该格式产物。
- 后端程序集与 MSBuild 任务链保持 `netstandard2.0`：任务程序集须同时被 .NET Framework `MSBuild.exe` 和 `dotnet msbuild`（.NET SDK）双宿主加载，`netstandard2.0` 是两者公共面。
  测试、示例、fixture、原型工具等可执行项目使用 .NET 10。
- **构建宿主下限按三层分别声明，互不混淆**：
  1. **打包工具（能力）下限**：该格式所需打包工具本身支持的最低宿主——是否绑定宿主 OS、必需工具的最早可用宿主版本；声明时不得并入 Bundler 自身约束；
  2. **后端下限**：后端程序集（`netstandard2.0`）能被宿主上哪些 .NET 运行时加载并执行——只看后端，不含 MSBuild/CLI 应用层；
  3. **入口下限**：CLI、MSBuild Task 等各入口自身要求的宿主/SDK 下限，各自独立记录。
- 官方支持口径随 OS 厂商支持期滚动：以工具链/运行时官方支持矩阵为准，EOL 版本只记"可运行"不作支持承诺。
- 可选特性的宿主门槛逐条做可降级处理（降级+警告），不抬高必需链路下限。

## 3. 工具随包供应

- **默认打包必需工具和资源随对应后端包分发**：普通使用者无需另装 NSIS/WiX，也无需运行时联网下载打包工具。
- 固定可信来源、版本、SHA-256 哈希、包内容清单、合法再分发与必要源码/声明义务；缓存污染、损坏和并发有安全处理。
- 显式本地工具覆盖可作为受控高级入口。
- NuGet 包获取和调用方 SDK/MSBuild 环境属于另一层，不能由"包内工具离线"推断整台零环境机器无需其他依赖。
- 预编译冻件（如 `Bundler.Updater.Bootstrap` 的 per-target `bundler-updater` 引导件）按目标宿主工具链产件：musl RID 必须在 musl 工具链宿主内产（`mcr.microsoft.com/dotnet/sdk:10.0-alpine` 容器或 musl 发行版）——glibc 宿主直接 `dotnet publish -r linux-musl-*` 产出"解释器标 musl、NEEDED 仍挂 glibc `ld-linux`"的混血件，musl 宿主上起不来；
  验收看 `readelf -d` NEEDED 仅 `libc.musl-*`（全静态件无 NEEDED 条目亦合规）加目标宿主实跑。
- 工具选型目标：**合法免费、无付费必需服务、体积尽量小、尽量覆盖更多构建宿主和安装设备**；实际支持范围以原生验证为准。

## 4. 目标与格式门禁

- 允许的限制依据仅两类：①工具链真实能力（打包工具或宿主确实做不到，如 WiX 需 Windows 宿主、`dmg`/`pkg` 需 macOS）；②产物语义真实性（能产出文件但安装/运行语义不成立的组合——如 musl 载荷打进 `.deb`/`.rpm`，musl 生态无对应发行版消费面——默认拒绝，显式豁免须用户明确要求）。
- 除上述两类外不替工具预判：目标不做组合白名单；`BundleTarget.TryParse` 按 `<os>[-<libc>]-<arch>` 语法解析，枚举即 OS、libc 与架构全集，枚举外值解析失败，出现需求时按需补枚举与后端映射。
- 工具能力由后端自报，不用与后端脱节的静态表替后端说不——防止矩阵与后端口径漂移。
- 目标语法、架构词与产物命名契约见 `docs/targets.md`。

## 5. 版本与工程约定

- 实现、修复或随包文档调整导致 NuGet 包内容变化时，只在根 `Directory.Build.props` 递增 `BundlerPackageVersion`；包依赖、仓外包消费 fixture 的 `PackageReference` 与集成测试默认值引用此值。
  不在同一 ID/版本上发布不同内容并指望 NuGet 缓存刷新；一个未提交阶段使用同一新版本。
  工具包版本与被打包应用版本独立；公开示例应用版本保持稳定，升级/降级由独立 fixture 测试。
- 外部依赖版本集中在 `Directory.Packages.props`（中央包管理），`PackageReference` 不带 `Version=`（否则 NU1008）；引用仓内 `DotNet.Bundler*` 包时用 `VersionOverride="$(BundlerPackageVersion)"` 覆盖中央版本。
- `src/` 各工程共享元数据（`Authors`、`PackageVersion`、`PackageReadmeFile`、`netstandard2.0` TFM、README 与 THIRD-PARTY-NOTICES 打包项）统一在 `src/Directory.Build.props`（链式导入根 props），个别工程按项目名条件豁免；不在单 csproj 里重复这些值。
- 仓内示例与 fixture 一律以项目引用消费打包系统：共享接线在 `Bundler.ProjectReference.targets`（引用 `src/Bundler.MSBuild`、`ReferenceOutputAssembly=false`、`_BundlerTaskAssembly` 指向源树构建输出并导入 `buildTransitive` props/targets；`BuildReference=false` 使消费方不重建任务工程，避免常驻 MSBuild 节点持锁导致 MSB3021/3027），消费项目只加一行 `GetPathOfFileAbove` 导入即可直接 `dotnet publish`。
- 仓外复制的独立包消费 fixture（模拟外部用户消费发布的 nupkg，是包契约的验收腿）仍走 `PackageReference`：还原垫片只保留在 `Bundler.LocalPackages.props`，由测试代码连同项目复制并传入本轮包源、版本与隔离缓存，真实还原来源由 `MsiSupport.AssertLocalBundlerRestore` 逐包核验；
  缺包先 Pack，不假定未发布版本在公网源，也不用项目引用掩盖包消费问题。
- MSBuild 默认值须按目标族推导：`BundlerMainExecutable` 对 `macos-*`/`linux-*` 取 `$(TargetName)`（无 `.exe` 后缀），其余取 `$(TargetName).exe`；新增目标族或新宿主后缀规则时同步检查该默认。

## 6. 示例契约

- 统一公开示例 `samples/HelloBundlerApp` 必须覆盖全部后端与全部公开旋钮：公共旋钮在主工程，各后端专属旋钮按 `formats/<Format>.props` 导入，用户可直接复制该组织方式；不用测试 fixture 冒充示例。
- 默认命令应直接构建并生成安装包；该格式当前公开且适用的**所有用户能力**都要在示例中有实际配置、可复现的变体命令或明确的操作演示，不能只列名称；
  新增格式/旋钮时同步把对应 `formats/<Format>.props` 演示与传透旋钮补齐。
- 互斥配置分开演示；包含可检查的资源、元数据、桌面集成、语言、安装范围、签名配置等适用内容，以及安装/卸载、验证和清理说明。
- 真实证书、提权或专用环境写明前提和未验收边界，不放私钥。
- 示例是演示，自动回归须另有 fixture；新增公开能力在同阶段更新示例和中文说明；不适用的格式特性无需硬塞。

## 7. 质量与安全契约

- 每项新增或修改功能必须有可区分新旧行为的自动化断言，覆盖验证/映射、真实打包、NuGet 包内容、仓库外直接 API 包消费、应用层消费和适用的系统生命周期；
  只运行旧测试、只查模板或只发布公开示例，不证明新系统行为。
  测试组织与断言机制见 `tests/README.md`。
- 集成测试按 Pack、隔离还原、生成产物、执行系统行为、断言最终状态组织；每轮使用独立输出和产品身份，预检无碰撞；
  "build→pack"链在 build 前一律关闭构建服务器（测试腿走 `Dotnet.ShutdownBuildServers()`，脚本腿用 `dotnet build-server shutdown`）——Windows 常驻 MSBuild 节点持任务程序集锁会致 `dotnet pack` MSB3027（POSIX 腿不需要）。
- 签名私钥、PFX 密码、访问令牌等秘密不得进入仓库、示例项目、普通命令行或可回显日志；签名失败不得留下被误认为成功的最终产物。
- 签名是打包发布能力，但生产私钥、HSM/云账户及第三方服务由发行方提供；生产证书与公开信任链只按真实发布环境验收。
- 格式受管模式须定义身份、所有权与失败边界；用户自备脚本/模板或专家模式要显式标明责任范围，不能把任意用户逻辑宣称为 Bundler 内建安全能力。
- MSI 用 Windows Installer 原生事务，NSIS 的 journal 不自动成为其他格式的设计。
- 不自动发现、下载、安装、修复或卸载任意**应用运行时/先决条件**（WebView2、VC++、.NET、JRE 等）——这是有意的产品边界，详见 `docs/roadmap.md` §2；调用方可准备普通文件载荷。

## 8. 兼容策略

- 快速开发期可依据实际用例调整早期 alpha API 与配置，不为未发布兼容性阻塞正确设计（不背债）；破坏性修改仍须有依据、先汇报。
- 已发布安装身份、用户数据与升级路径仍须按各格式迁移规则保护。
- 只服务一种格式的项目、目录、`.csproj` 和说明采用格式名（`Nsis`、`Msi` 等）；跨格式项目和文档才用泛名。
  重命名项目可能改变安装身份、产物名或升级关系，须单独核对。

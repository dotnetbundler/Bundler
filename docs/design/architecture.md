# 实现原理

本文件讲整条打包管线**怎么工作**：输入模型、三层入口、格式门面、规划、编排、工作目录、工具缓存、命名与 UPDATE 挂点。
"代码必须满足什么"的规则在 `docs/project/project-rules.md`；各格式语义契约在 `docs/design/formats/`；当前事实在 `PROJECT_CONTEXT.md`。
类型名与源码对应，细节以代码为准。

## 1. 总览

```text
调用方输入（MSBuild 属性 / bundler.json / 直接 API 构造）
  ↓ 入口适配层：BundleDesktopApplication Task、CLI、直接实例化 <Format>Bundler
BundleConfiguration（统一配置模型）
  ↓ 门面层：IFormatBundler.Validate → BuildAsync（内嵌资源物化 → 工具解析 → 组装 IBundleBackend）
BundlePipeline（Core 编排器）
  ↓ BundlePlanner.Create：共享校验 → target×format 展开 → 计划项
  ↓ 逐计划项：解析后端 → 建隔离工作目录 → IBundleBackend.BuildAsync → 产物核验 → 清理
BundleArtifact[]（产物清单）
  ↓ 入口层收尾：UPDATE 清单与旁车发射（配置启用时）
```

- 三个入口**都落到同一套门面+管线**，没有任何一个入口自带打包语义（`docs/project/project-rules.md` §1）。
- 一个配置可同时请求多格式多目标：入口先**统一预检**（共享校验 + 各格式 `Validate`，配置类错误一次报完），再**逐格式构建**（单格式失败不拖累其余，末位汇总）。

## 2. 输入模型（`Bundler.Abstractions`）

- `BundleConfiguration` 是全部格式的统一配置：产品名/`Identifier`/`Version`/发布者/描述/主页/版权/`LicenseFile`、`OutputDirectory`、`OutputLayout`（`Flat` 默认｜`ByFormat` 按格式分一层子目录）、`Icons`、`Resources`、`FileAssociations`、`UrlProtocols`、`Targets[]`、`Update?`。
- 每个 `BundleTargetConfiguration`：`Target`（canonical 词 `{os}[-{libc}]-{arch}`）、`InputDirectory`（已备好的普通应用目录）、`MainExecutable?`、`SigningFiles[]`、`Formats[]`。
- `UpdateBundleConfiguration`：`FeedUrl`、`Channel`（默认 `latest`）、签名密钥/公钥、notes、`BootstrapperDirectory?`。
- 目标语法与枚举全集见 `docs/design/targets.md`；`BundleTarget.TryParse` 在规划期把字符串解析成结构化目标。

## 3. 入口层

- **MSBuild**：`buildTransitive` 导入的 targets 调 `BundleDesktopApplication` Task（MSBuild 进程内，不启动额外 CLI）；`Bundler*` 属性与 `Bundler*` 项（`BundlerIcon`/`BundlerResource`/`BundlerMacPkgFile` 等）映射成 `BundleConfiguration`，`WindowsSigningFiles` 等签名项进 target 的 `SigningFiles`；构建后触发 UPDATE 发射。
- **CLI**：`bundler`（`validate`/`plan`/`bundle`）把 `bundler.json` 与命令行参数合成 `BundleConfiguration`——JSON 侧走 `CliConfig.Resolve`：`JsonNode` 解析 → CLI 覆盖应用 → 相对路径解析 → schema 强制 → 物化（`BundlerJsonContext` 源生成序列化 + `BundleConfigurationPaths.Resolve`）；`--<fmt>.<knob>` 透传格式旋钮；`FormatDispatcher` 按格式实例化门面（RID 裁剪的 AOT 构建里被剔格式报"本宿主不支持"）。
- **直接 API**：消费方 `PackageReference` 任一后端包后调门面 `Validate`/`BuildAsync`；多数门面构造为 `new <Format>Bundler(configuration?, options?)`，mac 的 dmg/pkg 门面带两个配置形参（自家配置 + `.app` 配置——dmg/pkg 以 `.app` 为上游载荷）。
- 异常分类在入口统一兑现：配置错 → `ArgumentException` 族（聚合一次报完）；宿主门禁 → `PlatformNotSupportedException`（逐格式容错跳过）；不支持的格式 → `NotSupportedException`；共享校验 → `BundleValidationException`（带 `ValidationIssue` 列表）。

## 4. 门面层（`IFormatBundler`）

每个后端包暴露一个 `<Format>Bundler` 门面，实现 `IFormatBundler`：

- `Validate(BundleConfiguration)`：扇出前预检——版本/目标合法性、`SigningFiles` 配置了但没给 signer、载荷内平台病态文件名等能在构建前拦下的错误都在这里拒绝。
- `BuildAsync(BundleConfiguration)`：先跑同一套 `Validate`（直调也保异常契约），然后**物化内嵌资源**（`NsisEmbeddedResources.MaterializeAsync`：工具集压缩包/模板/语言/插件从程序集资源落到工具缓存目录）、**解析工具**（`ToolCache`/`ZipToolCache` 或显式本地覆盖）、**组装 `IBundleBackend`**，最后交给 `BundlePipeline` 跑。

## 5. 规划（`BundlePlanner`）

`BundlePipeline` 起手调 `BundlePlanner.Create(configuration)`：

1. 共享校验：`BundleConfigurationValidator` 产出 `ValidationIssue` 列表，非空即抛 `BundleValidationException`。
2. 展开：每个 target 的 `Formats` 去重后按优先级排（`.app`→`.dmg`→`.pkg` 链优先，`App`/`Dmg`/`Pkg` 为 0/1/2 其余 0）；请求了 `Dmg`/`Pkg` 而未请求 `App` 时自动补一个 `Intermediate` 的中间 `.app` 计划项（dmg/pkg 以 `.app` 为上游载荷）。
3. 每计划项记：目标、格式、`InputDirectory`、主可执行（默认 Windows 取 `ProductName.exe`、其余 `ProductName`）、输出目录（`ByFormat` 时 `out/<format>/`，`AppImage`→`appimage`、`TarGz`→`targz`，其余格式名小写）、`SigningFiles`。
4. 规划期重名断言：同输出目录下产物名按 `ArtifactNaming` 预判必须互异，冲突即 `BundleValidationException`——新后端接入自动罩住。

## 6. 编排（`BundlePipeline`）

- 后端注册表按 `(OperatingSystem, Format)` 分组、再按 `RequiredLibC` 区分；解析顺序：精确 `RequiredLibC` 匹配 → `null`（任意 libc）兜底；同键同 libc 重复注册直接 `ArgumentException`。
- 构建前先全量解析：任一计划项无后端 → `NotSupportedException`（不产生半成品）。
- 逐计划项建**隔离工作目录** `out/.bundler-work/<target>/<format>/<guid>`，经 `BundleBuildContext`（配置+计划项+工作目录+日志）交给后端。
- 后端返回产物列表（一个计划项可扇出多件，如 MSI 每语言一件）；管线逐件核验：返回 `null` 违反契约、产物路径必须实际存在、全局路径归一化后去重（`InvalidOperationException`，撞名提示 `archiveName` 类覆盖可绕过默认命名）。
- `finally` 清理工作目录：删失败只告警不遮蔽构建异常；再逐层回收空脚手架目录（`<fmt>`→`<target>`→`.bundler-work`），非空即停——只清壳层，绝不动输出本体。
- 崩溃进程留下的 `<target>/<fmt>/<guid>` 残骸无 finally 可执行——每轮构建启动时按 mtime>24h 阈值清扫旧工作目录；阈值内（并发在跑）的目录不动，清扫范围严格限定在 `.bundler-work` 三层壳内。

## 7. 后端（`IBundleBackend`）

- 契约面：`Format`、`OperatingSystem`、`RequiredLibC`（`null`=不分 libc；AlpineApk 声明 `Musl`）+ `BuildAsync(BundleBuildContext)`。
- 后端拿到工作目录后在内部完成格式全流程：NSIS 渲模板跑 `makensis`、MSI 跑内嵌 WiX、`.app` 搭骨架写 plist、deb/rpm/apk 纯托管写入器直接产字节等——格式细节各见其 `docs/design/formats/` 文档。
- 一个计划项可多产物；产物路径由后端按 `ArtifactNaming` 落盘，管线只核验不命名。
- 共享帮手件（`Bundler.Core` internal，IVT 对各后端开放）：`ToolRunner`（后端进程执行共享入口：环境注入、输出捕获、取消、`TryRun` 不抛变体、测试缝；单次调用有界等待默认 15min——超时杀进程树（net10 面）+5s 有界排空+`rc=-1` 工具名耗时标记，合法长任务经 `timeout` 形参豁免如 notarytool 1h，`BUNDLER_TOOL_TIMEOUT_SECONDS` 可配；进程面统一走 `ToolProcess`：Unix 由 `NativeSpawnUnix` 走 libc posix_spawn（file_actions dup2/addchdir、SETPGROUP 进程组树杀、spawn 专线程 60s 有界弃件、自有 reaper 轮询线程收尸——不碰运行时 spawn/reap 全局锁）；Windows 包装 `System.Diagnostics.Process`（CreateProcess 无该失效类）——macOS CI 实证过 .NET Unix spawn/reap 全局锁死锁（fork 子件卡死持读锁→reaper 饿死→zombie 不回收、spawn 面全堵，dotnet/runtime #135294 同族），src 全部进程点与测试侧经 `TestProc`/`ProcessRunner` 收编到同构件；`BUNDLER_TOOL_TRACE` 指文件则每次 spawn 落一行（argv 密位遮罩）；`MacArchiveTools`/`Signing.Windows`/updater 家族全部经 `ToolProcess` 收编——自包含件（updater/bootstrap/signing）同源链接编译 `ToolProcess`+`NativeSpawnUnix` 源码，命名空间随宿主预处理符号切换；musl static-pie 引导件的 libc 符号经 csproj `DirectPInvoke` 逐一直绑（裸 `DllImport("libc")` 在无 `.dynsym` 静态件里运行期不可解析））、`Hashes`/`Gzip`（SHA-256/MD5/SHA-1→小写 hex 与确定性 gzip 的唯一实现点）、`PayloadSource`（deb/rpm/apk/AppImage 共享载荷枚举：有序遍历、非常规文件告警跳过、树内符号链接保留为条目+目标重写相对、外指/悬空拒绝、resources 归一与越界拒绝）、`PayloadPathBook`（载荷路径认领簿：叶落位自动认领父目录链、文件/目录双向碰撞、共享系统目录边界）、`LinuxLinkName`（deb/rpm/apk/AppImage 的 binLink 单文件名校验单源）、`LinkProbe`（链接目标读取单源：Unix `readlink`、Windows reparse point 布局）、`FileTree`（目录树复制：链接策略 Traverse/Reject + 排序 + 深度封顶 + 碰撞回调；reparse 拒绝件 `RejectReparsePoint` 共用）。

## 8. 工具供应与缓存（`ToolCache`）

- 工具随后端 nupkg 内嵌（csproj `EmbeddedResource`：NSIS/WiX 是工具集 zip，AppImage 是裸 appimagetool/runtime 二进制）；资源先经各后端物化步落到磁盘（zip 落出工具压缩包、裸件开流），内容寻址缓存目录统一走 `ToolCache.ResolveAsync` 管线；zip 形态由 `ZipToolCache` 薄封装先做整包 SHA-256 核验与清单读取再委托。
- 默认缓存根全后端统一：`%LocalAppData%/DotNetBundler/tools`（非 Windows 为 `~/.local/share` 等；空值回落临时目录），各后端 options 的 `*CacheDirectory` 旋钮覆盖同一根。
- 缓存目录**内容寻址**：`<name>-<version>-<sha256前12位>`——同 (name,version) 不同哈希的并发解析互不清扫对方暂存。
- 校验链：zip 先整包 SHA-256 核验再读包内逐文件清单（拒绝链接与重复路径——链接检测依赖 `ExternalAttributes`，net8+ 资产生效，netstandard2.0 资产上该检查为降级空操作）；裸文件集逐件钉 SHA-256、写入暂存时逐件复核；保留名 `.bundler-tool-manifest` 前置拒绝；缓存目录存在则按清单全量复核（路径集一致+manifest 匹配+逐文件哈希）。
- 并发与完整性：`<dir>.lock` 文件锁跨进程互斥，`AcquireLockAsync` 争用有界等待（持锁超 `ProcessTimeout` 抛 `TimeoutException`；锁文件确定性缺席 1s 宽限后直抛底层 `IOException`）；物化走 `.{name}-{version}-{sha12}-{guid}` 暂存目录、自验后 `Directory.Move` 原子落位；崩溃遗留暂存按 glob 清扫。
- 安全面：缓存根与锁拒绝重解析点、相对路径逐条验证不逃逸缓存项目录、枚举遍历遇重解析点即拒、删除带 Windows 文件锁重试。
- 显式本地工具覆盖（如 `CompilerPath`/`ToolsetArchivePath`/`TemplatePath` 类选项）是受控逃生门，不改变默认"随包离线"路径。

## 9. 产物命名（`ArtifactNaming`）

- 单一来源：自描述格式统一 `{name}-{ver}-{platarch}.{ext}`；NSIS `…-setup.exe`、MSI `….msi`（多语言加后缀）、`.app`/`.dmg`/`.pkg`、`.AppImage`、`.zip`/`.tar.gz` 各带生态架构词与 `-musl` 段；`deb`/`rpm`/`apk` 保各生态原生命名（`{name}_{ver}-{rel}_{arch}.deb` 等）。
- 架构词按后端生态取（x64/x86_64/amd64/aarch64…）；归档格式用 `windows-x64`/`macos-arm64`/`linux-<arch>` 生态词，边缘架构回退规范 GNU 词。
- 规划期查重与后端产件都走这里，命名契约目录见 `docs/design/targets.md`。

## 10. UPDATE 挂点

- UPDATE 不是格式，分两层挂：**载荷内身份**由后端在 `BuildAsync` 期写入——`UpdateIdentitySidecar.WriteIfEnabled` 把 `bundler-update.json` 安装身份落进产物树（archive/`.app`/AppImage 等可更新格式）；**清单与旁车**是入口层收尾步——各格式产物齐后，入口（MSBuild Task、CLI）调 `UpdateManifestEmitter.EmitAsync(configuration, artifacts)` 发射通道清单 `bundler-update-feed.{channel}.json` 与 `.sig`/`feed.sig`/`.blockmap` 旁车（`.app` 另产 `.zip` 运输件）。
- 更新注入件是**保留名**：用户载荷在注入件落位处自带 `bundler-update.json`/`bundler-updater[.exe]` 同路径条目时，全后端统一显式拒绝（不静默覆盖也不静默剔除）。判定按各格式安装位——`.app` 的旁车落 `Contents/Resources/`，用户根级同名文件进 `Contents/MacOS/` 不构成撞名（也不干扰身份读取）；引导件缺件时全后端统一 `ResolveOrThrow` 诊断抛出（死更新链不产出）——nsis/msi 属 installer-replay 例外：写旁车不注引导件。
- `Bundler.Core/Update/` 提供清单模型、签名/验签、引导件暂存与身份侧车的共享件；应用内 `DotNet.Bundler.Updater` 是另一侧（查/下/验/换）。
- 语义契约见 `docs/design/update.md`。

## 11. 测试与验证入口

- 进程内零子进程：`tests/Unit`；公共 API 产真实产物：`tests/Api`；端到端真装真卸/容器矩阵：`tests/Integration`；本地 nupkg 消费契约：`tests/Package`；脚本化特殊验收：`tests/Manual`。
- 组织、过滤与门禁机制见 `tests/README.md`。

# 测试体系规格

本文件是 Bundler 测试的**设计契约与覆盖清单**：分层判据、工程划分、逐模块必须覆盖的行为点、CI 拓扑与"测不了"的登记边界。
重写完成后，"覆盖是否够"以本文件清单逐项对账；新增功能必须同步扩本清单（没有清单项的新行为不算有测试）。

## 1. 分层判据（.NET 惯例）

| 层 | 工程 | 判据 | 能做什么 | 不能做什么 |
| --- | --- | --- | --- | --- |
| 单元 | `tests/Unit/`（`Bundler.UnitTests`） | 纯进程内、零子进程 spawn | 临时目录文件 IO、seam 假件（`ToolRunner.Handler`）、内存断言 | spawn 任何外部进程、碰系统态、网络 |
| API | `tests/Api/`（`Bundler.ApiTests`） | **不授 InternalsVisibleTo**——编译期强制只用公开面；真跑内嵌工具产真物 | 直调 `XxxBundler`/公共入口产真包，断言产物**结构与契约**（非仅文件存在） | 碰 internal、真装真卸、系统突变 |
| 集成 | `tests/Integration/`（`Bundler.IntegrationTests`） | 真环境层：子进程、fixture 发布链、系统行为 | MSBuild 任务链产包、真装真卸、docker、提权、GUI、更新链、样品链、内部件真进程腿 | 需要凭证/真人观察/可抛弃机器的 |
| 契约 | `tests/Package/`（`Bundler.PackageTests`） | 发货包契约：消费**本地打出的 nupkg** | 还原来源、`buildTransitive` 注入、任务程序集、包内容清单 | 不经 nupkg 的验证 |
| 特殊 | `tests/Manual/` | dotnet test/CI 装不下的 | 脚本化验收 + 证据文件契约 | — |
| 登记 | `docs/project/special-acceptance.md` | 完全测不了的 | 条目登记 | 不得冒充已验 |

宿主差异用 `Assert.Skip`；资源门禁用 trait：`Requires=docker|elevation|localinstall|interactive`，真装真卸另加 `BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1` 同意闸（本机默认不启，CI 一次性环境启闸）。

## 2. 目录与基建

```
tests/
  Unit/            Bundler.UnitTests.csproj（IVT 到手）
  Api/             Bundler.ApiTests.csproj（无 IVT——公开面硬约束）
  Integration/     Bundler.IntegrationTests.csproj（IVT 到手）
  Package/         Bundler.PackageTests.csproj（无 IVT；消费 nupkg 不经项目引用）
  Shared/          宿主探测/进程驱动/临时工作区/证据/包定位/UIA——被各工程链接
  Fixtures/        被测 fixture 工程 + 载荷（按用例需求建，非旧结构照搬）
  Manual/          特殊验收脚本（win/mac/linux 分宿主）+ shared/evidence 契约
```

- xUnit v3 + Microsoft.Testing.Platform；直跑测试 dll（`dotnet test` 对 MTP 工程发现行为不可靠时以直跑为准）
- 命名：`XxxTests`/`XxxContractTests`/`XxxIntegrationTests` 类名按层；方法 `行为_场景_期望` 语义化命名
- 断言惯例：`Assert.Equal/ThrowsAny`/`Assert.Skip`；不用 try/catch 断言、不吞异常、不 `Assert.True(a==b)`
- 每用例独立临时工作区、独立身份；并发安全；失败留现场（日志/路径）后清理自身产物
- 测试密钥等假凭证可入库（标注测试专用），真凭证绝不入库

## 3. 覆盖清单（逐模块行为点 → 层 → 断言）

### 3.1 Bundler.Abstractions（Unit）

| 行为点 | 断言 |
| --- | --- |
| `BundleTarget.TryParse/Parse` | canonical `{os}[-{libc}]-{arch}` 全词表往返；架构别称归一（x64/amd64→x86_64、arm64→aarch64 等；OS 段无别称，仅 windows/macos/linux/linux-musl）；`macos-universal`；非法词拒；LibC 维度 |
| `BundleTarget.ArchName` | 全枚举映射正确、未知名抛 |
| `ArtifactNaming.FileName` | 每格式生态词产物名（nsis `-setup.exe`、msi `.msi`、deb `.deb`、rpm `.rpm`、AppImage、zip/tar.gz/apk、dmg/pkg/app）；版本/架构 token 规则；重名处理 |
| `ArtifactNaming.ArchToken` | 格式×架构 token 表正确（msi x64/x86/arm64、deb amd64/armhf、rpm x86_64/aarch64、apk x86_64/aarch64、appimage x86_64/aarch64、归档 token 为 os-arch 复合词等） |
| `BundleConfiguration` 记录面 | 默认值、init 语义、嵌套记录（Update/Resource/KeyFile/FileAssociation/UrlProtocol/Target） |
| `IFormatBundler`/`IBundleBackend`/`IBundleLogger`/`IBundleSigner` 契约 | 接口形状稳定（编译即验）、`NullBundleLogger` 行为 |

### 3.2 Bundler.Core——配置/校验/规划/编排（Unit）

| 行为点 | 断言 |
| --- | --- |
| `TargetMatrix.Supports` | 格式×OS×libc 支持矩阵全表断言（windows=nsis/msi/zip/targz；macos=app/dmg/pkg/zip/targz；linux=deb/rpm/appimage/zip/targz；apk 仅 linux-musl 等边界） |
| `UnixFileTypes` | stat P/Invoke 与 st_mode 偏移按 ABI 分支的正确性（x64/s390x/ppc64le=24、asm-generic=16、macOS `stat$INODE64` 回退）；FIFO/socket 等特殊文件造样本需真环境——归 Integration 宿主腿 |
| `BundleConfigurationValidator` | 逐条规则：必填四件（productName/identifier/version/outputDirectory）；identifier 反 DNS 正则；version SemVer 形；homepage 仅 http/https；targets 非空；target 可解析；formats 非空+矩阵支持；mainExecutable 空白/越界/存在性（win 默认 `.exe` 后缀）；inputDirectory 存在；outputDirectory 不得落在 inputDirectory 内（含大小写语义差异）；signingFiles 相对路径+存在；icons 存在；resources source/destination 必填+禁越界（`..`/rooted）；licenseFile 扩展名白名单（.txt/.rtf）；fileAssociations 扩展名正则+去重；urlProtocols scheme 正则+去重；update 节：feedUrl 必填/channel 文件名安全/signingKeyFile 必填+存在/publicKey P-256 65B+0x04/公钥与私钥派生一致/bootstrapperDirectory 存在；输出重名检测（含 dmg/pkg 隐式注入 app 占位） |
| `BundlePlanner.Create` | 校验失败抛 `BundleValidationException` 携带 issues；dmg/pkg 缺 app 时注入 intermediate app 项；优先级排序 app<dmg<pkg；OutputLayout.Flat/ByFormat 目录扇出；产物名碰撞抛；MainExecutable 默认推断 |
| `BundlePipeline` | 假后端：os+format+libc 精确/回退解析；同 libc 重复注册拒；无后端 `NotSupportedException`；后端返回 null/不存在路径/路径碰撞的契约违例；`.bundler-work` 创建+清空+脚手架逐层回收（非空即停不越界）；取消传播；日志序列 |
| `BundleConfigurationLoader` | bundler.json 反序列化（枚举字符串、缺失节、畸形 JSON 报错路径） |
| `BundleConfigurationPaths` | 相对路径按基目录归一化 |
| `FileTree.CopyTree` | Reject/WarnSkip/Traverse 三策略；Traverse 必须 maxDepth（断言）；符号链接环防；非常规文件跳过；排序稳定；resolveFileDestination 碰撞回调；overwrite 语义；reparse 点拒绝 |
| `Hashes` | sha256 byte[]/stream/file 三源+sha1 byte[]/stream+md5 byte[]/file（按实际重载面）；lower/upper hex 逐字节正确 |
| `Gzip`/`TarWriter` | gzip 确定性（同输入同输出）；ustar 头字段（名字/大小/模式/mtime/typeflag/校验和）、长名处理、symlink 项、目录项、结尾块 |
| `ToolRunner` | RunAsync 经 `Handler` 缝：参数引用、退出码传播、输出捕获、取消传播；`RunSync` 不经缝（文档化边界；超时非本 API 特性） |
| `ToolCache` | `ResolveCacheDirectory` 默认根与显式覆盖；`ComputeContentId` 稳定性与序无关性；`ResolveToolPath` 越界拒绝；`ResolveAsync` 全生命周期：段校验/sha 校验/锁/暂存原子落位/清单写读/校验失败重建/陈旧暂存清扫/缓存命中不重写；reparse 点拒绝 |
| `ZipToolCache` | zip 内嵌资源物化：条目归一化、目录项、非法条目、清单契约 |
| `PayloadSource` | deb/rpm/apk 载荷枚举：输入目录遍历、file entries 覆盖、模式位、目录项、符号链接、binlink |
| `FreedesktopFiles` | .desktop 合成（Name/Exec/Icon/Categories/MimeType）、图标收集、目标路径归一化 |
| `Update/` 模型组 | `UpdateKeyMaterial` 生成/加载/公钥派生（P-256 65B）；`UpdateJson` 序列化兼容；`UpdateFeedModel`/`UpdateFeedArtifact` 往返；`UpdateIdentitySidecar`/`UpdateInstallIdentity` 读写；`UpdateBlockMapModel` 块图结构；`UpdateSignatureVerifier` 验签对错；`UpdatePayloadStaging` 暂存目录语义；`UpdateBootstrapper` 内嵌引导件供应（目标→件映射、缺件报错） |

### 3.3 Bundler.Core——格式无关写读器（Unit，纯字节/文件断言）

| 行为点 | 断言 |
| --- | --- |
| `TarWriter` | 见上；另：targz 解包 round-trip（自写自读+系统 tar 可读由集成腿验） |
| `ZipWriter`（Archive） | local/central 头、CRC32、存储/压缩方式、目录项、zip64 边界、非 ASCII 名编码 |
| `ArWriter`（Deb） | ar 归档头、成员对齐、长名（`#1/`/`/` 表）、debian-binary/control/data 三段序 |
| `CpioWriter`+`RpmHeaderWriter`（Rpm） | newc 头、header 结构（magic/索引区/存储区）、tag 写入、签名段、lead 结构 |
| `ApkPackageWriter`（AlpineApk） | 三段 gzip 流、PKGINFO 字段、.SIGN.RSA 位置、脚本段 |
| 各 `*PackageReader` | 自写自读 round-trip + 系统工具反向解析我件（真实第三方样本包解析非目标——DebPackageReader 不解 GNU 长名/PAX） |

### 3.4 入口适配层（Unit）

| 行为点 | 断言 |
| --- | --- |
| `Bundler.Cli` | `CliArguments.Parse` 全动词/选项/错误；`CliConfig.Resolve` 参数→配置映射+bundler.json 合并优先级；`CliProgram.Run` 退出码表（0/1/2/用法错）；`FormatDispatcher` format→后端映射全表；`CliBundleLogger` 级别过滤 |
| `Bundler.MSBuild` | `BundleDesktopApplication` 任务：MSBuild 属性→`BundleConfiguration` 全旋钮映射、targets/items 展开、错误路径（任务内直接实例化断言，不经 msbuild 宿主）；`MergeUniversalPayload` 输入契约 |

### 3.5 Bundler.Signing.Windows（Unit）

| 行为点 | 断言 |
| --- | --- |
| `WindowsAuthenticodeSigner` | 证书文件/指纹/sha1 参数→签名请求构造；无证书环境报错路径 |
| `WindowsExternalCommandSigner` | 外部命令模板渲染、占位符展开、退出码传播 |

### 3.5a Bundler.Update（Unit + Integration）

| 行为点 | 层 | 断言 |
| --- | --- | --- |
| `EcdsaSigner` | Unit | P-256 签/验自洽；P1363↔DER 双向转换逐字节；坏签拒 |
| `UpdateManifestEmitter` | Unit | `IsUpdateAdapted` 覆盖格式表；`FeedFileName` 通道映射；`EmitAsync` 产物清单（`.app` zip 运输件/`*.sig` 签名旁车/`*.blockmap`/feed json/`feed.sig`）；无适配产物仍发射签名空 feed；重复 app 产物去重保最终件；URL 转义/相对 URL；必须签名钥 |
| 打包侧集成 | Integration | 各格式产包时 update 节注入侧车/feed 的端到端面 |

### 3.6 Bundler.Updater（Unit + Integration）

| 行为点 | 层 | 断言 |
| --- | --- | --- |
| `UpdateVersion` | Unit | 解析/SemVer 序/预发通道/非法拒 |
| `ArchiveExtractor`/`UstarReader` | Unit | zip/targz 解压到临时目录；越界链接拒（`LinkEscapesRoot`/`ContainsEscapingLink`）；单顶目录解析；损坏件报错 |
| `UpdateClient` | Unit | `FromIdentity`/`FromInstallDirectory` 身份读取；feed URL 解析、签名 URL 派生、artifact 相对/绝对 URL、channel→feed 文件名、版本比较选新、无更新返 null；`FreeSpaceProbe` 缝：ENOSPC 预检 |
| `UpdateApplier` | Unit | staging 清扫（`SweepStaleStaging`）、retention 目录解析、single-top-dir 断言 |
| Apply/Rollback 真链 | Integration | v1 装→feed v2→Check/Download/Verify/Apply 进程链→换包生效；Rollback 还原；互嵌拒；KeepPayload/KeepRollbackBackup；WaitPid 等待；sh/dash/AOT 三引导件对照（Linux/mac 腿） |
| `UpdateDownloader` | Integration | file:// feed 全链；Range 差分（blockmap 复用率断言）；签名验真/验伪 |

### 3.7 Bundler.Updater.Bootstrap（Unit + Integration）

| 行为点 | 层 | 断言 |
| --- | --- | --- |
| `BootstrapPlan.Run` 参数面 | Unit | 动词/参数解析、缺参报错、退出码 |
| `Apply` 交换语义 | Integration | 提权/非提权目录换装；写失败回滚；`MacAppGate` 剥离 .app 语义；`CanonicalPath`；`ForceManagedCopy`/`IoFaultProbe`/`FreeSpaceProbe` 三缝 |
| `Program.Main` | Integration | AOT 件与 sh 降级件行为奇偶 |

### 3.8 十格式后端（Api + Integration + Unit 内件）

每格式统一面（各格式一份）：校验错误路径（非法配置→issues）、产物命名、身份确定性（同输入同指纹/ProductCode/包名）、最小配置产包、全旋钮产包、产物结构断言、签名路径（有则）、更新适配（有则）。

| 格式 | 格式专属行为点（节选关键面） |
| --- | --- |
| NSIS | `NsisBundler` 产 setup.exe；`NsisToolResolver` 内嵌工具集物化+并发锁+篡改恢复；`TemplateRenderer` 变量/语言/段渲染；`NsisLanguageCatalog` 22 语言键集逐位一致+自定义语言文件校验；`WindowsFileNames` 非法名；`SemanticVersion` 策略比较；资源物化并发/篡改恢复；**事务族**：journal 写/恢复、中断安装/卸载续跑、manifest 失配拒绝、篡改拒绝、各阶段回滚、降级阻塞/放行、锁文件安全失败、RM 关进程、快捷方式迁移、数据保留、junction 拒绝、重啟队列（3010/不启动）、静默/被动/向导退出码、参数透传；legacy MSI 三旋钮（ProductCode/UpgradeCode/AutoDetect 名称+发布者匹配）；Integration：localinstall 全生命周期+交互向导+签名校验链 |
| MSI(WiX) | `WixBundler` 产 .msi；`WixToolsetResolver` 物化+并发锁+**篡改恢复族**（编译器篡改复原/归档拒绝/并发串行/同版本载荷变化拒）；`WixIdentity` ProductCode/UpgradeCode 稳定、PackageCode 逐件互异、大写指纹契约；`WixProductDocument` wxs 全旋钮生成（组件/目录/快捷键/注册表/卸载/启动勾选/目录选择/位图）；`WixLanguage`/`WixLocale` 语言-代码页-区域三向映射+全声明语言产包+调用方覆盖；`WixExtensionValidator` 白名单/专家模板/merge module/引用文件变更拒绝；`MsiIdentityProbe` 旧包探测；FIPS；编译告警即失败；x86/arm64/x64 三架构；Integration：localinstall 腿全族（真装/主升级/降级拒/受损修复/特性/目录/语言共存/专家模板/交互向导） |
| .app | `MacAppBundler` 产 .app；InfoPlist 全键面+合并方优先级（身份键强制）；DocumentTypes/UrlTypes/共享关联归并；`IcnsBuilder`/`AssetsCar`/actool 降级；MachO/Fat 解析+universal 合并（异构拒/超载拒/链接环拒/外码拒）；`PortableExecutable` 拒绝非 MachO 主程序；签名 inside-out 序/硬化运行时/entitlements/公证凭证解析/临时 keychain 不留密于 argv/失败清理；LSDB 注册；Integration：真产真启动、可执行位、重建确定性 |
| .dmg | `MacDmgBundler` 产 .dmg（Ulmo/Udzo/Udbz 映射）；布局窗口/图标位/背景/卷名/EULA（license 资源注入+签名序）；**hdiutil 族**：attach/convert/detach 全链、EAGAIN/busy 重试预算、detach 完成判据（rc+挂载点消失）、失败零残留；Integration：mac 腿真挂载+SLA 交互腿 |
| .pkg | `MacPkgBundler` 产 .pkg；pkgbuild/productbuild 组件+分发包；安装域（system/user-home）；脚本目录；welcome/conclusion；xar 结构；productsign 组件/分发两段；公证+staple 接线+互斥校验；Integration：installer 收据/forget/覆盖升级 |
| .deb | `DebBundler` 纯托管产 .deb；control 全字段（含 Epoch/Release/关系五件）；ar 三段序+长名/溢出错；maintainer 四脚本+conffiles+systemd daemon-reload；md5sums 逐件；SemVer→debian 版本映射；kebab 名派生；目录项所有权；确定性字节+sha256 旁车；Integration：docker 发行版矩阵（debian/ubuntu 系）装/卸/run+conffile 升级语义+lintian+arm64 结构 |
| .rpm | `RpmBundler` 纯托管产 .rpm；header 全 tag+rich-deps 拒绝/接受规则；四 scriptlet+四 program；config(noreplace) 标志位；GPG/RSA 签名+验签（隔离 rpmdb）+任意宿主产签；changelog；`RpmPackageReader`；Integration：docker fedora/rocky/alma/opensuse 矩阵+scriptlet/config/升级语义+rpmlint+arm64 |
| .AppImage | `AppImageBundler` 产 .AppImage；`AppDirBuilder`（desktop/图标/AppRun/库目录/metachar 拒绝）；`AppImageToolset`（appimagetool+runtime 内嵌物化）；确定性字节；交叉编译 aarch64；GPG 签名旁车+半配置拒；Integration：`APPIMAGE_EXTRACT_AND_RUN` 直跑+FUSE 挂载+docker 矩阵+断网腿+appimagelint |
| zip/tar.gz | `ArchiveBundler` 双格式；`ZipWriter` zip64 边界（>4GiB/>64k 项/流式>2GiB）；`TarWriter` ustar+链接；`UnixLinks`/`WindowsLinks` 宿主差异；`MacArchiveTools` xattr/ditto 保元数据；exec 位/符号链接保留；命名生态词；Integration：解开→exec 位→运行→第三方实现互读（python zipfile/tar） |
| .apk | `AlpineApkBundler` 纯托管产 .apk；三段 gzip 结构+尾签；PKGINFO 全字段+extra 字典；六脚本旋钮+升级脚本；RSA 签名段+加密钥；确定性；armv7sf→armel 映射；Integration：alpine docker 装/卸/索引+签名验签+arm64（qemu/原生） |

**更新链总面**（跨 Update/Updater/Bootstrap 的工程级族，现散布三工程）：manifest 发射（feed 文件名/sig 旁车/blockmap/通道）；ECDSA P1363/DER；客户端四动词（Check 选新/Download 差分+Range 回退/Verify 验签/Apply 进程派生）；**BootstrapPlan 奇偶族**（managed·sh·AOT 三实现同断言）：换包/备份/回滚/retain/stale 清扫/锁超时/WaitPid/IO 故障探针/ENOSPC/符号链接族/嵌套拒/非 ASCII 路径/锁定文件宿主差异；v1 兼容腿（旧 feed/旧侧车/旧引导件各实现）。 |

### 3.9 联合交换（Integration + CI 设施）

CI `verify.yml` 的 `consume` 腿为 **(消费宿主 × 产件源) 配对矩阵**：各宿主产件 job 上传 `产物+SHA256SUMS` → 每台消费宿主下载**所有自己能跑**的产件，清单校验后真装/真跑/真卸：

| 产件腿 | 产 | 消费宿主（配对源） | 验 |
| --- | --- | --- | --- |
| windows-x64 | nsis.exe + msi + zip/targz | windows-x64 同构 + **windows-arm64（WOW64 异构）** | 真装/跑/卸 |
| windows-arm64 | nsis.exe + msi + zip/targz | windows-arm64 同构 | 真装/跑/卸 |
| ubuntu-* | deb+rpm+AppImage+apk+zip+targz | 同架构 ubuntu/docker 子矩阵 | docker 内真装真跑；apk 在 alpine 容器 |
| macos-arm64 | .app+.dmg+.pkg 走 arm64；另产 app-x64/dmg-x64 切片目录与 arm64 归档 | macos-arm64 同构 + **macos-intel（只吃 app-x64/dmg-x64 切片）** | 挂载/安装/启动 |
| macos-intel | .app+.dmg+.pkg+归档（纯 x64） | macos-intel 同构 + **macos-arm64（Rosetta 全量）** | 挂载/安装/启动 |

异构腿定义规则：只接真实宿主机制——WOW64/Rosetta 是产品级兼容层（用户真这么用），qemu/binfmt 类模拟不算"能使用"故 linux 无双向交换。macOS 侧 arm64 产件中 pkg/archive 无 x64 切片故 intel 消费腿对它们映射 `-` 刻意跳过（consume-macos.sh 腿=子目录映射参数）。`universal` .app 产/消对并入该拓扑（一产两消）。

### 3.10 样品链（Integration）

HelloBundlerApp：`dotnet build/publish` 链 → 经 buildTransitive 产包 → 产物可装/可跑断言；MSBuild 三模式（build/publish/pack 触发面）+ CLI 多 input-dir 面 + universal 合并面。
覆盖面由各格式 fixture 发布链等价承担；样品工程本体的字面 publish 链经裁决不收编自动化，留 Manual 验收面（脚本以 `-InstallerPath` 消费样品产物）。

### 3.11 包契约（Package）

本地 `dotnet pack` → `Bundler.PackageTests` 消费：nupkg 还原来源、buildTransitive 目标注入、`Bundler.MSBuild` 任务程序集就位、各后端包内容清单、`DotNet.Bundler` 元包依赖图与随包文件（README/LICENSE/THIRD-PARTY-NOTICES）；MSI fixture 真装腿保留（localinstall 闸）。

## 4. CI 拓扑（全新）

```
.github/workflows/
  test.yml       测试矩阵唯一定义：workflow_dispatch(suite×runners) + workflow_call
                 legs: ubuntu-26.04 / ubuntu-26.04-arm / windows-2025-vs2026 /
                       windows-11-vs2026-arm / macos-26-intel / macos-26
                 每腿: build → run-tests 驱动（四工程直跑 dll + 逐类进度日志）
  verify.yml     发布门：workflow_dispatch
                 jobs: tests=call test.yml(suite=all,runners=all) →
                       pack → produce-{win,linux,mac}（fixture 产件+SHA256SUMS）→
                       consume 矩阵（联合交换真装真跑）→ manual-auto 腿 → universal 产/消
  produce-bootstrap.yml  产件流：per-RID AOT 引导件产出（workflow_dispatch，全 RID 五腿）
scripts/ci/
  run-tests.{sh,ps1}   唯一测试驱动（四工程直跑 dll、逐类、进度日志、-longRunning）
  produce.{sh,ps1}     fixture 产件 + 清单
  consume-*.{sh,ps1}   联合交换消费腿
```

**消重原则**：测试驱动逻辑只存在 `run-tests.*` 一份；测试腿定义只在 `test.yml` 一份；verify 经 `workflow_call` 复用不复制；push 不自动触发任何 workflow（本机=基础面，全量=手动 dispatch）。

## 5. 进不了 CI 的清单（落 `docs/project/special-acceptance.md`）

两类一档：

- **脚本可跑但需外部条件**：生产凭证签名链、可抛弃 VM 故障注入、真实重启、交互桌面 UI、公网/CDN、真机 ARM64、干净宿主——`tests/Manual/` 脚本 + 证据契约，逐条登记运行条件
- **完全人工**：母语审校、观感核对、真实旧 MSI 产品迁移、生产密钥分发演练——登记表 + 证据要求

## 6. 验收标准

重写完成 = 本清单每行有对应测试或登记条目；验证面 workflow（test/verify）全绿（produce-bootstrap 为产件流另行验证）；单测零子进程可机器核查（`Process.Start|ToolRunner.(RunAsync|TryRunAsync|RunSync)` 在 Unit 工程实调用零命中——`ToolRunnerTests` 自身经 `Handler` 桩的调用与 CliTests/MacAppTests 收割注释提及除外）。

**实施期同步义务**：本文件随引入登记进 AGENTS.md §4 文档地图与 `docs/README.md` 索引；工程改名面（Bundler.Tests→UnitTests、LocalPackagesTests→PackageTests、tests/Special→Manual）触发 AGENTS.md §6 命令表、tests/README 工程表、workflow 中 `tests/Special/` 路径引用的同步。

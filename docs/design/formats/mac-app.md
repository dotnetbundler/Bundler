# macOS `.app` 包

本文件是 `.app` 后端的唯一文档：工具链裁决、语义契约、能力状态与外部待验收。
当前实现事实以代码、测试和 `PROJECT_CONTEXT.md` 为准；跨格式项目规则见 `docs/project/project-rules.md`。

## 1. 状态与边界

- `.app` 格式已冻结（基线 `0.1.0-alpha.45`），冻结后仅缺陷修复附回归测试。
- `.app` 是目录产物：无安装事务、收据、卸载器；安装=拷贝、卸载=删除、升级=整体替换，不伪造 MSI 式语义。
- per-user/per-machine 安装范围不适用 `.app`（受管安装归 `.pkg`）；iOS 属边界外。

## 2. 工具链裁决（对"工具随包"条款的格式级偏差）

- **macOS 打包工具不可再分发**（`codesign`/`notarytool`/`hdiutil` 等为 Apple 专有二进制，只能随 macOS/Xcode 使用）——采用"宿主工具探测 + 版本下限 + 明确报错"策略。
- 构建工具尽量覆盖更多宿主：优先系统自带工具；Xcode 专属工具（`actool`/`assetutil`/`SetFile`）只服务可选能力，缺失降级而非报错；版本下限只在功能确实需要时设；能用 CLT 完成的不得要求完整 Xcode。
- 不自动安装 Xcode/CLT、不替用户激活 Rosetta；公证外全程离线（公证是显式开启的网络动作）。
- 宿主三层下限：未签名 `.app` 目录组装是纯文件系统操作，**任意宿主可产**（跨宿主产物附警告：Windows 宿主不保 Mach-O 可执行位）；走 Apple 工具的步骤绑定 macOS——不公证链（`.app`+DMG+PKG）Intel 宿主 macOS 10.7+；公证需 Xcode 13+/宿主约 macOS 11.3+；`.icon`→`Assets.car` 需 Xcode 26+/约 macOS 15.6+（可降级）。后端 `netstandard2.0`；MSBuild 入口需 .NET 10 SDK。
- 后端分包：`Bundler.MacApp`/`Bundler.MacDmg`/`Bundler.MacPkg` 各自独立；签名/公证/钥匙串/工具探测共享件在 `Bundler.Signing.Mac`（对齐 `Bundler.Signing.Windows`）。

## 3. 语义契约

1. **身份**：`Identifier`→`CFBundleIdentifier`，按 Apple 规则校验不通过即拒绝；发布后 bundle ID 不可变。`CFBundleName` 可独立于 `CFBundleDisplayName`/`ProductName`。
2. **版本**：`CFBundleShortVersionString` 取规范化可见版本；`CFBundleVersion` 为构建迭代号默认取同一串可显式覆盖；`LSMinimumSystemVersion` 调用方显式配置才写入（不代设下限）；版本键至多三段数字，不合即拒。
3. **载荷映射**：`InputDirectory` 不拍平——主可执行（Mach-O 校验）→`Contents/MacOS/`；`Resources` 项→`Contents/Resources/` 保相对路径；显式 `Contents` 映射表覆盖其余；`Frameworks` 仅接显式 `.framework`/`.dylib`，不做宿主目录隐式查找。符号链接/重解析点/越界一律拒绝；`Contents/` 顶层保留名不可被映射覆写。
4. **架构**：`macos-x86_64`/`macos-arm64`/`macos-universal` 独立产物；x86_64 在 Apple Silicon 依赖 Rosetta 属系统行为。`macos-universal` 只收**已合并 universal 目录**：每个 Mach-O 强制双切片（托管解析 Mach-O 头，等价 `lipo -info`），非 Mach-O 资产须架构便携；`UniversalCodeProbe` 嗅探拒收 ELF/原生 PE/RTR 程序集，瘦 Mach-O 由架构校验拦，FDD 纯 IL 放行。
   universal 目录的三种已实测配方：`PublishSingleFile`×2+lipo、`PublishAot`×2+lipo、FDD（不带 `-r` 发布+双 apphost lipo）；松散目录式自包含物理不可跑，契约不承诺。
   生产路径：MSBuild `BundlerUniversalTargets`（多 RID 内层 publish + 纯托管合并器：Mach-O 逐件并片/非 Mach-O 全同取一/dSYM+pdb 取首份/其余不同即拒）；CLI 多 `--input-dir` 走同一合并器，架构戳托管件相异时硬拒并指向 `--reference-dir`。
   Bundler 不合成 fat binary——`lipo` 合成、rpath 改写、依赖解析是调用方构建职责。
5. **签名**：默认不签名，载荷既有签名原样保留；显式配置后才 `codesign`：identity 字符串 / `-` ad-hoc / 临时钥匙串导入三种提供器；hardened runtime 默认开、只作用可执行目标；entitlements 显式给定；inside-out 顺序（`MacOS`/`Frameworks`/`Plugins`/`Helpers`/`XPCServices`/`Libraries` 内文件先签含托管 dll，后主可执行、再整包）；签名前 `xattr -crs`；失败不留看似成功的产物。
6. **公证**：显式开启的网络上传，默认关；`xcrun notarytool`/`stapler`；凭证经环境变量/密钥提供器传入（Apple ID+专用密码或 ASC API Key），不入库不入命令行；`SkipStapling`/`NotaryWait` 独立开关；submit 非零即构建失败并附输出；上钉失败不标"已公证"。
7. **秘密**：证书/密码/API key 只经密钥提供器或约定环境变量；临时钥匙串用完即删。

## 4. 能力状态

已实现并验证：

- `.app` 骨架（`Contents/{Info.plist,MacOS,Resources,Frameworks}`）、全部 Info.plist 键（含 `NSAppTransportSecurity` 例外域默认关）、调用方自备 Info.plist 合并（身份键回读强制一致，冲突即拒）。
- 图标：`.icns` 直采、位图合成 `.icns`、`.icon`→`Assets.car`（Xcode≥26 `actool`，缺失降级警告）、`*.car` 直采优先。
- 桌面集成：`CFBundleDocumentTypes`/`UTExportedTypeDeclarations`/`CFBundleURLTypes`（与共享 `FileAssociations`/`UrlProtocols` 按扩展名/scheme 合并）。
- x86_64 启动已实测（macos-26-intel 真 Intel + macos-26 Rosetta 两腿）。
- ad-hoc 签名实跑全绿；Developer ID 签名流程代码化并桩断言。
- 横切：独立 `Bundler.MacApp` 包、仓外 NuGet 消费、MSBuild 一致、离线构建、工具探测门槛、`samples/HelloBundlerApp`。
- Gatekeeper 拦截断言：未签名隔离包被 `com.apple.quarantine`+`open` 阻断已实测。

外部待验收（详见 `docs/project/special-acceptance.md`）：

- Developer ID 真实证书签名、真实公证/上钉/撤销（SA-P-02、SA-P-08）、Gatekeeper 已公证场景。
- universal links（`associated-domains`）：需 provisioning profile 与开发者账号，走 expert 边界。
- 观感项与干净宿主/不同 macOS 版本实测。

## 5. 有意排除

- `.pkg` 系统安装/卸载/收据——另立格式不混入。
- `lipo` fat binary 合成、rpath 改写、依赖解析——调用方职责。
- `providerShortName`、`LSRequiresCarbon` 等上游死键——不复制。
- `MACOSX_DEPLOYMENT_TARGET` 环境变量联动——调用方编译链职责。

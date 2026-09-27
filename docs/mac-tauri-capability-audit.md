# Tauri 通用 macOS 能力审计（`.app` 与 `.dmg`，2026-09-26 快照）

本文件是 MAC 系列格式的上游参照审计。
本仓库尚无 macOS 后端实现；“当前 Bundler 事实”列描述的是公共模型与规划器已存在的部分。
审计方法、状态口径与通用规则见 [`docs/development-rules.md`](development-rules.md) 第 2、6 节；格式阶段分解见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)，PKG 取舍见 [`docs/mac-format-decision.md`](mac-format-decision.md)。
`.app` 与 `.dmg` 在上游是同一条 macOS 管线（DMG 依赖 APP），故共用一份审计；各项的归属阶段在“选择与阶段”列标明。
只比较用户可观察能力，不复制上游字段、模板、脚本或内部机制。

## 上游快照与方法

沿用仓库既有固定快照：`tauri-apps/tauri` `dev` 分支提交 `7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf`（与 `docs/msi-tauri-capability-audit.md` 同一基线，首次核对于 2026-09-25）。
2026-09-26 复核上游漂移：`dev` 已移至 `9f8922a0388986c38158e531707115ce473a3fa2`；`crates/tauri-bundler/src/bundle/macos/**`、`settings.rs` 与 `tauri-utils/src/config.rs` 的 macOS 相关表面逐文件比对无实质差异（仅一处 `unwrap_or`→`unwrap_or_else` 风格改动与一段与本格式无关的文档注释增补），本审计维持 `7dbfc1f` 基线结论有效。
2026-09-26 MAC-APP-5 冻结前复核：`dev` 再移至 `447fa9f3f993fe77724189e355078b38ce20baea`；`app.rs`/`sign.rs`/`icon.rs`/`dmg/mod.rs`/`settings.rs`/`config.rs`/`tauri-macos-sign`（lib.rs、keychain.rs）共 8 个 macOS 相关文件与 `9f8922a` 字节一致，无新能力缺口，基线结论继续有效。
2026-09-26 MAC-DMG-5 冻结前复核：`dev` 头仍为 `447fa9f`（自 MAC-APP-5 无新提交）；`.dmg` 侧关键文件 `dmg/mod.rs` 与内嵌 `bundle_dmg`（create-dmg 1.2.1 fork，638 行，SHA-256 `dce473d5…`）在 `7dbfc1f`..`447fa9f` 间字节一致，无新能力缺口，基线结论继续有效。

固定比较点（均按快照 SHA 链接）：

- [`crates/tauri-bundler/src/bundle/macos/app.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/macos/app.rs)：`.app` 结构与 Info.plist。
- [`crates/tauri-bundler/src/bundle/macos/dmg/mod.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/macos/dmg/mod.rs) 与 [`dmg/bundle_dmg`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/macos/dmg/bundle_dmg)：DMG 生成（后者是 `create-dmg` 1.2.1 的内嵌 fork）。
- [`crates/tauri-bundler/src/bundle/macos/sign.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/macos/sign.rs) 与 [`crates/tauri-macos-sign/src/lib.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-macos-sign/src/lib.rs)、[`keychain.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-macos-sign/src/keychain.rs)：签名与公证。
- [`crates/tauri-bundler/src/bundle/macos/icon.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-bundler/src/bundle/macos/icon.rs)：`.icns`/`Assets.car` 图标管线。
- [`crates/tauri-utils/src/config.rs`](https://github.com/tauri-apps/tauri/blob/7dbfc1fe5c36143b6a4614cbbd9d4c14ee9dfdcf/crates/tauri-utils/src/config.rs)：`MacConfig`、`DmgConfig`、`FileAssociation`、`DeepLinkProtocol`。

不在本审计范围：`ios.rs`（iOS 属移动平台，桌面打包器边界外）、`PackageType.Updater`（Tauri 更新协议绑定其运行时，如需应用更新另立跨格式路线）、上游 CLI 的构建编排细节。
上游 `dev` 会继续漂移，进入对应阶段前按快照路径重新核对差异。

## `.app` 能力逐项审计（MAC-APP 范围）

| Tauri 用户能力或配置 | 当前 Bundler 事实 | 选择与阶段 |
| --- | --- | --- |
| `.app` 目录骨架 `Contents/{Info.plist,MacOS,Resources,Frameworks}`，产物名 `<产品名>.app` 不含版本/架构 | `PackageFormat.App` 已在公共枚举，规划器已能为 `Dmg` 请求补 `App` 中间产物；无后端 | MAC-APP-1；沿用上游标准布局，产物命名契约以本仓库输出目录规则为准（`OutputDirectory/<rid>/app`） |
| 主可执行与附加可执行文件装入 `Contents/MacOS/`；sidecar 文件名剥离 `-<target triple>` 后缀 | `BundleTargetConfiguration.MainExecutable`/`SigningFiles` 已有；载荷目录如何映射进 `Contents/` 未定 | MAC-APP-1；输入映射约定见 `mac-app-roadmap.md` 第 3 节，待用户确认 |
| 资源文件装入 `Contents/Resources/` 并保留相对路径 | 公共模型 `Resources` 已有 | MAC-APP-1 |
| 任意文件/目录装入 `Contents/` 指定相对位置（上游 `macos.files` 映射） | 无对应配置 | MAC-APP-1；用于 Info.plist 之外的补充载荷（如 `embedded.provisionprofile`） |
| framework 装入 `Contents/Frameworks/`：显式 `.framework`/`.dylib` 路径，或裸名在 `~/Library/Frameworks`、`/Library/Frameworks`、`/Network/Library/Frameworks` 三处查找 | 无对应配置 | MAC-APP-1 只接显式路径；**有意收紧**：不做构建机标准位置隐式查找，避免把宿主未声明的库静默打进产物 |
| Info.plist 固定键 `CFBundleInfoDictionaryVersion=6.0`、`CFBundlePackageType=APPL`、`CFBundleDevelopmentRegion`、`NSHighResolutionCapable`、`CSResourcesFileMapped` | 无 | MAC-APP-1；上游另写 `LSRequiresCarbon=true`，属无效果的遗留死键，**不复制** |
| `CFBundleDisplayName`/`CFBundleName`（可独立于显示名）、`CFBundleExecutable`、`CFBundleIdentifier` | `ProductName`/`Identifier`/`MainExecutable` 已有；`bundleName` 无对应字段 | MAC-APP-1；`CFBundleName` 与 `CFBundleDisplayName` 的差异是真实用户可见面（菜单栏/访达），提供独立可配置项 |
| `CFBundleShortVersionString`（用户可见）与 `CFBundleVersion`（构建迭代号，上游可独立配置） | 公共模型只有单一 `Version` | MAC-APP-1；版本映射规则见 `mac-app-roadmap.md` 第 3 节 |
| `LSMinimumSystemVersion`（上游默认 `10.13`，`null` 移除该键） | 无 | MAC-APP-1；已确认由调用方显式配置、未配置不写入（不代设下限，与上游默认 10.13 不同），见路线第 3 节 |
| `LSApplicationCategoryType`（`category`，38 项枚举） | 公共模型无 `Category` 字段 | MAC-APP-1 提供受限枚举；访达“显示简介”可见，属真实用户结果 |
| `NSHumanReadableCopyright`（`copyright`） | 公共模型 `Copyright` 已有 | MAC-APP-1 |
| 图标：`.icns` 直接采用；PNG 等位图合成 `.icns`（按 `@2x` 文件名判定密度、向下取 2 的幂缩放）；`.icon`（Icon Composer）经 `actool` 编译出 `Assets.car` 并置 `CFBundleIconName`（需 Xcode 26+）；`.car` 直接采用 | 公共模型 `Icons` 已有 | `.icns` 透传与 PNG 合成进 MAC-APP-1；`Assets.car` 管线进 MAC-APP-2（构建宿主要求 actool≥26，缺失时降级只用 `.icns` 并报警告） |
| 文件关联 → `CFBundleDocumentTypes`（`CFBundleTypeExtensions`、`CFBundleTypeName` 默认 `ext[0]`、`CFBundleTypeRole`、`LSHandlerRank`、`LSItemContentTypes`）+ `UTExportedTypeDeclarations` + 扩展名/MIME→UTI 推断 | 公共模型 `FileAssociations` 有 `Extensions`/`Name`/`Description`/`MimeType`；缺 role/rank/contentTypes/exportedType | MAC-APP-2；macOS 专属字段进后端配置，不为假想复用扩大公共模型 |
| URL scheme → `CFBundleURLTypes`（`CFBundleURLSchemes`、`CFBundleURLName` 默认 `<id> <scheme0>`、`CFBundleTypeRole`） | 公共模型 `UrlProtocols` 有 `Schemes`/`Name`；缺 role | MAC-APP-2 |
| 域名关联（universal links）：需 `embedded.provisionprofile` 夹带与 `associated-domains` entitlement | 无 | 实现依赖签名与 provisioning profile，归入 MAC-APP-3 之后的外部/专家边界；普通 deep link 只做 scheme |
| `NSAppTransportSecurity` 例外域（`exceptionDomain` → `NSExceptionAllowsInsecureHTTPLoads`+`NSIncludesSubdomains`） | 无 | MAC-APP-2；默认关闭，显式配置才放宽 ATS |
| 调用方自备 Info.plist（文件或内联 plist）与生成键浅合并、后写覆盖 | 无 | MAC-APP-2；**有意收紧**：身份相关键（`CFBundleIdentifier`/`CFBundleExecutable`/`CFBundleShortVersionString`/`CFBundleVersion`/`CFBundlePackageType`）合并后回读校验，与配置不一致即拒绝，参照 MSI 专家模式身份回读思路 |
| 代码签名：`signingIdentity`（默认钥匙串）/`"-"` ad-hoc；`APPLE_CERTIFICATE`+`APPLE_CERTIFICATE_PASSWORD` 环境变量导入临时钥匙串（用完删除）；`hardenedRuntime` 默认开但仅作用于可执行目标；`entitlements` 支持路径或内联 plist；由内向外签名嵌套代码（`MacOS`/`Frameworks`/`Plugins`/`Helpers`/`XPCServices`/`Libraries` 目录约定，`.framework` 取 `Versions/Current`，`.app`/`.xpc` 递归，`.dylib` 与无扩展名文件按可执行处理）；签名前 `xattr -crs` 清理扩展属性 | 仓库已有 Windows Authenticode 抽象（PFX/证书库/外部命令三种提供器）；无 macOS 签名 | MAC-APP-3；提供器形态与秘密处理规则见路线第 3 节；嵌套签名顺序契约与 Apple “inside-out” 要求一致 |
| 公证：`ditto -c -k --keepParent --sequesterRsrc` 打包 zip → 签 zip → `xcrun notarytool submit --output-format json`（默认 `--wait`）→ `xcrun stapler staple`；凭证来自 `APPLE_ID`/`APPLE_PASSWORD`/`APPLE_TEAM_ID` 或 `APPLE_API_KEY`/`APPLE_API_ISSUER`/`APPLE_API_KEY_PATH`（含 `private_keys` 系列约定目录搜索）；缺 `TEAM_ID` 报错，其余凭证缺失仅警告跳过；`skipStapling` 不等待结果不上钉；失败自动拉 `notarytool log` | 无 | MAC-APP-3；**有意收紧**：上游是“签名且有凭证即自动公证”，Bundler 默认不做任何网络上传、公证须显式开启；实现沿用 `notarytool`/`stapler`，宿主下限与上游同等（Xcode 13+/macOS 11.3+，用户已确认无需为更老宿主自带 API 客户端）；真实 Apple 凭证属外部待验收（见 `mac-app-open-items.md`） |
| `--no-sign` 整体跳过签名与公证 | 无 | MAC-APP-1 产物即不签名；MAC-APP-3 起签名走显式配置 |
| `universal-apple-darwin`：上游要求调用方提供已合成 fat binary，bundler 不做 `lipo`；DMG 产物名带 `universal` | RID 校验仅接受 `osx-x64`/`osx-arm64`，无 universal 目标表达 | MAC-APP-2 校验 fat 输入（`lipo -info` 断言）；已确认不加 `osx-universal` 枚举，双产物覆盖（见路线第 3 节） |
| 构建宿主：上游 `.app`/`.dmg` 实质要求 macOS 宿主（`macos` 模块 `#[cfg(target_os = "macos")]` 门控，非 macOS 宿主请求仅警告跳过） | 无 | 已确认：未签名 `.app` 任意宿主可构建，需 Apple 工具的步骤限 macOS（见路线第 2 节第 4 条）；宿主下限与上游对齐 |

## `.dmg` 能力逐项审计（MAC-DMG 范围，本文仅登记不实施）

| Tauri 用户能力或配置 | 说明 | 归属 |
| --- | --- | --- |
| 产物 `<产品名>_<版本>_<arch>.dmg`（`x64`/`aarch64`/`universal`）；请求 DMG 时先自动产出 `.app` | 分发镜像包装已完成的 `.app` | MAC-DMG；本仓库规划器已表达同等依赖（`Dmg` 自动补 `App` 中间产物） |
| 卷内容：`.app` + `/Applications` 拖放链接（`--app-drop-link` 坐标可配）；隐藏 `.app` 扩展名 | 拖放式安装交互的标准形态 | MAC-DMG |
| Finder 布局：`osascript` 驱动窗口尺寸（默认 660×400）、窗口位置、`.app` 图标位置（默认 180,170）、Applications 位置（默认 480,170）、图标大小 128，结果写入 `.DS_Store` | 依赖 Finder GUI 会话；`CI=true` 时自动 `--skip-jenkins` 跳过布局，`TAURI_BUNDLER_DMG_IGNORE_CI` 可覆盖 | MAC-DMG；无 GUI CI 的降级路径需保留等价开关 |
| 卷图标 `.VolumeIcon.icns`+`SetFile -c icnC`；窗口背景图 `png`/`jpg`/`gif` | 品牌资源 | MAC-DMG |
| EULA：许可文件经 `hdiutil udifrez` 注入 SLA 资源，挂载时弹“同意/不同意” | 公共模型 `LicenseFile` 可复用 | MAC-DMG |
| 流程：`hdiutil create -srcfolder`(UDRW)→resize→读写挂载→osascript 布局→detach（EBUSY 最多 3 次指数退避）→`convert` 为 `UDZO` 只读压缩 | 脚本另支持 `UDBZ`/`ULFO`/`ULMO` 与 `bless`/`internet-enable` 遗留项，上游未暴露 | MAC-DMG；遗留开关不复制 |
| DMG 级 `codesign`（identity `-` 跳过）；**不对 DMG 本身公证**，`.app` 内 stapled ticket 随包 | 用户可观察差异：下载后 Gatekeeper 对外层容器的核验与离线验票 | MAC-DMG 待决项：评估是否对 DMG 本体 `notarytool submit`+`stapler staple`，决策记录进 MAC-DMG 路线 |

## 上游不存在或与本项目边界的关系

- 上游 macOS 没有 `.pkg` 输出（`PackageType` 仅 `MacOsBundle`/`IosBundle`/`Dmg`/`Updater` + 三种 Linux/两种 Windows）。PKG 取舍由 [`docs/mac-format-decision.md`](mac-format-decision.md) 记录，与本审计无关字段不强行映射。
- `.app` 无“安装器事务、卸载器、安装范围（per-user/per-machine）、升级协议”概念：macOS 惯例是拖放拷贝与整体删除，升级由用户替换或应用自更新完成。Bundler 不在 `.app` 上伪造这些语义；受管安装语义归 MAC-PKG 候选。
- 上游 `providerShortName` 在本快照是无人消费的死配置（旧 `altool` 时代遗留），不采纳。
- 上游把 `MACOSX_DEPLOYMENT_TARGET` 环境变量与 `LSMinimumSystemVersion` 联动：前者是 Tauri 自身编译链职责；Bundler 不编译调用方代码，只写 plist 键，不设置该环境变量。
- `publisher`/`homepage`/`description`/`longDescription` 等字段在 `.app` 无对应 plist 落点，不强行映射；`InfoPlist.strings`/`.lproj` 本地化名称上游未实现，如未来需要按候选增强另行登记。
- 应用运行时/框架部署边界不变：`frameworks` 只是“把调用方备好的文件拷进包”，Bundler 不负责解析依赖、`install_name_tool` 改写 rpath 或运行时安装。
- 上游凭证均走环境变量；Bundler 沿用“秘密不入库”规则的同时，把凭据接口设计为不 echo、不落库的形式（见路线第 3 节）。

## 与实施路线的关系

MAC-APP 阶段目标、测试与退出条件见 [`docs/mac-app-roadmap.md`](mac-app-roadmap.md)；逐项当前状态见 [`docs/mac-app-capability-matrix.md`](mac-app-capability-matrix.md)；需专用环境/凭证的验收项归 [`docs/mac-app-open-items.md`](mac-app-open-items.md) 与 [`docs/mac-app-manual-testing.md`](mac-app-manual-testing.md)。
本表“选择与阶段”列是拟定计划，不是已完成声明；关键选择在路线文档中标注“待用户确认”。

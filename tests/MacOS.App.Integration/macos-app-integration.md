# macOS .app 集成测试

MAC-APP-1..4 的本机真实验证入口：真实 .NET payload → 打包 → `plutil` 校验 → 直接启动产物 → LaunchServices 注册与唤起 → 文件关联/URL scheme/ATS/plist 合并断言 → inside-out codesign 签名与公证（ad-hoc 可演示，`codesign --verify --deep --strict` 硬断言）→ 删除即卸载。

## 运行

```bash
bash tests/MacOS.App.Integration/Verify.sh
```

## 前置条件

- macOS 宿主（脚本自带 `uname` 检查，非 macOS 直接拒绝）；
- dotnet SDK（打包 `DotNet.Bundler.MacApp`/MSBuild 包供 fixture 消费）；
- `plutil`、`unzip` 可用（macOS 自带）；
- `clang` 可选：存在时用 `libfixture.dylib` 覆盖 `Contents/Frameworks` 通道断言，缺失则跳过该断言；
- `open -W` 尝试 LaunchServices 启动；无 GUI 会话时自动降级为直接执行 `Contents/MacOS/` 二进制（等价于校验 Mach-O 可运行），仅作备注不判失败；LaunchServices 可用时才执行注册/唤起断言，不可用则该组跳过并仅保留 plist 断言；
- `python3` 用于 `plutil -extract ... json` 输出断言（系统自带）；
- `lipo` 用于 Mach-O 架构交叉校验（系统自带）。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.MacApp`/`DotNet.Bundler.MSBuild` 包内容与 `tasks/netstandard2.0/` 后端装载；
- `BundlerFormats=app` 经 MSBuild 产出 `artifacts/osx-arm64/app/<产品名>.app`；
- 目录骨架：`Contents/{MacOS,Resources,Info.plist,PkgInfo}`、`Resources/docs/` 资源、`SharedSupport/` 显式映射、`Frameworks/` 动态库、`*.icns` 图标容器；
- `plutil -lint` 通过 + `plutil -p` 回读全部核心键（含 `BundlerMacApp*` 覆盖值）；
- 主可执行 `+x` 权限、直接执行输出标记串、重建指纹一致（Info.plist SHA-256 不变）、删除即卸载无残留；
- MAC-APP-2：`plutil -extract` 断言 `CFBundleDocumentTypes`/`CFBundleURLTypes`/`UTExportedTypeDeclarations`/`NSAppTransportSecurity` 结构与调用方合并键；
- `lipo -info` 交叉校验后端托管 Mach-O 解析结论；
- `lsregister -f` 注册后 `lsregister -dump` 可见、`open <.hifix 文件>` 与 `open <hifix://>` 真实唤起（应用内落盘 `.launch-marker` 为证）；
- `.app` 拷入 `~/Applications` → 注册 → `open` 启动 → 注销 → 移出的完整用户域投递链路；
- 直接 API 入口（`MacApp.Api.PackageFixture` NuGet 消费 `DotNet.Bundler.MacApp`）同样产出合法 `.app` 并输出 MAC-APP-2 键组。

LaunchServices 链路不可用（无 GUI 会话的宿主）时注册/唤起断言整体跳过，其余断言照常执行。

所有产物写入 `artifacts/macos-app-integration/`（脚本专属身份标记），`~/Applications` 拷贝与 LaunchServices 注册项均随脚本退出清理，不触碰用户机器上任何其他位置。

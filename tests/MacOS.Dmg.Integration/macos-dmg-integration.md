# macOS .dmg 集成测试

MAC-DMG-1 的本机真实验证入口：真实 .NET payload → `BundlerFormats=dmg`（自动产出 `.app` 中间产物）→ `hdiutil attach` 挂载断言卷内容 → `detach` → `hdiutil verify`。

## 运行

```bash
bash tests/MacOS.Dmg.Integration/Verify.sh
```

## 前置条件

- macOS 宿主（脚本自带 `uname` 检查，非 macOS 直接拒绝）；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `hdiutil`/`plutil`/`unzip`/`python3`（macOS 自带）。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.MacDmg`/`DotNet.Bundler.MSBuild` 包内容与 `tasks/netstandard2.0/` 后端装载；
- `BundlerFormats=dmg` 经 MSBuild 产出 `artifacts/osx-arm64/dmg/<产品名>.dmg`，且规划器自动产出 `app/<产品名>.app` 中间产物；
- `hdiutil attach -readonly` 后卷内容：`.app` 存在、`Applications` 为指向 `/Applications` 的符号链接、`.app` 内 Info.plist 有效；
- 挂载卷内的 `.app` 二进制真实启动（stdout 标记）；
- `hdiutil detach` + `hdiutil verify` 校验通过；
- `BundlerMacDmgCompression=Udzo` 变体：`hdiutil imageinfo` 断言 `Format: UDZO` 并可挂载。

产物仅落在 `artifacts/macos-dmg-integration`（脚本用 `.bundler-identity` 标记自建目录，退出时整体清理；已挂载卷强制 detach）。

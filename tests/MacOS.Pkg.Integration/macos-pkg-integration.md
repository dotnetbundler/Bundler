# macOS .pkg 集成测试

MAC-PKG-1..4 的本机真实验证入口：真实 .NET payload → `BundlerFormats=pkg`（自动产出 `.app` 中间产物）→ `pkgutil --expand-full` 断言 payload/PackageInfo → `xar -tf` 结构 → `installer -dominfo` 域名信息；另覆盖 per-user 域免提权真实安装（`~/Applications` 落位+收据断言）、分发包页面、`--scripts` 专家脚本真实执行、覆盖升级、osx-x64 产物、签名拒绝路径。

## 运行

```bash
bash tests/MacOS.Pkg.Integration/Verify.sh
```

## 前置条件

- macOS 宿主（脚本自带 `uname` 检查，非 macOS 直接拒绝）；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `pkgutil`/`xar`/`installer`/`plutil`/`unzip`（macOS 自带）。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.MacPkg` 包产出与 `DotNet.Bundler.MSBuild` 包内 `DotNet.Bundler.MacPkg.dll` 装载；
- `BundlerFormats=pkg` 经 MSBuild 产出 `artifacts/osx-arm64/pkg/<产品名>.pkg`，且规划器自动产出 `app/<产品名>.app` 中间产物；
- `pkgutil --expand-full` 后 payload 树含 `.app` 与 `BundlerPkgPayload` 显式项（`support/helper.txt`）；payload 内 `.app` 的 Info.plist 有效且二进制真实启动；
- `PackageInfo` 断言：identifier 默认取 `BundlerIdentifier`、version 默认取 `Version`、install-location 默认 `/Applications`；
- `xar -tf` 结构断言 `PackageInfo`/`Payload`/`Bom`；
- `installer -dominfo` 报告 `system` 域；
- 覆盖变体（`BundlerTestPkgIdentifier`/`BundlerTestPkgVersion`/`BundlerTestPkgInstallLocation`）逐项回读断言；
- 失败路径：相对 install-location 使 publish 失败且无 `.pkg` 产物。

产物仅落在 `artifacts/macos-pkg-integration`（脚本用 `.bundler-identity` 标记自建目录，退出时整体清理）。

`system` 域真实安装（需管理员授权）与 Installer.app GUI 观感属外部待验收（`docs/mac-pkg-open-items.md` OI-01/OI-05）。

# HelloMacPkg 示例

DotNet.Bundler 的 macOS `.pkg` 演示：`BundlerFormats=pkg` 会先经规划器自动产出 `.app` 中间产物，再用 `pkgbuild` 打包成组件 `.pkg`（payload 含 `.app` + `BundlerPkgPayload` 显式项）。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloMacPkg/HelloMacPkg.csproj -c Release
```

产物：`artifacts/samples/HelloMacPkg/osx-arm64/app/Hello Mac PKG.app`（中间产物）与 `artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg`。

## 演示的能力（全量）

- **identifier 覆盖**（默认=项目 `BundlerIdentifier`）：

  ```bash
  dotnet publish samples/HelloMacPkg/HelloMacPkg.csproj -c Release -p:HelloMacPkgIdentifier=com.example.custom
  ```

- **pkg 版本覆盖**（默认=项目 `Version`）：

  ```bash
  dotnet publish ... -p:HelloMacPkgVersion=2.0.0
  ```

- **安装位置覆盖**（默认 `/Applications`；例如把工具装进 `/usr/local`）：

  ```bash
  dotnet publish ... -p:HelloMacPkgInstallLocation=/usr/local
  ```

- **任意文件树载荷**（`BundlerPkgPayload` 项，`Destination` 元数据为相对 install-location 的路径）：

  ```xml
  <BundlerPkgPayload Include="Assets/readme.txt" Destination="support/readme.txt" />
  ```

- **`.app` 中间产物自动产出**：`BundlerFormats=pkg` 不需要手工先打 app，同一次 publish 同时给出 `app/` 与 `pkg/` 两个产物目录。

## 验证

```bash
pkgutil --expand-full "artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg" /tmp/hellopkg
cat /tmp/hellopkg/PackageInfo        # identifier/version/install-location 回读
ls /tmp/hellopkg/Payload             # Hello Mac PKG.app + support/readme.txt
xar -tf "artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg"
installer -dominfo -pkg "artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg"
```

真实安装（需管理员授权，payload 落入 `/Applications` 并写 `/var/db/receipts` 收据）：

```bash
sudo installer -pkg "artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg" -target /
pkgutil --pkgs | grep hellomacpkg
pkgutil --files com.dotnetbundler.hellomacpkg
sudo pkgutil --forget com.dotnetbundler.hellomacpkg   # 只清收据，文件需自行删除
```

分发包页面（欢迎/许可/结语）、Developer ID Installer 签名与公证属 MAC-PKG-2/3 阶段能力，本示例暂不含。

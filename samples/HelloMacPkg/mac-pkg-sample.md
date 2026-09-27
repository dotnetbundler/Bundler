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

- **分发包（配置任一分发特性即自动升级 `productbuild`）**——标题/欢迎/许可/结语页 + 安装域名：

  ```bash
  dotnet publish samples/HelloMacPkg/HelloMacPkg.csproj -c Release \
      -p:HelloMacPkgTitle="Hello PKG Installer" \
      -p:HelloMacPkgWelcome=true -p:HelloMacPkgConclusion=true -p:HelloMacPkgLicense=true
  xar -tf "artifacts/.../Hello Mac PKG.pkg"   # 含 Distribution + Resources/*
  ```

- **免提权安装域**（`CurrentUserHome`，payload 落到 `~/Applications`，收据写 `~/Library/Receipts`）：

  ```bash
  dotnet publish ... -p:HelloMacPkgDomain=CurrentUserHome
  installer -pkg "artifacts/samples/HelloMacPkg/osx-arm64/pkg/Hello Mac PKG.pkg" \
      -target CurrentUserHomeDirectory -dumplog
  pkgutil --pkgs --volume ~ | grep hellomacpkg
  pkgutil --files com.dotnetbundler.hellomacpkg --volume ~
  pkgutil --forget com.dotnetbundler.hellomacpkg --volume ~
  rm -rf ~/Applications/"Hello Mac PKG.app" ~/Applications/support
  ```

- **安装脚本（专家旋钮）**——`Scripts/` 目录原样交给 pkgbuild 的 scripts 旗标；内含 `postinstall` 会在真实安装时以安装器权限运行（本示例写一行到 `~/.hello-macpkg-postinstall.log`）：

  ```bash
  dotnet publish ... -p:HelloMacPkgScripts=true -p:HelloMacPkgDomain=CurrentUserHome
  installer -pkg "artifacts/.../Hello Mac PKG.pkg" -target CurrentUserHomeDirectory
  cat ~/.hello-macpkg-postinstall.log   # 证明脚本真实执行过
  ```

- **签名与公证**（MAC-PKG-3 能力；需要真实 Developer ID Installer 证书与 Apple 公证凭证，外部条件）：

  ```bash
  dotnet publish ... \
      -p:HelloMacPkgSignIdentity="Developer ID Installer: Your Name (TEAMID)"
  # 或临时证书路径（导入一次性钥匙串，构建后销毁）：
  dotnet publish ... \
      -p:HelloMacPkgSignCertificatePath=/path/cert.p12 \
      -p:HelloMacPkgSignCertificatePassword=***
  # 公证（签名开启后）：
  dotnet publish ... -p:HelloMacPkgNotarize=true -p:HelloMacPkgNotaryProfile=my-profile
  ```

  注意 `.pkg` **没有 ad-hoc 签名**——要么用真实 Installer 证书签名，要么不签；签名是公证的前置条件（公证的正是这个签名包本身）。

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

签名、公证与安装脚本旋钮已在 MAC-PKG-3 接入示例（上节），真实证书/公证属外部凭证待验收（MAC-PKG-OI-02/03）。

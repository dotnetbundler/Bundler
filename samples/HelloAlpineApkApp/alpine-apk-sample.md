# HelloAlpineApkApp 示例

DotNet.Bundler 的 Alpine `.apk` 演示：`BundlerFormats=alpineapk` 由纯托管 `Bundler.AlpineApk` 写入器直接产出 `.apk`，不需要 `apk`/`abuild`，任何构建宿主（Linux/macOS/Windows CI）均可。

## 运行

```bash
dotnet publish samples/HelloAlpineApkApp/HelloAlpineApkApp.csproj -c Release
```

产物：`samples/HelloAlpineApkApp/artifacts/linux-musl-x64/apk/hello-alpine-apk-app-1.0.0-r0.apk` 与同名 `.sha256` 侧车。

默认装载布局：`/usr/lib/hello-alpine-apk-app/`（载荷）+ `/usr/bin/hello-alpine-apk-app`（相对符号链接）+ `docs/readme.txt`（`BundlerResource` 演示项）+ `/etc/hello-alpine-apk-app/defaults.conf`（`BundlerAlpineApkFile` 演示项）+ `.post-install`/`.pre-deinstall`（脚本演示项）。

在 Alpine 宿主或容器上可真实安装：

```bash
docker run --rm -it -v "$PWD/samples/HelloAlpineApkApp/artifacts/linux-musl-x64/apk:/pkg:ro" alpine:latest
apk add --allow-untrusted /pkg/hello-alpine-apk-app-1.0.0-r0.apk
hello-alpine-apk-app            # 经 /usr/bin 链接启动
apk del hello-alpine-apk-app
```

## 演示的能力（全量）

- **默认最小配置**：仅 `BundlerFormats=alpineapk`，包名/版本/架构/release 全部按契约自动推导。

- **包名与 release 覆盖**（默认包名=产品名 kebab-case，`Hello Alpine Apk App` → `hello-alpine-apk-app`；release 默认 0，进 `pkgver=<version>-r<release>` 与文件名）：

  ```bash
  dotnet publish samples/HelloAlpineApkApp/HelloAlpineApkApp.csproj -c Release \
      -p:HelloAlpineApkPackageName=my-tool -p:HelloAlpineApkRelease=3
  ```

- **依赖声明**（分号列表；本示例默认 `libstdc++;libgcc`——musl 自包含 .NET 运行时的真实需求）：

  ```bash
  dotnet publish ... -p:HelloAlpineApkDepends="musl;libssl3"
  ```

- **provides/triggers/license/builddate**：`HelloAlpineApkProvides`/`HelloAlpineApkTriggers`（分号列表，triggers 须为绝对路径）、`HelloAlpineApkLicense`、`HelloAlpineApkBuildDate`（Unix 秒，默认 0 保确定性）。

- **`usr/bin` 链接名覆盖或关闭**：

  ```bash
  dotnet publish ... -p:HelloAlpineApkBinLink=my-tool-cli   # 或 none 关闭
  ```

- **安装脚本**：六段 `.pre-install`/`.post-install`/`.pre-deinstall`/`.post-deinstall`/`.pre-upgrade`/`.post-upgrade`，整文件注入控制段（0755，须非空 + LF）。

- **任意绝对路径映射**：`@(BundlerAlpineApkFile)` `Destination` 元数据（绝对路径含文件名，拒相对/`..`/`.`/尾斜杠）。

- **RSA 签名**（APK-3）：`HelloAlpineApkSigningKeyFile` 指向 PEM RSA 私钥即前置 `.SIGN.RSA.<密钥文件名>.rsa.pub` 签名段（PKCS#1 v1.5 RSA+SHA1 签在控制 gzip 流上，纯托管 BouncyCastle）；`HelloAlpineApkSigningKeyPassphrase` 可选配加密私钥。公钥文件按 `<私钥文件名>.rsa.pub` 命名落目标机 `/etc/apk/keys/` 后 `apk add` 免 `--allow-untrusted`。

  ```bash
  openssl genrsa -out /tmp/sample.rsa 2048
  openssl rsa -in /tmp/sample.rsa -pubout -out /tmp/sample.rsa.rsa.pub
  dotnet publish ... -p:HelloAlpineApkSigningKeyFile=/tmp/sample.rsa
  ```

- **aarch64 交叉产出**：`-r linux-musl-arm64` 产出 `arch = aarch64` 的包；本机为 x86_64 时安装/运行需仿真或真机（见 `docs/alpine-apk-open-items.md`）。

## 确定性

相同输入与条件下产物逐字节确定（mtime=0、pax atime/ctime=0、条目排序、RSA-PKCS1 确定性签名）；`AlpineApkIntegrationTests` 确定性腿连产三次核对 sha256。

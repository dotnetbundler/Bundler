# HelloRpmApp 示例

DotNet.Bundler 的 Linux `.rpm` 演示：`BundlerFormats=rpm` 由纯托管 `Bundler.Rpm` 写入器直接产出 `.rpm`，不需要 `rpmbuild`，任何构建宿主（Linux/macOS/Windows CI）均可。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloRpmApp/HelloRpmApp.csproj -c Release
```

产物：`samples/HelloRpmApp/artifacts/linux-x64/rpm/hello-rpm-app-1.0.0-1.x86_64.rpm` 与同名 `.sha256` 侧车。

默认装载布局：`/usr/lib/hello-rpm-app/`（载荷，目录条目显式登记进包）+ `/usr/bin/hello-rpm-app`（相对符号链接 `../lib/hello-rpm-app/HelloRpmApp`）+ `docs/readme.txt`（`BundlerResource` 演示项）。

在 Fedora/RHEL 系宿主上可真实安装：

```bash
sudo rpm -i samples/HelloRpmApp/artifacts/linux-x64/rpm/hello-rpm-app-1.0.0-1.x86_64.rpm
hello-rpm-app            # 经 /usr/bin 链接启动
sudo rpm -e hello-rpm-app
```

查看元数据无需安装：`rpm -qip <pkg.rpm>`、`rpm -qpl <pkg.rpm>`（清单）、`rpm -qp --provides/--requires <pkg.rpm>`。

## 演示的能力（全量）

- **默认最小配置**：仅 `BundlerFormats=rpm`，包名/版本/release/架构/vendor 全部按契约自动推导。

- **包名覆盖**（默认=产品名 kebab-case，`Hello Rpm App` → `hello-rpm-app`）：

  ```bash
  dotnet publish samples/HelloRpmApp/HelloRpmApp.csproj -c Release -p:HelloRpmPackageName=my-tool
  ```

- **rpm Version 覆盖**（rpm `Version` 禁含 `-`，合法字符 `[0-9A-Za-z.+_]`；跳过 SemVer 映射直接生效）：

  ```bash
  dotnet publish ... -p:HelloRpmVersion=2.0.0 -p:HelloRpmRelease=3
  ```

- **SemVer→rpm 自动映射**（`Version=2.0.0-beta.1` → `Version: 2.0.0` + `Release: 0.1.beta.1`，Fedora 惯例 `0.<n>.<label>` 使预发布先于正式版排序；`+build` 元数据附加进 Release）：

  ```bash
  dotnet publish ... -p:Version=2.0.0-beta.1
  dotnet publish ... -p:Version=2.0.0+build42      # Release=1+build42
  ```

- **epoch 单独旋钮**（`Version=1.0.0` + epoch `2` → `2:1.0.0-1`；epoch 写入 EPOCH tag，不进文件名）：

  ```bash
  dotnet publish ... -p:HelloRpmEpoch=2
  ```

- **架构覆盖**（默认按 RID：linux-x64→x86_64、linux-arm64→aarch64；可写任意 rpm 架构名，如 `noarch`）：

  ```bash
  dotnet publish ... -p:HelloRpmArchitecture=noarch    # 文件名与 ARCH tag 均用 noarch
  ```

- **Vendor 覆盖**（默认 `BundlerPublisher`，缺省时回退 `BundlerIdentifier`）：

  ```bash
  dotnet publish ... -p:HelloRpmVendor="Jane Doe <jane@example.com>"
  ```

- **安装根覆盖**（默认 `/usr/lib/<包名>`；非 `/usr` 下时 `usr/bin` 链接目标自动转绝对路径）：

  ```bash
  dotnet publish ... -p:HelloRpmInstallRoot=/opt/my-tool
  ```

- **`usr/bin` 链接覆盖或关闭**（默认=包名；自定义名或 `none` 关闭）：

  ```bash
  dotnet publish ... -p:HelloRpmBinLink=my-cli        # 自定义链接名
  dotnet publish ... -p:HelloRpmBinLink=none          # 不产出 usr/bin 链接
  ```

- **`BundlerResource` 任意资源**（`TargetPath` 为 install-root 内相对路径）：`Assets/readme.txt` → `/usr/lib/hello-rpm-app/docs/readme.txt`。

- **`deb;rpm` 同次 publish 扇出**（`BundlerFormats` 接受分号分隔多格式，各格式按自身契约并行产出）：

  ```bash
  dotnet publish samples/HelloRpmApp/HelloRpmApp.csproj -c Release -p:BundlerFormats="deb%3Brpm"
  ```

  注意：MSBuild `-p:` 传含分号的属性须用 `%3B` 转义。

## 已知边界（LINUX-RPM-1 未做）

关系字段（Requires/Provides 等专家声明）、`.desktop`/图标/metainfo、维护者脚本与 systemd unit、`%config` 标记、非 gzip 压缩、GPG 签名、rpmlint 硬基线——分别属 LINUX-RPM-2/3/4 或已登记边界；详见 `docs/linux-rpm-roadmap.md`。

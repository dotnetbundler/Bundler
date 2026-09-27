# HelloDebApp 示例

DotNet.Bundler 的 Linux `.deb` 演示：`BundlerFormats=deb` 由纯托管 `Bundler.Deb` 写入器直接产出 `.deb`，不需要 `dpkg-deb`，任何构建宿主（Linux/macOS/Windows CI）均可。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloDebApp/HelloDebApp.csproj -c Release
```

产物：`samples/HelloDebApp/artifacts/linux-x64/deb/hello-deb-app_1.0.0-1_amd64.deb` 与同名 `.sha256` 侧车。

默认装载布局：`/usr/lib/hello-deb-app/`（载荷）+ `/usr/bin/hello-deb-app`（相对符号链接）+ `docs/readme.txt`（`BundlerResource` 演示项）。

在 Debian/Ubuntu 系宿主上可真实安装：

```bash
sudo dpkg -i samples/HelloDebApp/artifacts/linux-x64/deb/hello-deb-app_1.0.0-1_amd64.deb
hello-deb-app            # 经 /usr/bin 链接启动
sudo dpkg -r hello-deb-app
```

## 演示的能力（全量）

- **默认最小配置**：仅 `BundlerFormats=deb`，包名/版本/架构/维护者全部按契约自动推导。

- **包名覆盖**（默认=产品名 kebab-case，`Hello Deb App` → `hello-deb-app`）：

  ```bash
  dotnet publish samples/HelloDebApp/HelloDebApp.csproj -c Release -p:HelloDebPackageName=my-tool
  ```

- **完整 Debian 版本覆盖**（跳过 SemVer 映射，`epoch:upstream-revision` 原样使用）：

  ```bash
  dotnet publish ... -p:HelloDebVersion="2:3.0.0-2"
  ```

- **SemVer→Debian 自动映射 + 修订号**（`Version=2.0.0-beta.1` → `2.0.0~beta.1-<revision>`，`~` 使预发布先于正式版排序；默认修订号 1）：

  ```bash
  dotnet publish ... -p:Version=2.0.0-beta.1 -p:HelloDebRevision=4
  ```

- **epoch 单独旋钮**（`Version=1.0.0` + epoch `2` → `2:1.0.0-1`，文件名不含 epoch）：

  ```bash
  dotnet publish ... -p:HelloDebEpoch=2
  ```

- **架构覆盖**（默认按 RID：linux-x64→amd64、linux-arm64→arm64；可写任意 Debian 架构名）：

  ```bash
  dotnet publish ... -p:HelloDebArchitecture=armhf        # 文件名与 control 均用 armhf
  ```

- **维护者覆盖**（默认 `BundlerPublisher`，缺省时回退 `BundlerIdentifier`）：

  ```bash
  dotnet publish ... -p:HelloDebMaintainer="Jane Doe <jane@example.com>"
  ```

- **安装根覆盖**（默认 `/usr/lib/<包名>`；非 `/usr` 下时 `usr/bin` 链接目标自动转绝对路径）：

  ```bash
  dotnet publish ... -p:HelloDebInstallRoot=/opt/my-tool
  ```

- **`usr/bin` 链接覆盖或关闭**（默认=包名；自定义名或 `none` 关闭）：

  ```bash
  dotnet publish ... -p:HelloDebBinLink=my-cli        # 自定义链接名
  dotnet publish ... -p:HelloDebBinLink=none          # 不产出 usr/bin 链接
  ```

- **`BundlerResource` 任意资源**（`TargetPath` 为 install-root 内相对路径）：`Assets/readme.txt` → `/usr/lib/hello-deb-app/docs/readme.txt`。

## 已知边界（LINUX-DEB-1 未做）

`Depends`/desktop 集成/维护者脚本/systemd/conffiles 属后续阶段；详见 `docs/linux-deb-roadmap.md`。

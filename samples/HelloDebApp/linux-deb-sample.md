# HelloDebApp 示例

DotNet.Bundler 的 Linux `.deb` 演示：`BundlerFormats=deb` 由纯托管 `Bundler.Deb` 写入器直接产出 `.deb`，不需要 `dpkg-deb`，任何构建宿主（Linux/macOS/Windows CI）均可。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloDebApp/HelloDebApp.csproj -c Release
```

产物：`samples/HelloDebApp/artifacts/linux-x64/deb/hello-deb-app_1.0.0-1_amd64.deb` 与同名 `.sha256` 侧车。

默认装载布局：`/usr/lib/hello-deb-app/`（载荷）+ `/usr/bin/hello-deb-app`（相对符号链接）+ `docs/readme.txt`（`BundlerResource` 演示项）+ `/usr/share/applications/hello-deb-app.desktop`（生成的桌面入口）+ `/usr/share/icons/hicolor/48x48{,@2}/apps/hello-deb-app.png`（图标）+ `/usr/share/metainfo/hello-deb-app.metainfo.xml` + `/usr/share/doc/hello-deb-app/{copyright,changelog.gz}` + `/etc/hello-deb-app/defaults.conf`（`BundlerDebFile` 演示项）。

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

- **关系字段**（`Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces`，分号分隔子句、原样透传进 control）：

  ```bash
  dotnet publish ... -p:HelloDebDepends="libc6 (>= 2.35);libssl3" -p:HelloDebRecommends=ca-certificates \
      -p:HelloDebProvides=virtual-hello -p:HelloDebConflicts=old-hello -p:HelloDebReplaces=old-hello
  ```

- **Section/Priority 覆盖**（Section 默认省略，Priority 默认 `optional`）：

  ```bash
  dotnet publish ... -p:HelloDebSection=utils -p:HelloDebPriority=extra
  ```

- **`.desktop` 自动生成**（默认始终生成 `usr/share/applications/<包名>.desktop`）：本示例默认装 `Utility;Development` 分类、`.hellodeb` 文件关联与 `hellodeb:` URL 协议 → `MimeType=application/x-hellodeb;x-scheme-handler/hellodeb;`、`Exec=hello-deb-app %u`（有协议时取 `%u` 占位符）。装后可 `desktop-file-validate /usr/share/applications/hello-deb-app.desktop` 验证。

- **图标入 hicolor**（PNG 尺寸自动探测；文件名 `@2x` 后缀 → `<WxH>@2` 槽位）：`icon48.png` → `48x48/apps/`、`icon@2x.png`（96×96）→ `48x48@2/apps/`；`.desktop` 的 `Icon=` 自动指向包名。

- **AppStream metainfo**（可选，默认示例已装；`HelloDebMetainfoFile` 指向自定义文件）：

  ```bash
  dotnet publish ... -p:HelloDebMetainfoFile=Assets/app.metainfo.xml
  ```

- **`copyright` 与 `changelog.gz`**（`BundlerLicenseFile` → `usr/share/doc/<包名>/copyright`；`BundlerDebChangelogFile` → `changelog.gz`，自动 gzip）：示例默认已装。

- **`BundlerDebFile` 任意包内路径**（`Destination` 是绝对路径含文件名）：`Assets/defaults.conf` → `/etc/hello-deb-app/defaults.conf`。

- **`BundlerDebDesktopFile` 整文件覆盖**（专家旋钮：完全接管 `.desktop` 内容，跳过自动生成）：

  ```bash
  dotnet publish ... -p:HelloDebDesktopFile=Assets/custom.desktop
  ```

- **维护者脚本**（`preinst`/`postinst`/`prerm`/`postrm`，shebang + LF 必备，入 control 归档 0755）：示例默认装 `postinst`/`prerm`/`postrm`（postinst 建 `/var/lib/hello-deb-app`，postrm 清除）；覆盖：

  ```bash
  dotnet publish ... -p:HelloDebPostinstFile=Assets/postinst.sh -p:HelloDebPreinstFile=path/to/preinst.sh
  ```

- **托管 systemd unit**（`BundlerDebSystemdServiceFile` → `usr/lib/systemd/system/<包名>.service`，0644；postinst 自动合成 `systemctl daemon-reload` 尾段——有自定义 postinst 时与其合并）。装而不启，启用策略归调用方脚本：

  ```bash
  dotnet publish ... -p:HelloDebSystemdServiceFile=Assets/hello-deb-app.service
  ```

- **conffile 语义**（`BundlerDebFile` 落到 `/etc` 下自动登记 conffile；`HelloDebConffiles` 可显式追加其他包内绝对路径）：本示例 `/etc/hello-deb-app/defaults.conf` 即 conffile——`dpkg -r` 保留本地修改，`dpkg -P` 才清除；升级时本地修改默认保留（`-confold`）。

  ```bash
  sudo dpkg -i .../hello-deb-app_1.0.0-1_amd64.deb
  echo "local=1" | sudo tee /etc/hello-deb-app/defaults.conf
  sudo dpkg -r hello-deb-app      # conffile 保留
  sudo dpkg -P hello-deb-app      # 清除
  ```

- **压缩格式**：`BundlerDebCompression` 当前仅 `gzip`——托管写入器在 netstandard2.0 无 xz/zstd 编码器（引第三方依赖属单独裁决），且 zstd 要求安装宿主 dpkg ≥ 1.21.18。

## 已知边界（LINUX-DEB-3 未做）

systemd 真实 enable/start、升级/降级多版本链、xz/zstd 压缩属 `LINUX-DEB-4` 或已登记边界；详见 `docs/linux-deb-roadmap.md`。

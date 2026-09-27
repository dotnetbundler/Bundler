# HelloRpmApp 示例

DotNet.Bundler 的 Linux `.rpm` 演示：`BundlerFormats=rpm` 由纯托管 `Bundler.Rpm` 写入器直接产出 `.rpm`，不需要 `rpmbuild`，任何构建宿主（Linux/macOS/Windows CI）均可。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloRpmApp/HelloRpmApp.csproj -c Release
```

产物：`samples/HelloRpmApp/artifacts/linux-x64/rpm/hello-rpm-app-1.0.0-1.x86_64.rpm` 与同名 `.sha256` 侧车。

默认装载布局：`/usr/lib/hello-rpm-app/`（载荷，目录条目显式登记进包）+ `/usr/bin/hello-rpm-app`（相对符号链接 `../lib/hello-rpm-app/HelloRpmApp`）+ `docs/readme.txt`（`BundlerResource` 演示项）。

RPM-2 起默认再带 freedesktop 与文档件：`/usr/share/applications/hello-rpm-app.desktop`（自动生成，`Exec` 取 `/usr/bin` 链接名，`MimeType` 合并 `BundlerFileAssociation`/`BundlerUrlProtocol`）+ hicolor 图标 `/usr/share/icons/hicolor/{48x48,48x48@2}/apps/hello-rpm-app.png` + `/usr/share/metainfo/hello-rpm-app.metainfo.xml` + `/usr/share/doc/hello-rpm-app/changelog.gz`（%doc 标记）+ `/usr/share/licenses/hello-rpm-app/LICENSE.txt`（%license 标记，`BundlerLicenseFile` 包级旋钮）+ `/etc/hello-rpm-app/defaults.conf`（`BundlerRpmFile` 任意绝对路径映射演示）。

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

- **关系字段透传**（Requires/Provides/Conflicts/Obsoletes/Recommends/Suggests 六族；子句语法 `name` 或 `name <op> evr`，op ∈ `< <= = >= >`；分号列表）：

  ```bash
  dotnet publish ... -p:HelloRpmRequires="libc.so.6%3Blibpng >= 1.6" -p:HelloRpmProvides="hello-plugin = 2.0"
  ```

- **License/Group/Url 覆盖**（License 惯例写 SPDX 表达式，默认 `Unspecified`；Group 默认 `Unspecified`，Url 默认 `BundlerHomepage`）：

  ```bash
  dotnet publish ... -p:HelloRpmLicense="MIT OR Apache-2.0" -p:HelloRpmGroup="Applications/Engineering" -p:HelloRpmUrl="https://example.com"
  ```

- **`.desktop` 覆盖**（`BundlerRpmDesktopFile` 整文件替换生成产物，`Assets/custom.desktop` 演示）：

  ```bash
  dotnet publish ... -p:HelloRpmDesktopFile="$PWD/samples/HelloRpmApp/Assets/custom.desktop"
  ```

- **`BundlerRpmFile` 任意绝对路径映射**（`Destination` 须为带文件名的绝对路径；包自有叶子目录自动占有，共享系统目录不占有）：

  ```xml
  <BundlerRpmFile Include="Assets/defaults.conf" Destination="/etc/hello-rpm-app/defaults.conf" />
  ```

- **`deb;rpm` 同次 publish 扇出**（`BundlerFormats` 接受分号分隔多格式，各格式按自身契约并行产出）：

  ```bash
  dotnet publish samples/HelloRpmApp/HelloRpmApp.csproj -c Release -p:BundlerFormats="deb%3Brpm"
  ```

  注意：MSBuild `-p:` 传含分号的属性须用 `%3B` 转义（关系字段列表同理）。

- **维护者 scriptlet**（`%pre`/`%post`/`%preun`/`%postun` 专家旋钮，文件注入；LF 行尾强制，`#!` 首行剥离为解释器 tag，缺省 `/bin/sh`，`*Program` 属性可显式覆盖）：

  ```bash
  dotnet publish ... -p:HelloRpmPostInstallFile="$PWD/samples/HelloRpmApp/Assets/post-install.sh"
  ```

- **托管 systemd unit**（`BundlerRpmSystemdServiceFile` 落 `/usr/lib/systemd/system/<包名>.service`；`%post`/`%postun` 自动合成 `daemon-reload` 尾段，装而不启）：

  ```bash
  dotnet publish ... -p:HelloRpmSystemdServiceFile="$PWD/samples/HelloRpmApp/Assets/hello-rpm-app.service"
  ```

- **`%config(noreplace)`**（`/etc` 下的 `BundlerRpmFile` 目标自动标记；`BundlerRpmConfigFiles` 显式列表可把任意载荷路径标为 `%config(noreplace)`——装后本地修改经 `rpm -e` 保留为 `.rpmsave`、`rpm -U` 升级原地保留）：

  ```bash
  dotnet publish ... -p:HelloRpmConfigFiles="/etc/hello-rpm-app/defaults.conf"
  ```

- **压缩枚举**：仅 `gzip`；xz/zstd 登记拒绝（netstandard2.0 无托管编码器）。

## 已知边界（LINUX-RPM-3 未做）

GPG 包签名、rpmlint 硬基线、rockylinux/opensuse 容器矩阵、arm64 真机安装——属 LINUX-RPM-4/后续独立阶段或已登记边界；详见 `docs/linux-rpm-roadmap.md` 与 `linux-rpm-open-items.md`。

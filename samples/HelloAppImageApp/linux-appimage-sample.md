# HelloAppImageApp 示例

DotNet.Bundler 的 Linux `.AppImage` 演示：`BundlerFormats=appimage` 由 `Bundler.AppImage` 后端组装 AppDir 并驱动内嵌的 `appimagetool` 产出单文件 `.AppImage`。
仅 Linux 构建宿主可用；无需 FUSE（内部以 `APPIMAGE_EXTRACT_AND_RUN=1` 调用工具）；x86_64 宿主可交叉产出 aarch64 产物（`runtime-aarch64` 已内嵌）。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release
```

产物：`samples/HelloAppImageApp/artifacts/linux-x64/appimage/hello-appimage-app_1.0.0_amd64.AppImage` 与同名 `.sha256` 侧车。

默认 AppDir 布局：`usr/lib/hello-appimage-app/`（载荷）+ `usr/bin/hello-appimage-app`（相对符号链接 `../lib/hello-appimage-app/HelloAppImageApp`）+ `usr/share/applications/hello-appimage-app.desktop`（自动生成，`Icon=` 恒在）+ hicolor 图标 + `usr/share/metainfo/hello-appimage-app.metainfo.xml` + 根目录 `hello-appimage-app.desktop`（指向 staged desktop 的符号链接）、`hello-appimage-app.png`、`.DirIcon`、脚本式 `AppRun`。

直接运行无需安装（runtime 支持无 FUSE 直跑）：

```bash
./hello-appimage-app_1.0.0_amd64.AppImage                  # 或 --appimage-extract-and-run
./hello-appimage-app_1.0.0_amd64.AppImage --appimage-extract   # 解出 squashfs-root/ 检查内容
```

## 演示的能力（全量）

- **默认最小配置**：仅 `BundlerFormats=appimage`，包名/版本/架构/图标全部按契约自动推导（无图标时回落内置默认 PNG）。

- **包名覆盖**（默认=产品名 kebab-case，`Hello AppImage App` → `hello-appimage-app`）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release -p:HelloAppImagePackageName=my-tool
  ```

- **版本覆盖**（原样进入文件名，AppImage 无 EVR 规则）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release -p:HelloAppImageVersion=2.0.0-beta.1
  ```

- **架构覆盖与交叉产出**（`-r linux-arm64` 或显式 `HelloAppImageArchitecture=aarch64`；aarch64 产物用内嵌 `runtime-aarch64`，x86_64 宿主可产，宿主不可执行）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release -r linux-arm64
  ```

- **安装根与 bin 链接覆盖**（`install-root` 是 AppDir 相对路径；`bin-link=none` 关闭链接，`AppRun` 直接 exec 主程序）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release \
      -p:HelloAppImageInstallRoot=opt/myapp -p:HelloAppImageBinLink=my-runner
  ```

- **图标与桌面文件覆盖**（`icon-file` 拷贝为根 `<包名>.png`+`.DirIcon`；`desktop-file` 整文件替换生成件——注意 appimagetool 要求其中含 `Categories=`）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release \
      -p:HelloAppImageIconFile=$PWD/samples/HelloAppImageApp/Assets/icon48.png \
      -p:HelloAppImageDesktopFile=$PWD/samples/HelloAppImageApp/Assets/custom.desktop
  ```

- **任意 AppDir 相对路径映射**（`@(BundlerAppImageFile)` 带 `Destination` 元数据，落到 AppDir 内相对路径；拒绝绝对路径/`..`/与生成件碰撞）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release -p:HelloAppImageFiles=1
  # 解包后可见 opt/hello-appimage-app/defaults.conf
  ```

- **Categories/metainfo 覆盖**（命令行分号用 `%3B` 转义）：

  ```bash
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release \
      -p:HelloAppImageCategories='Utility%3BDevelopment' -p:HelloAppImageMetainfoFile= Assets/app.metainfo.xml 已默认装载
  ```

- **GPG 签名**（SIGN-2 能力）：供 OpenPGP 私钥文件才签——走内嵌 `appimagetool --sign`，私钥经隔离 `GNUPGHOME` 导入、口令经 `APPIMAGETOOL_SIGN_PASSPHRASE` 注入不上命令行；签名嵌 `.sha256_sig`/`.sig_key` ELF 段，`gpgv` 可验，无密钥产物与未签名现状一致：

  ```bash
  gpg --batch --quick-gen-key "Hello AppImage Test <test@local>" rsa4096
  gpg --export-secret-keys --armor "test@local" > /tmp/hello-appimage.rsa
  dotnet publish samples/HelloAppImageApp/HelloAppImageApp.csproj -c Release \
      -p:HelloAppImageSigningKeyFile=/tmp/hello-appimage.rsa
  ```

  可选 `HelloAppImageSigningKeyPassphrase` 配加密私钥；测试密钥即弃不入库。
  验签口径：取 `.sha256_sig` 段内分离签名，对"两段置零"镜像的 sha256 裸 hex 做 `gpgv` 断言（`tests/Linux.AppImage.Integration/Verify.sh` 已封此链路）。

## 说明

- 压缩固定 zstd：所钉 appimagetool 构建的 mksquashfs 仅支持 zstd（决策 10 按实测收口）。
- 脚本/systemd/依赖字段不适用：AppImage 无包管理器（见 `docs/linux-appimage-roadmap.md` 语义对应表）。
- 集成验证见 `tests/Linux.AppImage.Integration/linux-appimage-integration.md`。

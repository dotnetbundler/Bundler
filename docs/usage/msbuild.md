# MSBuild 使用指南

`DotNet.Bundler` 元包引入 `DotNet.Bundler.MSBuild`：`dotnet publish` 产物自动打包，或 `-t:BundlerBundle` 显式驱动。
本文件覆盖接入、推导规则、全部共享旋钮与项；各格式专属旋钮见对应后端使用文档。

## 1. 接入

```xml
<PackageReference Include="DotNet.Bundler" Version="0.1.0-alpha.102" PrivateAssets="all" />
```

包的 `buildTransitive` 资产随引用自动接入，不需要手写 `Import`。
MSBuild Task 及其直接加载的程序集提供 `netstandard2.0` 资产，`MSBuild.exe`（.NET Framework）与 `dotnet msbuild` 双宿主可加载。

## 2. 两条打包路径

### 2.1 自动路（推荐）：Publish 钩子

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <BundlerEnabled>true</BundlerEnabled>
</PropertyGroup>
```

```powershell
dotnet publish -c Release
```

`BundlerEnabled=true` 时 `Publish` 之后自动打包；普通 `Build` 不产安装包。
`BundlerEnabled` 只管这条路；显式 `-t:BundlerBundle` 不检查它。

### 2.2 显式路：`-t:BundlerBundle` 三模式

```powershell
# 内层发布+打包：按 BundlerTarget 查 canonical RID 发布到 obj\BundlerPublish\ 再打包
dotnet msbuild -t:BundlerBundle -p:BundlerTarget=windows-x86_64 -p:Configuration=Release
# 只打包不发布：拿已有发布目录直接打
dotnet msbuild -t:BundlerBundle -p:BundlerInputDir=bin\Release\net10.0\win-x64\publish -p:BundlerTarget=windows-x86_64
# canonical 表外 RID：显式指定内层发布 RID
dotnet msbuild -t:BundlerBundle -p:BundlerTarget=linux-x86_64 -p:BundlerPublishRid=ubuntu.24.04-x64
```

## 3. 目标推导

`BundlerTarget` 解析顺序：显式值 > `$(RuntimeIdentifier)` 查 canonical 表 > SDK 宿主 RID 查表。
RID 推导在 eval 期完成，csproj 内按 `$(BundlerTarget)` 条件化的配置照常命中；显式 `-p:BundlerTarget=` 恒覆盖（含显式置空时回落 canonical）。
`macos-universal` 无单一 RID，走 `BundlerUniversalTargets`（见 §3.2）。

### 3.1 canonical RID 表

| `BundlerTarget` | 发布 RID |
| --- | --- |
| `windows-x86_64` / `windows-i686` / `windows-aarch64` | `win-x64` / `win-x86` / `win-arm64` |
| `macos-x86_64` / `macos-aarch64` | `osx-x64` / `osx-arm64` |
| `linux-x86_64` / `linux-i686` / `linux-aarch64` | `linux-x64` / `linux-x86` / `linux-arm64` |
| `linux-musl-x86_64` / `linux-musl-aarch64` | `linux-musl-x64` / `linux-musl-arm64` |

target 语法、架构词与产物命名契约见 [`../targets.md`](../design/targets.md)。

### 3.2 格式集与 universal

- `BundlerFormats` 缺省按目标 OS 推导：`windows-*→nsis`、`macos-*→dmg`、`linux-*→deb`、`linux-musl-*→alpineapk`。
  分号分隔多值扇出：`nsis;msi;zip`（命令行 `-p:` 需 `%3B` 转义分号）。
  可用名：`nsis` `msi` `app` `dmg` `pkg` `deb` `rpm` `appimage` `zip` `targz` `alpineapk`。
- `BundlerUniversalTargets`（macOS universal）：复数 .NET RID（`osx-x64;osx-arm64`，命令行 `%3B`）逐 RID 内层 publish 后托管合并成 universal 目录，再按 `macos-universal` 打包。要求 framework-dependent 非单文件：`SelfContained`/`PublishAot`/`PublishSingleFile` 任一开即报错；与 `BundlerInputDir` 互斥（同设即 Error）。
- 多格式扇出由 Core 管线编排，混排 `zip`/`targz` 与安装器格式同样支持。

## 4. 共享属性（全格式通用）

| 属性 | 必填 | 默认值与说明 |
| --- | --- | --- |
| `BundlerEnabled` | 自动路必填 | `false` |
| `BundlerTarget` | 条件必填 | 推导见 §3 |
| `BundlerInputDir` | 否 | `-t:BundlerBundle` 下非空即只打包不发布 |
| `BundlerPublishRid` | 否 | canonical 表；表外 RID 显式覆盖 |
| `BundlerFormats` | 否 | 按目标 OS 推导（§3.2） |
| `BundlerUniversalTargets` | 否 | 无；macOS universal 专用 |
| `BundlerIdentifier` | 否 | 缺省合成 `dev.bundler.<归一化 AssemblyName>`（小写、非字母数字→`-`、空→`app`）并打警告；正式发布应显式指定 |
| `BundlerProductName` | 否 | `$(AssemblyName)` |
| `BundlerVersion` | 否 | `$(Version)`；`$(Version)` 亦空时回落 `0.1.0` 并打警告 |
| `BundlerMainExecutable` | 否 | Windows 目标 `$(TargetName).exe`，macOS/Linux/universal `$(TargetName)`（无扩展名） |
| `BundlerPublisher` | 否 | `$(Company)` |
| `BundlerDescription` | 否 | `$(Description)` |
| `BundlerHomepage` | 否 | `$(PackageProjectUrl)` |
| `BundlerCopyright` | 否 | `$(Copyright)` |
| `BundlerLicenseFile` | 否 | 无；NSIS 支持 `.txt`/`.rtf`，MSI 交互许可页仅 `.rtf`，DMG 注入 SLA 挂载面板，`.pkg` 作分发包许可页；独立 `.app` 后端按契约硬拒许可文件 |
| `BundlerOutputPath` | 否 | `$(MSBuildProjectDirectory)\artifacts` |
| `BundlerOutputLayout` | 否 | `flat`；`byFormat` 按 `artifacts\<format>\` 分格 |
| `BundlerToolCachePath` | 否 | `LocalApplicationData\DotNetBundler\tools`（非 Windows 为 `~/.local/share` 等；空值回落临时目录）；内容寻址共享缓存——对 AppImage 不生效（其工具缓存走 `$XDG_CACHE_HOME`/`~/.cache`，经 `AppImageBundlerOptions.ToolsetCacheDirectory` 覆盖） |
| `BundlingDotnetExe` | 否 | 内层 `dotnet publish` 的 dotnet 可执行覆盖（逃生舱） |
| `BundlerInnerPublishArgs` | 否 | 追加给内层 publish 的原样参数串（逃生舱） |

## 5. 共享项（全格式通用）

```xml
<ItemGroup>
  <BundlerIcon Include="Assets\app.ico" />
  <BundlerResource Include="Assets\licenses\**\*">
    <Destination>licenses\%(RecursiveDir)%(Filename)%(Extension)</Destination>
  </BundlerResource>
  <BundlerFileAssociation Include="mydoc" Name="我的应用文档"
      Description="我的应用文档文件" MimeType="application/x-myapp-document" />
  <BundlerUrlProtocol Include="myapp" Name="我的应用链接" />
</ItemGroup>
```

- `BundlerIcon`：图标集，按扩展名分发——Windows `.ico`（第一个作 NSIS 安装器/卸载器回退）、Linux PNG（hicolor 集）、macOS `.png`/`.icns`/`.car`/`.icon`（见 [`mac-app.md`](formats/mac-app.md) §3.2）。
- `BundlerResource`：任意资源随载荷进安装根；`Destination` 省略时用源文件名；目标路径冲突或逃逸安装目录打包前报错。
- `BundlerFileAssociation`：`Include`=扩展名（可带前导点，不区分大小写），`Name`/`Description`/`MimeType` 元数据。
  Windows 注册 ProgID/"打开方式"/默认应用 Capabilities（不抢用户默认），Linux 生成 freedesktop MimeType，macOS 落 `CFBundleDocumentTypes`+UTI。
- `BundlerUrlProtocol`：自定义 URL scheme，安装后 `myapp:...` 直达；卸载只删仍指向本次安装目录的命令，不破坏后来者。

## 6. 格式专属旋钮

每格式的完整旋钮表、对应 `bundler.json` 键与 API 成员在各后端使用文档：

| 格式 | MSBuild 前缀 | 载荷项 | 使用文档 |
| --- | --- | --- | --- |
| NSIS | `BundlerNsis*` | `BundlerNsisLanguageFile` | [`win-nsis.md`](formats/win-nsis.md) |
| MSI | `BundlerWix*` | `BundlerWixLanguageFile`/`…Extension*`/`…ExpertMergeModule` | [`win-msi.md`](formats/win-msi.md) |
| `.app` | `BundlerMacApp*` | `BundlerMacAppFile`/`…FrameworkDirectory`/`BundlerMacDocumentType`/`BundlerMacUrlType` | [`mac-app.md`](formats/mac-app.md) |
| `.dmg` | `BundlerMacDmg*` | —（经内层 `.app`） | [`mac-dmg.md`](formats/mac-dmg.md) |
| `.pkg` | `BundlerMacPkg*` | `BundlerMacPkgFile` | [`mac-pkg.md`](formats/mac-pkg.md) |
| `.deb` | `BundlerDeb*` | `BundlerDebFile` | [`linux-deb.md`](formats/linux-deb.md) |
| `.rpm` | `BundlerRpm*` | `BundlerRpmFile` | [`linux-rpm.md`](formats/linux-rpm.md) |
| `.AppImage` | `BundlerAppImage*` | `BundlerAppImageFile` | [`linux-appimage.md`](formats/linux-appimage.md) |
| `.apk` | `BundlerAlpineApk*` | `BundlerAlpineApkFile` | [`linux-alpine-apk.md`](formats/linux-alpine-apk.md) |
| `.zip`/`.tar.gz` | `BundlerArchive*` | `BundlerArchiveFile` | [`archive.md`](formats/archive.md) |

## 7. Windows Authenticode 签名

作用于 `nsis`/`msi` 产物；内置 PFX/证书存储区与外部命令 provider 二选一。
签名次序：隔离副本中签主 EXE→`BundlerWindowsSigningFile` 列出的文件→Bundler 自带 NSIS 插件→卸载器→最终安装器；不改原始发布目录，任一步失败删除最终产物。

| 属性/项 | 说明 |
| --- | --- |
| `BundlerWindowsSigningPfxFile` | PFX/P12 代码签名证书路径 |
| `BundlerWindowsSigningPfxPasswordEnvironmentVariable` | 存 PFX 密码的环境变量名（密码不写进工程/命令行） |
| `BundlerWindowsSigningCertificateThumbprint` | `My` 存储区证书指纹（与 PFX 二选一） |
| `BundlerWindowsSigningCertificateStoreLocation` | `CurrentUser`（默认）/`LocalMachine` |
| `BundlerWindowsSigningTimestampUrl` | RFC 3161 时间戳 URL；正式发布强烈建议配置 |
| `BundlerWindowsSigningCommand` | 外部签名 provider 可执行文件（云 HSM/USB Token）；与 PFX/指纹互斥 |
| `@(BundlerWindowsSigningFile)` | 输入目录内需追签的 DLL/sidecar 相对路径 |
| `@(BundlerWindowsSigningCommandArgument)` | 外部命令独立参数；至少一项含 `{path}`/`%1`，另支持 `{artifactKind}`/`{target}`/`{productName}` 占位符；秘密经环境变量传入 |

```xml
<PropertyGroup>
  <BundlerWindowsSigningPfxFile>$(SigningCertificatePath)</BundlerWindowsSigningPfxFile>
  <BundlerWindowsSigningPfxPasswordEnvironmentVariable>BUNDLER_SIGNING_PASSWORD</BundlerWindowsSigningPfxPasswordEnvironmentVariable>
  <BundlerWindowsSigningTimestampUrl>https://时间戳服务</BundlerWindowsSigningTimestampUrl>
</PropertyGroup>
```

内置签名器走 Windows Authenticode API，启用时构建宿主必须 Windows；外部 provider 可在其支持的宿主运行。
本地自签名端到端验证脚本见 [`win-nsis.md` §4.5](formats/win-nsis.md)；签名决策与裁决见 [`../signing.md`](../design/signing.md)。

## 8. UPDATE 自更新

打包侧开关；能力契约与应用侧接入见 [`../update.md`](../design/update.md)。

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `BundlerUpdateEnabled` | `false` | 总闸 |
| `BundlerUpdateFeedUrl` | 无 | 清单发布位置（http(s)/`file://`/UNC；制品按清单同目录解析） |
| `BundlerUpdateChannel` | `latest` | 通道名，进清单文件名 `bundler-update-feed.<channel>.json` |
| `BundlerUpdateSigningKeyFile` | 无 | ec-p256 JWK 私钥文件（签名强制；`bundler update-keygen` 生成；私钥不入库） |
| `BundlerUpdatePublicKey` | 无 | base64 验签公钥，烙进载荷 `bundler-update.json` |
| `BundlerUpdateNotes` | 无 | 版本说明写入清单 `notes` |

启用后产物附带 `.sig`（ECDSA P-256）与 `.blockmap` 旁车 + feed 清单。
自更新仅 `nsis`/`msi`/`.app`/`zip`/`targz`/`appimage` 支持；`deb`/`rpm`/`apk`/`dmg`/`pkg` 按权威实践不做。

## 9. 产物

- 默认落 `artifacts/`（`BundlerOutputPath` 可改）；`BundlerOutputLayout=byFormat` 时分格到 `artifacts\<format>\`。
- 文件名由 `ArtifactNaming` 统一生成：`<包名>-<版本>-<生态词>-setup.exe`/`.msi`/`.dmg`/`.pkg`/`.AppImage`/`.zip`/`.tar.gz`；`.app` 为目录。
  包管理器三格式走各自生态命名：`<名>_<ver>-<rel>_<arch>.deb`、`<名>-<ver>-<rel>.<arch>.rpm`、`<名>-<ver>-r<rel>.<arch>.apk`。
  "生态词"是各生态命名段而非 canonical target 字面（如 `x64-setup.exe`、`windows-x64.zip`、`linux-x86_64-musl.zip`）；非英文 MSI 带 `-<culture>` 后缀。命名全契约见 [`../targets.md`](../design/targets.md)。
- 预览不构建：`dotnet msbuild -t:BundlerBundle` 前可用 CLI `bundler plan` 看计划，或读构建日志中展开的计划行。

## 10. 排错

- `BundlerIdentifier` 警告：显式设置正式 identifier 消警。
- `-p:` 传分号列表：必须 `%3B` 转义（`-p:BundlerFormats=nsis%3Bmsi`）。
- MSI/mac 宿主门禁：`msi` 仅 Windows 宿主、`dmg`/`pkg` 仅 macOS、`appimage` 仅 Linux；`bundler validate` 对宿主不可产格式静默跳过，`bundle` 在对应构建腿按格式报错 rc=1（MSBuild 任务同构）。
- x86 载荷运行前置：`windows-i686` 包在仅 x64 运行时宿主上启动报 `0x800700C1`，属目标环境前置。
- 工具缓存损坏：删 `%LOCALAPPDATA%\DotNetBundler\tools`（或 `BundlerToolCachePath` 所指）后重构建，跨进程锁内自动安全重建。

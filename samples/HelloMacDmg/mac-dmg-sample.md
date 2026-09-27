# HelloMacDmg 示例

DotNet.Bundler 的 macOS `.dmg` 演示：`BundlerFormats=dmg` 会先经规划器自动产出 `.app` 中间产物，再用 `hdiutil` 打只读压缩镜像（卷内含 `.app` + `/Applications` 拖放符号链接）。

## 运行

```bash
dotnet pack Bundler.slnx -c Release -o artifacts/packages   # 本仓库打包为本地 nupkg
dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release
```

产物：`artifacts/samples/HelloMacDmg/osx-arm64/app/Hello Mac DMG.app`（中间产物）与 `artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello Mac DMG.dmg`。

## 演示的能力（全量）

- **压缩格式三选一**（默认 `Ulmo`，挂载侧需 macOS 10.12+；`Udzo` 兼容 10.1+；`Udbz` 为 bzip2）：

  ```bash
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgCompression=Udzo
  hdiutil imageinfo artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello\ Mac\ DMG.dmg | grep 'Format:'
  ```

- **卷名覆盖**（默认=产品名）：

  ```bash
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgVolumeName="Hello DMG"
  hdiutil attach ... -mountpoint /tmp/hd && ls /tmp/hd   # 卷名即挂载显示名
  ```

- **`.app` 中间产物自动产出**：`BundlerFormats=dmg` 不需要手工先打 app，同一次 publish 同时给出 `app/` 与 `dmg/` 两个产物目录。
- **`.app` 侧全部旋钮同 HelloMacApp**：`HelloMacDmgMinSystemVersion`、`HelloMacDmgSignIdentity=-`（ad-hoc 签名）、`HelloMacDmgHardenedRuntime`、`HelloMacDmgEntitlementsFile` 等均透传到中间 `.app`。
- **窗口背景图与卷图标**（默认演示 `Assets/bg.png` 与 `Assets/volume.icns`，可关闭）：背景图拷入卷内 `.background/` 并被 `.DS_Store` 引用；卷图标落盘 `.VolumeIcon.icns` 并 `SetFile -a C` 触发 `icns` 资源派生。
- **Finder 窗口布局全旋钮**（默认对齐上游：窗口 200,120 + 660×400，`.app` 图标位 180,170，`/Applications` 图标位 480,170，图标大小 128）：
  `HelloMacDmgWindowX/Y/Width/Height`、`HelloMacDmgAppIconX/Y`、`HelloMacDmgApplicationsIconX/Y`、`HelloMacDmgIconSize`。
- **显式跳过布局**：

  ```bash
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgSkipWindowLayout=true
  ```

  无 GUI 会话宿主（无人值守 CI）自动降级为警告并仍产可挂载 `.dmg`；GUI 宿主上才真实写 `.DS_Store`。

- **EULA 许可面板**（默认演示 `Assets/eula.txt`；`hdiutil udifrez` 注入 SLA 资源，挂载时弹"同意/不同意"）：换 `.rtf` 文件或关闭：

  ```bash
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgLicenseFile=path/to/license.rtf
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgLicenseFile=none   # 不注入 SLA
  hdiutil udifderez -xml artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello\ Mac\ DMG.dmg   # 回读 SLA 资源
  ```

- **DMG 本体签名**（`codesign`，与已签名 `.app` 组合）；ad-hoc 直接可演示，真实证书走 identity 或 p12 钥匙串：

  ```bash
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgDmgSignIdentity=-
  codesign -dvvv artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello\ Mac\ DMG.dmg   # Signature=adhoc
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release -p:HelloMacDmgDmgSignIdentity="Developer ID Application: ..."
  dotnet publish samples/HelloMacDmg/HelloMacDmg.csproj -c Release \
      -p:HelloMacDmgDmgSignCertificatePath=cert.p12 -p:HelloMacDmgDmgSignCertificatePassword=...
  ```

## 手动验收

```bash
hdiutil attach artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello\ Mac\ DMG.dmg -nobrowse
# Finder/hdiutil 卷内应见 "Hello Mac DMG.app" 与 Applications 拖放链接、.background/bg.png、.VolumeIcon.icns
hdiutil verify artifacts/samples/HelloMacDmg/osx-arm64/dmg/Hello\ Mac\ DMG.dmg
```

自动化等价入口：`tests/MacOS.Dmg.Integration`（真实 attach/断言/detach/verify + SLA 回读 + ad-hoc 验签）。

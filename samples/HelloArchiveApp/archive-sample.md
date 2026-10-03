# HelloArchiveApp 示例

把 `HelloArchiveApp` publish 成 `hello-archive-app-1.2.3-linux-x64.zip` 与 `.tar.gz`，
演示 `BundlerFormats=zip;targz` 与 `@(BundlerArchiveFile)` 任意映射。

## 构建

```bash
dotnet publish -c Release   # 项目内经 Bundler.ProjectReference.targets 引用仓内打包项目
```

产物在 `artifacts/linux-x64/{zip,targz}/` 下（`BundlerOutputPath` 未设置时的默认布局），
各带 `.sha256` 侧车。

## 验证

```bash
unzip -l hello-archive-app-1.2.3-linux-x64.zip        # 清单含顶层目录与 docs/readme.txt
zipinfo -l hello-archive-app-1.2.3-linux-x64.zip    # 0755/0644/0777 mode 列
tar -tzvf hello-archive-app-1.2.3-linux-x64.tar.gz  # 同上 + 链接目标列
unzip hello-archive-app-1.2.3-linux-x64.zip && ./hello-archive-app-1.2.3-linux-x64/HelloArchiveApp
```

## 旋钮

`BundlerArchivePackageName`/`BundlerArchiveVersion`/`BundlerArchiveName` 覆盖归档名；
`@(BundlerArchiveFile)` 以 `Destination`（归档相对 POSIX 路径）追加任意文件。

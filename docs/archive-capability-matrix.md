# ARCHIVE 能力矩阵

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 外部待验收项的明细在 `docs/archive-open-items.md`。

| 能力 | 状态 | 阶段 | 备注 |
| --- | --- | --- | --- |
| `.zip` 托管写入器（local+central directory、deflate） | 已实现 | ARCHIVE-1 | 自实现（ZipArchive 不支持 unix mode/symlink 写出） |
| `.tar.gz` 托管写入器（ustar+gzip） | 已实现 | ARCHIVE-1 | 复用 Bundler.Deb tar 写法提取共享件 |
| 单顶层目录布局 `<pkg>-<ver>-<rid>/` | 已实现 | ARCHIVE-1 | 归档惯例 |
| 可执行位保留（tar mode + zip unix attrs） | 已实现 | ARCHIVE-1 | `zipinfo -l`/`tar -tvf`/`test -x` 断言 |
| 符号链接保留（tar symlink + zip Info-ZIP 惯例） | 已实现 | ARCHIVE-1 | 解出后 `readlink` 断言 |
| `.sha256` 侧车 | 已实现 | ARCHIVE-1 | 与 deb/rpm/appimage 一致 |
| `BundlerFormats=zip;targz` 多格式扇出 | 已实现 | ARCHIVE-1 | 含与 deb/rpm/appimage 混排 |
| `BundlerArchiveFile` 归档相对映射 | 已实现 | ARCHIVE-1（提前落地） | 逃逸/碰撞拒绝同 AppDir 口径 |
| 归档签名 | 明确拒绝 | — | 无通行惯例；签名诉求属平台格式层 |
| tar.xz/tar.zst/7z | 明确拒绝 | — | 编码器依赖/普及度；需要时另起决策 |
| Windows 宿主产归档 | 已实现 | ARCHIVE-1 | 托管写入器任意宿主可产 |
| 跨 OS 互读实测（Linux 产 → Windows/macOS 解） | 外部待验收 | ARCHIVE-3 | 本机无 Windows/macOS 宿主，OI-01..04 |
| 第三方实现互读（python3 `zipfile`/`tarfile` 读产物） | 已实现 | ARCHIVE-3 | Verify.sh 断言：条目/执行位/symlink |

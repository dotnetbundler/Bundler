# ARCHIVE 能力矩阵

> `已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
> 外部待验收项的明细在 `docs/archive-open-items.md`。

| 能力 | 状态 | 阶段 | 备注 |
| --- | --- | --- | --- |
| `.zip` 托管写入器（local+central directory、deflate） | 已实现 | ARCHIVE-1 | 自实现（ZipArchive 不支持 unix mode/symlink 写出） |
| `.tar.gz` 托管写入器（ustar+gzip） | 已实现 | ARCHIVE-1 | 复用 Bundler.Deb tar 写法提取共享件 |
| 单顶层目录布局 `<pkg>-<ver>-<rid>/` | 已实现 | ARCHIVE-1 | 归档惯例 |
| 可执行位保留（tar mode + zip unix attrs） | 已实现 | ARCHIVE-1 | 文件头魔数探针判定（ELF/shebang/Mach-O 8 值→0755，其余 0644；非 POSIX 宿主同源语义，PR #12 补 Mach-O）；`zipinfo -l`/`tar -tvf`/`test -x` 断言 |
| 符号链接保留（tar symlink + zip Info-ZIP 惯例） | 已实现 | ARCHIVE-1 | 解出后 `readlink` 断言 |
| `.sha256` 侧车 | 已实现 | ARCHIVE-1 | 与 deb/rpm/appimage 一致 |
| `BundlerFormats=zip;targz` 多格式扇出 | 已实现 | ARCHIVE-1 | 含与 deb/rpm/appimage 混排 |
| `BundlerArchiveFile` 归档相对映射 | 已实现 | ARCHIVE-1（提前落地） | 逃逸/碰撞拒绝同 AppDir 口径 |
| 归档签名 | 明确拒绝 | — | 无通行惯例；签名诉求属平台格式层 |
| tar.xz/tar.zst/7z | 明确拒绝 | — | 编码器依赖/普及度；需要时另起决策 |
| Windows 宿主产归档 | 已实现 | ARCHIVE-1 | 托管写入器任意宿主可产 |
| 全目标 RID（win-x86/x64/arm64、osx、osx-x64/arm64、linux-x64/arm64、linux-musl-x64/arm64） | 已实现 | PR #12、`02b2521` | 目标 OS 分发无架构门禁；win-x86 于联合测试批次开放并实产；musl 目标仅挂本格式；`osx` 通用载荷契约（预合并 universal 目录、可跑配方）见 `docs/mac-app-capability-matrix.md` |
| 同条件产物逐字节确定 | 已实现 | 联合测试 | 纯托管写入器同输入同条件 sha256 一致（同宿主跨轮 + linux↔qemu 同型跨宿主 16/16 对）；跨 OS 差异归因载荷 publish 元数据与宿主 mode 表达，非写入器缺陷 |
| 跨 OS 互读实测（跨宿主互产互解） | 已实现 | ARCHIVE-3 + 联合测试 | 2026-09-29/30 四宿主：win/linux/mac/qemu 互产互装——解压、exec 位/symlink 还原、载荷运行全过（ARCHIVE-OI-04 消解） |
| 第三方实现互读（python3 `zipfile`/`tarfile` 读产物） | 已实现 | ARCHIVE-3 | Verify.sh 断言：条目/执行位/symlink |

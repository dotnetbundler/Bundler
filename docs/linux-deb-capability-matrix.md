# Linux `.deb` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
外部待验收行绑定到 `docs/linux-deb-open-items.md`、`docs/linux-deb-manual-testing.md` 中的明确 ID。
上游参照见 `docs/linux-tauri-capability-audit.md`；决策与阶段见 `docs/linux-deb-roadmap.md`。
本矩阵于 2026-09-27 规划轮建立；LINUX-DEB-1/2 已于同日完成，相关行更新为"已实现"并以 `tests/Linux.Deb.Integration/Verify.sh` 与 `tests/Bundler.Tests` DebTests 为证据。

## 产物与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.deb` 产物（托管 ar/tar.gz 写入器，零原生工具） | 已实现 | LINUX-DEB-1 | `OutputDirectory/<rid>/deb/<package>_<version>_<arch>.deb` + `.sha256` 侧车；`dpkg-deb -I`/`ar t`/`tar` 结构断言已通过 |
| 任意构建宿主（含 Windows/macOS CI） | 已实现（Linux 宿主验证）/外部待验收（跨宿主矩阵） | LINUX-DEB-1 | 纯托管写出，无宿主 OS 门控；跨宿主构建矩阵属 LINUX-DEB-OI-05 |
| 目录载荷 → `usr/lib/<package-name>/` | 已实现 | LINUX-DEB-1 | 默认安装根；`BundlerDebInstallRoot` 可改 `/opt/<name>` 等绝对路径 |
| `usr/bin/<command>` 符号链接 | 已实现 | LINUX-DEB-1 | `/usr` 根下指向 `../lib/<package>/<main>`，其他根用绝对目标；`BundlerDebBinLink` 可改名或 `none` 关闭 |
| 版本映射 SemVer→deb（`~` 预发布、`-revision`、`+` 保留） | 已实现 | LINUX-DEB-1 | 默认映射表；`BundlerDebVersion`/`BundlerDebRevision`/`BundlerDebEpoch` 覆盖 |
| 架构映射 `linux-x64→amd64`、`linux-arm64→arm64` | 已实现 | LINUX-DEB-1 | 其他 deb 架构名经 `BundlerDebArchitecture` 覆盖 |
| control 核心字段（Package/Version/Architecture/Installed-Size/Maintainer/Priority/Homepage/Description） | 已实现 | LINUX-DEB-1 | `Package` 默认 ProductName kebab 化，`Maintainer` 默认 Publisher→Identifier 回退 |
| `md5sums` 清单 | 已实现 | LINUX-DEB-1 | 逐文件 MD5；Verify.sh 用 `md5sum -c` 对解包载荷真实核对 |
| 确定性构建（tar 条目排序、uid/gid 0、归一化时间戳策略） | 已实现 | LINUX-DEB-1 | uid/gid 0、固定 mtime 1980-01-01 UTC、排序条目；DebTests 断言同输入字节级一致（1980 取值规避 lintian ancient-file） |
| 失败清理 | 已实现 | LINUX-DEB-1 | 构建失败删除半成品 `.deb` 与侧车；验证测试断言无残留 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces` 显式透传 | 已实现 | LINUX-DEB-2 | `BundlerDeb*` 分号列表 → control 逗号子句，原样透传、拒换行；Verify.sh metadata 变体逐字段断言 |
| `Section`/`Priority`/`Maintainer`/`Homepage`/`Installed-Size` 覆盖 | 已实现 | LINUX-DEB-2 | `Section`/`Priority` 校验（Priority 限定五值，默认 optional）；`Maintainer`/`Homepage`/`Installed-Size` 为 DEB-1 既有面 |
| `.desktop` 生成与 `usr/share/applications/` 落位 | 已实现 | LINUX-DEB-2 | 决策 5 全字段（Type/Name/Comment/Exec 占位符/Icon/Terminal/Categories/MimeType 并集）；`desktop-file-validate` 对生成与安装后文件双重断言 |
| 自定义 `.desktop` 整文件覆盖 | 已实现 | LINUX-DEB-2 | `BundlerDebDesktopFile` 原样打包到 applications 路径；内容合法性属调用方责任 |
| hicolor 图标（PNG 尺寸探测、`@2x` 密度目录） | 已实现 | LINUX-DEB-2 | PNG 签名+IHDR 探测尺寸；`@2x` 文件名后缀 → `<W>x<H>@2`；非 PNG 报错 |
| AppStream metainfo | 已实现 | LINUX-DEB-2 | `BundlerDebMetainfoFile` 可选 → `usr/share/metainfo/<包名>.metainfo.xml` |
| `ChangelogFile`→`changelog.gz`、`LicenseFile`→`copyright` | 已实现 | LINUX-DEB-2 | `usr/share/doc/<包名>/`；changelog 自动 gzip |
| 包内绝对路径自定义映射（`/etc` 等载荷外路径） | 已实现 | LINUX-DEB-2 | `@(BundlerDebFile)` `Destination` 元数据（绝对路径含文件名，拒相对/`..`/空段）；API 面 `DebFileEntry` |
| 桌面观感（菜单项/图标渲染/关联双击打开） | 外部待验收 | LINUX-DEB-4 起 | 需 GUI 桌面环境：LINUX-DEB-MT-01 |

## 脚本、服务与压缩

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| maintainer 脚本（preinst/postinst/prerm/postrm） | 已实现 | LINUX-DEB-3 | `BundlerDeb{Preinst,Postinst,Prerm,Postrm}File` 整文件注入 control 归档 0755（shebang+LF 校验）；真实 `dpkg -i/-r` 标记断言 |
| systemd unit（`usr/lib/systemd/system/`） | 已实现（落位+合成）/外部待验收（真实 enable/start） | LINUX-DEB-3 | `BundlerDebSystemdServiceFile` → `usr/lib/systemd/system/<包名>.service`；postinst `daemon-reload` 自动合成（与调用方脚本合并）；OI-04 保留真实 systemd 宿主验收 |
| conffiles 声明 | 已实现 | LINUX-DEB-3 | `BundlerDebConffiles` 显式列表 + `/etc` 下 `BundlerDebFile` 自动登记（均校验载荷存在）；真实 `-r` 保留/`-P` 清除与升级 `--force-confold` 断言 |
| 压缩选项（gzip 默认；xz/zstd 候选） | 已实现（gzip）/登记拒绝（xz/zstd） | LINUX-DEB-3 | `BundlerDebCompression` 仅接受 `gzip`；xz/zstd 拒绝并说明（netstandard2.0 无托管编码器；zstd 另需 dpkg≥1.21.18） |
| 升级/降级语义实测 | 已实现（升级）/写实登记（降级） | LINUX-DEB-3 | 同包 `dpkg -i` 1.0.0→1.0.1 + conffile 修改保留断言；降级需 `--force-downgrade` 属 dpkg 语义写实 |

## 签名与分发

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.sha256` 侧车校验和 | 已实现 | LINUX-DEB-1 | 每产物一份；Verify.sh `sha256sum -c` 断言 |
| `.deb` 包级签名（dpkg-sig/debsigs） | 明确拒绝 | — | deb 签名惯例在 apt 仓库侧（`Release`/`InRelease`）；包级签名工具覆盖率极低；有真实需求再评估 |
| apt 仓库生成/签名（`Release`/`InRelease`/`Packages`） | 明确拒绝 | — | 仓库管理属分发管线而非打包器；登记为独立产品候选 |

## 生命周期

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `dpkg -i` 真实安装（本机 sudo） | 已实现 | LINUX-DEB-1 | Verify.sh 装卸烟雾 + `dpkg -s`/`dpkg -L`/链接启动断言 |
| `dpkg -r`/`dpkg -P` 卸载语义 | 已实现 | LINUX-DEB-1/3 | `-r` 删普通文件留 conffile、`-P` 全清——真实断言 |
| 同包升级覆盖安装 | 已实现 | LINUX-DEB-3 | `dpkg -i` 1.0.0→1.0.1 后 `dpkg -s` 版本断言 + conffile 本地修改保留 |
| `linux-arm64` 产物 | 计划实现（结构）/外部待验收（运行） | LINUX-DEB-4 | 结构断言本机可做；arm64 真实安装属 LINUX-DEB-OI-01 |
| 多发行版安装矩阵 | 部分本机自动/外部待验收 | LINUX-DEB-4 | docker debian/ubuntu 容器本机可测；GUI/更多发行版属 LINUX-DEB-OI-02 |

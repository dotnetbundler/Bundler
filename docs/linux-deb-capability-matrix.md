# Linux `.deb` 能力矩阵

`已实现`、`部分实现`、`计划实现`、`外部待验收`、`不适用`、`明确拒绝` 状态口径见 `docs/development-rules.md`；
外部待验收行绑定到 `docs/linux-deb-open-items.md`、`docs/linux-deb-manual-testing.md` 中的明确 ID。
上游参照见 `docs/linux-tauri-capability-audit.md`；决策与阶段见 `docs/linux-deb-roadmap.md`。
本矩阵于 2026-09-27 规划轮建立，当前全部能力为"计划实现"或更早状态；随各阶段完成逐行更新为"已实现"并附证据。

## 产物与载荷

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.deb` 产物（托管 ar/tar.gz 写入器，零原生工具） | 计划实现 | LINUX-DEB-1 | `OutputDirectory/<rid>/deb/<package>_<version>_<arch>.deb` + `.sha256` 侧车；`dpkg-deb -I`/`ar t`/`tar` 结构断言 |
| 任意构建宿主（含 Windows/macOS CI） | 计划实现 | LINUX-DEB-1 | 纯托管写出，无宿主 OS 门控；跨宿主构建矩阵属 OI-05 |
| 目录载荷 → `usr/lib/<package-name>/` | 计划实现 | LINUX-DEB-1 | 默认安装根；`BundlerDebInstallRoot` 可改 `/opt/<name>` 等绝对路径 |
| `usr/bin/<command>` 符号链接 | 计划实现 | LINUX-DEB-1 | 指向 `../lib/<package>/<main>`；`BundlerDebBinLink` 可改名或关闭 |
| 版本映射 SemVer→deb（`~` 预发布、`-revision`、`+` 保留） | 计划实现 | LINUX-DEB-1 | 默认映射表；`BundlerDebVersion`/`BundlerDebRevision`/`BundlerDebEpoch` 覆盖 |
| 架构映射 `linux-x64→amd64`、`linux-arm64→arm64` | 计划实现 | LINUX-DEB-1 | 其他 deb 架构名经覆盖旋钮 |
| control 核心字段（Package/Version/Architecture/Installed-Size/Maintainer/Priority/Homepage/Description） | 计划实现 | LINUX-DEB-1 | `Package` 默认 ProductName kebab 化，`Maintainer` 默认 Publisher→Identifier 回退 |
| `md5sums` 清单 | 计划实现 | LINUX-DEB-1 | 逐文件 MD5（lintian 合规） |
| 确定性构建（tar 条目排序、uid/gid 0、归一化时间戳策略） | 计划实现 | LINUX-DEB-1 | 同输入重复构建哈希稳定（记录可复现边界） |
| 失败清理 | 计划实现 | LINUX-DEB-1 | 构建失败不留伪 `.deb`；沿用管线工作目录契约 |

## 元数据与桌面集成

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces` 显式透传 | 计划实现 | LINUX-DEB-2 | 纯元数据直通；不做运行时依赖探测 |
| `Section`/`Priority`/`Maintainer`/`Homepage`/`Installed-Size` 覆盖 | 计划实现 | LINUX-DEB-2 | — |
| `.desktop` 生成与 `usr/share/applications/` 落位 | 计划实现 | LINUX-DEB-2 | `desktop-file-validate` 断言；`MimeType` 合并文件关联与 `x-scheme-handler/<scheme>` |
| 自定义 `.desktop` 整文件覆盖 | 计划实现 | LINUX-DEB-2 | `BundlerDebDesktopFile` 专家旋钮；责任边界标注 |
| hicolor 图标（PNG 尺寸探测、`@2x` 密度目录） | 计划实现 | LINUX-DEB-2 | 非 PNG 首版不支持 |
| AppStream metainfo | 计划实现 | LINUX-DEB-2 | `BundlerDebMetainfoFile` 可选，落 `usr/share/metainfo/` |
| `ChangelogFile`→`changelog.gz`、`LicenseFile`→`copyright` | 计划实现 | LINUX-DEB-2 | `usr/share/doc/<package>/` |
| 包内绝对路径自定义映射（`/etc` 等载荷外路径） | 计划实现 | LINUX-DEB-2 | `BundlerDebFile`；逃逸与冲突校验 |
| 桌面观感（菜单项/图标渲染/关联双击打开） | 外部待验收 | LINUX-DEB-4 起 | 需 GUI 桌面环境：LINUX-DEB-MT-01 |

## 脚本、服务与压缩

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| maintainer 脚本（preinst/postinst/prerm/postrm） | 计划实现 | LINUX-DEB-3 | 专家旋钮整文件注入 0755；真实 `dpkg -i` 执行断言；责任边界标注 |
| systemd unit（`usr/lib/systemd/system/`） | 计划实现 | LINUX-DEB-3 | 默认装不启用；postinst `daemon-reload` 片段合成 |
| conffiles 声明 | 计划实现 | LINUX-DEB-3 | `dpkg -r` 保留 / `dpkg -P` 清除断言 |
| 压缩选项（gzip 默认；xz/zstd 候选） | 计划实现 | LINUX-DEB-3 | 按宿主 dpkg 版本矩阵裁决；`data.tar.zst` 需 dpkg≥1.21.18 |
| 升级/降级语义实测 | 计划实现 | LINUX-DEB-3 | 同包新版 `dpkg -i` 升级、降级策略写实文档 |

## 签名与分发

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `.sha256` 侧车校验和 | 计划实现 | LINUX-DEB-1 | 每产物一份 |
| `.deb` 包级签名（dpkg-sig/debsigs） | 明确拒绝 | — | deb 签名惯例在 apt 仓库侧（`Release`/`InRelease`）；包级签名工具覆盖率极低；有真实需求再评估 |
| apt 仓库生成/签名（`Release`/`InRelease`/`Packages`） | 明确拒绝 | — | 仓库管理属分发管线而非打包器；登记为独立产品候选 |

## 生命周期

| 能力 | 状态 | 适用性与计划阶段 | 完成条件/边界 |
| --- | --- | --- | --- |
| `dpkg -i` 真实安装（本机 sudo） | 计划实现 | LINUX-DEB-1 | Verify.sh 装卸烟雾 + 零残留断言 |
| `dpkg -r`/`dpkg -P` 卸载语义 | 计划实现 | LINUX-DEB-1/3 | `-r` 保留 conffiles、`-P` 全清（LINUX-DEB-3 起断言） |
| 同包升级覆盖安装 | 计划实现 | LINUX-DEB-3 | `dpkg -i` v1→v2 版本更新断言 |
| `linux-arm64` 产物 | 计划实现（结构）/外部待验收（运行） | LINUX-DEB-4 | 结构断言本机可做；arm64 真实安装属 LINUX-DEB-OI-01 |
| 多发行版安装矩阵 | 部分本机自动/外部待验收 | LINUX-DEB-4 | docker debian/ubuntu 容器本机可测；GUI/更多发行版属 LINUX-DEB-OI-02 |

# Linux `.rpm` 后端实施路线（LINUX-RPM）

> 状态：**`.rpm` 冻结基线 `0.1.0-alpha.55`**（2026-09-27 LINUX-RPM-5 收口，`linux-rpm-development` 分支）。冻结后仅接受带回归测试的缺陷修复。
> 上游审计已完成（Tauri 固定快照 `447fa9f`，DEB-5 复核确认零漂移）。
> 规范入口：`docs/roadmap.md`；跨格式规则见 `docs/development-rules.md`。
> 逐项能力状态见 [`docs/linux-rpm-capability-matrix.md`](linux-rpm-capability-matrix.md)；外部条件见 [`docs/linux-rpm-open-items.md`](linux-rpm-open-items.md)；人工步骤见 [`docs/linux-rpm-manual-testing.md`](linux-rpm-manual-testing.md)。

## 1. 已确认决策（2026-09-27，用户"按推荐执行"确认）

1. **格式范围**：本线只做 `Rpm`（公共枚举已有）；dnf/yum（Fedora/RHEL/Rocky/openSUSE 经 zypper 亦可消费 rpm）为安装面。
   SUSE 专属宏与 `pattern`/`product` 等发行版扩展不做，按通用 RPM 4 规范产出。
2. **工具供应：纯托管写入器，零原生工具**（延续 `.deb` 决策 2）。
   `.rpm` = lead（96B）+ 签名 header + 主 header + cpio(newc) 载荷归档（压缩）；由 `Bundler.Rpm` 后端用 C# 直接写出，不调用 `rpmbuild`、不内嵌二进制、不运行时下载。
   上游 Tauri 同款策略（`rpm` crate 纯 Rust，含签名 header 写入路径）——托管实现可行性有先例背书。
   收益与 deb 一致：任意构建宿主可产 rpm、无 GPL 再分发义务、产出确定可复现。
   代价：rpm 头结构（tag 索引+typed store）比 deb 复杂——正确性由 `rpm -qip`/`rpm -K`/`rpm -i`、`rpmlint`、容器真实装卸断言背书；同时写出 rpm 读取器（`RpmPackageReader`）供测试回读，双向格式互证。
3. **包拆分**：新建 `src/Bundler.Rpm`（netstandard2.0，`DotNet.Bundler.Rpm`）+ 同形 MSBuild 接线 + `tests/Rpm.Api.PackageFixture` + `tests/Linux.Rpm.Integration/Verify.sh` + `samples/HelloRpmApp`。
   共享件：`TarWriter`/`ArWriter` 等 deb 内部件不共享（格式不同）；freedesktop 数据树生成（`.desktop`/hicolor 图标/metainfo/`usr/` 布局）按 deb 决策 3 提取到 `Bundler.Core` 内部件，`InternalsVisibleTo` 供 Deb/Rpm 复用——DEB 端仅内部移动不改行为（回归由既有 141 项断言背书）。
4. **payload 布局**：与 deb 同构——默认 `usr/lib/<package-name>/` + `usr/bin/<command>` 相对符号链接；`BundlerRpmInstallRoot`/`BundlerRpmBinLink` 覆盖。
   rpm 与 deb 差异：rpm 要求**每个目录显式登记为包文件条目**（目录条目带 mode+owner），上游 `rpm` crate 用空文件 hack 代偿（rpm-rs#177）；本写入器按规范直接写目录条目（`FILEMODES` dir+0755），不做 hack。
5. **桌面集成**：完全复用共享 freedesktop 生成器（决策 3）——`.desktop` 全字段生成/`DesktopFile` 覆盖/hicolor 图标/metainfo/`usr/share/doc`，语义与 deb 行逐格对齐。
6. **依赖声明**：`Requires`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests` 透传显式声明（`Dependency::any` 同款纯文本）；不做运行时探测、`AutoReqProv` 显式置 0（自包含载荷不产自动依赖）。
   rpm 关系语法（`pkg >= 1.0`、`pkg = epoch:ver-rel`）原样透传，合法性属调用方；写入器额外登记 `rpmlib(...)` 自依赖集（`CompressedFileNames`、`PayloadFilesHavePrefix`、`PayloadIsXz` 等按实际输出特性集写）。
7. **维护者脚本**：专家旋钮 `BundlerRpm{PreInstall,PostInstall,PreRemove,PostRemove}File` → header scriptlet tag（`PREIN`/`POSTIN`/`PREUN`/`POSTUN`+`…PROG` 解释器 `/bin/sh`），shebang/LF 校验口径与 deb 一致。
   托管 `systemd` unit `BundlerRpmSystemdServiceFile`（`usr/lib/systemd/system/`，0644 装而不启）：post/postun 自动合成 `systemctl daemon-reload || true`，与调用方脚本合并；启用由调用方脚本决定（沿用 deb 决策 7）。
   `BundlerRpmConfigFile`/`@(BundlerRpmFile)` 的 `/etc` 目标自动按 `%config(noreplace)` 标记（rpm 的 conffile 等价物；`FILEFLAGS` config+noreplace 位）——`rpm -e` 保留已修改 conffile（`.rpmsave`），与 deb `-r` 语义对应。
8. **版本映射**：rpm `Version` 禁含 `-`（`-` 是 Version/Release 分隔符）。
   `BundlerVersion`(SemVer)→rpm 默认映射：正式版 `1.0.0`→`Version=1.0.0 Release=1`；预发布 `1.0.0-alpha.1`→`Version=1.0.0 Release=0.1.alpha.1`（Fedora 预发布惯例，`0.x` 保证 `<1` 排序低于正式版）；`+build` 元数据进 `Release` 后缀；`BundlerRpmVersion`/`BundlerRpmRelease`/`BundlerRpmEpoch` 显式覆盖（epoch 语义同 deb）。
   上游原样透传 `version_string`（含 `-` 会产出非法包），本仓库显式收紧——同 deb 决策 8 立场。
9. **签名**：rpm 包级 GPG 签名是真实生态惯例（`rpm --sign`/`rpm -K`）——v1 不实现签名写入（签名 header 结构额外一轮工作），仅 `sha256` 侧车；GPG 签名排 `LINUX-RPM-3` 之后独立评估（需要时另起 `LINUX-RPM-SIGN` 阶段，密钥走秘密引用不入库）。
10. **命名与输出契约**：文件名用 rpm 原生约定 `<name>-<version>-<release>.<arch>.rpm`（arch：`x86_64`/`aarch64`）；目录契约不变 `OutputDirectory/<rid>/rpm/`。
11. **MSBuild 多格式**：放开适配层单格式限制（deb 决策 11 遗留项）——`BundlerFormats=deb;rpm` 同次 publish 由管线扇出，RPM-1 落地并验证。
12. **压缩**：载荷压缩默认 gzip（`Compression=gzip`）；xz/zstd 同 deb 立场登记拒绝（netstandard2.0 无托管编码器），`PAYLOADCOMPRESSOR`/`PAYLOADFLAGS` 如实填写。
13. **确定性**：`BUILDTIME`/`FILEMTIMES`/`header` 条目排序全部固定（沿用 deb mtime=1980-01-01 策略）；同输入字节级一致断言。
14. **阶段骨架**：沿用五段——`LINUX-RPM-1`（托管写入器最小可用 + 容器真实装卸）、`LINUX-RPM-2`（元数据与桌面集成）、`LINUX-RPM-3`（脚本/systemd/config/压缩）、`LINUX-RPM-4`（矩阵与冻结前收口：arm64、多发行版容器矩阵、rpmlint 基线）、`LINUX-RPM-5`（审计复核与冻结）。
15. **命名**：示例 `samples/HelloRpmApp`；fixture `tests/Bundler.ApiTests`（`RpmApiTests`）；集成 `tests/Bundler.IntegrationTests` 的 `RpmIntegrationTests`（fixture `Fixtures/Rpm/`）；文档 ID 前缀 `LINUX-RPM-OI-xx`/`LINUX-RPM-MT-xx`。

## 2. 打包工具下限（三层口径）

- **打包工具（能力）下限**：无原生工具——`Bundler.Rpm` 以托管代码写出 lead/header/cpio/gzip，能力下限即后端可运行的宿主；任意能跑 .NET 的宿主可产 `.rpm`。
- **验证工具**：`rpm`（`-qip`/`-K`/`--queryformat`/`rpm2cpio`）、`rpmlint`、容器内 `rpm -i`/`rpm -e`/`dnf`——仅验收链使用，缺失降级 SKIP 并记录。
- **安装宿主边界**：`.rpm` 面向 RPM 系发行版；SUSE/RHEL 旧版宏差异按写实记录，不作全版本承诺。

## 3. 与 deb 的语义对应表（实现参照）

| deb 概念 | rpm 对应 | 处理 |
| --- | --- | --- |
| `control` 字段 | header tag（NAME/VERSION/RELEASE/EPOCH/SUMMARY/DESCRIPTION/LICENSE/URL/GROUP/OS/ARCH） | 字段映射表在 RPM-1 落定 |
| `Depends:`/`Recommends:` 等 | `REQUIRES`/`RECOMMENDS`/`PROVIDES`/`CONFLICTS`/`OBSOLETES`/`SUGGESTS` tag | 透传 |
| `Installed-Size` | `SIZE`/`LONGSIZE` | 计算 |
| `md5sums` | `FILEDIGESTS`（sha256，`FILEDIGESTALGO=8`） | 逐文件 sha256 |
| `conffiles` | `FILEFLAGS` config 位 + `%config(noreplace)` 语义 | `/etc` 自动标记 |
| `preinst`/`postinst`/`prerm`/`postrm` | `PREIN`/`POSTIN`/`PREUN`/`POSTUN` + `…PROG` | 专家旋钮 |
| `data.tar.gz` 目录条目 | header 文件清单含目录条目（dir mode） | 规范直写 |
| `dpkg -i/-r/-P` | `rpm -i/-e` | 容器矩阵断言 |

## 4. 阶段分解

### LINUX-RPM-1：托管写入器最小可用（已实现 2026-09-27）

- **交付**：`src/Bundler.Rpm`——lead+签名 header（SIZE/MD5/PAYLOADSIZE/SHA1HEADER/SHA256HEADER）+主 header（NAME/VERSION/RELEASE/EPOCH/SUMMARY/DESCRIPTION/BUILDTIME/BUILDHOST/SIZE/VENDOR/LICENSE/GROUP/URL/OS/ARCH/RPMVERSION/PAYLOAD*、文件清单全 tag、rpmlib 三依赖、自提供 `name = evr` 与 `name(arch) = evr`）+cpio newc+gzip；`RpmPackageReader` 回读器；SemVer→rpm 映射与八旋钮（PackageName/Version/Release/Epoch/Architecture/Vendor/InstallRoot/BinLink）；usr/lib+usr/bin 布局、目录显式条目；`.sha256` 侧车；MSBuild `BundlerFormats=rpm` 接线与 `deb;rpm` 扇出；`Rpm.Api.PackageFixture`；`Verify.sh` 全闭环。
- **退出达成**：`rpm -qip` 识别产物；docker `fedora:latest` 真实 `rpm -i`/`rpm -q`/`rpm -ql`/运行/`rpm -V`/`rpm -e` 零残留全绿；`deb;rpm` 扇出断言通过。
- **实测修正**（相对原始格式笔记）：ENCODING tag 实为 5062（5012=BUGURL）；PAYLOADDIGEST(5092) 是**压缩后**载荷 sha256、PAYLOADDIGESTALT(5097) 才是解压前 cpio 的；GNU cpio 提取不为符号链接自动建缺失父目录（`rpm -i` 无此问题），Verify.sh 按清单预建。
- **不做**：关系字段、.desktop/图标、脚本、config 标记、压缩选项、rpmlint（信息级运行，RPM-3 转硬基线）。

### LINUX-RPM-2：元数据与桌面集成（已实现 2026-09-27）

- **交付**：`Requires`/`Provides`/`Conflicts`/`Obsoletes`/`Recommends`/`Suggests` 六族透传——`RpmDependency` 解析 `name [op evr]` 子句（`< <= = >= >` → LESS/GREATER/EQUAL 位组合，拒 `!=`）写入 REQUIRE*/PROVIDE*/CONFLICT*/OBSOLETE*/RECOMMEND*/SUGGEST* 三件套 tag；`License`（SPDX 惯例，`Unspecified` 默认）/`Group`（可置空省略 tag）/`Url`（默认 `Homepage`，置空省略）覆盖；共享 freedesktop 件提取到 `Bundler.Core` 内部 `FreedesktopFiles`（`.desktop` 生成/覆盖 + hicolor 图标 + metainfo，deb/rpm 双后端复用，deb 行为不变——153 断言背书）；`ChangelogFile`→`usr/share/doc/<pkg>/changelog.gz`（%doc 标记）；包级 `LicenseFile`→`usr/share/licenses/<pkg>/<文件名>`（%license=128 标记，rpm 原生惯例）；`@BundlerRpmFile` 任意绝对路径映射（逃逸校验）；目录占有规则对齐发行版惯例（包自有叶子目录占有，`/usr/share/icons` 子树与共享系统目录不占有）。
- **实测修正**：cpio 成员名需带 `./` 前缀——`rpmlib(PayloadFilesHavePrefix)` 依赖的真实含义；绝对路径成员名会让裸 `cpio` 提取（rpmlint 管线）撞上宿主根权限而 fatal。`rpm -i` 本不受影响，但按上游惯例修正，RPM-1 产物缺陷一并回填。
- **AUTOREQPROV 注记**：路线图曾列 `AUTOREQPROV`——实测它不是二进制 header tag（specfile 指令），无需落位；`rpmlib(...)` 自依赖已覆盖。
- **退出达成**：`rpm -qip`/`--queryformat` 逐字段断言全绿；生成与覆盖 `.desktop` 均过 `desktop-file-validate`；docker `fedora:latest` 装后 `rpm -ql` 逐路径断言 + `rpm -qd` %doc 可见 + 卸载零残留全绿。
- **不做**：scriptlet、systemd、`%config(noreplace)` 标记（`/etc` 文件暂为普通文件）、压缩选项、rpmlint 硬基线（信息级：6E4W，与预期一致）。

### LINUX-RPM-3：脚本、systemd、config 与压缩（已实现 2026-09-27）

- **交付**：`PreInstallFile`/`PostInstallFile`/`PreUninstallFile`/`PostUninstallFile` 四 scriptlet 专家旋钮 + `*Program` 解释器覆盖——文件 LF 行尾强制（拒 CRLF/裸 CR），`#!` 首行剥离为 `*PROG` tag（缺省 `/bin/sh`）；`SystemdServiceFile` 落 `/usr/lib/systemd/system/<pkg>.service` 并向 `%post`/`%postun` 合成 `daemon-reload` 尾段（装而不启）；`/etc` 下 `RpmFile` 目标自动 `%config(noreplace)`（FILEFLAGS 17）+ `ConfigFiles` 显式列表（须命中载荷常规文件）；`Compression` 仅 `gzip`（xz/zstd 拒绝）。
- **实测注记**：scriptlet 语义为解释器喂体——script tag 存无 shebang 的正文，PROG tag 存解释器；rpm 安装/升级/卸载计数以 `$1` 传给 scriptlet。
- **退出达成**：容器内 `rpm -i`/`rpm -e` 四个 scriptlet 标记断言、`rpm -U`（Release 1→2）升级 + `%config(noreplace)` `.rpmsave`/原地保留断言全绿；`PREINPROG`/`--scripts`/FILEFLAGS 主机侧断言全绿。

### LINUX-RPM-4：原生 E2E 与支持矩阵（已实现 2026-09-27）

- 交付：`rpmlint` 硬断言门控（`rpmlint-exemptions.txt` 豁免清单入档，7 项豁免——no-signature/no-packager-tag/no-group-tag/no-changelogname-tag/invalid-license/binary-or-shlib-defines-rpath/no-manual-page-for-binary；`summary-ended-with-dot` 以修 fixture 描述消解而非豁免）；`linux-arm64` 产物结构断言（`*.aarch64.rpm`+`ARCH=aarch64`+载荷结构）；docker 发行版装卸矩阵扩为 `fedora:latest`+`rockylinux:9`+`opensuse/leap:latest` 三容器真实 `rpm -i/-e`（镜像不可拉取记 SKIP）；干净宿主复核：`src/Bundler.Rpm` 零外部进程调用。
- 证据：`Verify.sh` 全绿含三容器矩阵 + `rpm -U`/`%config` 语义段 + rpmlint 门控；`Bundler.Tests` 163 全绿无新增用例（纯测试基建与断言强化）。
- 边界：arm64 真机安装（OI-01）、更广发行版宿主（OI-04）、dnf/zypper 仓库（OI-06）、GUI 桌面（OI-02）、systemd 真机（OI-03）、GPG 签名（后置评估）——均登记未冒充。

### LINUX-RPM-4 原始范围（存档）

- **目标/交付**：`rpmlint` 硬断言基线（豁免清单入档）；`linux-arm64` 产物结构断言；docker 发行版矩阵（`fedora:latest`+`rockylinux:9`+`opensuse/leap`）；干净宿主复核；示例全旋钮收口。
- **退出**：矩阵实测格子有证据；未测格子进 OI/MT 清单。

### LINUX-RPM-5：审计复核与冻结（已实现 2026-09-27）

- 上游复核：`git ls-remote` 确认 `tauri-apps/tauri` `dev` HEAD 仍为 `447fa9f`——rpm 侧审计行（rpm crate 托管写入器、PGP 签名路径、`usr/lib` 布局惯例）基线保持有效，零漂移。
- 能力矩阵定稿：无悬空"计划实现"行；GPG 签名明确为冻结外后置评估（`LINUX-RPM-SIGN`，未排期）。
- OI/MT 收口：OI-01..07 逐条复核维持登记（OI-04 备注 fedora/rockylinux/opensuse 已由容器覆盖）；MT-01..07 全量保留。
- **冻结基线**：`RpmBundleConfiguration` 配置面与行为契约冻结于 `0.1.0-alpha.55`；冻结测试向量 = `Bundler.Tests` 163/163 + `Verify.sh` 全绿（三容器真实 `rpm -i/-e/-U`、`%config`/scriptlet 语义、rpmlint 豁免门控、arm64 结构断言）。
- 冻结后规则：仅接受带回归测试的缺陷修复；新能力另起阶段。

### LINUX-RPM-5：审计与格式冻结

- **前置**：LINUX-RPM-1..4 完成。
- **目标/交付**：上游复核（快照漂移重核审计行）；能力矩阵定稿；OI/MT 收口；冻结基线写入本路线与 `PROJECT_CONTEXT.md`；`docs/roadmap.md` 推进 `LINUX-APPIMAGE`。
- **退出**：`.rpm` 冻结基线写入，`LINUX-APPIMAGE` 规划轮待启动。

## 5. 验证分层

| 验证层 | 内容 |
| --- | --- |
| 本机自动 | `RpmPackageReader` 双向互证、`rpm -qip`/`--queryformat`/`rpm -K`、`rpmlint`、单元断言（结构/映射/确定性/拒绝路径） |
| 容器真实 | docker `fedora`/`rockylinux`/`opensuse` 内 `rpm -i/-e/-U`、二进制运行、conffile 语义、systemd unit 落位 |
| 人工/外部 | GUI 桌面观感、arm64 真实宿主、签名验证（若引入）、`dnf`/`zypper` 仓库工作流 |

### 2026-10-06 回归修复：样品声明运行时依赖

- **发现**：main `dbbbf91` 联合测试——产物在裸 `fedora` 容器装后探针 rc=134（缺 libicu）；`Requires` 仅 rpmlib 自依赖，样品未用 `BundlerRpmRequires` 旋钮（写入器纯透传，非后端缺陷）。
- **处置**：`samples/HelloBundlerApp/formats/Rpm.props` 默认声明 `glibc;libgcc;libstdc++;zlib;libicu`（`HelloBundlerRpmRequires` 仍可覆盖）。
- **实证**：裸 `fedora:latest` 容器 `dnf install ./pkg.rpm` 自动解析拉入 `libicu`，探针 rc=0。

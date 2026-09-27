# DotNet.Bundler 产品边界与实施路线

> 最后整理：2026-09-26
> 路线状态：`NSIS-R4` 已在 `71a5c90` 形成冻结基线，随后加固 journal 恢复目标、跨配置恢复流程及快照完整性；当前提交以 Git HEAD 为准
> 当前实施对象：MSI 已收官，下一格式 macOS
> 当前状态：`WIN-MSI-1..9` 全部完成，MSI 基线冻结于 `alpha.43`；版本沿革：`alpha.37` 为既有 MSI 身份基线，`alpha.40` 增加 x86、显式版本映射与可选降级，`alpha.41` 增加范围内安装目录、自定义 UI、可选 Feature、PATH 与交互启动勾选，`alpha.42` 增加 38 语言独立产物、调用方翻译覆盖、快捷方式图标与 FIPS 选项，`alpha.43` 增加受控 WiX 扩展与专家模式。
> 2026-09-26 完成 MAC 规划轮（分支 `mac-app-development`）：上游审计 [`mac-tauri-capability-audit.md`](mac-tauri-capability-audit.md)、PKG 格式决策 [`mac-format-decision.md`](mac-format-decision.md)、`MAC-APP-1..5` 阶段分解 [`mac-app-roadmap.md`](mac-app-roadmap.md) 均已就位。
> PKG 已确认纳入公共 `PackageFormat`（顺序：`MAC-DMG` 后、Linux 前）；工具供应采用宿主检测策略；后端按格式分包；universal 走双产物。
> 全部决策已确认（2026-09-26），`MAC-APP` 待用户下达 `MAC-APP-1` 启动指令。
> 默认下一实施阶段为 `MAC-APP`。
> 既有 per-machine、生产签名、交互 UI、干净宿主及重启等外部验收仍各自记录，不阻塞新增功能开发，也不冒充已通过。

本文档是项目后续路线的规范入口；
跨格式执行规则见 [`docs/development-rules.md`](development-rules.md)，NSIS 历史与冻结记录见 [`docs/nsis-roadmap.md`](nsis-roadmap.md)，MSI 专项细则见 [`docs/msi-roadmap.md`](msi-roadmap.md)。
它们使后续开发不依赖某一次对话或某个 AI 的记忆。

- `PROJECT_CONTEXT.md` 记录当前实现事实与验证证据；
- `AGENTS.md` 与 `docs/development-rules.md` 是接管和跨格式开发规则的入口；
- 本文档记录从当前状态向后推进的正式计划；
- `docs/manual-testing-index.md` 按格式链接独立的人工验收、能力矩阵和外部待办；
- 代码和自动化测试是实现事实的最终依据。若它们与文档冲突，先调查差异，再同时修正文档和实现状态，不能默默选择其中一方。

## 1. 产品定位

`DotNet.Bundler` 是通用桌面应用打包工具，不是只服务 .NET 应用的安装器生成器。

当前首先通过 MSBuild 集成，是因为项目现阶段需要在 `dotnet publish` 中使用它；这只是一个入口，不是产品边界。
直接公共 API、MSBuild Task 和将来正式发布的 CLI 必须调用相同的 Core 与格式后端，不得各自实现打包语义。

稳定的分层应为：

```text
用户入口
  ├─ MSBuild 适配器
  ├─ CLI 适配器
  └─ 直接公共 API
        ↓
格式无关 Core：验证、规划、编排、工作目录、工具缓存
        ↓
格式后端：NSIS、WiX/MSI、macOS、Linux 等
        ↓
格式所需编译器或平台工具
```

因此：

1. Core 和后端的公开模型不能依赖 MSBuild 类型、`.csproj`、`dotnet publish` 目录惯例或 .NET 运行时概念。
2. MSBuild 和 CLI 只负责输入映射、日志与输出呈现，不拥有 NSIS/WiX 等业务逻辑。
3. 新格式复用 Core，并建立自己的后端；不复制完整管线。
4. 当前仍按“一次完成一个安装格式”推进。跨格式抽象只在已有两个真实消费者，或第二个格式已经提出明确需求时提取，避免为假想复用过度设计。
5. 快速开发阶段允许调整或删除早期 alpha API 和配置，不为未发布兼容性阻塞正确设计。

## 2. 明确不做的“运行时依赖能力”

本项目当前不提供任意应用运行时/先决条件的发现、下载、版本检测、安装、修复和卸载编排。具体包括但不限于：

- WebView2 安装模式和最低版本管理；
- VC++ Runtime、.NET Runtime、JRE、数据库等先决条件安装器；
- 通用 prerequisite DSL、在线下载源、镜像选择和依赖版本求解；
- 为第三方运行时定义所有权、升级、回滚、重启和卸载策略。

这是有意的产品边界，而不是遗漏。
Tauri 的 WebView2 等选项服务于 Tauri 自身运行时；直接复制会把通用打包器变成框架运行时部署系统，并引入下载信任、供应链、检测规则、管理员权限、重启和第三方所有权等另一套产品责任。

仍属于本项目范围的相关能力是：

- 把调用方已经准备好的文件和资源装入安装包；
- 提供格式后端的生命周期 Hook 或自定义模板扩展点；
- 对安装包载荷中适用的 Windows PE 文件执行调用方明确配置的签名；
- 随 Bundler 包供应生成安装包所必需的构建工具，例如 `makensis`。这些是打包工具依赖，不是被打包应用的运行时依赖。

只有出现至少两个框架/语言无关的实际用例，并能先写清下载信任、检测、所有权、失败恢复和卸载语义时，才能重新提案；不得因为上游有一个字段就自动加入路线。

## 3. 上游能力参照

Tauri 等产品只作为通用桌面打包能力的参照，不决定本项目的内部架构、配置字段或应用运行时部署范围。
固定上游快照、比较用户结果、记录适用/拒绝理由和状态证据的统一方法见 [开发规则](development-rules.md) 第 2、6 节。
NSIS 的取舍见 [NSIS 上游参考](nsis-upstream-reference.md)，MSI 的当前对照见 [MSI Tauri 审计](msi-tauri-capability-audit.md)；
具体状态以各格式能力矩阵和代码测试为准。

## 4. 阶段执行协议

跨格式的接管、**新后端先制定并确认完整路线**、分层、工具随包供应、测试、完整示例、文档和 Git 规则，以 [`docs/development-rules.md`](development-rules.md) 为唯一规范；
本文件只决定格式顺序和阶段目标。
当前格式的具体前置条件、交付物、不做事项、自动化/真实测试、人工边界和退出条件在格式专用路线中维护。

一个阶段获准启动后，先按上述规则核对现状并向用户报告，再完成整个阶段。
发现数据丢失、安全或主流程问题时在本阶段处理；独立增强按路线登记。
阶段结束时同步本文件状态、格式能力矩阵和 `PROJECT_CONTEXT.md`，区分本机已验证与外部待验收。
规划确认本身不等于授权实施下一阶段。

## 5. Windows NSIS 冻结基线

NSIS 原路线、能力基线、旧阶段映射和 `NSIS-R1..R4` 的历史证据集中在 [NSIS 专用路线](nsis-roadmap.md)；
逐项当前能力见 [NSIS 能力矩阵](nsis-capability-matrix.md)。
真实 UAC、生产签名、重启、旧 MSI、跨宿主等仍按 [NSIS 人工验收](nsis-manual-testing.md) 与 [外部待办](nsis-open-items.md)记录，不将外部缺环境等同于未实现。

## 6. NSIS 之后的路线

对任何尚未实现的新后端，以下顺序只是产品候选路线；开始其第一阶段代码前，须先按开发规则制定覆盖该后端负责格式直至冻结的完整方案，确认产品语义、合法免费工具供应、宿主与设备范围、阶段测试及外部边界，并写入格式专用文档。

### WIN-MSI：WiX/MSI 后端

按一个完整格式推进，不与 macOS/Linux 并行混做。
用户暂定在 **Windows 宿主用 WiX 3.14.1** 构建；它的许可证、可信归档、固定哈希、最小子集、Framework 宿主条件和离线分发须在第一阶段实测过关。
现有 WiX 5.0.2 仅是 NSIS 迁移测试 fixture，不是正式后端。

1. `WIN-MSI-1`：工具供应与身份/版本/组件契约，最小 current user 安装和卸载；**本阶段必须通过真实 Windows 安装及卸载烟雾测试**。
2. `WIN-MSI-2`：major upgrade、降级保护、per machine 独立产物、快捷方式、关联和 URL 协议。
3. `WIN-MSI-3`：签名、语言、静默/被动、退出码、修复维护及失败/回滚/重启。
4. `WIN-MSI-4`：完整 Windows/架构/安全矩阵、人工边界、文档及格式冻结；仅补缺陷和测试，不把首个安装测试拖到此阶段。

`WIN-MSI-1..8` 的当前 Windows 11 x64 主机自动化已通过；
`alpha.37` 是既有 x64/ARM64 身份基线，`alpha.40` 增加 x86、显式 MSI 版本映射和可选降级，`alpha.41` 增加范围内安装目录、自定义 UI、可选 Feature、PATH 与交互启动勾选，`alpha.42` 增加 38 语言独立产物、调用方 `.wxl` 覆盖、快捷方式图标与 FIPS 选项，`alpha.43` 增加受控 WiX 扩展与专家模式，`alpha.44` 增加 Tauri 对齐的跨格式收尾（NSIS 可选旧 MSI 自动检测、MSI 读取前 NSIS 安装目录延续）。
这不是 Tauri 通用 MSI 能力全集。
per-machine 只验证构建产物和数据库，原生 x86/ARM64 宿主、生产证书、真实 UI、FIPS 策略宿主与重启仍按 MSI 专用人工及外部清单记录。
`WIN-MSI-1..9` 全部完成，MSI alpha 基线冻结于 `0.1.0-alpha.43`：

5. `WIN-MSI-5`（本机范围已完成）：x86 目标、显式安全 MSI 版本映射、可选降级及既有身份兼容。
6. `WIN-MSI-6`（本机范围已完成）：范围内安装目录、自定义 UI/品牌、受管可选功能、PATH 与仅交互勾选启动；交互 UI 实际行为和 junction 目标见人工清单。
7. `WIN-MSI-7`（本机范围已完成）：38 语言独立 MSI 产物、调用方 `.wxl` 覆盖、快捷方式图标、`candle -fips` 透传；各语言母语审校与 FIPS 策略宿主见人工清单。
8. `WIN-MSI-8`（本机范围已完成）：常规模式白名单 `.wxs` fragment + 调用方 ID 前缀 + 显式 Component/Feature 引用；显式专家模式整份模板/merge module，身份经 `candle -d` 变量与构建后数据库回读强制；扩展输入计入指纹。
9. `WIN-MSI-9`（已完成）：逐项 Tauri 通用能力审计（快照漂移复核无 MSI 实质变化）、全部本机适用回归、工具许可/体积复核和再冻结，无新增功能。

前置条件、交付物、不做事项、自动化/真实安装测试及退出条件见 [`docs/msi-roadmap.md`](msi-roadmap.md) 第 10 节；
[MSI Tauri 能力审计](msi-tauri-capability-audit.md)区分已实现、计划、明确排除及另立产品路线，[MSI 能力矩阵](msi-capability-matrix.md)保存逐项状态。
原 WIN-MSI-4 的实际证据继续有效，不能借新路线宣称计划能力已完成。
MSI 路线至此收官；默认下一实施阶段为 `MAC-APP`；
未经用户明确要求不开始代码。

### MAC：macOS `.app`、DMG 与 PKG（候选）

`MAC-APP-1`..`MAC-APP-5` 已完成（2026-09-26，分支 `mac-app-development`），macOS `.app` 格式已冻结：`DotNet.Bundler.MacApp` 后端 + MSBuild/直接 API 入口交付 `.app`（骨架、Info.plist 键组、`.icns`/`Assets.car` 图标、文件关联/URL scheme/ATS、签名与显式公证管线、原生 E2E 矩阵），云 macOS VM 全链实测通过；`PackageFormat.Pkg` 已进公共枚举与 osx 目标矩阵。
2026-09-26 `MAC-DMG` 规划轮已收官（分支 `mac-dmg-development`）：决策全部确认（C# 原生编排不内嵌 create-dmg、DMG 本体可签名不公证、GUI 缺失降级、EULA SLA、固定 UDZO、`OutputDirectory/<rid>/dmg/<产品名>.dmg`、布局全可配），路线见 [`docs/mac-dmg-roadmap.md`](mac-dmg-roadmap.md)。
`MAC-DMG-1`..`MAC-DMG-5` 已完成（2026-09-26，分支 `mac-dmg-development`），macOS `.dmg` 格式已冻结（冻结基线 `0.1.0-alpha.45`）：`DotNet.Bundler.MacDmg` 后端 + MSBuild/直接 API 交付 `.dmg`（hdiutil 全链、拖放卷、`Udzo`/`Ulmo`/`Udbz`、Finder 布局与品牌、DMG 签名、EULA SLA、原生 E2E 矩阵），云 macOS VM 全链实测通过；默认下一阶段为 `MAC-PKG`，待启动指令。
2026-09-26 规划轮已产出该格式要求的完整前置文档（分支 `mac-app-development`）：

- 上游审计 [`mac-tauri-capability-audit.md`](mac-tauri-capability-audit.md)：固定快照 `7dbfc1f`（复核 `dev` `9f8922a` 无实质漂移），逐项登记 `.app`/`DMG` 用户可观察能力与选择阶段；
- 格式决策 [`mac-format-decision.md`](mac-format-decision.md)：PKG 已确认纳入公共 `PackageFormat`；
- 阶段分解 [`mac-app-roadmap.md`](mac-app-roadmap.md)：`MAC-APP-1..5`（结构/元数据 → 分发与桌面集成 → codesign/notarization → 原生 E2E → 冻结），含宿主工具供应策略与 `.app` 语义契约，全部决策已确认；
- 能力矩阵 [`mac-app-capability-matrix.md`](mac-app-capability-matrix.md)、人工清单 [`mac-app-manual-testing.md`](mac-app-manual-testing.md)、外部待办 [`mac-app-open-items.md`](mac-app-open-items.md)。

`Pkg` 枚举值与目标矩阵放行已随 `MAC-APP-1` 进入公共模型；格式顺序固定为 `MAC-DMG` 之后、Linux 之前。
2026-09-27 `MAC-PKG-1..5` 全部完成、`.pkg` 冻结于 `0.1.0-alpha.47`；`MAC-PKG-3` 已完成（签名/公证/专家脚本：`pkgbuild --sign`+`productsign --sign`、notarytool+stapler 接线、`--scripts` 旋钮实测真实执行）；`MAC-PKG-2` 已完成（分发包与页面：title/页面/域名自动升级 `productbuild`，per-user 免提权真实安装实测通过）；`MAC-PKG-1` 已完成（2026-09-26（分支 `mac-pkg-development`）：`DotNet.Bundler.MacPkg` 后端落地——`pkgbuild` 组件包全链（identifier/version/install-location 默认与覆盖、`BundlerPkgPayload` 任意载荷、宿主门控、失败清理），MSBuild `BundlerMacPkg*` 接线；`Bundler.Tests` 109 项 + `tests/MacOS.Pkg.Integration` 实测全绿；示例 `samples/HelloMacPkg`；路线见 [`docs/mac-pkg-roadmap.md`](mac-pkg-roadmap.md)；`.pkg` 已冻结（`MAC-PKG-1..5` 全部完成，基线 `0.1.0-alpha.47`）；默认下一阶段 `LINUX` 规划轮，待启动指令。
不得在没有原生 macOS 验证环境时宣称签名和 notarization 完成。

### LINUX：Linux 格式

公共模型已经包含 `Deb`、`Rpm`、`AppImage`；`.deb` 已冻结入 `main`，`.rpm` 后端已冻结，`AppImage` 后端已起步（APPIMAGE-1）。
为避免每次交接重新选择，默认顺序固定为 `LINUX-DEB`（已冻结）→ `LINUX-RPM`（已冻结）→ `LINUX-APPIMAGE`（进行中，APPIMAGE-1 完成），仍然一次只推进一个完整格式。
每个格式分别完成元数据、文件布局、桌面集成、升级/卸载语义、签名或仓库验证边界、原生发行版 E2E，再进入下一个格式。
若真实用户需求或可用原生验证环境要求调整，必须先在本文档写明依据和新顺序。

2026-09-27 `LINUX-DEB` 规划轮完成（分支 `linux-deb-development`）：上游审计 [`linux-tauri-capability-audit.md`](linux-tauri-capability-audit.md)（固定快照 `447fa9f`）、阶段分解与决策 [`linux-deb-roadmap.md`](linux-deb-roadmap.md)、能力矩阵/人工清单/外部待办均已就位，全部决策已确认。
核心决策：deb/rpm 用纯托管写入器（无原生工具、任意构建宿主），AppImage 内嵌固定版本 `appimagetool`（构建限 Linux 宿主）。
2026-09-27 `LINUX-DEB-1` 已完成（分支 `linux-deb-development`，`0.1.0-alpha.48`）：`DotNet.Bundler.Deb` 纯托管 ar/tar/gzip 写入器落地——最小 control 字段、`md5sums`、`usr/lib`+`usr/bin` 布局、SemVer→deb 版本映射、`.sha256` 侧车；MSBuild `BundlerDeb*` 接线、`Deb.Api.PackageFixture`、`tests/Linux.Deb.Integration/Verify.sh`（含真实 `sudo dpkg -i/-r`）、示例 `samples/HelloDebApp` 全部就位且验证全绿。
2026-09-27 `LINUX-DEB-2` 已完成（同分支，`0.1.0-alpha.49`）：`Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces` 透传、`Section`/`Priority` 覆盖、`.desktop` 生成（决策 5 全字段含 `x-scheme-handler/` 并集）与 `BundlerDebDesktopFile` 整文件覆盖、hicolor PNG 图标（尺寸探测 + `@2x`）、AppStream metainfo、`changelog.gz`/`copyright`、`@(BundlerDebFile)` 绝对路径映射；`desktop-file-validate` 与装后 `dpkg -L` 回读断言全绿，lintian 信息级残余登记 LINUX-DEB-OI-07。
2026-09-27 `LINUX-DEB-3` 已完成（同分支，`0.1.0-alpha.50`）：`preinst`/`postinst`/`prerm`/`postrm` 专家旋钮（0755、shebang+LF 校验）、`BundlerDebSystemdServiceFile` 托管 unit（`usr/lib/systemd/system/` 落位 + postinst `daemon-reload` 自动合成/合并）、conffiles（`/etc` 下 `BundlerDebFile` 自动登记 + 显式列表，真实 `-r` 保留/`-P` 清除断言）、压缩枚举仅 gzip（xz/zstd 登记拒绝并记 OI-08）、同包升级与 conffile 修改保留实测全绿。
2026-09-27 `LINUX-DEB-4` 已完成（同分支，`0.1.0-alpha.51`）：lintian 由信息级升级为豁免清单硬断言（`lintian-exemptions.txt`，新 tag 即失败）；自动补发 `changelog.Debian.gz` 与扩展描述默认行消解全部可修 lintian 发现；`linux-arm64` 产物结构与 `Architecture: arm64` 断言（真机安装仍属 OI-01）；docker `debian:stable`/`ubuntu:latest` 容器真实装卸+运行+conffile 语义矩阵全绿；构建侧零系统依赖复核完成（`src/Bundler.Deb` 无外部进程调用）。
2026-09-27 `LINUX-DEB-5` 已完成——**`.deb` 冻结基线 `0.1.0-alpha.51`**：上游复核零漂移（`tauri@dev` 仍为 `447fa9f`）、能力矩阵定稿、OI/MT 收口、冻结写入路线与 PROJECT_CONTEXT。
2026-09-27 `LINUX-RPM` 规划轮完成并确认（分支 `linux-rpm-development`）：决策清单见 `docs/linux-rpm-roadmap.md` §1（托管 RPM 写入器、SemVer→rpm 版本映射、scriptlet/systemd/config 对应关系、gzip-only 压缩、`deb;rpm` 多格式扇出）。
2026-09-27 `LINUX-RPM-1` 已完成（同分支，`0.1.0-alpha.52`）：`DotNet.Bundler.Rpm` 纯托管 lead/header/cpio/gzip 写入器落地，`rpm -qip` 识别 + `RpmPackageReader` 回读互证，docker `fedora:latest` 真实 `rpm -i`/`rpm -e` 零残留全绿；`BundlerFormats=deb;rpm` 多格式扇出落地。`LINUX-APPIMAGE` 仍在其规划轮定稿。
2026-09-27 `LINUX-RPM-2` 已完成（同分支，`0.1.0-alpha.53`）：六族关系字段（Requires/Provides/Conflicts/Obsoletes/Recommends/Suggests，`name [op evr]` 解析）透传 + License(SPDX)/Group/Url 覆盖 + freedesktop 生成器提取到 `Bundler.Core` 内部件供 deb/rpm 复用（deb 行为不变）+ `changelog.gz`%doc/`usr/share/licenses/<pkg>/`%license + `BundlerRpmFile` 任意路径映射；`rpm -qip`/`--queryformat` 逐字段、`desktop-file-validate`、docker 装后 `rpm -ql` 逐路径全绿；cpio 成员名 `./` 前缀修正（rpmlib PayloadFilesHavePrefix 的真实语义）。
2026-09-27 `LINUX-RPM-3` 已完成（同分支，`0.1.0-alpha.54`）：四 scriptlet 旋钮 + `*Program` 解释器覆盖 + systemd unit 与 `daemon-reload` 合成 + `%config(noreplace)` 自动/显式标记 + gzip-only 压缩；容器内 `rpm -i/-e` 标记、`rpm -U` 升级原地保留、`rpm -e` `.rpmsave` 断言全绿。
2026-09-27 `LINUX-RPM-4` 已完成（同分支，`0.1.0-alpha.55`）：`rpmlint` 豁免清单硬断言（`rpmlint-exemptions.txt`，7 项豁免入档，新 tag 即失败；`summary-ended-with-dot` 修 fixture 描述消解）；`linux-arm64` 产物结构断言；docker 矩阵扩为 `fedora:latest`+`rockylinux:9`+`opensuse/leap:latest` 三容器真实 `rpm -i/-e` 全绿；`src/Bundler.Rpm` 零外部进程复核通过。
2026-09-27 `LINUX-RPM-5` 已完成——**`.rpm` 冻结基线 `0.1.0-alpha.55`**：上游复核零漂移（`tauri@dev` 仍为 `447fa9f`）、能力矩阵定稿、OI-01..07/MT-01..07 收口、冻结写入路线与 PROJECT_CONTEXT；GPG 签名明确为冻结外后置评估（`LINUX-RPM-SIGN` 未排期）。
2026-09-27 `LINUX-APPIMAGE-1` 已完成（分支 `linux-appimage-development`，`0.1.0-alpha.56`）：`DotNet.Bundler.AppImage` 内嵌 appimagetool（x86_64/aarch64）+ type2 runtime（x86_64/aarch64）带 SHA-256 provenance；AppDir 组装（共享 freedesktop 件 + 脚本 `AppRun` + 根 desktop 符号链接 + 图标三态回落）→ extract-and-run 调工具；决策 4 实测"可交叉"（runtime-aarch64 内嵌）、决策 10 实测"仅 zstd"（pinned mksquashfs 约束）、pinned 构建缺 runtime 会联网下载故始终外供；九 `BundlerAppImage*` 旋钮接线 + `deb;rpm;appimage` 扇出；`Bundler.Tests` 171 + `Verify.sh`（extract 结构/真实运行/三容器/arm64 结构/失败变体/API 消费）全绿。

### ARCHIVE：通用压缩包格式

记录一项后续格式决策（2026-10-03，用户提出）：在平台格式全部完成后增加一个压缩包后端
（zip / tar.gz 等归档分发形式），跨 Windows/macOS/Linux 通用，阶段号 `ARCHIVE`。
当前仅登记意图：公共模型 `PackageFormat` 尚无对应枚举，无能力清单与阶段分解；
在阶段启动前先做格式决策记录（归档类型范围、可执行位/符号链接保留、校验和与签名策略等）。

### CLI-C1：在打包格式完成后产品化 CLI

仓库中的 `DotNet.Bundler.Cli` 目前只是有限 NSIS 参数的原型，不是当前阶段。
CLI 不产生新的格式能力，也不应反过来驱动后端设计；先完成公共模型已经列出的 MSI、App、DMG、DEB、RPM、AppImage 及 macOS 决策新增的必需格式和 ARCHIVE，再进入 CLI-C1。

届时范围为：固化共享配置 schema；
让 `validate`、`plan`、`bundle` 共用 Core/后端；
定义稳定退出码、机器可读输出、日志、帮助、版本和发布方式；
覆盖全部已冻结格式；
删除重复或错误的早期 alpha 参数而不承诺兼容。
完成后 CLI、MSBuild 和直接 API 都只是同一打包能力的适配器。

当前实施顺序为：已完成并冻结的 `WIN-MSI-1..9` → `MAC-APP`（已冻结并入 main）→ `MAC-DMG`（已冻结并入 main）→ `MAC-PKG`（规划完成，默认下一阶段）→ `LINUX-DEB`（已冻结）→ `LINUX-RPM`（已冻结）→ `LINUX-APPIMAGE`（进行中，APPIMAGE-1 完成） → `ARCHIVE` → `CLI-C1`。
Tauri updater 协议/提升权限计划任务若有需求另立跨格式产品路线，不混入 MSI 或提前产品化 CLI。
调整顺序必须依据真实用户需求、验证能力和维护成本更新本文档，不能只在对话中临时改口。

## 7. 两条不阻塞开发的并行队列

这些队列不是新的实现阶段，不得借它们无限延长当前格式。

### 7.1 外部验收队列

由 [`docs/manual-testing-index.md`](manual-testing-index.md) 按格式引导；
NSIS 和 MSI 的清单、结论及外部待办分别维护，包括 UAC、真实重启、生产证书、历史 MSI、Windows ARM64、真实 ACL/磁盘耗尽等。
取得环境时逐项执行并保存证据；
没有环境时保留准确边界。

### 7.2 候选增强队列

只有满足以下任一条件才进入正式阶段：

- 解决数据安全、供应链或主流程正确性问题；
- 有明确用户用例并能写出完成条件；
- Tauri 新增的能力经审计后确属通用打包能力；
- 第二个格式暴露了必须共享的抽象。

纯粹“可能以后有用”、单一框架运行时需求或无法定义所有权的依赖安装，不进入实现路线。

## 8. 路线维护与接班

阶段结项及交接字段按 [`docs/development-rules.md`](development-rules.md) 第 5、6 节执行。
每阶段在同一轮修改中把本文件顶部基线和默认下一阶段改成实际状态，更新格式路线/能力矩阵、人工清单与外部待办，并在 `PROJECT_CONTEXT.md` 留下 Git、版本、命令、结果和未决问题。
参考 Tauri 时同步格式专用的固定上游快照。
没有实施或没有对应环境证据的能力不得标为完成。

下一位开发者应能仅凭仓库、Git、代码和测试回答当前做什么、为什么、完成标准、下一步和未验证边界；做不到就说明交接尚未完成。

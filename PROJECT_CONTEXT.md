# DotNet.Bundler 项目上下文

> 最后更新：2026-09-27
> 当前分支：`msi-development`（NSIS 开发线已并入，冻结提交 `71a5c90`）
> 当前包版本：`0.1.0-alpha.42`（根 `Directory.Build.props` 的 `BundlerPackageVersion`）
> 当前阶段：WIN-MSI-1..7 本机自动化范围完成（WIN-MSI-7 提交 `1b99b6a`）
> 默认下一阶段：`WIN-MSI-8`（需用户明确启动指令）
>
> 本文只保存**当前事实**：版本、阶段、结构、最近验证摘要、未决问题、下一步。
> 规则在 `docs/development-rules.md`；产品顺序在 `docs/roadmap.md`；历史记录在 `docs/project-history.md`；各格式细节在各 `docs/<format>-*.md`。
> 本文内容须与 Git、代码和测试一致；不一致时以后三者为准并先修正本文。

## 1. 项目结构（现况）

| 项目 | 角色 | 目标框架 |
| --- | --- | --- |
| `src/Bundler.Abstractions` | 跨包稳定契约 | `netstandard2.0` |
| `src/Bundler.Core` | 格式无关的校验、规划、编排、工作目录生命周期、内容寻址工具缓存 | `netstandard2.0;net8.0` |
| `src/Bundler.Nsis` | Windows + NSIS 后端（内嵌 NsisToolset 3.12-r1 多宿主工具与 win-x86 Native AOT 插件） | `netstandard2.0` |
| `src/Bundler.Wix` | Windows + WiX 3.14.1/MSI 后端（内嵌固定 WiX 工具子集） | `netstandard2.0` |
| `src/Bundler.Signing.Windows` | Windows Authenticode 签名 API（NSIS/MSI 共用） | `netstandard2.0` |
| `src/Bundler.MSBuild` | MSBuild Task 适配层（`buildTransitive` 导入） | `netstandard2.0` |
| `src/Bundler.Cli` | 开发原型（`IsPackable=false`，不发布） | `net8.0` |
| `src/Bundler.Package` | 便利元包 `DotNet.Bundler`（聚合 NSIS+WiX 与 MSBuild 支持） | `netstandard2.0` |
| `tests/Bundler.Tests` | 唯一快速测试入口（当前 75 项） | `net8.0` |
| `tests/Msi.Api.PackageFixture` / `tests/Nsis.Api.PackageFixture` | 仅引用 NuGet 后端的 API 消费 fixture | `net8.0` |
| `tests/Windows.Nsis.Integration` / `tests/Windows.Msi.Integration` | 真实 Windows 集成入口；`Fixture/` 为 MSBuild 消费 fixture；NSIS 侧含 `LegacyMsiFixture`（旧 MSI 迁移源） | PowerShell / `net8.0` |
| `tests/Windows.Nsis.Reboot` | 需可抛弃 VM 的重启测试占位 | — |
| `samples/HelloNsisApp` / `samples/HelloMsiApp` | 公开可运行示例（应用版本 `1.0.0`） | `net8.0` |
| `tools/Bundler.Nsis.Plugin` | NSIS 原生插件源码（有意在 slnx 之外，重建需 .NET 10 + Windows 原生链） | — |
| `third_party/` | 第三方归档、许可证、逐文件 SHA-256 与 provenance 文档 | — |

## 2. 各格式当前状态

### Windows + NSIS（已冻结，`71a5c90`）

基线于 `0.1.0-alpha.31` 冻结，alpha.32/33 后续加固 journal 恢复；此后仅文档与共享层随 MSI 工作演进，语义不变。
实现契约（自动化协议、退出码、事务 journal、前向恢复、快捷方式规则、签名管线、工具供应、原生插件）见 [`docs/nsis-roadmap.md`](docs/nsis-roadmap.md)；
能力矩阵、人工清单、外部待办、上游取舍、专项决策分别在对应 `nsis-*.md`。
NSIS 集成 `tests/Windows.Nsis.Integration/Verify.ps1` 在 2026-09-27 的 alpha.42 回归中保持全绿（`IBundleBackend` 多产物契约变更未改变 NSIS 行为）。

### Windows + WiX/MSI（进行中）

WIN-MSI-1..7 实现与本机自动化验证完成：

- `WIN-MSI-1` 基础 MSI（current-user、x64、candle/light `-wx` 严格编译、UUIDv5 身份）
- `WIN-MSI-2` 结构与测试整理
- `WIN-MSI-3` 本地化界面、RTF 许可、签名管线
- `WIN-MSI-4` 发布级工具链（WiX 3.14.1 随包、警告即错误、ICE91 唯一豁免）
- `WIN-MSI-5` x86、显式 MSI 版本映射、可选降级
- `WIN-MSI-6` 安装目录选择（范围校验）、自定义 UI 序列、品牌位图、可选 Feature（快捷方式/PATH/卸载入口）、仅交互启动、ARP 元数据
- `WIN-MSI-7` 38 语言独立产物（一次构建每语言一个 MSI）、调用方 `.wxl` 键覆盖、快捷方式图标、`candle -fips` 透传

设计决策、阶段目标、实施记录与验证证据见 [`docs/msi-roadmap.md`](docs/msi-roadmap.md)；
能力状态见 [`docs/msi-capability-matrix.md`](docs/msi-capability-matrix.md)。

### 未开始的格式

`MAC-APP`、`MAC-DMG`、`MAC-PKG`、`LINUX-DEB`、`LINUX-RPM`、`LINUX-APPIMAGE`、`CLI-C1`：无实现，顺序与边界见 `docs/roadmap.md`。

## 3. 最近验证（2026-09-27，Windows 11 Pro build 26200 x64）

- `dotnet build Bundler.slnx -c Release`：0 警告/0 错误。
- `tests/Bundler.Tests` Release：75/75 PASS。
- MSI 集成全部 PASS：`Verify.ps1`、`VerifyWinMsi6.ps1`、`VerifyPublicSample.ps1`、新增 `VerifyWinMsi7.ps1`（en-US/ja-JP 并存安装与独立卸载、de-DE 直接 API 产物）。
- 38 个支持 culture 的 `light` 真编译逐一验证；`hi-IN`/`kk-KZ` 上游译文不可编译，已排除在支持表外。
- NSIS 全量集成 PASS（`IBundleBackend` 多产物契约变更后的回归）。
- 测试 ProductCode 复核无安装残留。

## 4. 外部验收边界（未执行，不视为完成）

以下项目在本机无法自动化，转入人工/外部清单，**不得宣称已完成**：

- 真实交互 UI 流转、勾选启动实际行为、`InvalidDirDlg`、位图显示、缩放/辅助功能；
- junction/重解析点作为安装目标的安装时行为（原生 MSI 条件检测受限，记录为能力边界）；
- per-machine UAC 真实提权安装/卸载；
- 干净 Windows 10/11 宿主、x86/ARM64 宿主执行；
- 生产 Authenticode 证书与 RFC 3161 时间戳；
- 真实重启与锁定文件场景、本地化内容评审；
- 启用 Windows FIPS 策略宿主的 `candle -fips` 真实行为与非拉丁语言交互安装审校（MSI-OI-13/MSI-MT-12）。

逐项清单见 `docs/msi-open-items.md`（`MSI-OI-*`）与 `docs/msi-manual-testing.md`（`MSI-MT-*`），NSIS 侧见 `docs/nsis-open-items.md`/`docs/nsis-manual-testing.md`。

## 5. 未决问题（等待用户或外部输入）

- 本仓库开源许可证尚未确定。
- WiX v3 已退出免费社区服务；大范围公开分发前须重新评估维护风险（见 `third_party/wix/msi-wix-provenance.md`）。
- `MSI-OI-12`/`MSI-OI-13` 等外部事项待有对应环境时验收。

## 6. 默认下一步

`WIN-MSI-8`：受控 WiX 扩展（常规模式 `.wxs` fragment 白名单）与显式专家模式（自备模板/merge module）。
详细目标/退出条件见 `docs/roadmap.md` 与 `docs/msi-roadmap.md` 相应阶段节。
未经用户明确启动指令不实施；未经明确要求不提交、不推送。

## 7. 历史记录

按时间排序的过往工作、当时证据、已知历史问题与任务衔接标识见 [`docs/project-history.md`](docs/project-history.md)。
该文件是档案，不代表当前状态。

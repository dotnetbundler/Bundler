# DotNet.Bundler 产品边界与实施路线

> 路线状态：全部格式（NSIS/MSI/`.app`/`.dmg`/`.pkg`/`.deb`/`.rpm`/`.AppImage`/`.zip`/`.tar.gz`/Alpine `.apk`）、CLI 与 UPDATE 自更新模块均已冻结并入 `main`，无进行中的格式阶段。
> 剩余仅为各格式外部待验收项（统一登记在 [`docs/special-acceptance.md`](special-acceptance.md)）与 §5.2 候选增强队列。
> 既有 per-machine、生产签名、交互 UI、干净宿主及重启等外部验收不阻塞新增功能开发，也不冒充已通过。

本文档是项目后续路线的规范入口；
跨格式执行规则见 [`docs/development-rules.md`](development-rules.md)，各格式决策/契约/能力状态各见其 `docs/formats/` 文档（如 [`win-nsis.md`](formats/win-nsis.md)、[`win-msi.md`](formats/win-msi.md)）。
它们使后续开发不依赖某一次对话或某个 AI 的记忆。

- `PROJECT_CONTEXT.md` 记录当前实现事实与验证证据；
- `AGENTS.md` 与 `docs/development-rules.md` 是接管和跨格式开发规则的入口；
- 本文档记录产品边界、格式顺序和向后推进的正式计划；
- `docs/special-acceptance.md` 统一登记只能人工/外部付费完成的验收与外部待办；各格式文档按格式链接；
- 代码和自动化测试是实现事实的最终依据。若它们与文档冲突，先调查差异，再同时修正文档和实现状态，不能默默选择其中一方。

## 1. 产品定位

`DotNet.Bundler` 是通用桌面应用打包工具，不是只服务 .NET 应用的安装器生成器。

当前首先通过 MSBuild 集成，是因为项目现阶段需要在 `dotnet publish` 中使用它；这只是一个入口，不是产品边界。
直接公共 API、MSBuild Task 与正式 CLI 都调用相同的 Core 与格式后端，不得各自实现打包语义。

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
4. 当前仍按"一次完成一个安装格式"推进。跨格式抽象只在已有两个真实消费者，或第二个格式已经提出明确需求时提取，避免为假想复用过度设计。
5. 快速开发阶段允许调整或删除早期 alpha API 和配置，不为未发布兼容性阻塞正确设计。

## 2. 明确不做的"运行时依赖能力"

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
各格式的上游取舍与逐项能力见 `docs/formats/` 对应文档；
具体状态以各格式文档和代码测试为准。

## 4. 阶段执行协议

跨格式的接管、**新后端先制定并确认完整路线**、分层、工具随包供应、测试、完整示例、文档和 Git 规则，以 [`docs/development-rules.md`](development-rules.md) 为唯一规范；
本文件只决定格式顺序和阶段目标。

一个阶段获准启动后，先按上述规则核对现状并向用户报告，再完成整个阶段。
发现数据丢失、安全或主流程问题时在本阶段处理；独立增强按路线登记。
阶段结束时同步本文件状态、对应格式文档和 `PROJECT_CONTEXT.md`，区分本机已验证与外部待验收。
规划确认本身不等于授权实施下一阶段。

## 5. 格式顺序与队列

### 5.1 格式顺序与冻结状态

既有格式按以下顺序逐个推进并已冻结（阶段过程与验收证据查 git/PR 记录）：

| 顺序 | 格式/模块 | 冻结基线 | 文档 |
| --- | --- | --- | --- |
| 1 | Windows NSIS | `0.1.0-alpha.31`（alpha.32/33 journal 加固） | [`win-nsis.md`](formats/win-nsis.md) |
| 2 | Windows WiX/MSI | `0.1.0-alpha.43` | [`win-msi.md`](formats/win-msi.md) |
| 3 | macOS `.app` | `0.1.0-alpha.45` | [`mac-app.md`](formats/mac-app.md) |
| 4 | macOS `.dmg` | `0.1.0-alpha.45` | [`mac-dmg.md`](formats/mac-dmg.md) |
| 5 | macOS `.pkg` | `0.1.0-alpha.47` | [`mac-pkg.md`](formats/mac-pkg.md) |
| 6 | Linux `.deb` | `0.1.0-alpha.51` | [`linux-deb.md`](formats/linux-deb.md) |
| 7 | Linux `.rpm` | `0.1.0-alpha.55`（GPG 签名后置并入） | [`linux-rpm.md`](formats/linux-rpm.md) |
| 8 | Linux `.AppImage` | `0.1.0-alpha.58` | [`linux-appimage.md`](formats/linux-appimage.md) |
| 9 | `.zip`/`.tar.gz` | `0.1.0-alpha.59` | [`archive.md`](formats/archive.md) |
| 10 | CLI | `0.1.0-alpha.62` | [`cli.md`](cli.md) |
| 11 | Alpine `.apk` | `0.1.0-alpha.63` | [`linux-alpine-apk.md`](formats/linux-alpine-apk.md) |
| — | UPDATE 自更新 | `0.1.0-alpha.74` | [`update.md`](update.md) |
| — | 签名横切面 | 随各格式 | [`signing.md`](signing.md) |

新后端默认仍一次推进一个完整格式；
若真实用户需求或可用原生验证环境要求调整顺序，必须先在本文档写明依据和新顺序。
Tauri updater 协议/提升权限计划任务若有需求另立跨格式产品路线，不混入单格式阶段。

### 5.2 候选增强队列

只有满足以下任一条件才进入正式阶段：

- 解决数据安全、供应链或主流程正确性问题；
- 有明确用户用例并能写出完成条件；
- Tauri 新增的能力经审计后确属通用打包能力；
- 第二个格式暴露了必须共享的抽象。

纯粹"可能以后有用"、单一框架运行时需求或无法定义所有权的依赖安装，不进入实现路线。

已裁决不予登记的类别（记录理由，防重复提案）：

- **发行版专属 RID**（`ubuntu.20.04`/`rhel.8` 等）：.NET 8 起官方弃用便携 RID 外枚举，产出载荷与便携 RID 完全相同，登记只会制造差异假象；
- **`linux-bionic-*`**（Android）：非桌面边界，移动端若另立路线再议，届时须显式区分 Alpine APK 与 Android `.apk` 命名；
- **Android `.apk`**：同上，与本仓 Alpine `.apk` 无关系。

## 6. 外部验收队列

由 [`docs/special-acceptance.md`](special-acceptance.md) 统一登记；
取得环境时逐项执行并保存证据；
没有环境时保留准确边界。

## 7. 路线维护与接班

阶段结项及交接字段按 [`docs/development-rules.md`](development-rules.md) 第 5、6 节执行。
每阶段在同一轮修改中把本文件顶部基线和默认下一阶段改成实际状态，更新对应格式文档与 `docs/special-acceptance.md`，并在 `PROJECT_CONTEXT.md` 留下 Git、版本、命令、结果和未决问题。
参考 Tauri 时同步格式专用的固定上游快照。
没有实施或没有对应环境证据的能力不得标为完成。

下一位开发者应能仅凭仓库、Git、代码和测试回答当前做什么、为什么、完成标准、下一步和未验证边界；做不到就说明交接尚未完成。

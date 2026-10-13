# 文档索引

`docs/` 按读者分三层：**怎么用** → **是什么** → **项目治理**。

## `usage/` — 使用文档（给使用者）

每个入口、每个后端一件：接入 → 最小使用 → 全旋钮（MSBuild/`bundler.json`/API 三列对照）→ 场景 → 产物 → 边界。

| 入口 | 文档 |
| --- | --- |
| MSBuild | [`usage/msbuild.md`](usage/msbuild.md) |
| `bundler` CLI | [`usage/cli.md`](usage/cli.md) |
| 后端 ×10 | [`usage/formats/`](usage/formats/)（与 `design/formats/` 同名镜像） |

## `design/` — 设计与契约（给实现者与集成者）

| 文档 | 内容 |
| --- | --- |
| [`design/architecture.md`](design/architecture.md) | 全管线实现原理（输入→入口→门面→规划→编排→产物） |
| [`design/formats/`](design/formats/) | 十件格式契约：决策、语义、能力边界、验收口径 |
| [`design/targets.md`](design/targets.md) | target 语法、架构词、产物命名契约 |
| [`design/support-matrix.md`](design/support-matrix.md) | 格式×平台生产/消费/签名支持总览 |
| [`design/update.md`](design/update.md) | UPDATE 自更新模块契约 |
| [`design/signing.md`](design/signing.md) | 签名横切面契约与各格式裁决 |

## `project/` — 项目治理（给维护者与验收者）

| 文档 | 内容 |
| --- | --- |
| [`project/project-rules.md`](project/project-rules.md) | 产品与工程不变量 |
| [`project/roadmap.md`](project/roadmap.md) | 产品边界、格式顺序、候选队列 |
| [`project/special-acceptance.md`](project/special-acceptance.md) | 只能人工/外部付费完成的验收登记 |

## 目录外

- [`../README.md`](../README.md)：项目门面与快速入口
- [`../PROJECT_CONTEXT.md`](../PROJECT_CONTEXT.md)：当前事实（版本/阶段/未决）
- [`../AGENTS.md`](../AGENTS.md)：AI 行为规则与接管入口
- [`../tests/README.md`](../tests/README.md)：测试入口与门禁

# Bundler 测试入口

测试体系重写进行中，完整规格（分层判据、覆盖清单、CI 拓扑）见 `docs/design/testing.md`。

## 结构

| 目录 | 工程 | 层职责 |
| --- | --- | --- |
| `Unit/` | `Bundler.UnitTests` | 单元层：纯进程内、零子进程 spawn |
| `Api/` | `Bundler.ApiTests` | 公共 API functional 层（无 IVT，真跑内嵌工具产真物） |
| `Integration/` | `Bundler.IntegrationTests` | 真环境层：fixture 发布链、真装真卸、docker、更新链 |
| `Package/` | `Bundler.PackageTests` | 本地 nupkg 消费契约层 |
| `Shared/` | — | 四工程链接共享的基建源码 |
| `Fixtures/` | — | 被测 fixture 工程与载荷 |
| `Manual/` | — | 特殊验收脚本（CI 装不下的验收项；登记见 `docs/project/special-acceptance.md`） |

## 入口

```powershell
dotnet test tests/Unit/Bundler.UnitTests.csproj -c Release
dotnet test tests/Api/Bundler.ApiTests.csproj -c Release
dotnet test tests/Integration/Bundler.IntegrationTests.csproj -c Release
dotnet test tests/Package/Bundler.PackageTests.csproj -c Release
```

门禁、trait、fixture 地图随测试落位补全；以 `docs/design/testing.md` 为准。

# CLI 外部待办清单（CLI-OI）

> 本清单只登记"本机环境无法完成、需外部条件或人工宿主"的待办；每项对应矩阵中的"外部待验收"格子。
> 完成时用证据回填并改状态；不完成不得标"已实现"。ID 前缀 `CLI-OI-xx`。

| ID | 关联阶段 | 外部条件 | 待办内容 |
| --- | --- | --- | --- |
| CLI-OI-01 | ~~后置评估~~ 已消解 | — | AOT 已于 CLI-AOT 落地（linux-x64 实测 ~49.5MB ELF）；其余 RID 二进制产出为 `dotnet publish -r <rid>` 例行扩展，不再单独立项 |
| CLI-OI-02 | CLI-3 | ~~Windows 宿主~~ 已验证 | **已消解**：2026-09-28 Windows Server 2022 x64 验收会话执行 MT-01——`dotnet tool install` 后 `bundler bundle` 实产 nsis/msi/zip 断言落盘 |
| CLI-OI-03 | CLI-3 | ~~macOS 宿主~~ 已验证 | **已消解**：2026-09-28 macOS 26.5.2 arm64 验收会话执行 MT-02——`bundler bundle` 实产 app/dmg/pkg/targz，`codesign --verify` 通过 |
| CLI-OI-04 | CLI-3 | ~~CI 环境~~ 已验证 | **已消解**：2026-09-28 Linux 验收会话在脚本管道（非 tty）消费 `--json` 与退出码判定，属 `Cli.Integration/Verify.sh` 既有断言 |
| CLI-OI-05 | 测试基建 | 任意宿主 | `tests/Cli.Integration/Verify.sh` 缺 Linux `uname` 门禁：在 macOS 宿主上跑 appimage 段必误报（appimagetool 为 Linux ELF，按设计拒绝）；待补门禁后该段在非 Linux 宿主应 SKIP |

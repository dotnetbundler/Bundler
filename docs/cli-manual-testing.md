# CLI 人工验收用例（cli-manual-testing）

> 只登记本机无法自动化、需外部宿主的用例；每项对应 OI 条目。完成时回填证据。

| ID | 用例 | 所需外部条件 | 验证记录 |
| --- | --- | --- | --- |
| CLI-MT-01 | Windows 宿主 `dotnet tool install` 后 `bundler bundle --formats nsis,msi,zip` 跑通 | Windows 宿主 | **已验证**（2026-09-28，Windows Server 2022 x64）：tool 实装 + nsis/msi/zip 实产断言 |
| CLI-MT-02 | macOS 宿主 `bundler bundle --formats app,dmg,pkg,targz` 跑通 | macOS 宿主 | **已验证**（2026-09-28，macOS 26.5.2 arm64）：四格式落盘 + `codesign --verify` 通过 |
| CLI-MT-03 | Linux 产出归档/安装包在对应宿主真实安装/解包（复用各格式 MT 清单） | 交叉宿主对 | 部分已验（2026-09-28）：Linux 产 zip 在 Windows `Expand-Archive`、macOS `ditto`/`tar` 互读通过；安装包跨宿主生成侧保留 |
| CLI-MT-04 | CI 环境（无 tty）`--json` 输出被管道消费、退出码判定 | CI 环境 | **已验证**（2026-09-28，Linux 宿主）：脚本管道非 tty 消费 `--json`、退出码判定，见 `CliIntegrationTests` |

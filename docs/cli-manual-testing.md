# CLI 人工验收用例（cli-manual-testing）

> 只登记本机无法自动化、需外部宿主的用例；每项对应 OI 条目。完成时回填证据。

| ID | 用例 | 所需外部条件 |
| --- | --- | --- |
| CLI-MT-01 | Windows 宿主 `dotnet tool install` 后 `bundler bundle --formats nsis,msi,zip` 跑通 | Windows 宿主 |
| CLI-MT-02 | macOS 宿主 `bundler bundle --formats app,dmg,pkg,targz` 跑通 | macOS 宿主 |
| CLI-MT-03 | Linux 产出归档/安装包在对应宿主真实安装/解包（复用各格式 MT 清单） | 交叉宿主对 |
| CLI-MT-04 | CI 环境（无 tty）`--json` 输出被管道消费、退出码判定 | CI 环境 |

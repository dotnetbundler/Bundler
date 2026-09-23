# DotNet.Bundler 人工与外部环境验收手册

本文档是当前已实现能力中，无法在开发任务的普通本地自动化环境诚实完成的验收项清单。它集中记录需要真实重启、UAC/管理员权限、生产凭据、真实历史安装包、特定 Windows 版本、ARM64 硬件或其他操作系统宿主的测试。

本文档不是将可自动化的测试转交给人工，也不是对尚未实现功能的验收承诺。每次准备发布时，应先由自动化完成“自动化前置”，再按本文档逐项人工验收。

## 1. 通用安全要求

- 需要重启、UAC、系统级待处理删除、磁盘耗尽或故意修改 ACL 的测试，只能在可抛弃虚拟机或专用测试机执行。
- 测试前创建虚拟机快照。不在主要开发机上填满系统盘、修改共享 `PendingFileRenameOperations` 或应用广泛拒绝 ACL。
- 每个用例都从干净快照或已确认清理的环境开始，不共用上一用例的安装状态。
- 生产 PFX 密码只通过环境变量传递，不写入项目、命令行记录或本文档。
- 真实 MSI 和生产证书可以保密；验收记录只需保存非敏感标识、文件 SHA-256、命令结果和截图。

## 2. 每次验收的记录头

每轮开始时保存以下信息：

```powershell
git rev-parse HEAD
git status --short
dotnet --info
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber, OsArchitecture
Get-FileHash -Algorithm SHA256 -LiteralPath "<待验收安装器>"
```

每个用例记录：

| 字段 | 内容 |
| --- | --- |
| 用例 ID | 例如 `MT-03` |
| 日期/测试人 | 当地时间和执行人 |
| Commit/包版本 | 完整 Git SHA 和 NuGet 版本 |
| 环境 | OS 版本、build、架构、虚拟机/实机 |
| 权限 | 标准用户、提权管理员或 LocalSystem |
| 输入 | 安装器 SHA-256、历史 MSI 版本/标识或证书公开信息 |
| 结果 | PASS / FAIL / BLOCKED |
| 证据 | 退出码、日志、注册表导出、签名输出、截图和重启前后状态 |
| 清理 | 卸载、恢复快照或删除专用虚拟机 |

## 3. 自动化前置（由 Codex 执行）

下列项目不应改为人工测试：

```powershell
dotnet run --project tests\Bundler.Tests\Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts\packages
tests\Windows.Nsis.Integration\Verify.ps1
git diff --check
```

这组自动化覆盖包结构、模板契约、22 种内置语言编译和规范键校验、非拉丁 Unicode 安装、静默 current-user 安装/升级/降级/卸载、快捷方式所有权、文件关联、深链接、进程关闭、可控 `3010`、自签名证书签名机制、Fixture MSI 迁移、安装事务回滚、安装进程中断恢复、锁定载荷安全失败和提交后清理；还会在事务快照、事务激活、载荷恢复、注册表恢复和 active journal 清理五个检查点注入一次性故障，验证检查点状态与下次启动恢复。卸载自动化另行覆盖 post-uninstall Hook 失败和卸载进程树被终止：两者都必须保留前向 journal、恢复卸载器和注册表路径锚点，下一次安装启动先幂等完成旧卸载并清理 journal。只有这组测试通过的 commit 才进入下面的人工验收。

## 4. 人工验收顺序

### MT-01：提权安装范围和 UAC

**需要**：可抛弃 Windows x64 虚拟机，标准用户会话，可输入管理员凭据。

1. 分别以 `perMachine` 和 `both` 生成安装器：

   ```powershell
   dotnet publish samples\HelloBundledApp\HelloBundledApp.csproj -c Release -r win-x64 `
     -p:HelloBundledAppInstallMode=perMachine `
     -p:BundlerOutputPath="$PWD\artifacts\manual\per-machine"
   dotnet publish samples\HelloBundledApp\HelloBundledApp.csproj -c Release -r win-x64 `
     -p:HelloBundledAppInstallMode=both `
     -p:BundlerOutputPath="$PWD\artifacts\manual\both"
   ```

2. 从标准用户桌面启动安装器，记录 UAC 页面和发布者。
3. `perMachine` 应安装到 Program Files，卸载信息应在 HKLM，快捷方式应使用所有用户 Shell 上下文。
4. `both` 在两个干净快照中分别选择 current-user 和 per-machine，核对目录、HKCU/HKLM 和快捷方式范围。
5. 从“已安装的应用”与 `Uninstall.exe` 各执行一次卸载，确认不遗留产品所有的程序目录、注册表项和快捷方式。

**PASS**：UAC、安装位置、注册表视图、Shell 上下文和卸载行为与所选范围一致。

### MT-02：提权安装后 `/R` 降权启动

**需要**：MT-01 的 per-machine 安装器，可查看进程 Integrity Level 的 Process Explorer 或等效工具。

1. 从标准用户会话运行：

   ```powershell
   & "<perMachine-setup.exe>" /S /R "/ARGS=--manual-elevation-check"
   $LASTEXITCODE
   ```

2. 验证安装器返回 `0`，并启动已安装应用。
3. 查看应用进程的用户与 Integrity Level。

**PASS**：应用属于原桌面用户，Integrity Level 为 Medium，不继承安装器的 High 令牌，且收到 `--manual-elevation-check`。

### MT-03：真实重启后完成锁定文件删除

**需要**：可抛弃、已提权的 Windows 虚拟机，以及当前 commit 生成的 per-machine 集成 Fixture 安装器。

1. 创建虚拟机快照。
2. 在提权 PowerShell 中执行：

   ```powershell
   $installer = (Resolve-Path "artifacts\windows-nsis-integration\bundle-per-machine\win-x64\nsis\Bundler Integration Fixture-1.0.0-setup.exe").Path
   tests\Windows.Nsis.Reboot\Verify.ps1 -Phase Prepare -InstallerPath $installer -ConfirmDisposableMachine
   ```

3. 只在看到 `PASS prepare` 后正常重启虚拟机。不手工编辑或清空 `PendingFileRenameOperations`。
4. 重启后以提权 PowerShell 执行：

   ```powershell
   tests\Windows.Nsis.Reboot\Verify.ps1 -Phase Verify -ConfirmDisposableMachine
   ```

**PASS**：Prepare 通过临时副本和 `_?=` 直接等待实际卸载进程，确认真实返回 `3010` 且存在本产品 pending delete；Verify 确认重启后目录、卸载注册表、安装与卸载 journal 和本产品 pending 项均已消失。不能使用安装目录内 `Uninstall.exe` 的外层自复制 launcher 退出码代替实际卸载进程证据。

### MT-04：生产 Authenticode 与 RFC 3161 时间戳

**需要**：受信任 CA 签发的生产代码签名身份（PFX 或证书存储区）、公开 RFC 3161 URL、可联网的干净 Windows 虚拟机。

1. 使用 PFX 时先在当前 PowerShell 会话设置密码环境变量，然后构建：

   ```powershell
   $env:BUNDLER_SIGNING_PASSWORD = Read-Host "PFX 密码" -MaskInput
   dotnet publish samples\HelloBundledApp\HelloBundledApp.csproj -c Release -r win-x64 `
     -p:HelloBundledAppSigningPfxFile="<certificate.pfx>" `
     -p:HelloBundledAppSigningPfxPasswordEnvironmentVariable=BUNDLER_SIGNING_PASSWORD `
     -p:HelloBundledAppSigningTimestampUrl="<RFC3161 URL>"
   Remove-Item Env:\BUNDLER_SIGNING_PASSWORD
   ```

2. 检查安装器签名：

   ```powershell
   Get-AuthenticodeSignature -LiteralPath "<setup.exe>" | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
   ```

3. 安装后对安装目录中的主 EXE、所有显式 `BundlerWindowsSigningFile` 和 `Uninstall.exe` 执行同样检查。
4. 若生产使用 HSM、USB Token 或云签名，再用对应 `BundlerWindowsSigningCommand` provider 重复构建，确认凭据不出现在 MSBuild 日志、provider 参数记录或项目文件中。
5. 从干净 Windows 虚拟机观察 SmartScreen/UAC 发布者，记录证书链和时间戳。

**PASS**：主 EXE、显式附加 payload、卸载器和安装器均为 `Valid`，签名者符合生产身份，`TimeStamperCertificate` 存在，无私钥、令牌或密码进入项目、日志或构建产物。

### MT-05：真实历史 MSI 迁移

**需要**：真实发布过的 x86/x64、current-user/per-machine MSI，准确 ProductCode/UpgradeCode，可抛弃 Windows 虚拟机。

1. 记录 MSI SHA-256、版本、架构、安装范围和 ProductCode/UpgradeCode。
2. 在干净快照安装 MSI，启动一次并创建代表性用户数据。
3. 用真实标识构建 NSIS 安装器：

   ```powershell
   dotnet publish samples\HelloBundledApp\HelloBundledApp.csproj -c Release -r win-x64 `
     -p:HelloBundledAppLegacyMsiProductCodes="<ProductCode>" `
     -p:HelloBundledAppLegacyMsiUpgradeCodes="<UpgradeCode>"
   ```

4. 交互执行迁移，分别检查原 MSI 产品、文件、服务/快捷方式（若有）和 NSIS 新安装。
5. 分别对 ProductCode 精确命中和 UpgradeCode 枚举路径执行。
6. 对 x86/x64 和 current-user/per-machine 组合重复，不在同一快照叠加结果。

**PASS**：只识别配置的历史产品，旧 MSI 成功卸载后完成 NSIS 安装，无误删同名或无关产品。用户数据是否保留应根据该产品的明确迁移策略记录，不做默认假设。

### MT-06：开始菜单/任务栏固定项矩阵

**需要**：产品支持的每个 Windows 10/11 build，以及有代表性的组策略配置。

1. 安装并把应用固定到开始菜单和任务栏。
2. 执行同版本更新、跨版本升级和产品/主程序改名升级，记录固定项是否仍能启动当前程序。
3. 卸载后检查两类固定项。
4. 创建同名但指向其他应用的快捷方式，重复卸载，确认外部所有的快捷方式未被删除。

**PASS**：普通快捷方式所有权保护正确；固定项的实际行为已按 OS build/策略记录。若 Windows 不提供可靠取消固定能力，应记录为平台限制，不得把观察到的单一 build 行为扩写为通用保证。

### MT-07：真实 ACL 和磁盘耗尽故障

本项故意不重复自动化中的一次性故障标记；它要验证的是 Windows 实际 ACL、文件/注册表访问拒绝和真实存储空间耗尽行为。

**需要**：两台或两个独立快照的可抛弃 Windows 虚拟机；其中一个可安全限制测试用户对安装目录/注册表的写入，另一个可通过独立虚拟磁盘或配额制造空间不足。

1. ACL 用例先安装稳定旧版，保存安装目录、产品注册表、快捷方式和关键文件哈希。
2. 仅对一个明确的测试文件或产品专用注册表项设置拒绝写入，执行升级；不对父目录、用户配置文件根或共享注册表根应用递归拒绝。
3. 记录退出码、旧状态恢复情况和 active journal。恢复 ACL 后重试，确认下次启动恢复。
4. 磁盘用例在专用虚拟磁盘/配额中执行，分别在事务快照阶段和新载荷写入阶段造成空间不足。不在日常开发机的系统盘填充文件。
5. 解除配额或恢复磁盘空间后，重新运行安装器并检查恢复和清理。

**PASS**：快照未完成时不修改旧安装；已激活事务后的失败会回滚，或在回滚同样受阻时保留 active journal，外部条件恢复后下次启动能完成恢复。不得报告新旧载荷混合状态为成功。

### MT-08：非 Windows 宿主编译矩阵

**需要**：Linux x64、Linux arm64、macOS x64、macOS arm64 的原生 runner。不用 QEMU 或 Rosetta 结果代替对应原生宿主证据。

在每个 runner 上使用同一 commit 执行：

```bash
dotnet --info
dotnet run --project tests/Bundler.Tests/Bundler.Tests.csproj -c Release
dotnet pack Bundler.slnx -c Release -o artifacts/packages
dotnet publish samples/HelloBundledApp/HelloBundledApp.csproj -c Release -r win-x64
```

记录内嵌 `makensis` 的实际宿主文件类型、退出码、生成的 Windows NSIS 安装器 SHA-256 和工具缓存位置。

**PASS**：每个原生宿主都能离线解压/复用内嵌工具并生成安装器，无需系统预装 NSIS。该项只证明跨宿主编译，生成的 Windows 安装器还应在 MT-09 的目标 Windows 上执行。

### MT-09：Windows x64/ARM64 目标安装矩阵

**需要**：原生 Windows x64 和 Windows ARM64 机器/虚拟机。

1. 分别生成 `win-x64` 和 `win-arm64` 应用及 NSIS 安装器。
2. 在匹配架构的干净 Windows 上执行安装、启动、升级、卸载。
3. 检查主程序架构、安装路径、64 位注册表视图、快捷方式和文件关联。
4. ARM64 项必须记录硬件/虚拟化架构；只在 x64 Windows 上看到文件生成不算 ARM64 运行验收。

**PASS**：两种架构都完成安装、运行、升级和卸载，且注册表视图与安装范围符合预期。

### MT-10：Restart Manager 无法关闭进程

**需要**：可抛弃 Windows 虚拟机，两个用户会话或专用的不可由当前安装上下文终止的测试进程。不得使用 Windows 关键/受保护系统进程作为 Fixture。

1. 安装稳定旧版，记录安装目录、版本注册表和主程序哈希。
2. 在另一用户会话或受控 Fixture 中运行安装目录里的主程序，使当前安装上下文可以检测它，但无权完成关闭。
3. 以静默模式执行升级，记录退出码。
4. 核对旧版本注册表、主程序哈希、快捷方式和 journal，然后终止 Fixture 并清理虚拟机。

**PASS**：安装器返回 `5`，不开始事务性载荷替换，旧安装保持不变，同名但不在安装目录的其他进程不受影响。

### MT-11：内置语言内容与界面审校

**需要**：能审校对应语言的母语使用者或专业本地化人员，以及有代表性的 Windows 10/11 显示缩放环境。仓库自动化只证明 22 种语言结构完整、可被真实 NSIS 编译，并验证非拉丁路径和元数据；它不能证明译文自然、准确或在所有页面中没有截断。

1. 对 Arabic、Bulgarian、Dutch、English、French、German、Italian、Japanese、Korean、Norwegian、Persian、Portuguese、PortugueseBR、Russian、SimpChinese、Spanish、SpanishInternational、Swedish、TradChinese、Turkish、Ukrainian、Vietnamese 逐一生成单语言安装器。
2. 每种语言交互走完首次安装、同版本重装、升级、禁止降级、卸载和删除应用数据页面，核对术语、语气、变量插值、换行和按钮含义。
3. 在 100%、150% 和 200% 显示缩放下保存截图，检查 Header、正文、单选框、复选框和错误消息是否截断或重叠。
4. Arabic 和 Persian 额外检查从右到左布局、标点、数字/版本号、产品名与路径混排；确认对外配置名 `Persian` 对应正确的波斯语界面。
5. Japanese、Korean、SimpChinese、TradChinese 和 Vietnamese 额外检查字体回退与字符显示；Portuguese/PortugueseBR、Spanish/SpanishInternational 分别确认地区用词差异。
6. 对至少一种系统 UI 语言不在安装器列表中的环境验证第一项语言回退；对多语言安装器验证选择器、记忆值和卸载器语言一致。

**PASS**：每份译文由可识别的审校人签署；没有改变安装/卸载含义的错译，没有乱码、关键截断或错误 RTL 排版；回退、选择器和卸载语言与文档契约一致。发现内容问题时修改对应 `.nsh` 并重新运行自动化编译测试。

## 5. 暂不执行的项目

以下功能尚未实现或已明确不作为当前保证，因此不得在人工测试中写成“待验证已实现能力”：

- 锁定安装载荷在重启后自动替换；当前策略是安全失败和恢复。
- 可回滚的事务式卸载；当前实现是保留 `/REBOOTOK` 语义的前向恢复状态机，不承诺撤销已经删除或已排队删除的文件。
- 完整保真自定义 ACL、ADS 和所有重解析点语义。
- WiX/MSI 新后端、macOS 安装格式和 Linux 安装格式。
- 所有 Windows build 上都能自动取消固定。

## 6. 最终签署表

| ID | 项目 | 结果 | 证据位置 | 备注 |
| --- | --- | --- | --- | --- |
| AUTO | 自动化前置 |  |  |  |
| MT-01 | 提权安装范围和 UAC |  |  |  |
| MT-02 | `/R` 降权启动 |  |  |  |
| MT-03 | 真实重启删除 |  |  |  |
| MT-04 | 生产 Authenticode/时间戳 |  |  |  |
| MT-05 | 真实历史 MSI 迁移 |  |  |  |
| MT-06 | 固定项矩阵 |  |  |  |
| MT-07 | 真实 ACL/磁盘耗尽 |  |  |  |
| MT-08 | 非 Windows 宿主编译 |  |  |  |
| MT-09 | Windows x64/ARM64 目标矩阵 |  |  |  |
| MT-10 | Restart Manager 关闭失败 |  |  |  |
| MT-11 | 内置语言内容与界面审校 |  |  |  |

只有必选项全部 PASS，或对 BLOCKED/平台限制有明确且被接受的发布边界时，才能将对应能力写入发布说明。

# Special 特殊验收脚本

不能收编成 `dotnet test` 的验收——真机、虚拟机、付费凭证、真人交互、公网服务、破坏性故障注入。
每个脚本：前置条件自检 → 缺失即明确报错退出（不静默通过）→ 执行 → **自动清理工件/挂载/卷** → 证据写入 `tests/Special/evidence/<id>-<宿主>-<日期>.md`。
半自动脚本在需要人工动作处暂停等待（UAC 点击、弹窗确认、真机观察），恢复后继续。
回填 `docs/special-acceptance.md` / 各 `<format>-open-items.md` 时贴证据文件内容。

## 通用约定

- 证据格式见 `shared/evidence-format.md`——记录头（OS/build/版本）+ 逐步骤结果 + 工件哈希。
- 需要付费凭证的脚本经环境变量/密钥库引用（证书 thumbprint、notarytool keychain profile、keyfile），密码/私钥绝不进进程命令行或仓库。
- 非破坏优先：能在本机跑的就本机跑；必须可丢弃环境的脚本开头硬性 `-ConfirmDisposableMachine` 类闸。
- 清场：脚本 finally/trap 卸载卷、删临时目录、卸装测试件；证据文件是唯一有意外留物。

## win/

| 脚本 | 对应验收 | 环境 | 人工点 |
| --- | --- | --- | --- |
| `nsis-reboot/Verify.ps1` | 真实重启+锁定文件删除（可丢弃 VM 故障族） | 可丢弃 Windows VM+管理员 | 重启后回跑 `-Phase Verify` |
| `uac-assisted.ps1` | UAC 提权安装/卸载（MT-01/02、per-machine ACL 取证） | Windows+交互桌面 | 弹 UAC 时手动批准 |
| `disposable-vm-faults.ps1` | 真实 ACL 拒绝/物理盘满/锁文件重启复核 | 可丢弃 Windows VM+管理员 | — |
| `mountvol-full-volume.ps1` | mountvol 全卷挂载语义实证 | Windows+管理员+可分配卷 | — |
| `pinned-items.ps1` | 跨 Windows 版本固定项（开始菜单/任务栏） | 目标 build 的 Windows | — |
| `msi-dialog.ps1` | MSI Browse/InvalidDir/Feature/启动勾选/位图缩放 | Windows+交互桌面 | 逐步确认 UI |
| `authenticode-production.ps1` | SA-P-01 生产签名+时间戳+SmartScreen | Windows+生产证书 | SmartScreen 观察记录 |
| `arm64-matrix.ps1` | Windows ARM64 各格式装/升/修/卸矩阵 | Windows ARM64 | — |
| `clean-host.ps1` | 无 .NET SDK 干净宿主 WiX/NSIS 装/卸 | 干净 Windows VM | — |

## mac/

| 脚本 | 对应验收 | 环境 | 人工点 |
| --- | --- | --- | --- |
| `developer-id-chain.sh` | SA-P-02..06 签名+公证+staple+spctl 全链 | macOS+Developer ID 凭证 | — |
| `signed-app-update.sh` | SA-P-07 签名 .app 更新链身份一致性 | macOS+Developer ID 凭证 | — |
| `clean-host-matrix.sh` | 干净 macOS 首装 .app/.dmg | 无 Xcode 干净宿主 | 首启弹窗观察 |
| `intel-x64.sh` | macos-x86_64 实跑/挂载/安装 | Intel Mac 或 Rosetta | — |
| `quarantine.sh` | quarantine 首启场景 | macOS+真实下载路径 | 首启观察 |

## linux/

| 脚本 | 对应验收 | 环境 | 人工点 |
| --- | --- | --- | --- |
| `arm64-real-hw.sh` | ARM64 真机 deb/rpm/appimage/apk 装/跑/卸 | ARM64 真机 | — |
| `cdn-range.sh` | 公网/CDN Range 差分语义 | 任意 linux+公网 feed URL | — |
| `update-public-pipeline.sh` | UPDATE 真实发布管线全链 | linux+公网 feed+私钥 | — |
| `gpg-production.sh` | 生产 GPG 密钥签名/分发/吊销流程 | linux+用户 GPG 密钥 | — |
| `apk-index.sh` | apk 仓库/索引工作流（可选扩展） | alpine 或 docker | — |

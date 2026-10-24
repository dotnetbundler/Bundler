# 参考说明（给后继项目）

本仓已停止开发（2026-10-24），作为后继项目的参考资料保留。
后继项目计划在 GitHub 组织内分多个仓库重新实现；复用这里的任何设计前，先评估是否有更好的方案——本仓的做法是"能跑通并有证据"，不等于"最优"。
本文只放对后继项目有用的结论：值得借鉴的设计、已知缺陷与过重之处、已实证的平台坑、工程经验。
标注"观点"的条目是判断而非事实；其余条目都有代码、测试或 CI 证据，证据位置随条目给出。
当前已知缺陷的完整记录（位置、根因、复现、建议解法）在 `PROJECT_CONTEXT.md` §5（K1–K5）。

## 1. 值得借鉴的设计

### 1.1 分层与入口

- 三层：入口（MSBuild Task / CLI / 直接公共 API）→ 格式无关 Core（校验、规划、编排、工作目录、工具缓存）→ 格式后端；入口只做输入映射与输出呈现，不实现打包语义（`docs/project/project-rules.md` §1）。
- CLI 与 MSBuild 的编排曾各自实现一遍，后收编为 Core 单源 `BundleOrchestrator`——后继项目应从第一天就只有一条编排管线。
- 后端不依赖 `.csproj`、`dotnet publish` 或 MSBuild 类型，输入是普通应用目录，因此非 .NET 应用也能用（`docs/design/architecture.md`）。
- 目标模型 `{os}[-{libc}]-{arch}`：`TargetOperatingSystem` + `LibC` 旁维度 + 架构枚举即全集，工具能力由后端自报，不用与后端脱节的静态白名单（`docs/design/targets.md`）。

### 1.2 工具随包供应

- 打包必需工具随后端包分发，使用者不装 NSIS/WiX、运行时不联网下载：固定来源、版本、SHA-256、逐文件清单与许可证义务，缓存污染/损坏/并发有处理（`third_party/*/*-provenance.md`、`ToolCache`/`ZipToolCache`）。
- 已供应：NSIS 3.12-r1 多宿主工具集、WiX 3.14.1 工具子集、appimagetool + type2 runtime。
- `.deb`/`.rpm`/Alpine `.apk`/`.zip`/`.tar.gz` 用纯托管写出器（ar/tar/cpio/rpm header/gzip/zip/Zip64），任意宿主可产、零原生依赖，并有确定性断言。

### 1.3 进程执行

- `ToolRunner`/`ToolProcess` 单一进程入口：全链有界等待（默认 15min，超时杀进程树 + 有界排空）、spawn 追踪（`BUNDLER_TOOL_TRACE`）、密位遮罩、测试缝。
- Unix 上弃用 `System.Diagnostics.Process`，改走 libc `posix_spawn`（`NativeSpawnUnix`）——原因见 §3.1；代价见 §2。

### 1.4 UPDATE 自更新

- 打包侧发清单 + ECDSA P-256 `.sig` 旁车 + `.blockmap` 差分；客户端"查/下/验/换"四动词，feed 先验签后解析（`docs/design/update.md`）。
- 换包交给独立引导件（Native AOT per-target + POSIX `sh` 降级件），引导件与客户端协议同源编译。
- `.app` 换包前签名门禁：按 `TeamIdentifier` 比对新旧签名者、坏签拒、已签→未签降级拒、探测失败 fail-closed；决策表 12 例由 AOT 与 `sh` 两份实现共用同一测试表（`tests/Shared/MacAppGateCases.cs`）。
- deb/rpm/apk 走受约束 installer-replay（`pkg-apply`），root 侧对暂存副本复核哈希，堵"验签→root 执行"之间的 TOCTOU。

### 1.5 测试与验收

- 四层测试：Unit（进程内、零子进程）/ Api（公共 API 真跑内嵌工具产真物）/ Integration（真装真卸、docker、更新链）/ Package（消费本地打出的 nupkg 验包契约）（`docs/design/testing.md`）。
- "仓外独立包消费"腿：复制一个只用 `PackageReference` 的普通项目，验证发布包本身可用——防止仓内项目引用掩盖包问题。
- 发布门 `verify.yml`：六宿主测试矩阵 + 产件/跨 OS 消费配对 + syspkg 三容器真装/更新/回滚，全手动触发。
- 能力状态口径（已实现须有自动化证据、外部待验收不冒充已通过）与特殊验收登记（`docs/project/special-acceptance.md`）——这套诚实标注口径本身值得保留。
- 反向变异验证：新测试写完后临时把被测逻辑改回旧行为，确认测试会失败——本仓多次靠它发现"测试从未真正覆盖"的问题（见 §3.6）。

## 2. 已知缺陷与过重之处（建议后继重新评估）

- 当前未修缺陷：K1 Archive 遇 `com.apple.provenance` 失去确定性并夹带来源信息、K2 NSIS 在 C locale 宿主失败、K3 NSIS 中断装测试偶发超时、K4 macOS ≤26 ARM64 回落路径 `FD_CLOEXEC` 不落位、K5 签名只接受受信任身份——详见 `PROJECT_CONTEXT.md` §5。
- （观点）`NativeSpawnUnix` 约 900 行托管 P/Invoke，承担 posix_spawn、fd 布置、进程组树杀、专线程有界弃件、reaper 与扑孤；正确性依赖逐平台 ABI 常量（§3.1、§3.2 都是这里出的事）。
  后继项目若能用 .NET 12+（上游已修 spawn/reap 死锁）或一个小型原生 shim，应优先于重写这套托管实现。
- （观点）引导件 AOT 与 `sh` 双实现：功能对等要靠逐项一致性测试维持，本仓就出过 `sh` 版 fail-open 而 AOT 版 fail-closed 的分叉（§3.6）。
  后继项目应先确认是否真的需要 `sh` 降级件。
- （观点）冻结二进制入库：8 个引导件由 `produce-bootstrap` 产出后人工下载入库，任何引导件源码改动都要重冻、核哈希、再跑全矩阵——流程重且易漏。
  可考虑在发布流水线里构建并签名，而不是把二进制提交进仓库。
- （观点）WiX 3.14.1：本仓选它是因为随包分发与许可可履行，但上游主线已转向 WiX v4+；后继项目应重新选型。
- （观点）`netstandard2.0` 约束：为让同一任务程序集被 .NET Framework `MSBuild.exe` 与 `dotnet msbuild` 双宿主加载，后端全部锁在 `netstandard2.0`，连 `X509KeyStorageFlags.EphemeralKeySet` 这类具名常量都得写字面值 32。
  若后继项目放弃 .NET Framework MSBuild 宿主，可直接用现代 TFM。
- （观点）文档密度过高：一句一行规则 + 高度压缩的术语让文档难读；`PROJECT_CONTEXT.md` 曾滑向分轮流水账、与代码事实漂移。
  后继项目的"当前事实"文件应短，过程证据只留在 git/PR。
- 签名门禁的语义边界：无 Team 的签名（ad-hoc、自签）视同未签，只靠 `CFBundleIdentifier` 兜底——这是有意边界，但意味着未用 Developer ID 签名的应用没有签名者连续性保护。

## 3. 平台与工具链坑（已实证）

### 3.1 .NET 进程与 libc

- **.NET Unix `Process.Start` spawn/reap 全局锁死锁**：fork 子件卡在 exec 前时持 `s_processStartLock` 读锁不放，SIGCHLD reaper 排写锁饿死，僵尸不回收、后续 spawn 全堵（dotnet/runtime #135294 同族，修进 .NET 12，10.x 无修复）；macOS intel CI 腿实测捕获。
- 在 fork 出的子进程里跑托管代码不可行：子件继承半死的运行时状态，WSL 实测 SIGSEGV——shim 必须是原生的。
- **macOS 27 起提供 `pipe2`/`dup3`**（SDK 标注 `macos(27.0)`）：代码若"有 `pipe2` 就用"并写死 Linux 的 `O_CLOEXEC`（`0x80000`；Darwin 为 `0x1000000`）会得 `EINVAL`；`ENOSYS` 也分平台（Linux 38、Darwin 78）。
  本仓因此在 macOS 27 上全部重定向 spawn 失败，`.app` 自更新被门禁 fail-closed 拒绝（PR #150）。
- **Apple ARM64 变参 ABI**：可变参数走栈，`DllImport fcntl(int,int,int)` 的第三参落寄存器，`F_SETFD` 拿不到 `FD_CLOEXEC`；x86_64 与 Linux ARM64 不受影响（K4）。
- musl 静态引导件必须在 musl 工具链宿主内产（alpine SDK 容器）；glibc 宿主直接 `-r linux-musl-*` 会产出"解释器是 musl、NEEDED 仍挂 glibc"的混血件，musl 上 rc=127（`docs/project/project-rules.md` §3）。
- musl 静态 PIE 无 `.dynsym`，裸 `DllImport("libc")` 运行期解析不到，须 `DirectPInvoke` 逐符号直绑。

### 3.2 macOS

- **`com.apple.provenance`**：从下载来源 App（IDE、Claude 桌面 App 等）拉起的进程所建文件会被自动打上，且受保护删不掉；`ditto` 会把它写进 zip 的 AppleDouble。
  CI runner 没有这个属性，问题只在开发机暴露（K1）。
- macOS 26+ 对测试进程写 `com.apple.quarantine` 不持久化（`xattr -w` 与 `setxattr` 都 rc=0 但读不回），依赖打标的测试只能探测后 Skip（SA 登记）。
- **自签证书签名**：`security find-identity -v` 把信任链不成立（`CSSMERR_TP_NOT_TRUSTED`）的身份排除，但 `codesign` 按 SHA-1 照样能签；自签签出的 `TeamIdentifier` 恒为 `not set`，证书 OU 不会成为 Team ID——依赖 Team ID 的逻辑不能用自签证书测（§3.6）。
- `hdiutil`/`diskimages-helper` 有簿记滞后与 EAGAIN 瞬态：detach 成功后 `hdiutil info` 仍可能列出挂载点，`convert`/`udifderez` 偶发 "Resource temporarily unavailable"——需要有界重试与 `hdiutil info` 复核（`docs/design/formats/mac-dmg.md`）。
- 临时 keychain 应以 `codesign --keychain` 直给，不改用户 keychain 搜索列表；`security import` 对 key+cert 同件 PEM 在部分宿主只进部分条目，需分件补导。
- GitHub 的 `xcode-27` 预览镜像底座是 macOS 27，镜像标签不等于 OS 版本，以 `sw_vers` 实测为准；正式 `macos-27` 标签在本仓停止时尚未上线。

### 3.3 Windows 与 NuGet

- **NuGet 签名包吊销检查**：Windows 上对签名包做 CRL/OCSP 检查走 HTTP/80，受限出口（windows-arm runner）不可达即挂到超时；CI 设 `NUGET_CERT_REVOCATION_MODE=offline`，且 `workflow_call` 不穿透 env，调用方与被调方各设一处（NuGet/Home#11548、#13659）。
- 每个测试独立 `RestorePackagesPath` 会把这类网络暴露面放大约 20 倍；类级共享缓存 + `-p:NuGetAudit=false` + 空闲看门狗 + 有界重试后才稳定（`docs/design/testing.md` §3.11）。
- `netstandard2.0` 引用面没有 `X509KeyStorageFlags.EphemeralKeySet` 具名常量（值 32），且需宿主 .NET Framework ≥4.7.2 或 .NET Core。

### 3.4 NSIS

- POSIX 版 `makensis` 即使传 `-INPUTCHARSET UTF8`，在 C/POSIX locale 下处理非 ASCII 脚本仍报 "Bad text encoding"；交叉构建须显式给 UTF-8 locale（K2）。
- `-OUTPUTCHARSET` 在非 Win32 平台被禁用（warning 5202）。

### 3.5 CI 基建

- GitHub runner 偶发失联、出口劣化窗口（restore 零输出僵死）与进程中断（NSIS 安装器 rc=0xC000013A）——这类故障要留现场、复跑并记录，不能放宽断言；本仓的进程快照、spawn 追踪、restore 追踪工件就是为此建的。
- CI 只覆盖 runner 当时提供的 OS 版本：macOS 27 的 `pipe2` 问题在 CI 全绿期间就存在，直到开发机升级才暴露。

### 3.6 测试设计教训

- **"诚实 Skip"会掩盖从未运行的测试**：`.app` 签名门禁的 5 条自签身份腿从加入起在所有 CI 宿主上一直 Skip，绿灯持续了约一周才被发现；Skip 原因还被误记为"仅 macOS 27"。
  后继项目应定期统计每条测试在各宿主上是否真的运行过，长期全 Skip 的测试要么修、要么删。
- 依赖外部条件（证书、权限、OS 特性）才能触达的逻辑，应把决策逻辑与外部工具调用分开，用测试缝注入桩输出常驻覆盖；真环境腿只验"桩假设与真工具一致"。
- 两份实现（AOT / `sh`）要共用同一张测试表，否则会悄悄分叉。
- 本地复跑前先确认环境：桌面 App 拉起的 shell 是 C locale、新建文件带 provenance，会制造与代码无关的失败。

## 4. 工程与流程经验

- 阶段先立完整路线（能力边界、工具供应与许可、格式语义、阶段表）再写代码，避免边写边猜产品语义——这条在本仓有效，值得保留（`AGENTS.md` §3.3）。
- 一个缺陷处理完整因果链（公共模型、后端、入口、独立包、示例、测试、文档），不只修首个报错。
- 失败留痕：首次失败、调查、复跑都要记录；集成失败即使复跑通过也不能只放宽断言。
- 统一示例覆盖全部公开旋钮（`samples/HelloBundlerApp`）对使用者友好，但维护成本随旋钮线性增长。
- （观点）后继项目按仓库拆分时，以下边界在本仓已比较清晰，可作为拆分参考：Core（编排 + 工具缓存 + 进程执行）、各平台后端族（Windows / macOS / Linux 包 / 归档）、UPDATE（打包侧 + 客户端 + 引导件）、入口（MSBuild / CLI）。
  跨仓库后，"仓外独立包消费"类测试会更重要。

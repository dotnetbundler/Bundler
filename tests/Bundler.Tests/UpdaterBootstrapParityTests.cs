// 引导件双腿平价收编：原宿主腿脚本断言库内化——
// AOT 侧进程内跑 BootstrapPlan.Run（当前源码，覆盖 UpdateTests 未含的平价矩阵），
// POSIX 侧真实跑 `sh src/.../tools/posix/bundler-updater.sh` 子进程，
// 同一断言集双侧各跑一遍，断言退出码与落地文件系统状态完全一致。
// Windows 宿主无 sh，只跑 AOT 侧；符号链接腿在 POSIX 上双侧、Windows 上跳过。
// 退出码契约与 Program.Main 一致：0 成功；2 用法拒绝；3 等待超时；4 换包失败。
using System.Diagnostics;
using System.Text;
using DotNet.Bundler.Updater.Bootstrap;

public static class UpdaterBootstrapParityTests
{
    enum Impl { Aot, Sh }

    // POSIX 上双侧平价；Windows 上仅 AOT（sh 缺席）。
    static IEnumerable<Impl> Impls =>
        TestPlatform.IsWindows ? [Impl.Aot] : [Impl.Aot, Impl.Sh];

    static readonly string PosixScript = Path.Combine(
        RepoRoot(), "src", "Bundler.Updater.Bootstrap", "tools", "posix", "bundler-updater.sh");

    static int Invoke(Impl impl, params string[] args) => impl switch
    {
        Impl.Aot => RunAot(args),
        _ => RunSh(args),
    };

    static int RunAot(string[] args)
    {
        try
        {
            return BootstrapPlan.Run(args);
        }
        catch (UsageException)
        {
            return 2;
        }
        catch (WaitTimeoutException)
        {
            return 3;
        }
        catch
        {
            return 4;
        }
    }

    static int RunSh(string[] args)
    {
        Assert.SkipUnless(File.Exists(PosixScript), $"posix script missing: {PosixScript}");
        var startInfo = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(PosixScript);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(60_000);
        Assert.True(process.HasExited, "bundler-updater.sh did not exit within 60s");
        return process.ExitCode;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bundler.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }

    static string NewDir(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    static string InstallV1(string root)
    {
        var install = NewDir(root, "install");
        Write(Path.Combine(install, "app.txt"), "v1-app");
        Write(Path.Combine(install, "data", "state.txt"), "v1-state");
        return install;
    }

    static string PayloadV2(string root)
    {
        var payload = NewDir(root, "payload");
        Write(Path.Combine(payload, "app.txt"), "v2-app");
        Write(Path.Combine(payload, "data", "state.txt"), "v2-state");
        Write(Path.Combine(payload, "new.txt"), "v2-new");
        return payload;
    }

    static void AssertTree(string expected, string actual)
    {
        var expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(expected, p)).Order().ToList();
        var actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(actual, p)).Order().ToList();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var rel in expectedFiles)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, rel)),
                File.ReadAllBytes(Path.Combine(actual, rel)));
        }
    }

    static string Marker(string install) => install.TrimEnd('/', '\\') + ".bundler-swap";
    static string Backup(string install) => install.TrimEnd('/', '\\') + ".bundler-backup";

    [Fact]
    static void DefaultSwap_BackupLifecycle_AndRollbackRejects()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var log = Path.Combine(dir, "u.log");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
                AssertTree(expected, install);
                Assert.False(Directory.Exists(Backup(install)),
                    "transient backup must be removed after success");
                Assert.False(File.Exists(Marker(install)));

                Assert.Equal(4, Invoke(impl, "apply", "--install-dir", install,
                    "--rollback", "--log", log));
                AssertTree(expected, install);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void RetainBackupTo_LandsHashed_AndRollbackRestores()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var v1 = NewDir(dir, "v1-snapshot");
                CopyTree(install, v1);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var retain = NewDir(dir, "retain");
                var log = Path.Combine(dir, "u.log");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--retain-backup-to", retain, "--log", log));
                AssertTree(expected, install);
                // `--retain-backup-to` 指到备份本身（<名>-<哈希> 命名是库层 XDG 解析的产物）。
                AssertTree(v1, retain);

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--rollback", "--backup-dir", retain, "--log", log));
                AssertTree(v1, install);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void ExplicitBackupDir_IsUsed()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var backup = Path.Combine(dir, "explicit-backup");
                var log = Path.Combine(dir, "u.log");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--backup-dir", backup, "--log", log));
                AssertTree(expected, install);
                Assert.False(Directory.Exists(backup));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void SameOrNested_Reject_AllDirections()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var v1 = NewDir(dir, "v1-snapshot");
                CopyTree(install, v1);
                var log = Path.Combine(dir, "u.log");

                // 同址、payload⊂install、install⊂payload 三向全拒，两目录逐字节完好。
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", install, "--log", log));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", Path.Combine(install, "data"), "--log", log));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir",
                    Path.Combine(install, "data"), "--payload", install, "--log", log));
                // `..` 字面拼写逃逸同样 rc=2。
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", install + Path.DirectorySeparatorChar + ".."
                        + Path.DirectorySeparatorChar + "install", "--log", log));
                AssertTree(v1, install);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void RetainNesting_Reject_IncludingPayloadInsideRetain()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var retain = NewDir(dir, "retain");
                var log = Path.Combine(dir, "u.log");

                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--retain-backup-to", install, "--log", log));
                // payload⊂retain（第六向）：retain 包整个 payload 同样拒。
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", Path.Combine(retain, "payload"), "--retain-backup-to",
                    retain, "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void SymlinkFamily_Reject()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "symlink family legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var log = Path.Combine(dir, "u.log");

                // 目录级 install 被符号链接替换成指向 payload 内目录 → 字面逃脱物理判，rc=2。
                var linkInstall = Path.Combine(dir, "install-link");
                Directory.CreateSymbolicLink(linkInstall, Path.Combine(payload, "data"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", linkInstall,
                    "--payload", Path.Combine(payload, "data"), "--log", log));

                // 二级链：install→link1→payload/data。
                var hop = Path.Combine(dir, "hop-link");
                Directory.CreateSymbolicLink(hop, linkInstall);
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", hop,
                    "--payload", Path.Combine(payload, "data"), "--log", log));

                // 悬挂载荷链接 → rc=2。
                var dangling = Path.Combine(dir, "dangling");
                Directory.CreateSymbolicLink(dangling, Path.Combine(dir, "nonexistent"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", dangling, "--log", log));

                // 环链（自指）→ rc=2 秒回不挂起。
                var self = Path.Combine(dir, "self-link");
                Directory.CreateSymbolicLink(self, self);
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", self, "--log", log));

                // a↔b 互指环 → rc=2。
                var a = Path.Combine(dir, "a");
                var b = Path.Combine(dir, "b");
                Directory.CreateSymbolicLink(a, b);
                Directory.CreateSymbolicLink(b, a);
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", a, "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void LongChain_OverLimit_Reject()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "symlink family legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var log = Path.Combine(dir, "u.log");

                // 45 跳链——超过 41 跳硬限，按环拒绝 rc=2。
                var chain = Path.Combine(dir, "real-target");
                Directory.CreateDirectory(chain);
                var current = chain;
                for (var i = 0; i < 45; i++)
                {
                    var link = Path.Combine(dir, $"link-{i:D2}");
                    Directory.CreateSymbolicLink(link, current);
                    current = link;
                }
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", current, "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void BenignLeafLink_AtBackupSlot_ReplacedWithoutTouchingTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var outside = Write(Path.Combine(dir, "outside.txt"), "outside-data");
                var log = Path.Combine(dir, "u.log");

                // 备份槽预置良性叶链：必须删链节点、不触目标、rc=0。
                File.CreateSymbolicLink(Backup(install), outside);
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
                Assert.Equal("outside-data", File.ReadAllText(outside));
                AssertTree(expected, install);

                // 二次 apply 无楔形（首个 payload 已消费，重建一个）。
                payload = PayloadV2(dir);
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void MarkerAndBackup_CrashRecovery_RestoresThenSwaps()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var v1 = NewDir(dir, "v1-snapshot");
                CopyTree(install, v1);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var log = Path.Combine(dir, "u.log");

                // 模拟换包中途被杀：install→瞬备、install 缺失、marker 在。
                Directory.Move(install, Backup(install));
                File.WriteAllText(Marker(install), "swap-in-progress");
                Assert.False(Directory.Exists(install));

                // 恢复应先还原旧版、再完成本次换包到 v2——终态 v2 无残渣。
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
                AssertTree(expected, install);
                Assert.False(File.Exists(Marker(install)));
                Assert.False(Directory.Exists(Backup(install)));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void BackupParentIsFile_FailsClean_Rc4()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var v1 = NewDir(dir, "v1-snapshot");
                CopyTree(install, v1);
                var payload = PayloadV2(dir);
                var blocker = Write(Path.Combine(dir, "blocker"), "file-not-dir");
                var log = Path.Combine(dir, "u.log");

                // --backup-dir 父段是文件 → 备份建不起来，rc=4、install 完好、无 marker。
                Assert.Equal(4, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--backup-dir",
                    Path.Combine(blocker, "backup"), "--log", log));
                AssertTree(v1, install);
                Assert.False(File.Exists(Marker(install)));
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void FileLevel_Swap_AndRollback()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = Write(Path.Combine(dir, "install", "app.bin"), "v1-bin");
                var payload = Write(Path.Combine(dir, "payload", "app.bin"), "v2-bin");
                var log = Path.Combine(dir, "u.log");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log));
                Assert.Equal("v2-bin", File.ReadAllText(install));
                Assert.False(File.Exists(Backup(install)));

                Assert.Equal(4, Invoke(impl, "apply", "--install-dir", install,
                    "--rollback", "--log", log));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void RetainFailure_WarnDegrades_TransientLeft()
    {
        Assert.SkipWhen(TestPlatform.IsWindows || TestPlatform.IsRoot,
            "permission-based retain failure requires POSIX non-root");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var sealedRetain = NewDir(dir, "sealed-retain");
                File.WriteAllText(Path.Combine(sealedRetain, "filler"), "x");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(sealedRetain, UnixFileMode.UserRead);
                }
                var log = Path.Combine(dir, "u.log");

                var rc = Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--retain-backup-to", sealedRetain, "--log", log);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(sealedRetain,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                // 换包不阻断：rc=0、WARN 降级、瞬备留原位。
                Assert.Equal(0, rc);
                Assert.Contains("WARN", File.ReadAllText(log));
                Assert.True(Directory.Exists(Backup(install)),
                    "transient backup must stay when retain fails");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void ShippedBootstrap_SmokeApplies()
    {
        // 仓内 tools/<rid> 引导件冒烟：守住"源码修了但发布件没重产"的漂变。
        var arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64;
        var rid = TestPlatform.IsWindows ? (arm64 ? "win-arm64" : "win-x64")
            : TestPlatform.IsMacOS ? (arm64 ? "osx-arm64" : "osx-x64")
            : TestPlatform.IsLinux ? TestPlatform.LinuxRuntimeIdentifier
            : null;
        Assert.SkipUnless(rid is not null, "no shipped bootstrap RID for this host");
        var exeName = TestPlatform.IsWindows ? "bundler-updater.exe" : "bundler-updater";
        var binary = Path.Combine(RepoRoot(), "src", "Bundler.Updater.Bootstrap",
            "tools", rid!, exeName);
        Assert.SkipUnless(File.Exists(binary), $"shipped bootstrap missing: {binary}");

        var root = CreateTempDirectory();
        try
        {
            var install = InstallV1(root);
            var payload = PayloadV2(root);
            var expected = NewDir(root, "expected");
            CopyTree(payload, expected);
            var startInfo = new ProcessStartInfo(binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(binary,
                    File.GetUnixFileMode(binary) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            startInfo.ArgumentList.Add("apply");
            startInfo.ArgumentList.Add("--install-dir");
            startInfo.ArgumentList.Add(install);
            startInfo.ArgumentList.Add("--payload");
            startInfo.ArgumentList.Add(payload);
            using var process = Process.Start(startInfo)!;
            Assert.True(process.WaitForExit(60_000));
            Assert.Equal(0, process.ExitCode);
                AssertTree(expected, install);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void RestartApp_CwdMatchesAotContract()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "app stub is a POSIX sh script");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var markerFile = Path.Combine(dir, "cwd.txt");
                var app = Path.Combine(dir, "app-stub.sh");
                File.WriteAllText(app, "#!/bin/sh\npwd > \"" + markerFile + "\"\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(app,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                var log = Path.Combine(dir, "u.log");

                // 目录级：cwd=INSTALL_DIR。
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--app", app, "--log", log));
                Assert.True(WaitForFile(markerFile), "restart stub did not run (dir-level)");
                Assert.Equal(install, File.ReadAllText(markerFile).Trim());
                File.Delete(markerFile);

                // 文件级：cwd=install 的父目录。
                var fileDir = NewDir(dir, "file-level");
                var installFile = Path.Combine(fileDir, "app.bin");
                File.WriteAllText(installFile, "v1");
                var payloadFile = Path.Combine(dir, "payload.bin");
                File.WriteAllText(payloadFile, "v2");
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", installFile,
                    "--payload", payloadFile, "--app", app, "--log", log));
                Assert.True(WaitForFile(markerFile), "restart stub did not run (file-level)");
                Assert.Equal(fileDir, File.ReadAllText(markerFile).Trim());
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ENOSPC 注入腿（AOT 单侧）：注入缝只在进程内可达——sh 侧真盘满断言由宿主腿实证。
    // 通用约定：注入点在移动操作前抛 IOException = 该次原子移动不可用（强制两段式）；
    // 在复制操作前抛 = 该文件复制半途失败；先写截断字节再抛 = 截断半成品形态。
    static void WithIoFault(Action<string, string>? probe, Action body)
    {
        BootstrapPlan.ForceManagedCopy = true;
        BootstrapPlan.IoFaultProbe = probe;
        try
        {
            body();
        }
        finally
        {
            BootstrapPlan.IoFaultProbe = null;
            BootstrapPlan.ForceManagedCopy = false;
        }
    }

    static void AssertNoStagedResidue(string dir) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir, "*.partial-*", SearchOption.AllDirectories));

    [Fact]
    static void IoFault_BackupCopy_StagedResidueCleaned_InstallIntact()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var v1 = Path.Combine(dir, "v1-snapshot");
            CopyTree(install, v1);
            var payload = PayloadV2(dir);
            var backup = Backup(install);
            var log = Path.Combine(dir, "u.log");

            var stagedCopies = 0;
            var rc = 0;
            WithIoFault((source, destination) =>
            {
                // 第一次移动点（install→backup）：抛 IOException 强制两段式。
                if (source == install && destination == backup)
                {
                    throw new IOException("simulated ENOSPC at move");
                }
                // 备份 staged 树内第 2 个复制点炸（枚举顺序不做假设——
                // 计数保证至少一个文件已复制，半途语义才成立）：写截断半成品再炸。
                if (destination.Contains(".partial-"))
                {
                    stagedCopies++;
                    if (stagedCopies == 2)
                    {
                        File.WriteAllBytes(destination, "v1-sta"u8.ToArray());
                        throw new IOException("simulated ENOSPC mid-copy");
                    }
                }
            }, () => rc = RunAot(["apply", "--install-dir", install,
                "--payload", payload, "--log", log]));

            Assert.Equal(4, rc);
            Assert.True(stagedCopies >= 2, "fault must fire mid-copy, not before the first file");
            AssertNoStagedResidue(dir);
            Assert.False(Directory.Exists(backup), "backup slot must stay 'not started' after mid-copy fault");
            AssertTree(v1, install);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void IoFault_BackupStagedRename_StagedResidueCleaned()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var v1 = Path.Combine(dir, "v1-snapshot");
            CopyTree(install, v1);
            var payload = PayloadV2(dir);
            var backup = Backup(install);
            var log = Path.Combine(dir, "u.log");

            var rc = 0;
            WithIoFault((source, destination) =>
            {
                if (destination == backup)
                {
                    // 原子位与 staged 就位两跳都炸：前者强制两段式，后者模拟
                    // "复制全本完成但就位失败"——staged 残渣必须清走。
                    throw new IOException("simulated ENOSPC at backup slot");
                }
            }, () => rc = RunAot(["apply", "--install-dir", install,
                "--payload", payload, "--log", log]));

            Assert.Equal(4, rc);
            AssertNoStagedResidue(dir);
            Assert.False(Directory.Exists(backup));
            AssertTree(v1, install);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void IoFault_PayloadCopy_RollbackRestoresInstall()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var v1 = Path.Combine(dir, "v1-snapshot");
            CopyTree(install, v1);
            var payload = PayloadV2(dir);
            var backup = Backup(install);
            var log = Path.Combine(dir, "u.log");

            var rc = 0;
            WithIoFault((source, destination) =>
            {
                // 载荷 staged 复制途中炸（install.partial-* 名下）：写截断半成品再炸——
                // 备份已成立 → 恢复路径必须把 v1 全本搬回 install。
                if (destination.Contains(".partial-") && destination.EndsWith("new.txt", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(destination, "v2-n"u8.ToArray());
                    throw new IOException("simulated ENOSPC mid-payload-copy");
                }
                if (source == payload && destination == install)
                {
                    throw new IOException("simulated ENOSPC at payload move");
                }
            }, () => rc = RunAot(["apply", "--install-dir", install,
                "--payload", payload, "--log", log]));

            Assert.Equal(4, rc);
            AssertNoStagedResidue(dir);
            Assert.False(Directory.Exists(backup), "backup consumed by restore");
            AssertTree(v1, install);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void IoFault_FileLevelCopy_StagedFileCleaned()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var installFile = Path.Combine(dir, "app.bin");
            File.WriteAllText(installFile, "v1");
            var payloadFile = Path.Combine(dir, "payload.bin");
            File.WriteAllText(payloadFile, "v2");
            var backup = Backup(installFile);
            var log = Path.Combine(dir, "u.log");

            var rc = 0;
            WithIoFault((source, destination) =>
            {
                if (source == installFile && destination == backup)
                {
                    throw new IOException("simulated ENOSPC at move");
                }
                if (destination.Contains(".partial-"))
                {
                    File.WriteAllBytes(destination, "v1-t"u8.ToArray());
                    throw new IOException("simulated ENOSPC mid-file-copy");
                }
            }, () => rc = RunAot(["apply", "--install-dir", installFile,
                "--payload", payloadFile, "--log", log]));

            Assert.Equal(4, rc);
            AssertNoStagedResidue(dir);
            Assert.False(File.Exists(backup));
            Assert.Equal("v1", File.ReadAllText(installFile));
            Assert.Equal("v2", File.ReadAllText(payloadFile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- 并发互斥 / wait-pid / 空间预检 / 路径形态（对标 Squirrel/Sparkle 测试矩阵收编）----

    static string LockPath(string install) => install.TrimEnd('/', '\\') + ".bundler-lock";

    [Fact]
    static void ConcurrentApply_LiveLockHolder_SecondRefusesAfterTimeout()
    {
        // 锁件含活 pid → 第二实例等锁超时拒 rc=3、install 零变更、不留 marker；
        // 释放后同一 apply 成功且锁件随之清除（锁随进程退出释放）。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var lockPath = LockPath(install);
                File.WriteAllText(lockPath, Environment.ProcessId.ToString());
                try
                {
                    Assert.Equal(3, Invoke(impl, "apply", "--install-dir", install,
                        "--payload", payload, "--lock-timeout", "1"));
                    Assert.Equal("v1-app", File.ReadAllText(Path.Combine(install, "app.txt")));
                    Assert.False(File.Exists(Marker(install)));
                }
                finally
                {
                    File.Delete(lockPath);
                }
                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload));
                AssertTree(expected, install);
                Assert.False(File.Exists(lockPath), "released lock must be removed");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void ConcurrentApply_StaleLock_StolenAndSwapCompletes()
    {
        // 崩溃残留锁（owner pid 已死）→ 夺锁完成换包，锁件随成功清除——不留死锁。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);
                var lockPath = LockPath(install);
                File.WriteAllText(lockPath, int.MaxValue.ToString());

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload));
                AssertTree(expected, install);
                Assert.False(File.Exists(lockPath));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void WaitPid_ExitedProcess_SwapProceeds()
    {
        // --wait-pid 指已退出/不存在的进程 → 不等直接换包（等候退语义正向面）。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var expected = NewDir(dir, "expected");
                CopyTree(payload, expected);

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-pid", int.MaxValue.ToString(),
                    "--wait-timeout", "1"));
                AssertTree(expected, install);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void WaitPid_AliveProcess_TimesOutAndInstallIntact()
    {
        // 活进程等不到退出 → rc=3、install 零变更（等候退语义负向面）。
        var root = CreateTempDirectory();
        try
        {
            var sleeperStart = new ProcessStartInfo
            {
                UseShellExecute = false,
                FileName = TestPlatform.IsWindows ? "ping" : "sleep",
            };
            if (TestPlatform.IsWindows)
            {
                sleeperStart.ArgumentList.Add("-n");
                sleeperStart.ArgumentList.Add("30");
                sleeperStart.ArgumentList.Add("127.0.0.1");
            }
            else
            {
                sleeperStart.ArgumentList.Add("30");
            }
            using var sleeper = Process.Start(sleeperStart)!;
            try
            {
                foreach (var impl in Impls)
                {
                    var dir = NewDir(root, "case-" + impl);
                    var install = InstallV1(dir);
                    var payload = PayloadV2(dir);

                    Assert.Equal(3, Invoke(impl, "apply", "--install-dir", install,
                        "--payload", payload, "--wait-pid", sleeper.Id.ToString(),
                        "--wait-timeout", "1"));
                    Assert.Equal("v1-app", File.ReadAllText(Path.Combine(install, "app.txt")));
                }
            }
            finally
            {
                sleeper.Kill();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void NonAsciiInstallPath_SwapCompletes()
    {
        // 非 ASCII/含空格 install 路径换包断言——引用方把应用装进
        // "Program Files\中文目录"类路径时协议不炸（路径均按字面规范化后比较）。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = NewDir(dir, "安装 应用-ünïcode-アプリ");
                Write(Path.Combine(install, "app.txt"), "v1-app");
                var payload = NewDir(dir, "payload 甲");
                Write(Path.Combine(payload, "app.txt"), "v2-app");
                Write(Path.Combine(payload, "新增.txt"), "v2-new");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload));
                Assert.Equal("v2-app", File.ReadAllText(Path.Combine(install, "app.txt")));
                Assert.Equal("v2-new", File.ReadAllText(Path.Combine(install, "新增.txt")));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    static void WithFreeSpaceProbe(Func<string, long?>? probe, Action body)
    {
        BootstrapPlan.FreeSpaceProbe = probe;
        try
        {
            body();
        }
        finally
        {
            BootstrapPlan.FreeSpaceProbe = null;
        }
    }

    [Fact]
    static void FreeSpace_Insufficient_RefusesBeforeMutation()
    {
        // 目标卷探测剩余 0 → 换包在动备份/marker 之前拒 rc=4：install 原树完好、
        // marker/backup/staged 一律不落（Sparkle 式 fail-fast 契约）。
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var v1 = Path.Combine(dir, "v1-snapshot");
            CopyTree(install, v1);
            var payload = PayloadV2(dir);

            // --keep-payload 强制载荷腿产生体积需求（同卷 temp 树下否则需求为空探针不触）。
            var rc = 0;
            WithFreeSpaceProbe(_ => 0, () => rc = RunAot(
                ["apply", "--install-dir", install, "--payload", payload, "--keep-payload"]));

            Assert.Equal(4, rc);
            AssertTree(v1, install);
            Assert.False(File.Exists(Marker(install)));
            Assert.False(Directory.Exists(Backup(install)));
            AssertNoStagedResidue(dir);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void FreeSpace_ProbeFailure_WarnsAndProceeds()
    {
        // 探测失败只 WARN 放行不拒（拒错不如不拒）——换包照常完成。
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var payload = PayloadV2(dir);
            var expected = NewDir(dir, "expected");
            CopyTree(payload, expected);

            var rc = 0;
            WithFreeSpaceProbe(_ => throw new IOException("probe unavailable"),
                () => rc = RunAot(["apply", "--install-dir", install, "--payload", payload,
                    "--keep-payload"]));

            Assert.Equal(0, rc);
            AssertTree(expected, install);
        }
        finally
        {
            Cleanup(root);
        }
    }

    static bool WaitForFile(string path)
    {
        for (var i = 0; i < 100 && !File.Exists(path); i++)
        {
            Thread.Sleep(100);
        }
        return File.Exists(path);
    }

    // 宿主文件锁语义腿：Windows 上被独占打开的安装件不可重命名/删除，
    // apply 必须干净失败且 install 逐字节回到原树；POSIX unlink 不查打开状态，
    // 同一锁对换包无影响——两腿把平台差异钉成断言而非碰运气。
    [Fact]
    static void LockedInstallFile_WindowsCleanFail_PosixSwapSucceeds()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var payload = PayloadV2(dir);
            var expectedInstall = NewDir(dir, "expected-install");
            CopyTree(install, expectedInstall);
            var expectedPayload = NewDir(dir, "expected-payload");
            CopyTree(payload, expectedPayload);
            var log = Path.Combine(dir, "u.log");

            int rc;
            // 锁只挂到 apply 返回即放——断言树要重读同件，独占持有会把 AssertTree
            // 自己的 ReadAllBytes 拒在 Windows 共享语义之外。
            using (var hold = new FileStream(Path.Combine(install, "app.txt"),
                FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rc = Invoke(Impl.Aot, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log);
            }
            if (TestPlatform.IsWindows)
            {
                Assert.Equal(4, rc);
                AssertTree(expectedInstall, install);
                Assert.False(File.Exists(Marker(install)));
                Assert.False(Directory.Exists(Backup(install)));
            }
            else
            {
                Assert.Equal(0, rc);
                AssertTree(expectedPayload, install);
            }
        }
        finally { Cleanup(root); }
    }

    // 只读位拦的是就地改写/删除件本身，拦不住父目录级 rename——apply 在两侧宿主
    // 都能把含只读件的整树换走（真机 Windows 实证 rc=0），换后内容即载荷。
    [Fact]
    static void ReadOnlyInstallFile_SwapSucceeds()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = NewDir(root, "case");
            var install = InstallV1(dir);
            var payload = PayloadV2(dir);
            var expectedPayload = NewDir(dir, "expected-payload");
            CopyTree(payload, expectedPayload);
            var log = Path.Combine(dir, "u.log");

            var target = Path.Combine(install, "app.txt");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            try
            {
                var rc = Invoke(Impl.Aot, "apply", "--install-dir", install,
                    "--payload", payload, "--log", log);
                Assert.Equal(0, rc);
                AssertTree(expectedPayload, install);
            }
            finally
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                }
            }
        }
        finally { Cleanup(root); }
    }

    static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bundler-leg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        // 测试断言按字面拼写与引导件侧规范化结果比较——先把根目录物理化到同一口径
        //（macOS /var→/private/var 这类祖先软链不物理化会让注入探针等值判全部失配）。
        return BootstrapPlan.CanonicalPath(path) ?? path;
    }

    static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

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
        // stdout/stderr 异步消费：任一管道写满未消费会把子进程卡在写端。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail("bundler-updater.sh did not exit within 60s");
        }
        Task.WaitAll(stdout, stderr);
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
    static void OddShapeSlot_Rollback_ByBackupShape()
    {
        // 异型槽：回滚按备份形态选腿而非装位形态——文件装位+目录备份 /
        // 目录装位+文件备份都清槽重建，双侧同断言（AOT DeleteNodeIfPresent /
        // sh rm -rf 平价）。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var log = Path.Combine(dir, "u.log");

                // 文件装位 + 目录备份 → 目录腿清槽重建目录树。
                var fileInstall = Write(Path.Combine(dir, "odd1", "app.bin"), "stale-file");
                var dirBackup = NewDir(dir, "odd1-bksrc");
                Write(Path.Combine(dirBackup, "inner", "state.txt"), "v1-tree");
                Directory.Move(dirBackup, fileInstall + ".bundler-backup");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", fileInstall,
                    "--rollback", "--log", log));
                Assert.True(Directory.Exists(fileInstall));
                Assert.Equal("v1-tree", File.ReadAllText(
                    Path.Combine(fileInstall, "inner", "state.txt")));

                // 目录装位 + 文件备份 → 文件腿清槽还原单件。
                var dirInstall = NewDir(dir, "odd2");
                Write(Path.Combine(dirInstall, "stray"), "stale-tree");
                File.WriteAllText(dirInstall + ".bundler-backup", "v1-file");

                Assert.Equal(0, Invoke(impl, "apply", "--install-dir", dirInstall,
                    "--rollback", "--log", log));
                Assert.True(File.Exists(dirInstall));
                Assert.Equal("v1-file", File.ReadAllText(dirInstall));
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
        // 仓内 tools/<target> 引导件冒烟：守住"源码修了但发布件没重产"的漂变。
        var arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64;
        var target = TestPlatform.IsWindows ? (arm64 ? "windows-aarch64" : "windows-x86_64")
            : TestPlatform.IsMacOS ? (arm64 ? "macos-aarch64" : "macos-x86_64")
            : TestPlatform.IsLinux ? TestPlatform.LinuxTarget
            : null;
        Assert.SkipUnless(target is not null, "no shipped bootstrap target for this host");
        var exeName = TestPlatform.IsWindows ? "bundler-updater.exe" : "bundler-updater";
        var binary = Path.Combine(RepoRoot(), "src", "Bundler.Updater.Bootstrap",
            "tools", target!, exeName);
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
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                Assert.Fail("updater bootstrapper did not exit within 60s");
            }
            Task.WaitAll(stdout, stderr);
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
    static void WaitArgs_NonPositive_RejectedByBothLegs()
    {
        // R2-N8：wait-timeout/wait-pid 的零/负/非数参数双侧统一拒绝 rc=2——
        // AOT 曾放行 wait-timeout 0（立即超时被当成"等 0 秒"），sh 曾收下非整数。
        var root = CreateTempDirectory();
        try
        {
            var install = InstallV1(NewDir(root, "install"));
            var payload = PayloadV2(root);
            foreach (var impl in Impls)
            {
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-timeout", "0"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-timeout", "abc"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-pid", "0"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-pid", "-5"));
                Assert.Equal(2, Invoke(impl, "apply", "--install-dir", install,
                    "--payload", payload, "--wait-pid", "abc"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    static void LockTimeout_Message_RefersToLock_NotProcessWait()
    {
        // R1-#3 收尾：等锁超时的报错曾串 wait-pid 措辞（"did not exit in time"）——
        // 双侧锁定超时消息必须说锁，不串等进程话术。
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "case-" + impl);
                var install = InstallV1(dir);
                var payload = PayloadV2(dir);
                var lockPath = LockPath(install);
                File.WriteAllText(lockPath, Environment.ProcessId.ToString());
                try
                {
                    var (rc, message) = impl == Impl.Aot
                        ? RunAotCapturingMessage("apply", "--install-dir", install,
                            "--payload", payload, "--lock-timeout", "1")
                        : RunShCapturingStderr("apply", "--install-dir", install,
                            "--payload", payload, "--lock-timeout", "1");
                    Assert.Equal(3, rc);
                    Assert.Contains("lock", message);
                    Assert.DoesNotContain("did not exit", message);
                }
                finally
                {
                    File.Delete(lockPath);
                }
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // AOT 腿取异常消息（WaitTimeoutException.Message），sh 腿取 stderr——同断言口径。
    static (int rc, string message) RunAotCapturingMessage(params string[] args)
    {
        try
        {
            return (BootstrapPlan.Run(args), "");
        }
        catch (UsageException)
        {
            return (2, "");
        }
        catch (WaitTimeoutException exception)
        {
            return (3, exception.Message);
        }
        catch
        {
            return (4, "");
        }
    }

    static (int rc, string stderr) RunShCapturingStderr(params string[] args)
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail("bundler-updater.sh did not exit within 60s");
        }
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stderr.Result);
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

    // ---- pkg-apply 支路真跑奇偶（stub 垫片非特权全链）----
    // stub 命令经双侧同名 env 缝注入（BUNDLER_UPDATER_CMD_<TOOL>）：AOT 腿走
    // EnvProbe/CommandProbe 权威注入（不污染进程 PATH、不触真实包管理器），
    // sh 腿走子进程环境块 + 白名单 shim PATH——两腿跑的是同一组 stub 脚本、
    // 同一组 env 值，断言同一份记录件。
    // pkg-apply 退出码：0 成功；3 参数错；4 安装失败；5 pm 缺席；6 重启失败。

    static string PkgRealTool(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return name;
    }

    // sh 腿 PATH 白名单：只有协议需要的基础工具——真实包管理器一律不在 PATH，
    // "缺席"语义不依赖宿主是否恰好装着 apt/dnf（Debian 系宿主也照样判 rc5）。
    static void SeedShim(string shim)
    {
        foreach (var tool in new[] { "env", "dirname", "awk", "ls", "sed", "nohup", "tr" })
        {
            var real = PkgRealTool(tool);
            if (real != tool && !File.Exists(Path.Combine(shim, tool)))
            {
                File.CreateSymbolicLink(Path.Combine(shim, tool), real);
            }
        }
    }

    static string WritePkgStub(string path, string body)
    {
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    // 包管理器 stub：记录 <名> <argv>|DF=<DEBIAN_FRONTEND> 一行后按 rc 退出。
    static string PmStub(string bin, string name, int rc, string rec) =>
        WritePkgStub(Path.Combine(bin, name),
            "#!/bin/sh\n" +
            "{ printf '%s' '" + name + "'; for a do printf ' %s' \"$a\"; done;" +
            " printf '|DF=%s\\n' \"${DEBIAN_FRONTEND:-}\"; } >> '" + rec + "'\n" +
            "exit " + rc + "\n");

    // 降权工具 stub：记录 <名> <argv> 一行后链式执行内层——
    // setpriv/sudo/runuser 腿在 argv 里找 `env` 标记并 exec 其后内容，
    // su 腿找 `-c` 并交给 /bin/sh——端到端验证 env 回灌真的到达 --app。
    static string DropStub(string bin, string name, string rec) =>
        WritePkgStub(Path.Combine(bin, name),
            "#!/bin/sh\n" +
            "{ printf '%s' '" + name + "'; for a do printf ' %s' \"$a\"; done; printf '\\n'; } >> '" + rec + "'\n" +
            "while [ $# -gt 0 ]; do\n" +
            "    if [ \"$1\" = \"-c\" ]; then exec /bin/sh -c \"$2\"; fi\n" +
            "    if [ \"$1\" = \"env\" ]; then shift; exec env \"$@\"; fi\n" +
            "    shift\n" +
            "done\n" +
            "exit 0\n");

    // 重启目标 stub：落 pwd 与会话 env 实测值——回灌断言的证据面。
    static string AppStub(string dir, string rec) =>
        WritePkgStub(Path.Combine(dir, "app-under-test"),
            "#!/bin/sh\n" +
            "{ printf 'app|pwd=%s' \"$PWD\";" +
            " printf '|DISPLAY=%s|XAUTHORITY=%s|DBUS=%s|WAYLAND=%s|XDG=%s|HOME=%s|MYVAR=%s\\n'" +
            " \"${DISPLAY:-}\" \"${XAUTHORITY:-}\" \"${DBUS_SESSION_BUS_ADDRESS:-}\"" +
            " \"${WAYLAND_DISPLAY:-}\" \"${XDG_RUNTIME_DIR:-}\" \"${HOME:-}\" \"${MYVAR:-}\"; } >> '" + rec + "'\n");

    static string[] WaitRecord(string rec, int minLines)
    {
        for (var i = 0; i < 100; i++)
        {
            var lines = File.Exists(rec) ? File.ReadAllLines(rec) : [];
            if (lines.Length >= minLines)
            {
                return lines;
            }
            Thread.Sleep(100);
        }
        return File.Exists(rec) ? File.ReadAllLines(rec) : [];
    }

    static int InvokePkg(Impl impl, IReadOnlyDictionary<string, string> env,
        string shimPath, params string[] args)
    {
        if (impl == Impl.Aot)
        {
            PkgApplyPlan.EnvProbe = name =>
                env.TryGetValue(name, out var value) ? value : null;
            // CommandProbe 权威缺席：一切命令经 BUNDLER_UPDATER_CMD_* 缝供给，
            // 真实 PATH 上的包管理器对 AOT 腿同样不可见。
            PkgApplyPlan.CommandProbe = _ => null;
            try
            {
                return BootstrapPlan.Run(args);
            }
            finally
            {
                PkgApplyPlan.EnvProbe = null;
                PkgApplyPlan.CommandProbe = null;
            }
        }
        var startInfo = new ProcessStartInfo("/bin/sh")
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
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = shimPath;
        foreach (var pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail("bundler-updater.sh pkg-apply did not exit within 60s");
        }
        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }

    static Dictionary<string, string> PkgEnv(params (string Key, string Value)[] pairs)
    {
        var env = new Dictionary<string, string>
        {
            ["BUNDLER_UPDATER_CMD_NOHUP"] = PkgRealTool("nohup"),
        };
        foreach (var (key, value) in pairs)
        {
            env[key] = value;
        }
        return env;
    }

    [Fact]
    static void PkgApply_Deb_FallbackChain_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "deb-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_APT_GET", PmStub(bin, "apt-get", 1, rec)),
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)));

                var rc = InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app);

                Assert.Equal(0, rc);
                var lines = WaitRecord(rec, 3);
                Assert.Equal(
                    $"apt-get install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold {pkg}|DF=noninteractive",
                    lines[0]);
                Assert.Equal(
                    $"dpkg -i --force-confdef --force-confold {pkg}|DF=noninteractive",
                    lines[1]);
                Assert.StartsWith("app|pwd=" + dir, lines[2]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_Deb_AllFail_Rc4_AndPmMissing_Rc5_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "deb5-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);

                // 在场前端全败 → rc4（两腿退化记录齐备）。
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_APT_GET", PmStub(bin, "apt-get", 9, rec)),
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 7, rec)));
                Assert.Equal(4, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                Assert.Equal(2, WaitRecord(rec, 2).Length);

                // 包管理器全缺席 → rc5（记录件不再增长——重启腿也没跑）。
                File.Delete(rec);
                env.Remove("BUNDLER_UPDATER_CMD_APT_GET");
                env.Remove("BUNDLER_UPDATER_CMD_DPKG");
                Assert.Equal(5, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                Thread.Sleep(200);
                Assert.False(File.Exists(rec));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_Rpm_ChainOrder_AndRollback_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "rpm-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.rpm"), "pkg-bytes");
                var app = AppStub(dir, rec);

                // dnf 缺席跳过 → zypper 失败 → rpm -U 成功。
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_ZYPPER", PmStub(bin, "zypper", 5, rec)),
                    ("BUNDLER_UPDATER_CMD_RPM", PmStub(bin, "rpm", 0, rec)));
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "rpm", "--app", app));
                var lines = WaitRecord(rec, 3);
                Assert.Equal($"zypper install -y {pkg}|DF=", lines[0]);
                Assert.Equal($"rpm -U {pkg}|DF=", lines[1]);
                Assert.StartsWith("app|pwd=", lines[2]);

                // --rollback 子形态：rpm 末位换 --oldpackage。
                File.Delete(rec);
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "rpm", "--app", app, "--rollback"));
                lines = WaitRecord(rec, 3);
                Assert.Equal($"rpm -U --oldpackage {pkg}|DF=", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_Apk_AllowUntrusted_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "apk-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0.apk"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var keysDir = NewDir(dir, "apk-keys");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_APK", PmStub(bin, "apk", 0, rec)),
                    ("BUNDLER_UPDATER_APK_KEYS_DIR", keysDir));

                // keys 目录空 → --allow-untrusted。
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "apk", "--app", app));
                Assert.Equal($"apk add --allow-untrusted {pkg}|DF=", WaitRecord(rec, 2)[0]);

                // 预置公钥在场 → 不加旗标。
                File.Delete(rec);
                File.WriteAllText(Path.Combine(keysDir, "bundler-abc123.rsa.pub"), "pub");
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "apk", "--app", app));
                Assert.Equal($"apk add {pkg}|DF=", WaitRecord(rec, 2)[0]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_SetprivDrop_EnvReplay_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "drop-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var envFile = Write(Path.Combine(dir, "session.env"),
                    "DISPLAY=:9\nMYVAR=a b\nBAD KEY=x\n");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("BUNDLER_UPDATER_CMD_SETPRIV", DropStub(bin, "setpriv", rec)),
                    ("PKEXEC_UID", "4242"),
                    ("SUDO_UID", "5000"),   // PKEXEC_UID 优先
                    ("SUDO_GID", "4343"));

                var rc = InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile);

                Assert.Equal(0, rc);
                var lines = WaitRecord(rec, 3);
                Assert.Equal(
                    $"setpriv --reuid=4242 --regid=4343 --clear-groups env DISPLAY=:9 MYVAR=a b {app}",
                    lines[1]);
                // 端到端：env 件真的灌进了 --app（含空格值与坏行过滤）。
                Assert.StartsWith("app|pwd=" + dir, lines[2]);
                Assert.Contains("DISPLAY=:9", lines[2]);
                Assert.Contains("MYVAR=a b", lines[2]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_DropFallbackOrder_SudoRunuserSu_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "order-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var envFile = Write(Path.Combine(dir, "session.env"), "DISPLAY=:9\n");
                var passwd = Write(Path.Combine(dir, "fake-passwd"),
                    "root:x:0:0:root:/root:/bin/sh\n" +
                    "tester:x:4242:4343:Test User:/home/tester:/bin/sh\n");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("BUNDLER_UPDATER_CMD_SUDO", DropStub(bin, "sudo", rec)),
                    ("BUNDLER_UPDATER_CMD_RUNUSER", DropStub(bin, "runuser", rec)),
                    ("BUNDLER_UPDATER_CMD_SU", DropStub(bin, "su", rec)),
                    ("SUDO_UID", "4242"),
                    ("BUNDLER_UPDATER_PASSWD_FILE", passwd));

                // setpriv 缺席 → sudo -u #uid（内层 env 重建，env_reset 剥外层也不怕）。
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile));
                var lines = WaitRecord(rec, 3);
                Assert.Equal($"sudo -u #4242 env DISPLAY=:9 {app}", lines[1]);
                Assert.Contains("DISPLAY=:9", lines[2]);

                // sudo 缺席 → runuser -u <passwd 查名>。
                File.Delete(rec);
                env.Remove("BUNDLER_UPDATER_CMD_SUDO");
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile));
                lines = WaitRecord(rec, 3);
                Assert.Equal($"runuser -u tester -- env DISPLAY=:9 {app}", lines[1]);
                Assert.Contains("DISPLAY=:9", lines[2]);

                // runuser 缺席 → su -s /bin/sh -c 'exec env <quoted> <quoted app>' <name>。
                File.Delete(rec);
                env.Remove("BUNDLER_UPDATER_CMD_RUNUSER");
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile));
                lines = WaitRecord(rec, 3);
                Assert.Equal(
                    $"su -s /bin/sh -c exec env 'DISPLAY=:9' '{app}' tester",
                    lines[1]);
                Assert.Contains("DISPLAY=:9", lines[2]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_DirectSpawn_EnvReplay_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "direct-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var envFile = Write(Path.Combine(dir, "session.env"),
                    "DISPLAY=:9\nMYVAR=a b\n");
                // uid 变量全缺席=本来就 root 直跑——按当前权限直接 spawn。
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile));
                var lines = WaitRecord(rec, 2);
                Assert.StartsWith("app|pwd=" + dir, lines[1]);
                Assert.Contains("DISPLAY=:9", lines[1]);
                Assert.Contains("MYVAR=a b", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_NoEnvFile_MinimalEnv_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "minenv-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var log = Path.Combine(dir, "u.log");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("HOME", "/home/minimal"),
                    ("DISPLAY", ":7"));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--log", log));
                var lines = WaitRecord(rec, 2);
                // env 件缺省→最小集 HOME+DISPLAY 并记日志（双侧同断言）。
                Assert.Contains("no --env-file", File.ReadAllText(log));
                Assert.Contains("HOME=/home/minimal", lines[1]);
                Assert.Contains("DISPLAY=:7", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_DirectoryApp_Rc6_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "dirapp-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                // --app 指目录：-e/-x 双过的旧口径会让 nohup exec 目录被 >/dev/null
                // 吞成 rc0 假成功——普通文件判据（-f / File.Exists）两侧同拒 rc6。
                var appDir = NewDir(dir, "app-dir");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)));

                Assert.Equal(6, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", appDir));
                var lines = WaitRecord(rec, 1);
                // 安装已提交（pm 记录在场）但重启被拒——不得有 app 记录。
                Assert.StartsWith("dpkg ", lines[0]);
                Assert.Single(lines);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_DirectoryEnvFile_MinimalEnv_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "direnv-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                // env-file 指目录：-r 判真的旧口径会零对放行——env 零覆盖继承
                // 调用方全套 env。非普通文件按"读失败"退化最小集（双侧同口径）。
                var envDir = NewDir(dir, "env-dir");
                var log = Path.Combine(dir, "u.log");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("HOME", "/home/minimal"),
                    ("DISPLAY", ":7"));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envDir, "--log", log));
                var lines = WaitRecord(rec, 2);
                Assert.Contains("cannot read env file", File.ReadAllText(log));
                Assert.StartsWith("app|pwd=" + dir, lines[1]);
                Assert.Contains("HOME=/home/minimal", lines[1]);
                Assert.Contains("DISPLAY=:7", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_SpecialFiles_Reject_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "fifo-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var log = Path.Combine(dir, "u.log");
                // fifo 特殊件（exec 位也授上——隔离"非普通文件"判拒本身）：
                // sh -f 判拒、AOT IsRegularFile 判拒；File.Exists 只排目录的旧口径会收。
                var fifo = Path.Combine(dir, "fifo");
                using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", fifo)
                       { UseShellExecute = false })!)
                {
                    mkfifo.WaitForExit();
                    Assert.Equal(0, mkfifo.ExitCode);
                }
                if (!OperatingSystem.IsWindows())
                {
                    // CA1416 不认 SkipWhen——mode 面挂在非 Windows 分支
                    File.SetUnixFileMode(fifo,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("HOME", "/home/minimal"),
                    ("DISPLAY", ":7"));

                // --pkg 指 fifo → rc3，pm 零调用（记录件不落）。
                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", fifo, "--format", "deb", "--app", app));
                Thread.Sleep(200);
                Assert.False(File.Exists(rec));

                // --app 指 fifo → rc6：安装已提交但重启判拒。
                Assert.Equal(6, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", fifo));
                var lines = WaitRecord(rec, 1);
                Assert.StartsWith("dpkg ", lines[0]);
                Assert.Single(lines);

                // --env-file 指 fifo → "读失败"WARN + 最小集（ReadLines 会挂等
                // 写端的旧口径在本断言上死等——判拒腿必须先行）。
                File.Delete(rec);
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", fifo, "--log", log));
                lines = WaitRecord(rec, 2);
                Assert.Contains("cannot read env file", File.ReadAllText(log));
                Assert.Contains("HOME=/home/minimal", lines[1]);
                Assert.Contains("DISPLAY=:7", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_ZeroPairsEnvFile_MinimalEnv_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "zeroenv-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                // env 件可读但零有效对：同走最小集——空对让内层 env 零覆盖，
                // 会把调用方全套 env（pkexec 语境=root env）灌给降权 app。
                var envFile = Write(Path.Combine(dir, "session.env"),
                    "1BAD=x\n=orphan\nNO_EQUALS\nA B=x\n");
                var log = Path.Combine(dir, "u.log");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("HOME", "/home/minimal"),
                    ("DISPLAY", ":7"));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile, "--log", log));
                var lines = WaitRecord(rec, 2);
                Assert.Contains("no usable assignments", File.ReadAllText(log));
                Assert.Contains("HOME=/home/minimal", lines[1]);
                Assert.Contains("DISPLAY=:7", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_EnvFileCrlf_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "crlf-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                // CRLF 件：行尾 \r 不透进值——AOT ReadLines 按 \r\n 终结，
                // sh 侧 tr 折行同口径。
                var envFile = Write(Path.Combine(dir, "session.env"),
                    "DISPLAY=:9\r\nMYVAR=a b\r\n");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--env-file", envFile));
                var lines = WaitRecord(rec, 2);
                Assert.Contains("|DISPLAY=:9|XAUTHORITY=", lines[1]);
                // \r 若漏进值尾会让记录行以 "\r" 收尾——EndsWith 精确判。
                Assert.EndsWith("|MYVAR=a b", lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_UnwritableLog_Continues_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "badlog-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                // --log 父目录缺席写必败：日志失败降级 WARN 不阻断装包
                //（旧 AOT AppendAllText 抛出即 rc4——双侧统一降级口径）。
                var log = Path.Combine(dir, "missing-log-dir", "u.log");
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)));

                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
                    "--log", log));
                var lines = WaitRecord(rec, 2);
                Assert.StartsWith("dpkg ", lines[0]);
                Assert.StartsWith("app|pwd=" + dir, lines[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_UidGidEdgeCases_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "uidedge-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                var dpkg = PmStub(bin, "dpkg", 0, rec);
                var setpriv = DropStub(bin, "setpriv", rec);
                // 空 passwd 件钉死查名——真实宿主 /etc/passwd 凑巧含该 uid 会翻进
                // passwd gid/家目录让断言漂移。
                var emptyPasswd = Write(Path.Combine(dir, "empty-passwd"), "");

                // PKEXEC_UID 超 int64 → 值缺席落 SUDO_UID 兜底。
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", dpkg),
                    ("BUNDLER_UPDATER_CMD_SETPRIV", setpriv),
                    ("PKEXEC_UID", "99999999999999999999"),
                    ("SUDO_UID", "4242"),
                    ("SUDO_GID", "4343"),
                    ("BUNDLER_UPDATER_PASSWD_FILE", emptyPasswd));
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                Assert.Contains(
                    $"setpriv --reuid=4242 --regid=4343 --clear-groups env {app}",
                    WaitRecord(rec, 2)[1]);

                // 前导零 uid/gid 规范化进 argv。
                File.Delete(rec);
                env["PKEXEC_UID"] = "0042";
                env["SUDO_UID"] = "5000";
                env["SUDO_GID"] = "043";
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                Assert.Contains(
                    $"setpriv --reuid=42 --regid=43 --clear-groups env {app}",
                    WaitRecord(rec, 2)[1]);

                // passwd gid 段非数字 → 回退 uid（含 SUDO_GID 缺席时 passwd 兜底）。
                File.Delete(rec);
                var passwd = Write(Path.Combine(dir, "fake-passwd"),
                    "tester:x:4242:notanum:Test:/home/tester:/bin/sh\n");
                env["PKEXEC_UID"] = "4242";
                env.Remove("SUDO_UID");
                env.Remove("SUDO_GID");
                env["BUNDLER_UPDATER_PASSWD_FILE"] = passwd;
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                // gid 非数字回退 uid；passwd 家目录按降权口径进最小集。
                Assert.Contains(
                    $"setpriv --reuid=4242 --regid=4242 --clear-groups env HOME=/home/tester {app}",
                    WaitRecord(rec, 2)[1]);

                // 字面含转义形态的 uid（"1\060"）：sh 旧 awk -v 解 \060 转义
                // 会误收成 uid 10——ENVIRON 传值后与 AOT 逐字符数字判同拒，
                // 按"值缺席"落 SUDO_UID 兜底（双侧同断言）。
                File.Delete(rec);
                env["PKEXEC_UID"] = "1\\060";
                env["SUDO_UID"] = "4242";
                env["SUDO_GID"] = "4343";
                env["BUNDLER_UPDATER_PASSWD_FILE"] = emptyPasswd;
                Assert.Equal(0, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                Assert.Contains(
                    $"setpriv --reuid=4242 --regid=4343 --clear-groups env {app}",
                    WaitRecord(rec, 2)[1]);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_RestartFailure_Rc6_InstallCommitted_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "rc6-" + impl);
                var bin = NewDir(dir, "bin");
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var rec = Path.Combine(dir, "rec.txt");
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, rec);
                // uid 在场而降权工具全缺席 → rc6：安装已提交（pm 记录在场）但重启没发生。
                var env = PkgEnv(
                    ("BUNDLER_UPDATER_CMD_DPKG", PmStub(bin, "dpkg", 0, rec)),
                    ("PKEXEC_UID", "4242"));

                Assert.Equal(6, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app));
                var lines = WaitRecord(rec, 1);
                Assert.StartsWith("dpkg ", lines[0]);
                Assert.Single(lines);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void PkgApply_UsageErrors_Rc3_Parity()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "pkg-apply real-exec parity legs run on POSIX hosts");
        var root = CreateTempDirectory();
        try
        {
            foreach (var impl in Impls)
            {
                var dir = NewDir(root, "usage-" + impl);
                var shim = NewDir(dir, "shim");
                SeedShim(shim);
                var pkg = Write(Path.Combine(dir, "app-2.0-1.deb"), "pkg-bytes");
                var app = AppStub(dir, Path.Combine(dir, "rec.txt"));
                var env = PkgEnv();

                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--format", "deb", "--app", app));
                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "deb"));
                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--app", app));
                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", pkg, "--format", "msi", "--app", app));
                Assert.Equal(3, InvokePkg(impl, env, shim,
                    "pkg-apply", "--pkg", Path.Combine(dir, "missing.deb"),
                    "--format", "deb", "--app", app));
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

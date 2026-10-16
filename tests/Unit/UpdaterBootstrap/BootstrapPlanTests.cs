using DotNet.Bundler.Updater.Bootstrap;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="BootstrapPlan"/> 三段式换包的进程内面：参数面/退出码、备份+换包+标记位、
/// 互嵌拒绝、崩溃恢复、并发锁、空间/IO 故障缝。重启与等待外部进程不是本层事——
/// 测试恒不传 <c>--app</c>/<c>--wait-pid</c>，<see cref="BootstrapPlan.ForceManagedCopy"/>
/// 置真让 POSIX 腿也走托管复制，保证零子进程。
/// </summary>
public sealed class BootstrapPlanTests : TempBase
{
    private readonly List<string> _log = [];

    public BootstrapPlanTests()
    {
        BootstrapPlan.ForceManagedCopy = true;
    }

    public override void Dispose()
    {
        BootstrapPlan.ForceManagedCopy = false;
        BootstrapPlan.FreeSpaceProbe = null;
        BootstrapPlan.IoFaultProbe = null;
        BootstrapPlan.MountInfoProbe = null;
        PkgApplyPlan.CommandProbe = null;
        PkgApplyPlan.EnvProbe = null;
        PkgApplyPlan.ExecProbe = null;
        PkgApplyPlan.SpawnProbe = null;
        base.Dispose();
    }

    // ---- 参数面 / 退出码 ----

    [Fact]
    public void Main_NoArgs_UsageError2()
    {
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main([]));
    }

    [Fact]
    public void Main_UnknownCommand_UsageError2()
    {
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main(["frobnicate"]));
    }

    [Fact]
    public void Main_MissingInstallDir_UsageError2()
    {
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main(["apply", "--payload", Path.Combine(Root, "p")]));
    }

    [Fact]
    public void Main_MissingPayload_UsageError2()
    {
        var install = Path.Combine(Root, "i1");
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main(["apply", "--install-dir", install]));
    }

    [Theory]
    [InlineData("--wait-pid", "abc")]
    [InlineData("--wait-pid", "0")]
    [InlineData("--wait-pid", "-5")]
    [InlineData("--wait-timeout", "0")]
    [InlineData("--lock-timeout", "-1")]
    public void Main_InvalidNumericOption_UsageError2(string option, string value)
    {
        var install = Path.Combine(Root, "i-" + option.TrimStart('-'));
        var payload = Path.Combine(Root, "p-" + option.TrimStart('-'));
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["apply", "--install-dir", install, "--payload", payload, option, value]));
    }

    [Fact]
    public void Main_UnknownOption_UsageError2()
    {
        Assert.Equal(2, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["apply", "--install-dir", Path.Combine(Root, "i"), "--payload",
             Path.Combine(Root, "p"), "--frobnicate", "x"]));
    }

    [Fact]
    public void Main_OrphanMarkerWithoutBackup_Returns4()
    {
        // marker 在而备份缺席 → UpdateRejectedException → Main 映射 rc 4。
        var install = Dir("install-rc4", ("a", "b"));
        var payload = Dir("payload-rc4", ("a", "c"));
        File.WriteAllText(install + ".bundler-swap", "swap in progress");

        var rc = DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["apply", "--install-dir", install, "--payload", payload]);

        Assert.Equal(4, rc);
    }

    [Fact]
    public void Main_MissingInstallParent_UsageError2NotLockTimeout()
    {
        // 装位父目录缺席时建锁必败——按用法错（rc2）拒绝，
        // 不能空转到 --lock-timeout 报假竞争（rc3）。
        var install = Path.Combine(Root, "absent-parent", "install");
        var payload = Dir("payload-noparent", ("a", "b"));

        var rc = DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["apply", "--install-dir", install, "--payload", payload, "--lock-timeout", "0"]);

        Assert.Equal(2, rc);
    }

    // ---- 换包语义 ----

    [Fact]
    public void Apply_Swap_ReplacesInstallWithPayload()
    {
        var install = Dir("install-swap", ("app.txt", "old"));
        var payload = Dir("payload-swap", ("app.txt", "new"), ("added.txt", "a"));

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(install, "added.txt")));
        Assert.False(Directory.Exists(payload), "默认移动换包——载荷目录应被消费");
        Assert.False(File.Exists(install + ".bundler-swap"), "marker 必须回收");
        Assert.False(Directory.Exists(install + ".bundler-backup"), "默认不保留回滚点");
    }

    [Fact]
    public void Apply_KeepPayload_PayloadDirectoryStays()
    {
        var install = Dir("install-keep", ("app.txt", "old"));
        var payload = Dir("payload-keep", ("app.txt", "new"));

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            KeepPayload = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.True(Directory.Exists(payload), "--keep-payload 载荷按复制换入");
    }

    [Fact]
    public void Apply_RetainBackup_BackupMovesToRetainDir()
    {
        var install = Dir("install-retain", ("app.txt", "old"));
        var payload = Dir("payload-retain", ("app.txt", "new"));
        var retain = Path.Combine(Root, "rollback-points");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            RetainBackupDirectory = retain,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.True(Directory.Exists(retain), "--retain-backup-to 的备份要落到保留区");
        Assert.Equal("old", File.ReadAllText(Path.Combine(retain, "app.txt")));
    }

    [Fact]
    public void Apply_Rollback_RestoresBackupCopy()
    {
        var install = Dir("install-rb", ("app.txt", "broken"));
        var backup = Dir("install-rb.bundler-backup", ("app.txt", "good"));

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("good", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.True(Directory.Exists(backup), "回滚复制不消耗备份——保留可重试");
    }

    [Fact]
    public void Apply_RollbackMarkerRecovery_RestoresByCopyKeepingBackup()
    {
        var install = Dir("install-rbrec", ("app.txt", "half-restored"));
        var backup = Dir("install-rbrec.bundler-backup", ("app.txt", "good"));
        // 上轮回滚死在 Delete+Copy 中途："rollback" marker + 半成品安装位 + 完整备份。
        File.WriteAllText(install + ".bundler-swap", "rollback in progress");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("good", File.ReadAllText(Path.Combine(install, "app.txt")));
        // 回滚恢复是复制语义：备份不被消耗（留作可重试回滚点）——与 swap 恢复的移动语义相对。
        Assert.Equal("good", File.ReadAllText(Path.Combine(backup, "app.txt")));
        Assert.False(File.Exists(install + ".bundler-swap"));
    }

    [Fact]
    public void Apply_RollbackCopyFailure_LeavesRollbackMarkerForRecovery()
    {
        var install = Dir("install-rbfail", ("app.txt", "broken"));
        Dir("install-rbfail.bundler-backup", ("app.txt", "good"));
        BootstrapPlan.IoFaultProbe = (_, _) => throw new IOException("simulated ENOSPC");

        Assert.Throws<IOException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            Rollback = true,
        }));

        // 回滚半途失败必须留 "rollback" marker——下轮按内容分流走复制恢复，不是移动。
        Assert.Equal("rollback in progress", File.ReadAllText(install + ".bundler-swap"));

        BootstrapPlan.IoFaultProbe = null;
        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("good", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.False(File.Exists(install + ".bundler-swap"));
    }

    [Fact]
    public void Apply_MarkerCrashRecovery_RestoresBackupThenSwaps()
    {
        var install = Dir("install-crash", ("app.txt", "half-written"));
        Dir("install-crash.bundler-backup", ("app.txt", "old-good"));
        var payload = Dir("payload-crash", ("app.txt", "new"));
        File.WriteAllText(install + ".bundler-swap", "swap in progress");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.False(File.Exists(install + ".bundler-swap"));
    }

    [Fact]
    public void Apply_MarkerWithoutBackup_Rejected()
    {
        var install = Dir("install-orphan", ("app.txt", "x"));
        var payload = Dir("payload-orphan", ("app.txt", "y"));
        File.WriteAllText(install + ".bundler-swap", "swap in progress");

        Assert.Throws<UpdateRejectedException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
        }));
    }

    // ---- 拒绝面 ----

    [Fact]
    public void Apply_PayloadInsideInstall_Rejected()
    {
        var install = Dir("install-nest", ("app.txt", "x"));
        var payload = Dir("install-nest" + Path.DirectorySeparatorChar + "payload",
            ("app.txt", "y"));

        Assert.Throws<UsageException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
        }));
    }

    [Fact]
    public void Apply_BackupInsideInstall_Rejected()
    {
        var install = Dir("install-bnest", ("app.txt", "x"));
        var payload = Dir("payload-bnest", ("app.txt", "y"));
        var backup = Path.Combine(install, "backup");

        Assert.Throws<UsageException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            BackupDirectory = backup,
        }));
    }

    [Fact]
    public void Apply_MissingInstall_UsageError()
    {
        Assert.Throws<UsageException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = Path.Combine(Root, "does-not-exist"),
            PayloadDirectory = Dir("payload-m", ("a", "b")),
        }));
    }

    [Fact]
    public void Apply_MissingPayload_UsageError()
    {
        var install = Dir("install-m", ("a", "b"));
        Assert.Throws<UsageException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = Path.Combine(Root, "no-payload"),
        }));
    }

    [Fact]
    public void Apply_FileInstallUnit_SwapsSingleFile()
    {
        var installFile = Path.Combine(Root, "app.AppImage");
        File.WriteAllText(installFile, "old-image");
        var payloadFile = Path.Combine(Root, "new.AppImage");
        File.WriteAllText(payloadFile, "new-image");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = installFile,
            PayloadDirectory = payloadFile,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new-image", File.ReadAllText(installFile));
        Assert.False(File.Exists(payloadFile));
    }

    [Fact]
    public void Apply_FileRollback_RestoresBackupFile()
    {
        var installFile = Path.Combine(Root, "tool.AppImage");
        File.WriteAllText(installFile, "broken-image");
        File.WriteAllText(installFile + ".bundler-backup", "good-image");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = installFile,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("good-image", File.ReadAllText(installFile));
    }

    [Fact]
    public void Apply_FileRollback_DirNodeInInstallSlot_ClearsAndRestoresFile()
    {
        // 异型槽：文件级备份在场而安装槽挂着目录节点（形态切换后的回滚）——
        // 清槽还原而非把备份复制进目录内部（sh rm -rf 同语义）。
        var installPath = Path.Combine(Root, "tool.AppImage");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "stray"), "stale");
        File.WriteAllText(installPath + ".bundler-backup", "good-image");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = installPath,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.True(File.Exists(installPath));
        Assert.Equal("good-image", File.ReadAllText(installPath));
    }

    [Fact]
    public void Apply_Rollback_FileNodeInInstallSlot_ClearsAndRestoresTree()
    {
        // 反向异型槽：目录级备份在场而安装槽挂着文件节点——清槽重建目录树。
        var install = Path.Combine(Root, "install-oddslot");
        File.WriteAllText(install, "stale-file");
        Dir("install-oddslot.bundler-backup", ("app.txt", "good"));

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            Rollback = true,
        });

        Assert.Equal(0, rc);
        Assert.Equal("good", File.ReadAllText(Path.Combine(install, "app.txt")));
    }

    // ---- 并发锁 ----

    [Fact]
    public void Apply_LiveLockHolder_TimesOutRc3()
    {
        var install = Dir("install-lock", ("a", "b"));
        var payload = Dir("payload-lock", ("a", "c"));
        // 持锁者=本进程 pid（存活）→ 等锁直到 --lock-timeout 0 立即超时。
        File.WriteAllText(install + ".bundler-lock", Environment.ProcessId.ToString());

        var rc = DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["apply", "--install-dir", install, "--payload", payload, "--lock-timeout", "0"]);

        Assert.Equal(3, rc);
        // 被拒轮不得碰安装目录。
        Assert.Equal("b", File.ReadAllText(Path.Combine(install, "a")));
    }

    [Fact]
    public void Apply_DeadLockOwner_BreaksLockAndSwaps()
    {
        var install = Dir("install-deadlock", ("a", "b"));
        var payload = Dir("payload-deadlock", ("a", "c"));
        File.WriteAllText(install + ".bundler-lock", "99999999"); // 死 pid

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            LockTimeoutSeconds = 5,
        });

        Assert.Equal(0, rc);
        Assert.Equal("c", File.ReadAllText(Path.Combine(install, "a")));
    }

    // ---- 故障缝 ----

    [Fact]
    public void Apply_IoFaultMidSwap_RestoresOriginalInstall()
    {
        var install = Dir("install-fault", ("app.txt", "original"));
        var payload = Dir("payload-fault", ("app.txt", "new"), ("big.txt", "payload"));
        BootstrapPlan.IoFaultProbe = (_, _) => throw new IOException("simulated ENOSPC");

        Assert.Throws<IOException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            KeepPayload = true, // 复制换入才走逐文件 IO 点——注入才落在换包中段
        }));

        // 换包失败必须尽力还原——不许留半个安装目录。
        Assert.Equal("original", File.ReadAllText(Path.Combine(install, "app.txt")));
        Assert.False(File.Exists(install + ".bundler-swap"));
    }

    [Fact]
    public void Apply_InsufficientFreeSpace_RejectedBeforeChange()
    {
        var install = Dir("install-space", ("app.txt", "x"));
        var payload = Dir("payload-space", ("app.txt", "y"));
        BootstrapPlan.FreeSpaceProbe = _ => 0; // 缝：卷上零余量

        Assert.Throws<UpdateRejectedException>(() => Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
            KeepPayload = true, // 复制换入才有可计量的写盘需求
        }));

        Assert.Equal("x", File.ReadAllText(Path.Combine(install, "app.txt")));
    }

    // ---- 挂载表同卷判定 ----

    [Fact]
    public void ParseMountPoints_ExtractsFifthFieldAndUnescapes()
    {
        var mountInfo =
            "22 1 8:1 / / rw,relatime - ext4 /dev/sda1 rw\n" +
            "23 22 0:5 / /proc rw,nosuid - proc proc rw\n" +
            // 带可选字段（shared/master propagation）的真实形态——"-" 落在索引 6 之后。
            "25 22 0:7 / /var/lib rw,relatime shared:1 master:2 - ext4 /dev/sda2 rw\n" +
            // 非绝对路径的挂载点字段是坏行——丢弃，不进最长前缀匹配。
            "26 22 0:8 / relmount rw - tmpfs tmpfs rw\n" +
            @"24 22 0:6 / /mnt/my\040dir rw - tmpfs tmpfs rw" + "\n";

        var points = BootstrapPlan.ParseMountPoints(mountInfo);

        Assert.Equal(["/", "/proc", "/var/lib", "/mnt/my dir"], points);
    }

    [Fact]
    public void ParseMountPoints_IgnoresMalformedLines()
    {
        Assert.Empty(BootstrapPlan.ParseMountPoints("\nshort line\n"));
    }

    [Fact]
    public void ResolveMountPoint_LongestPrefixWins()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "挂载点判定只在 POSIX 腿走");
        BootstrapPlan.MountInfoProbe = () =>
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw\n" +
            "2 1 8:2 / /data rw - ext4 /dev/sdb1 rw\n" +
            "3 2 8:3 / /data/sub rw - ext4 /dev/sdc1 rw\n";

        Assert.Equal("/data/sub", BootstrapPlan.ResolveMountPoint("/data/sub/dir/file"));
        Assert.Equal("/data", BootstrapPlan.ResolveMountPoint("/data/other"));
        Assert.Equal("/", BootstrapPlan.ResolveMountPoint("/elsewhere"));
    }

    [Fact]
    public void ResolveMountPoint_PathSegmentBoundary_Required()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "挂载点判定只在 POSIX 腿走");
        BootstrapPlan.MountInfoProbe = () =>
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw\n" +
            "2 1 8:2 / /data rw - ext4 /dev/sdb1 rw\n";

        // /database 不是 /data 的子路径——只能命中 /。
        Assert.Equal("/", BootstrapPlan.ResolveMountPoint("/database/file"));
        Assert.Equal("/data", BootstrapPlan.ResolveMountPoint("/data"));
    }

    [Fact]
    public void Apply_CrossVolumeFileSwap_FallsBackToTwoPhase()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "挂载表判定只在 POSIX 腿走");

        // 文件级换包才走 TryAtomicMove/MoveFile——虚构挂载表把两路径钉到
        // 不同挂载点 → SameVolume 判跨卷 → 强制两段式。
        // 挂载键必须按 canonical 名键入（Apply 内部先 CanonicalPath 解链——
        // macOS /var→/private/var 链接会让字面键失配回落根挂载）。
        // IoFaultProbe 计数可观测所走腿：原子 1 次，两段式 3 次。
        var installFile = Path.Combine(Root, "install-xvol", "app.bin");
        var payloadFile = Path.Combine(Root, "payload-xvol", "app.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadFile)!);
        File.WriteAllText(installFile, "old");
        File.WriteAllText(payloadFile, "new");

        var root = BootstrapPlan.CanonicalPath(Root)!.TrimEnd('/').Replace(" ", "\\040");
        BootstrapPlan.MountInfoProbe = () =>
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw\n" +
            $"2 1 8:2 / {root}/install-xvol rw - ext4 /dev/sdb1 rw\n" +
            $"3 1 8:3 / {root}/payload-xvol rw - ext4 /dev/sdc1 rw\n";
        var ioCalls = 0;
        BootstrapPlan.IoFaultProbe = (_, _) => ioCalls++;

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = installFile,
            PayloadDirectory = payloadFile,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new", File.ReadAllText(installFile));
        Assert.False(File.Exists(payloadFile));
        // ApplyFile 两次 MoveFile：install→backup 同挂载原子 1 次，
        // payload→install 跨卷两段式 3 次，合计 4。
        Assert.Equal(4, ioCalls);
    }

    [Fact]
    public void Apply_SameMountPaths_TakeAtomicPath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "挂载表判定只在 POSIX 腿走");

        // 虚构表只挂根——一切路径同卷，文件级换包两次 MoveFile 皆原子
        // （install→backup、payload→install 各 1 次探测），合计 2。
        BootstrapPlan.MountInfoProbe = () =>
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw\n";
        var ioCalls = 0;
        BootstrapPlan.IoFaultProbe = (_, _) => ioCalls++;

        var installFile = Path.Combine(Root, "app-sv.AppImage");
        var payloadFile = Path.Combine(Root, "new-sv.AppImage");
        File.WriteAllText(installFile, "old-image");
        File.WriteAllText(payloadFile, "new-image");

        var rc = Apply(new BootstrapOptions
        {
            InstallDirectory = installFile,
            PayloadDirectory = payloadFile,
        });

        Assert.Equal(0, rc);
        Assert.Equal("new-image", File.ReadAllText(installFile));
        Assert.False(File.Exists(payloadFile));
        Assert.Equal(2, ioCalls);
    }

    // ---- pkg-apply 动词（探针缝——全链非特权可跑，stub 垫片由缝注入命令路径）----

    private sealed record CapturedExec(
        string File, string[] Args, Dictionary<string, string>? Env, string? Cwd);

    private readonly List<CapturedExec> _pkgExecs = [];
    private readonly List<CapturedExec> _pkgSpawns = [];

    // 双侧同名测试缝：命令存在性/路径、环境变量、同步执行与分离 spawn 全注入——
    // CommandProbe 置非 null 即权威（map 缺席=命令不存在，不会回退真实 PATH）。
    private void SetupPkgProbes(
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? commands = null,
        Func<ExecSpec, int>? exec = null,
        Func<ExecSpec, bool>? spawn = null)
    {
        PkgApplyPlan.EnvProbe = name =>
            env is not null && env.TryGetValue(name, out var value) ? value : null;
        PkgApplyPlan.CommandProbe = name =>
            commands is not null && commands.TryGetValue(name, out var path) ? path : null;
        PkgApplyPlan.ExecProbe = spec =>
        {
            _pkgExecs.Add(new(spec.FileName, spec.Arguments, spec.Environment, spec.WorkingDirectory));
            return exec?.Invoke(spec) ?? 0;
        };
        PkgApplyPlan.SpawnProbe = spec =>
        {
            _pkgSpawns.Add(new(spec.FileName, spec.Arguments, spec.Environment, spec.WorkingDirectory));
            return spawn?.Invoke(spec) ?? true;
        };
    }

    private string WritePkg(string name = "app-2.0-1.deb")
    {
        var path = TempPath(name);
        File.WriteAllText(path, "fake-pkg");
        return path;
    }

    private string WriteApp()
    {
        var path = TempPath("app");
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private string WritePasswd(string contents)
    {
        var path = TempPath("fake-passwd");
        File.WriteAllText(path, contents);
        return path;
    }

    private string WriteEnvFile(string contents)
    {
        var path = TempPath("session.env");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void PkgApply_UsageErrors_Rc3()
    {
        var pkg = WritePkg();
        var app = WriteApp();

        // 缺 --pkg/--format/--app、format 值非法、未知旗标、包文件缺席——全归参数错 rc3。
        Assert.Equal(3, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["pkg-apply", "--format", "deb", "--app", app]));
        Assert.Equal(3, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["pkg-apply", "--pkg", pkg, "--app", app]));
        Assert.Equal(3, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["pkg-apply", "--pkg", pkg, "--format", "deb"]));
        Assert.Equal(3, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["pkg-apply", "--pkg", pkg, "--format", "msi", "--app", app]));
        Assert.Equal(3, DotNet.Bundler.Updater.Bootstrap.Program.Main(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app, "--bogus", "x"]));
        Assert.Equal(3, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", TempPath("missing.deb"), "--format", "deb", "--app", app]));
    }

    [Fact]
    public void PkgApply_Deb_AptGetPreferred_DpkgFallback()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var env = new Dictionary<string, string> { ["DISPLAY"] = ":9" };
        var commands = new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["nohup"] = "/stub/nohup",
        };

        // apt-get 成功：只有一级在场并跑；argv 恒带 -y + Dpkg::Options 透传
        // confdef/confold；env 带 DEBIAN_FRONTEND=noninteractive。
        SetupPkgProbes(env, commands);
        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]);
        Assert.Equal(0, rc);
        var exec = Assert.Single(_pkgExecs);
        Assert.Equal("/stub/apt-get", exec.File);
        Assert.Equal(
            ["install", "-y",
             "-o", "Dpkg::Options::=--force-confdef",
             "-o", "Dpkg::Options::=--force-confold", pkg],
            exec.Args);
        Assert.Equal("noninteractive", exec.Env?["DEBIAN_FRONTEND"]);

        // apt-get 失败 → dpkg 退化（同级 env/force 旗标，argv 换 dpkg -i 形）。
        _pkgExecs.Clear();
        _pkgSpawns.Clear();
        SetupPkgProbes(env, commands,
            exec: spec => spec.FileName.EndsWith("apt-get") ? 1 : 0);
        rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]);
        Assert.Equal(0, rc);
        Assert.Equal(2, _pkgExecs.Count);
        Assert.Equal("/stub/apt-get", _pkgExecs[0].File);
        Assert.Equal("/stub/dpkg", _pkgExecs[1].File);
        Assert.Equal(["-i", "--force-confdef", "--force-confold", pkg], _pkgExecs[1].Args);
        Assert.Equal("noninteractive", _pkgExecs[1].Env?["DEBIAN_FRONTEND"]);
    }

    [Fact]
    public void PkgApply_Deb_AllFrontendsFail_Rc4_AndPmMissing_Rc5()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var commands = new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["nohup"] = "/stub/nohup",
        };

        // 在场前端全败 → rc4 安装失败；全缺席 → rc5 pm-missing（不跑任何命令）。
        SetupPkgProbes(commands: commands, exec: _ => 9);
        Assert.Equal(4, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]));
        Assert.Equal(2, _pkgExecs.Count);
        Assert.Empty(_pkgSpawns);

        _pkgExecs.Clear();
        SetupPkgProbes(commands: new Dictionary<string, string>());
        Assert.Equal(5, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]));
        Assert.Empty(_pkgExecs);
        Assert.Empty(_pkgSpawns);
    }

    [Fact]
    public void PkgApply_Rpm_ChainOrder_AndRollbackOldpackage()
    {
        var pkg = WritePkg("app-2.0-1.rpm");
        var app = WriteApp();
        var commands = new Dictionary<string, string>
        {
            ["zypper"] = "/stub/zypper",
            ["rpm"] = "/stub/rpm",
            ["nohup"] = "/stub/nohup",
        };

        // dnf 缺席跳过 → zypper 失败 → rpm -U 成功：退化序与缺席跳过同断言。
        SetupPkgProbes(commands: commands,
            exec: spec => spec.FileName.EndsWith("zypper") ? 5 : 0);
        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "rpm", "--app", app]);
        Assert.Equal(0, rc);
        Assert.Equal(2, _pkgExecs.Count);
        Assert.Equal("/stub/zypper", _pkgExecs[0].File);
        Assert.Equal(["install", "-y", pkg], _pkgExecs[0].Args);
        Assert.Equal("/stub/rpm", _pkgExecs[1].File);
        Assert.Equal(["-U", pkg], _pkgExecs[1].Args);

        // --rollback 子形态：zypper 仍先跑（不感知 rollback），rpm 链末位换 --oldpackage。
        _pkgExecs.Clear();
        _pkgSpawns.Clear();
        SetupPkgProbes(commands: commands,
            exec: spec => spec.FileName.EndsWith("zypper") ? 5 : 0);
        rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "rpm", "--app", app, "--rollback"]);
        Assert.Equal(0, rc);
        Assert.Equal(2, _pkgExecs.Count);
        Assert.Equal("/stub/rpm", _pkgExecs[1].File);
        Assert.Equal(["-U", "--oldpackage", pkg], _pkgExecs[1].Args);
    }

    [Fact]
    public void PkgApply_Rpm_DnfPreferredOverZypper()
    {
        var pkg = WritePkg("app-2.0-1.rpm");
        var app = WriteApp();
        SetupPkgProbes(commands: new Dictionary<string, string>
        {
            ["dnf"] = "/stub/dnf",
            ["zypper"] = "/stub/zypper",
            ["rpm"] = "/stub/rpm",
            ["nohup"] = "/stub/nohup",
        });
        Assert.Equal(0, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "rpm", "--app", app]));
        var exec = Assert.Single(_pkgExecs);
        Assert.Equal("/stub/dnf", exec.File);
        Assert.Equal(["install", "-y", pkg], exec.Args);
    }

    [Fact]
    public void PkgApply_Apk_AllowUntrustedGatedOnKeysDir()
    {
        var pkg = WritePkg("app-2.0.apk");
        var app = WriteApp();
        var commands = new Dictionary<string, string>
        {
            ["apk"] = "/stub/apk",
            ["nohup"] = "/stub/nohup",
        };

        // keys 目录空 → --allow-untrusted；预置公钥（任意条目）→ 不加。
        var keysDir = Temp("apk-keys-empty").FullName;
        SetupPkgProbes(
            env: new Dictionary<string, string>
            {
                ["BUNDLER_UPDATER_APK_KEYS_DIR"] = keysDir,
            },
            commands: commands);
        Assert.Equal(0, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "apk", "--app", app]));
        Assert.Equal(["add", "--allow-untrusted", pkg], Assert.Single(_pkgExecs).Args);

        File.WriteAllText(Path.Combine(keysDir, "bundler-abc123.rsa.pub"), "pub");
        _pkgExecs.Clear();
        _pkgSpawns.Clear();
        Assert.Equal(0, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "apk", "--app", app]));
        Assert.Equal(["add", pkg], Assert.Single(_pkgExecs).Args);

        // keys 目录缺席 → --allow-untrusted。
        _pkgExecs.Clear();
        _pkgSpawns.Clear();
        SetupPkgProbes(
            env: new Dictionary<string, string>
            {
                ["BUNDLER_UPDATER_APK_KEYS_DIR"] = TempPath("no-such-keys"),
            },
            commands: commands);
        Assert.Equal(0, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "apk", "--app", app]));
        Assert.Equal(["add", "--allow-untrusted", pkg], Assert.Single(_pkgExecs).Args);
    }

    [Fact]
    public void PkgApply_Restart_DirectSpawnWhenNoUidEnv()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var envFile = WriteEnvFile("DISPLAY=:9\nMYVAR=a b\n");
        SetupPkgProbes(
            env: new Dictionary<string, string>(),
            commands: new Dictionary<string, string> { ["dpkg"] = "/stub/dpkg", ["nohup"] = "/stub/nohup" });

        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]);

        Assert.Equal(0, rc);
        // 探针缝在场按 POSIX 腿求值——跨宿主同断言。
        var spawn = Assert.Single(_pkgSpawns);
        Assert.Equal("/stub/nohup", spawn.File);
        Assert.Equal(["env", "DISPLAY=:9", "MYVAR=a b", app], spawn.Args);
    }

    [Fact]
    public void PkgApply_Restart_SetprivDropUid_UsesPkexecThenSudoUid()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var envFile = WriteEnvFile("DISPLAY=:9\n");
        var env = new Dictionary<string, string>
        {
            ["PKEXEC_UID"] = "4242",
            ["SUDO_UID"] = "5000",      // PKEXEC_UID 优先
            ["SUDO_GID"] = "4343",
        };
        SetupPkgProbes(env,
            commands: new Dictionary<string, string>
            {
                ["dpkg"] = "/stub/dpkg",
                ["setpriv"] = "/stub/setpriv",
                ["nohup"] = "/stub/nohup",
            });

        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]);

        Assert.Equal(0, rc);
        var spawn = Assert.Single(_pkgSpawns);
        Assert.Equal("/stub/nohup", spawn.File);
        Assert.Equal(
            ["/stub/setpriv", "--reuid=4242", "--regid=4343", "--clear-groups",
             "env", "DISPLAY=:9", app],
            spawn.Args);
    }

    [Fact]
    public void PkgApply_Restart_DropFallbackOrder_SudoRunuserSu()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var envFile = WriteEnvFile("DISPLAY=:9\n");
        var passwd = WritePasswd(
            "root:x:0:0:root:/root:/bin/sh\n" +
            "tester:x:4242:4343:Test User:/home/tester:/bin/sh\n");
        var env = new Dictionary<string, string>
        {
            ["SUDO_UID"] = "4242",
            ["BUNDLER_UPDATER_PASSWD_FILE"] = passwd,
        };
        var commands = new Dictionary<string, string>
        {
            ["dpkg"] = "/stub/dpkg",
            ["sudo"] = "/stub/sudo",
            ["runuser"] = "/stub/runuser",
            ["su"] = "/stub/su",
            ["nohup"] = "/stub/nohup",
        };

        // setpriv 缺席 → sudo -u #uid（gid 来自 passwd 第 4 段：SUDO_GID 缺席时）。
        SetupPkgProbes(env, commands);
        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]);
        Assert.Equal(0, rc);
        Assert.Equal(
            ["/stub/sudo", "-u", "#4242", "env", "DISPLAY=:9", app],
            Assert.Single(_pkgSpawns).Args);

        // sudo 缺席 → runuser -u <name>（passwd 查名）。
        _pkgSpawns.Clear();
        commands.Remove("sudo");
        SetupPkgProbes(env, commands);
        rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]);
        Assert.Equal(0, rc);
        Assert.Equal(
            ["/stub/runuser", "-u", "tester", "--", "env", "DISPLAY=:9", app],
            Assert.Single(_pkgSpawns).Args);

        // runuser 缺席 → su -s /bin/sh -c 'exec env <quoted> <quoted app>' <name>。
        _pkgSpawns.Clear();
        commands.Remove("runuser");
        SetupPkgProbes(env, commands);
        rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]);
        Assert.Equal(0, rc);
        Assert.Equal(
            ["/stub/su", "-s", "/bin/sh", "-c",
             $"exec env 'DISPLAY=:9' '{app}'", "tester"],
            Assert.Single(_pkgSpawns).Args);
    }

    [Fact]
    public void PkgApply_Restart_NoDropTool_Rc6_InstallCommitted()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        SetupPkgProbes(
            env: new Dictionary<string, string> { ["PKEXEC_UID"] = "4242" },
            commands: new Dictionary<string, string>
            {
                ["dpkg"] = "/stub/dpkg",
                ["nohup"] = "/stub/nohup",
            });

        // 降权工具全缺席 → rc6（安装已提交——装成功的 exec 记录在场）。
        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]);

        Assert.Equal(6, rc);
        Assert.Single(_pkgExecs);
        Assert.Empty(_pkgSpawns);
    }

    [Fact]
    public void PkgApply_Restart_SpawnFailure_Rc6()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        SetupPkgProbes(
            env: new Dictionary<string, string>(),
            commands: new Dictionary<string, string>
            {
                ["dpkg"] = "/stub/dpkg",
                ["nohup"] = "/stub/nohup",
            },
            spawn: _ => false);

        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app]);

        Assert.Equal(6, rc);
        Assert.Single(_pkgExecs);
    }

    [Fact]
    public void PkgApply_Restart_MissingApp_Rc6()
    {
        var pkg = WritePkg();
        SetupPkgProbes(commands: new Dictionary<string, string>
        {
            ["dpkg"] = "/stub/dpkg",
            ["nohup"] = "/stub/nohup",
        });

        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", TempPath("no-app")]);

        Assert.Equal(6, rc);
        Assert.Single(_pkgExecs);
        Assert.Empty(_pkgSpawns);
    }

    [Fact]
    public void PkgApply_EnvFile_BadLinesSkipped()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var envFile = WriteEnvFile(
            "DISPLAY=:9\n" +
            "MYVAR=a b c\n" +
            "1BAD=x\n" +
            "=orphan\n" +
            "NO_EQUALS\n" +
            "A B=x\n" +
            "GOOD_OK=v\n" +
            "EMPTY=\n");
        SetupPkgProbes(
            env: new Dictionary<string, string>(),
            commands: new Dictionary<string, string>
            {
                ["dpkg"] = "/stub/dpkg",
                ["nohup"] = "/stub/nohup",
            });

        Assert.Equal(0, PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--env-file", envFile]));

        var spawn = Assert.Single(_pkgSpawns);
        Assert.Equal(
            ["env", "DISPLAY=:9", "MYVAR=a b c", "GOOD_OK=v", "EMPTY=", app],
            spawn.Args);
    }

    [Fact]
    public void PkgApply_NoEnvFile_MinimalEnv_Logged()
    {
        var pkg = WritePkg();
        var app = WriteApp();
        var passwd = WritePasswd("tester:x:4242:4343::/home/tester:/bin/sh\n");
        var env = new Dictionary<string, string>
        {
            ["PKEXEC_UID"] = "4242",
            ["SUDO_GID"] = "4343",
            ["BUNDLER_UPDATER_PASSWD_FILE"] = passwd,
            ["DISPLAY"] = ":7",
            ["HOME"] = "/root",
        };
        SetupPkgProbes(env,
            commands: new Dictionary<string, string>
            {
                ["dpkg"] = "/stub/dpkg",
                ["setpriv"] = "/stub/setpriv",
                ["nohup"] = "/stub/nohup",
            });
        var logFile = TempPath("pkg.log");

        var rc = PkgApplyPlan.Run(
            ["pkg-apply", "--pkg", pkg, "--format", "deb", "--app", app,
             "--log", logFile]);

        Assert.Equal(0, rc);
        var log = File.ReadAllText(logFile);
        Assert.Contains("no --env-file", log);
        // 最小集：降权场景 HOME 取目标用户 passwd 家目录而非 root 的 $HOME。
        Assert.Equal(
            ["/stub/setpriv", "--reuid=4242", "--regid=4343", "--clear-groups",
             "env", "HOME=/home/tester", "DISPLAY=:7", app],
            Assert.Single(_pkgSpawns).Args);
    }

    // ---- 装置 ----

    private int Apply(BootstrapOptions options) =>
        BootstrapPlan.Apply(options, _log.Add);

    private string Dir(string relative, params (string Name, string Content)[] files)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(path, name), content);
        }
        return path;
    }
}

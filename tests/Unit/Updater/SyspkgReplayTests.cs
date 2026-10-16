// SyspkgReplay（deb/rpm/alpineapk 客户端 replay 编排）进程内面：
// 预检三件套拒绝、三级提权退化链选腿、verb argv 逐字、env 件内容/权限、
// 留存件/applied 标记、回滚选件——全经 CommandProbe/EnvProbe/ExecProbe 缝断言，
// 零子进程；真 spawn 面在 Integration/UpdateClientChainTests 的 POSIX 垫片腿。
using System.Runtime.InteropServices;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;

namespace Bundler.UnitTests.Updater;

public sealed class SyspkgReplayTests : TempBase
{
    private readonly List<(string File, string[] Args)> _execs = [];
    private readonly List<string> _log = [];

    public override void Dispose()
    {
        SyspkgReplay.CommandProbe = null;
        SyspkgReplay.EnvProbe = null;
        SyspkgReplay.ExecProbe = null;
        base.Dispose();
    }

    // ---- fixture ----

    private void SetupProbes(
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? commands = null,
        int ledgerRc = 0)
    {
        SyspkgReplay.EnvProbe = name =>
            env is { } table && table.TryGetValue(name, out var value) ? value : null;
        SyspkgReplay.CommandProbe = name =>
            commands is { } table && table.TryGetValue(name, out var path) ? path : null;
        SyspkgReplay.ExecProbe = (file, args) =>
        {
            _execs.Add((file, args));
            return ledgerRc;
        };
    }

    // 无欢腿最小命令面：账本工具 + pkexec。
    private static Dictionary<string, string> PkexecCommands(string ledgerTool) => new()
    {
        [ledgerTool] = "/stub/" + ledgerTool,
        ["pkexec"] = "/stub/pkexec",
    };

    private static Protocol.UpdateInstallIdentity Identity(string format) => new()
    {
        Format = format,
        Target = "linux-x86_64",
        Channel = "stable",
        FeedUrl = "/feed/bundler-update.stable.json",
        PublicKey = "AA==",
        Pkg = "myapp",
    };

    // 引导件宿主名按 OS 解析口径（Windows 装 bundler-updater.exe）。
    private static string BootstrapperName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bundler-updater.exe" : "bundler-updater";

    // 安装位：注入件 bundler-updater 原地直跑（pkg-apply 不搬移安装目录）。
    private string InstallDir(bool withBootstrapper = true)
    {
        var dir = Path.Combine(Root, "install");
        Directory.CreateDirectory(dir);
        if (withBootstrapper)
        {
            File.WriteAllText(Path.Combine(dir, BootstrapperName), "stub-updater");
        }
        return dir;
    }

    private string Artifact(string name = "myapp_2.0-1_amd64.deb")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, "pkg-v2-bytes");
        return path;
    }

    private string RetentionDir => Path.Combine(Root, "retention");
    private string StagingRoot => Path.Combine(Root, "staging");
    private string AppFile => Path.Combine(Root, "app");

    private ApplyOptions Options(string? app = null, string? staging = null, string? retention = null) => new()
    {
        AppPath = app ?? AppFile,
        StagingRoot = staging ?? StagingRoot,
        RollbackBackupDirectory = retention ?? RetentionDir,
    };

    private string UpdaterPath => Path.Combine(Root, "install", BootstrapperName);

    // ---- apply：提权退化链 ----

    [Fact]
    public void PlanApply_PkexecLeg_VerbatimArgv_AndRetainedArtifact()
    {
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["pkexec"] = "/stub/pkexec",
            ["sudo"] = "/stub/sudo",
            ["zenity"] = "/stub/zenity",
        });
        var install = InstallDir();
        var artifact = Artifact();
        var retained = Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");

        var launch = SyspkgReplay.PlanApply(
            Identity("deb"), artifact, install, Options(), _log.Add);

        // pkexec 腿优先：sudo/zenity 在场也不降级；argv 逐字按 §2.3 签名。
        Assert.Equal("/stub/pkexec", launch.FileName);
        Assert.Equal(
            new[]
            {
                UpdaterPath,
                "pkg-apply",
                "--pkg", retained,
                "--format", "deb",
                "--app", Path.GetFullPath(AppFile),
            },
            launch.Arguments);
        // 留存件 + applied 标记落位（回滚件的"当前"口径）。
        Assert.True(File.Exists(retained));
        Assert.Equal("pkg-v2-bytes", File.ReadAllText(retained));
        Assert.Equal("myapp_2.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
        // 账本归属查询真查 dpkg -s（exec 缝记录）。
        var exec = Assert.Single(_execs);
        Assert.Equal("/stub/dpkg", exec.File);
        Assert.Equal(new[] { "-s", "myapp" }, exec.Args);
        // 会话 env 全缺席 → 不带 --env-file。
        Assert.Null(launch.EnvFile);
        Assert.DoesNotContain("--env-file", launch.Arguments);
    }

    [Fact]
    public void PlanApply_AskpassLeg_ZenityPipeline_ThenKdialog()
    {
        var install = InstallDir();
        var artifact = Artifact();
        var retained = Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");
        var appAbs = Path.GetFullPath(AppFile);

        // pkexec 缺席 + zenity+sudo 在场 → 问密码管线腿。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["zenity"] = "/stub/zenity",
            ["sudo"] = "/stub/sudo",
        });
        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, install, Options(), _log.Add);
        Assert.Equal("/bin/sh", launch.FileName);
        var expected =
            "'/stub/zenity' '--password' | '/stub/sudo' '-S' '-p' '' '--' " +
            $"'{UpdaterPath}' 'pkg-apply' '--pkg' '{retained}' '--format' 'deb' '--app' '{appAbs}'";
        Assert.Equal(new[] { "-c", expected }, launch.Arguments);

        // zenity 缺席退 kdialog（提示文本随 argv 进管线）。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["kdialog"] = "/stub/kdialog",
            ["sudo"] = "/stub/sudo",
        });
        _execs.Clear();
        launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, install, Options(), _log.Add);
        Assert.Equal("/bin/sh", launch.FileName);
        expected =
            "'/stub/kdialog' '--password' 'Authentication required to install the update' | " +
            $"'/stub/sudo' '-S' '-p' '' '--' '{UpdaterPath}' 'pkg-apply' '--pkg' '{retained}' " +
            $"'--format' 'deb' '--app' '{appAbs}'";
        Assert.Equal(new[] { "-c", expected }, launch.Arguments);
    }

    [Fact]
    public void PlanApply_BareSudo_LastResort()
    {
        var install = InstallDir();
        var artifact = Artifact();
        var retained = Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");
        // pkexec 缺席、问密码器缺席、只有裸 sudo → 末级腿。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["sudo"] = "/stub/sudo",
        });

        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, install, Options(), _log.Add);
        Assert.Equal("/stub/sudo", launch.FileName);
        Assert.Equal(
            new[]
            {
                "--", UpdaterPath,
                "pkg-apply",
                "--pkg", retained,
                "--format", "deb",
                "--app", Path.GetFullPath(AppFile),
            },
            launch.Arguments);
    }

    [Fact]
    public void PlanApply_ScriptBootstrapper_WrappedInPosixSh()
    {
        // 安装位只有 bundler-updater.sh 降级件 → "/bin/sh <script>" 前缀进 verb argv。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["pkexec"] = "/stub/pkexec",
        });
        var install = InstallDir(withBootstrapper: false);
        var script = Path.Combine(install, "bundler-updater.sh");
        File.WriteAllText(script, "#!/bin/sh\n");

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add);
        Assert.Equal("/stub/pkexec", launch.FileName);
        Assert.Equal("/bin/sh", launch.Arguments[0]);
        Assert.Equal(script, launch.Arguments[1]);
        Assert.Equal("pkg-apply", launch.Arguments[2]);
    }

    // ---- apply：预检三件套拒绝面 ----

    [Fact]
    public void PlanApply_NoPackageManager_Refuses()
    {
        // command -v 全缺席 → 任一前端腿都够不着。
        SetupProbes(commands: new Dictionary<string, string>());
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add));
        Assert.Contains("does not participate in updates", exception.Message);
        Assert.Contains("apt-get/dpkg", exception.Message);
        Assert.Empty(_execs);
    }

    [Fact]
    public void PlanApply_LedgerToolAbsent_Refuses()
    {
        // deb：apt-get 在场但账本工具 dpkg 缺席——归属查不了，拒绝。
        SetupProbes(commands: new Dictionary<string, string> { ["apt-get"] = "/stub/apt-get" });
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add));
        Assert.Contains("ledger tool 'dpkg' not found", exception.Message);
        Assert.Contains("does not participate in updates", exception.Message);
        Assert.Empty(_execs);
    }

    [Fact]
    public void PlanApply_LedgerQueryFails_Refuses()
    {
        // dpkg -s 返非零 = pkg 不在账本（repack/手动铺文件的漂移面）。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apt-get"] = "/stub/apt-get",
            ["dpkg"] = "/stub/dpkg",
            ["pkexec"] = "/stub/pkexec",
        }, ledgerRc: 1);
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add));
        Assert.Contains("not registered in the dpkg ledger", exception.Message);
        Assert.Contains("does not participate in updates", exception.Message);
        Assert.Single(_execs);
    }

    [Fact]
    public void PlanApply_NoElevationLeg_Refuses()
    {
        // 三级提权全缺席（dpkg 在场使预检通过）。
        SetupProbes(commands: new Dictionary<string, string> { ["dpkg"] = "/stub/dpkg" });
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add));
        Assert.Contains("privilege escalation", exception.Message);
    }

    [Fact]
    public void PlanApply_MissingAppPath_Refuses()
    {
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["dpkg"] = "/stub/dpkg",
            ["pkexec"] = "/stub/pkexec",
        });
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install,
                new ApplyOptions { StagingRoot = StagingRoot }, _log.Add));
        Assert.Contains("ApplyOptions.AppPath", exception.Message);
    }

    [Fact]
    public void PlanApply_MissingBootstrapper_Refuses()
    {
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir(withBootstrapper: false);
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add));
        Assert.Contains("not built with update support", exception.Message);
    }

    // ---- apply：格式族映射 ----

    [Fact]
    public void PlanApply_Rpm_LedgerAndArgv()
    {
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["dnf"] = "/stub/dnf",
            ["zypper"] = "/stub/zypper",
            ["rpm"] = "/stub/rpm",
            ["pkexec"] = "/stub/pkexec",
        });
        var install = InstallDir();
        var rpm = Artifact("myapp-2.0-1.x86_64.rpm");
        var launch = SyspkgReplay.PlanApply(Identity("rpm"), rpm, install, Options(), _log.Add);
        var exec = Assert.Single(_execs);
        Assert.Equal("/stub/rpm", exec.File);
        Assert.Equal(new[] { "-q", "myapp" }, exec.Args);
        var idx = Array.IndexOf(launch.Arguments, "--format");
        Assert.Equal("rpm", launch.Arguments[idx + 1]);
        Assert.Equal(Path.Combine(RetentionDir, "myapp-2.0-1.x86_64.rpm"),
            launch.Arguments[Array.IndexOf(launch.Arguments, "--pkg") + 1]);
    }

    [Fact]
    public void PlanApply_AlpineApk_MapsToApkVerb()
    {
        // 身份 format=alpineapk → 动词面 --format apk；账本查询是 apk info -e。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["apk"] = "/stub/apk",
            ["pkexec"] = "/stub/pkexec",
        });
        var install = InstallDir();
        var apk = Artifact("myapp-2.0.apk");
        var launch = SyspkgReplay.PlanApply(
            Identity("alpineapk"), apk, install, Options(), _log.Add);
        var exec = Assert.Single(_execs);
        Assert.Equal("/stub/apk", exec.File);
        Assert.Equal(new[] { "info", "-e", "myapp" }, exec.Args);
        Assert.Equal("apk", launch.Arguments[Array.IndexOf(launch.Arguments, "--format") + 1]);
    }

    [Fact]
    public void PlanApply_EnvOverride_BeatsCommandProbe()
    {
        // BUNDLER_UPDATER_CMD_<TOOL> env 覆盖先于 CommandProbe（生产=权威缺席化的
        // 垫片供应通道——与引导件 PkgApplyPlan 同名同源约定）。
        SetupProbes(
            env: new Dictionary<string, string>
            {
                ["BUNDLER_UPDATER_CMD_DPKG"] = "/env/dpkg",
                ["BUNDLER_UPDATER_CMD_PKEXEC"] = "/env/pkexec",
            },
            commands: new Dictionary<string, string>());   // CommandProbe 全缺席
        var install = InstallDir();
        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add);
        Assert.Equal("/env/pkexec", launch.FileName);
        var exec = Assert.Single(_execs);
        Assert.Equal("/env/dpkg", exec.File);
    }

    // ---- apply：env 件 ----

    [Fact]
    public void PlanApply_EnvFile_CollectsPresentSessionVars_FilteredAnd600()
    {
        SetupProbes(
            env: new Dictionary<string, string>
            {
                ["HOME"] = "/home/u",
                ["DISPLAY"] = ":99",
                ["XAUTHORITY"] = "/run/x auth",          // 含空格值原样保留
                ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/dbus",
                ["WAYLAND_DISPLAY"] = "multi\nline",      // 换行值被过滤不进件
                // XDG_RUNTIME_DIR/SSH_AUTH_SOCK 缺席 → 不出行
            },
            commands: PkexecCommands("dpkg"));
        var install = InstallDir();

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), install, Options(), _log.Add);

        Assert.NotNull(launch.EnvFile);
        Assert.Equal(
            new[]
            {
                "HOME=/home/u",
                "DISPLAY=:99",
                "XAUTHORITY=/run/x auth",
                "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/dbus",
            },
            File.ReadAllLines(launch.EnvFile!));
        // env 件落 <StagingRoot>/pkg-<guid>/session.env 并进 argv。
        Assert.Equal("session.env", Path.GetFileName(launch.EnvFile));
        Assert.StartsWith("pkg-", Path.GetFileName(launch.EnvFileDirectory));
        Assert.Equal(Path.GetFullPath(StagingRoot),
            Path.GetDirectoryName(launch.EnvFileDirectory));
        Assert.Contains("--env-file", launch.Arguments);
        Assert.Equal(launch.EnvFile,
            launch.Arguments[Array.IndexOf(launch.Arguments, "--env-file") + 1]);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(launch.EnvFile!));
        }
    }

    [Fact]
    public void PlanApply_RetentionFailure_FallsBackToOriginalArtifact()
    {
        // retention 目录开不出（父位是文件）→ WARN 降级放行，--pkg 指原件而非空转。
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        var artifact = Artifact();
        var blocker = Path.Combine(Root, "blocker");
        File.WriteAllText(blocker, "file-not-dir");
        var options = new ApplyOptions
        {
            AppPath = AppFile,
            StagingRoot = StagingRoot,
            RollbackBackupDirectory = Path.Combine(blocker, "retention"),
        };

        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, install, options, _log.Add);
        Assert.Equal(
            Path.GetFullPath(artifact),
            launch.Arguments[Array.IndexOf(launch.Arguments, "--pkg") + 1]);
        Assert.Contains(_log, line => line.Contains("cannot retain rollback artifact"));
    }

    // ---- rollback ----

    [Fact]
    public void PlanRollback_NoRetainedPackage_Refuses()
    {
        // 无留存件 = 首次 Bundler 链更新前/留存缺失 → 确定性拒绝，不空转 replay。
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add));
        Assert.Contains("no retained package", exception.Message);
    }

    [Fact]
    public void PlanRollback_ReplaysRetainedOld_WithRollbackFlag()
    {
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        // 当前件（applied 标记指向）+ 旧件——回滚应选旧件。
        File.WriteAllText(Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb"), "pkg-v2");
        File.WriteAllText(Path.Combine(RetentionDir, "myapp_1.0-1_amd64.deb"), "pkg-v1");
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "myapp_2.0-1_amd64.deb\n");

        var launch = SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add);
        var retained = Path.Combine(RetentionDir, "myapp_1.0-1_amd64.deb");
        Assert.Equal("/stub/pkexec", launch.FileName);
        Assert.Equal(
            new[]
            {
                UpdaterPath,
                "pkg-apply",
                "--pkg", retained,
                "--format", "deb",
                "--app", Path.GetFullPath(AppFile),
                "--rollback",
            },
            launch.Arguments);
        // 标记改记所选旧包——回滚后账本以它为"当前"（flip-flop 语义）。
        Assert.Equal("myapp_1.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
    }

    [Fact]
    public void PlanRollback_PicksNewestNonApplied()
    {
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        var older = Path.Combine(RetentionDir, "a_0.9-1_amd64.deb");
        var newer = Path.Combine(RetentionDir, "a_1.0-1_amd64.deb");
        var current = Path.Combine(RetentionDir, "a_2.0-1_amd64.deb");
        File.WriteAllText(older, "o");
        File.WriteAllText(newer, "n");
        File.WriteAllText(current, "c");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(-1)); // 最新但被标记排除
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "a_2.0-1_amd64.deb\n");

        var launch = SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add);
        Assert.Equal(newer,
            launch.Arguments[Array.IndexOf(launch.Arguments, "--pkg") + 1]);
    }

    [Fact]
    public void PlanRollback_PrecheckStillRuns()
    {
        // 回滚腿同样过预检——账本归属证不了不玩 replay。
        SetupProbes(commands: new Dictionary<string, string> { ["dpkg"] = "/stub/dpkg" },
            ledgerRc: 1);
        var install = InstallDir();
        Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add));
    }

    // ---- 退出码分类 ----

    [Fact]
    public void DescribeExitCode_ClassifiesPkgApplyProtocol()
    {
        Assert.Equal("success", SyspkgReplay.DescribeExitCode(0));
        Assert.Equal("usage error", SyspkgReplay.DescribeExitCode(3));
        Assert.Equal("package install failed", SyspkgReplay.DescribeExitCode(4));
        Assert.Equal("no package manager available", SyspkgReplay.DescribeExitCode(5));
        Assert.Contains("restart failed", SyspkgReplay.DescribeExitCode(6));
        Assert.Equal("elevation/launcher failure", SyspkgReplay.DescribeExitCode(126));
    }

    // ---- 分发闸 ----

    [Fact]
    public void IsSyspkgFormat_Classification()
    {
        Assert.True(SyspkgReplay.IsSyspkgFormat("deb"));
        Assert.True(SyspkgReplay.IsSyspkgFormat("RPM"));
        Assert.True(SyspkgReplay.IsSyspkgFormat("alpineapk"));
        Assert.False(SyspkgReplay.IsSyspkgFormat("apk"));   // 客户端身份词不含裸 apk
        Assert.False(SyspkgReplay.IsSyspkgFormat("zip"));
        Assert.False(SyspkgReplay.IsSyspkgFormat(null));
    }
}

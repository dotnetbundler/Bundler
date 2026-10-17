// SyspkgReplay（deb/rpm/alpineapk 客户端 replay 编排）进程内面：
// 预检三件套拒绝、三级提权退化链选腿与运行时退化（SpawnProbe 缝）、verb argv 逐字、
// env 件内容/权限、留存件/applied 标记/签名旁车再验、回滚选件——
// 全经 CommandProbe/EnvProbe/ExecProbe/ChmodProbe/SpawnProbe 缝断言，
// 零真提权子进程；真 spawn 面在 Integration/UpdateClientChainTests 的 POSIX 垫片腿。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        SyspkgReplay.ChmodProbe = null;
        SyspkgReplay.SpawnProbe = null;
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

    // 真实密钥材料：B2 起留存副本再验签吃身份公钥——"AA==" 式假 key 会让
    // VerifyRetainedPackage 恒假，apply 路径全部验签拒绝。
    private static readonly Protocol.UpdateKeyMaterial Material =
        Protocol.UpdateKeyMaterial.Generate();

    private static Protocol.UpdateInstallIdentity Identity(string format) => new()
    {
        Format = format,
        Target = "linux-x86_64",
        Channel = "stable",
        FeedUrl = "/feed/bundler-update.stable.json",
        PublicKey = Material.PublicPointBase64(),
        Pkg = "myapp",
    };

    // 喂给 PlanApply 的清单签名（base64）；留存 .sig 旁车写它的原始字节形态。
    private static string Sig(Protocol.UpdateKeyMaterial material, string path)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(material.ToPrivateParameters());
        return Convert.ToBase64String(Protocol.UpdateSignatureVerifier.ToP1363(
            ecdsa.SignData(File.ReadAllBytes(path), HashAlgorithmName.SHA256)));
    }

    // retention 里种 pkg+sig 成对候选：签名按件内容现签。
    private static void WriteSigSidecar(string packagePath) =>
        File.WriteAllBytes(packagePath + ".sig",
            Convert.FromBase64String(Sig(Material, packagePath)));

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
            Identity("deb"), artifact, Sig(Material, artifact), install, Options(), _log.Add);

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

        // pkexec 缺席 + zenity+sudo 在场 + 图形会话在位（DISPLAY）→ 问密码管线腿；
        // DISPLAY 本身进会话 env 件，管线 argv 尾段带 --env-file。
        SetupProbes(
            env: new Dictionary<string, string> { ["DISPLAY"] = ":0" },
            commands: new Dictionary<string, string>
            {
                ["apt-get"] = "/stub/apt-get",
                ["dpkg"] = "/stub/dpkg",
                ["zenity"] = "/stub/zenity",
                ["sudo"] = "/stub/sudo",
            });
        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, Sig(Material, artifact), install, Options(), _log.Add);
        Assert.Equal("/bin/sh", launch.FileName);
        Assert.NotNull(launch.EnvFile);
        var expected =
            "'/stub/zenity' '--password' | '/stub/sudo' '-S' '-p' '' '--' " +
            $"'{UpdaterPath}' 'pkg-apply' '--pkg' '{retained}' '--format' 'deb' '--app' '{appAbs}' " +
            $"'--env-file' '{launch.EnvFile}'";
        Assert.Equal(new[] { "-c", expected }, launch.Arguments);

        // zenity 缺席退 kdialog（提示文本随 argv 进管线）。
        SetupProbes(
            env: new Dictionary<string, string> { ["DISPLAY"] = ":0" },
            commands: new Dictionary<string, string>
            {
                ["apt-get"] = "/stub/apt-get",
                ["dpkg"] = "/stub/dpkg",
                ["kdialog"] = "/stub/kdialog",
                ["sudo"] = "/stub/sudo",
            });
        _execs.Clear();
        launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, Sig(Material, artifact), install, Options(), _log.Add);
        Assert.Equal("/bin/sh", launch.FileName);
        Assert.NotNull(launch.EnvFile);
        expected =
            "'/stub/kdialog' '--password' 'Authentication required to install the update' | " +
            $"'/stub/sudo' '-S' '-p' '' '--' '{UpdaterPath}' 'pkg-apply' '--pkg' '{retained}' " +
            $"'--format' 'deb' '--app' '{appAbs}' '--env-file' '{launch.EnvFile}'";
        Assert.Equal(new[] { "-c", expected }, launch.Arguments);
    }

    [Fact]
    public void PlanApply_AskpassLeg_NoDisplay_FallsToBareSudo()
    {
        // zenity/kdialog+sudo 在场但 DISPLAY/WAYLAND_DISPLAY 全缺席（headless）——
        // 问密码管线必败，直接退化裸 sudo 腿（tty 提示/NOPASSWD 场景存活）。
        SetupProbes(
            env: new Dictionary<string, string>(),
            commands: new Dictionary<string, string>
            {
                ["apt-get"] = "/stub/apt-get",
                ["dpkg"] = "/stub/dpkg",
                ["zenity"] = "/stub/zenity",
                ["kdialog"] = "/stub/kdialog",
                ["sudo"] = "/stub/sudo",
            });
        var install = InstallDir();

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);
        Assert.Equal("/stub/sudo", launch.FileName);
        Assert.Equal("--", launch.Arguments[0]);
        Assert.Null(launch.EnvFile);
        Assert.DoesNotContain("--env-file", launch.Arguments);
    }

    [Fact]
    public void PlanApply_AskpassLeg_WaylandDisplay_Suffices()
    {
        // 仅 WAYLAND_DISPLAY 在场同样算图形会话——管线腿照选。
        SetupProbes(
            env: new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-1" },
            commands: new Dictionary<string, string>
            {
                ["apt-get"] = "/stub/apt-get",
                ["dpkg"] = "/stub/dpkg",
                ["zenity"] = "/stub/zenity",
                ["sudo"] = "/stub/sudo",
            });
        var install = InstallDir();

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);
        Assert.Equal("/bin/sh", launch.FileName);
        Assert.StartsWith("'/stub/zenity' '--password' | ", launch.Arguments[1]);
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

        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, Sig(Material, artifact), install, Options(), _log.Add);
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

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);
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
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add));
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
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add));
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
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add));
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
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add));
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
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install,
                new ApplyOptions { StagingRoot = StagingRoot }, _log.Add));
        Assert.Contains("ApplyOptions.AppPath", exception.Message);
    }

    [Fact]
    public void PlanApply_MissingBootstrapper_Refuses()
    {
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir(withBootstrapper: false);
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add));
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
        var launch = SyspkgReplay.PlanApply(Identity("rpm"), rpm, Sig(Material, rpm), install, Options(), _log.Add);
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
            Identity("alpineapk"), apk, Sig(Material, apk), install, Options(), _log.Add);
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
        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);
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

        var launch = SyspkgReplay.PlanApply(Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);

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

        var launch = SyspkgReplay.PlanApply(Identity("deb"), artifact, Sig(Material, artifact), install, options, _log.Add);
        Assert.Equal(
            Path.GetFullPath(artifact),
            launch.Arguments[Array.IndexOf(launch.Arguments, "--pkg") + 1]);
        Assert.Contains(_log, line => line.Contains("cannot retain rollback artifact"));
    }

    [Fact]
    public void PlanApply_EnvFile_ChmodFailure_LeavesNoResidue()
    {
        // 收权失败注入（ChmodProbe 缝）：env 件已写出后失败——catch 必须先删件
        // 再删目录，否则非空目录的非递归删恒抛、env 件按默认 umask 残留。
        SetupProbes(
            env: new Dictionary<string, string> { ["HOME"] = "/home/u" },
            commands: PkexecCommands("dpkg"));
        SyspkgReplay.ChmodProbe = _ => 1;
        var install = InstallDir();

        var launch = SyspkgReplay.PlanApply(
            Identity("deb"), Artifact(), Sig(Material, Artifact()), install, Options(), _log.Add);

        Assert.Null(launch.EnvFile);
        Assert.DoesNotContain("--env-file", launch.Arguments);
        Assert.Contains(_log, line => line.Contains("cannot write session env file"));
        // pkg-<guid>/ 目录连同 session.env 全清走——staging 根下零残件。
        Assert.True(Directory.Exists(StagingRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(StagingRoot));
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
        // 当前件（applied 标记指向）+ 旧件——回滚应选旧件；B2 起候选必须 pkg+sig 成对。
        var current = Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");
        var old = Path.Combine(RetentionDir, "myapp_1.0-1_amd64.deb");
        File.WriteAllText(current, "pkg-v2");
        File.WriteAllText(old, "pkg-v1");
        WriteSigSidecar(current);
        WriteSigSidecar(old);
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
        WriteSigSidecar(older);
        WriteSigSidecar(newer);
        WriteSigSidecar(current);
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

    // ---- B1：spawn 失败还原 ----

    [Fact]
    public void Apply_SpawnFailure_RestoresAppliedMarker_AndDeletesRetainedPair()
    {
        // B1：探测后提权工具被删/执行位丢失 → Process.Start 抛——全腿失败时
        // applied 标记必须回记规划前值，本轮新留存件（pkg+.sig）一并删，
        // env 件目录一并清。
        SetupProbes(
            env: new Dictionary<string, string> { ["HOME"] = "/home/u" },
            commands: PkexecCommands("dpkg"));
        SyspkgReplay.SpawnProbe = _ => throw new InvalidOperationException("spawn gone");
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "myapp_1.0-1_amd64.deb\n");
        var artifact = Artifact();

        // 全腿失败统一抛 UpdateException（spawn 原异常入 inner、notes 细节并消息）。
        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.Apply(Identity("deb"), artifact, Sig(Material, artifact),
                install, Options(), _log.Add));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("spawn gone", exception.Message);

        Assert.Equal("myapp_1.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
        // retention 只剩 applied——本轮留存对（pkg+.sig）已随失败删走。
        Assert.Equal(
            new[] { Path.Combine(RetentionDir, "applied") },
            Directory.EnumerateFiles(RetentionDir).ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(StagingRoot));
    }

    [Fact]
    public void Rollback_SpawnFailure_RestoresAppliedMarker()
    {
        // B1：rollback 规划期把 applied 翻记成所选旧包——全腿 spawn 失败必须翻回；
        // 所选旧包是既有件，失败还原绝不能误删。
        SetupProbes(commands: PkexecCommands("dpkg"));
        SyspkgReplay.SpawnProbe = _ => throw new InvalidOperationException("spawn gone");
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        var current = Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");
        var old = Path.Combine(RetentionDir, "myapp_1.0-1_amd64.deb");
        File.WriteAllText(current, "pkg-v2");
        WriteSigSidecar(current);
        File.WriteAllText(old, "pkg-v1");
        WriteSigSidecar(old);
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "myapp_2.0-1_amd64.deb\n");

        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.Rollback(Identity("deb"), install, Options(), _log.Add));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("spawn gone", exception.Message);

        Assert.Equal("myapp_2.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
        Assert.True(File.Exists(old) && File.Exists(old + ".sig"));
    }

    // ---- B2：留存件签名再验 ----

    [Fact]
    public void PlanApply_RetainedCopyFailsReverify_RefusesAndRestores()
    {
        // B2：直喂提权 pkg-apply 的留存副本必须自身过签——喂异件签名则副本
        // 验签失败→确定性拒绝不绕道，applied/留存对还原不留不可信件。
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        var artifact = Artifact();
        var other = Path.Combine(Root, "other.deb");
        File.WriteAllText(other, "different-bytes");

        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanApply(Identity("deb"), artifact, Sig(Material, other),
                install, Options(), _log.Add));

        Assert.Contains("re-verification", exception.Message);
        Assert.False(File.Exists(Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb")));
        Assert.False(File.Exists(Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb.sig")));
        Assert.False(File.Exists(Path.Combine(RetentionDir, "applied")));
    }

    [Fact]
    public void PlanRollback_TamperedCandidate_SkippedForNextVerified()
    {
        // B2：最新候选签后被换内容 → 验签失败跳过，拣下一个过签件；
        // .sig 件自身永不作候选。
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        var current = Path.Combine(RetentionDir, "a_3.0-1_amd64.deb");
        var valid = Path.Combine(RetentionDir, "a_2.0-1_amd64.deb");
        var tampered = Path.Combine(RetentionDir, "a_1.0-1_amd64.deb");
        File.WriteAllText(current, "v3");
        WriteSigSidecar(current);
        File.WriteAllText(valid, "v2");
        WriteSigSidecar(valid);
        File.WriteAllText(tampered, "v1");
        WriteSigSidecar(tampered);
        File.WriteAllText(tampered, "v1-tampered");   // 签后篡改——验签必败
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(tampered, DateTime.UtcNow.AddHours(-2)); // 比 valid 新
        File.SetLastWriteTimeUtc(valid, DateTime.UtcNow.AddHours(-3));
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "a_3.0-1_amd64.deb\n");

        var launch = SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add);

        Assert.Equal(valid,
            launch.Arguments[Array.IndexOf(launch.Arguments, "--pkg") + 1]);
        Assert.Contains(_log, line => line.Contains("skipping retained package"));
    }

    [Fact]
    public void PlanRollback_UnpairedCandidate_Refuses()
    {
        // B2：无 .sig 旁车的留存件不成对不候选——全部不可用即"无留存件"拒绝。
        SetupProbes(commands: PkexecCommands("dpkg"));
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        File.WriteAllText(Path.Combine(RetentionDir, "a_1.0-1_amd64.deb"), "v1");

        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.PlanRollback(Identity("deb"), install, Options(), _log.Add));
        Assert.Contains("no retained package", exception.Message);
    }

    // ---- B3：提权腿运行时退化 ----

    [Fact]
    public void Apply_ElevationLegFastFail_DegradesToNextLeg()
    {
        // B3：pkexec 在而无 polkit agent → 拉起即非零速退——观察窗内退出按该腿
        // 失败退化裸 sudo；第二腿窗口内仍在运行按"已拉起"交还。
        SetupProbes(commands: new Dictionary<string, string>
        {
            ["dpkg"] = "/stub/dpkg",
            ["pkexec"] = "/stub/pkexec",
            ["sudo"] = "/stub/sudo",
        });
        var spawned = new List<ProcessStartInfo>();
        using var sleeper = SleeperProcess();
        SyspkgReplay.SpawnProbe = startInfo =>
        {
            spawned.Add(startInfo);
            return spawned.Count == 1 ? FastFailProcess() : sleeper;
        };
        var install = InstallDir();
        var artifact = Artifact();

        var process = SyspkgReplay.Apply(Identity("deb"), artifact, Sig(Material, artifact),
            install, Options(), _log.Add);

        Assert.Same(sleeper, process);
        Assert.Equal(2, spawned.Count);
        Assert.Equal("/stub/pkexec", spawned[0].FileName);
        Assert.Equal("/stub/sudo", spawned[1].FileName);
        Assert.StartsWith("\"--\"", spawned[1].Arguments);   // 裸 sudo 腿 "--" 前缀保留
        // 退化成功不还原——applied 标记指向新包。
        Assert.Equal("myapp_2.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
        Assert.Contains(_log, line => line.Contains("trying next leg"));
        try
        {
            sleeper.Kill();
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Apply_AllLegsFastFail_RefusesAndRestores()
    {
        // B3+B1：唯一腿快败且无腿可退 → 确定性拒绝 + applied/留存对还原。
        SetupProbes(commands: PkexecCommands("dpkg"));
        SyspkgReplay.SpawnProbe = _ => FastFailProcess();
        var install = InstallDir();
        Directory.CreateDirectory(RetentionDir);
        File.WriteAllText(Path.Combine(RetentionDir, "applied"), "myapp_1.0-1_amd64.deb\n");
        var artifact = Artifact();

        var exception = Assert.Throws<UpdateException>(() =>
            SyspkgReplay.Apply(Identity("deb"), artifact, Sig(Material, artifact),
                install, Options(), _log.Add));

        Assert.Contains("no privilege escalation leg", exception.Message);
        Assert.Equal("myapp_1.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(RetentionDir, "applied")).Trim());
        Assert.Equal(
            new[] { Path.Combine(RetentionDir, "applied") },
            Directory.EnumerateFiles(RetentionDir).ToArray());
    }

    // 观察窗进程道具：快败件即刻非零退出；长活件窗内不退出（测试尾即杀）。
    private static Process SpawnRaw(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static Process FastFailProcess() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? SpawnRaw("cmd.exe", "/c exit 3")
            : SpawnRaw("/bin/sh", "-c \"exit 3\"");

    private static Process SleeperProcess() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? SpawnRaw("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
            : SpawnRaw("/bin/sh", "-c \"sleep 30\"");

    // ---- B5：artifact/identity format 词面一致性 ----

    [Fact]
    public void Apply_FormatLiteralMismatch_Refuses()
    {
        // B5：SelectArtifact 大小写不敏感匹配放进来的漂移 format（"DEB" vs "deb"）
        // → syspkg 分支词面一致性闸确定性拒绝（emitter 双侧恒小写不会触发）。
        var artifact = new Protocol.UpdateFeedArtifact
        {
            Target = "linux-x86_64",
            Format = "DEB",
            Url = "x.deb",
            File = "x.deb",
            Signature = "AA==",
        };
        var exception = Assert.Throws<UpdateException>(() =>
            UpdateApplier.Apply(Identity("deb"), artifact, Artifact(),
                InstallDir(), Options(), _log.Add));
        Assert.Contains("does not literally match", exception.Message);
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

    // ---- 留存默认根（跨宿主断言——UserBackupsRoot 平台形参缝）----

    [Fact]
    public void UserBackupsRoot_Windows_LocalAppData()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DotNet.Bundler", "backups"),
            SyspkgReplay.UserBackupsRoot(OSPlatform.Windows));
    }

    [Fact]
    public void UserBackupsRoot_MacOS_UserLibraryApplicationSupport()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "DotNet.Bundler", "backups"),
            SyspkgReplay.UserBackupsRoot(OSPlatform.OSX));
    }

    [Fact]
    public void UserBackupsRoot_Linux_XdgOverrideOrLocalShare()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "dotnet-bundler", "backups");

        // XDG_DATA_HOME 缺席 → ~/.local/share 兜底。
        SyspkgReplay.EnvProbe = _ => null;
        Assert.Equal(expected, SyspkgReplay.UserBackupsRoot(OSPlatform.Linux));

        // 空值按缺席处理（{ Length: > 0 } 口径）。
        SyspkgReplay.EnvProbe = name => name == "XDG_DATA_HOME" ? "" : null;
        Assert.Equal(expected, SyspkgReplay.UserBackupsRoot(OSPlatform.Linux));

        // 显式 XDG_DATA_HOME 覆盖优先。
        var xdg = Path.Combine(Root, "xdg-data");
        SyspkgReplay.EnvProbe = name => name == "XDG_DATA_HOME" ? xdg : null;
        Assert.Equal(Path.Combine(xdg, "dotnet-bundler", "backups"),
            SyspkgReplay.UserBackupsRoot(OSPlatform.Linux));

        // B-N8：相对值 XDG_DATA_HOME 按缺席退化——不漂到 cwd 下。
        SyspkgReplay.EnvProbe = name => name == "XDG_DATA_HOME" ? "relative/dir" : null;
        Assert.Equal(expected, SyspkgReplay.UserBackupsRoot(OSPlatform.Linux));
    }

    [Fact]
    public void ResolveRetentionDirectory_DefaultRoot_UserBackupsRootPlusLeafHash()
    {
        // 显式 RollbackBackupDirectory 缺席 → 默认根 = 宿主平台 UserBackupsRoot
        // + "<装位叶名>-<路径哈希>"（UpdateApplier.RetentionDirName 同口径）。
        var install = InstallDir();
        var resolved = SyspkgReplay.ResolveRetentionDirectory(install, new ApplyOptions());
        var leaf = UpdateApplier.RetentionDirName(Path.GetFullPath(install));
        Assert.Equal(leaf, Path.GetFileName(resolved));
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : OSPlatform.Linux;
        Assert.Equal(Path.Combine(SyspkgReplay.UserBackupsRoot(platform), leaf), resolved);
    }

    // ---- argv 拼接转义 ----

    [Fact]
    public void JoinArgv_EscapesEmbeddedQuote_AndTrailingBackslash()
    {
        // Arguments 是整条字符串进真 spawn/Exec——内嵌 " 折 \"、串尾 \ 翻倍、
        // 非引号相邻 \ 原样（CommandLineToArgvW 规则）。
        Assert.Equal(
            "\"a\\\"b\" \"c\\\\\" \"d\\e\" \"plain\"",
            SyspkgReplay.JoinArgv(new[] { "a\"b", "c\\", "d\\e", "plain" }));
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

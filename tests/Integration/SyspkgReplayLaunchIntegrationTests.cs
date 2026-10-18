// SyspkgReplay 提权拉起的真进程层：SpawnProbe 返回真 spawn 的有限时道具进程
//（Windows: cmd.exe /c exit N；POSIX: /bin/sh -c "exit N"），断言"已启动腿即
// 终局"语义——观察窗内/外任意退出码都只留痕+清 env 件，绝不换腿重放；
// 只有 spawn 抛异常（Process.Start 未起成）才退化下一腿。
// 道具统一有限时（即时退或 ~3s 延迟退），只清本轮登记进程，不起多余子代。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;

public sealed class SyspkgReplayLaunchIntegrationTests : TempBase
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<Process> _props = [];

    public override void Dispose()
    {
        SyspkgReplay.CommandProbe = null;
        SyspkgReplay.EnvProbe = null;
        SyspkgReplay.ExecProbe = null;
        SyspkgReplay.ChmodProbe = null;
        SyspkgReplay.SpawnProbe = null;
        // 道具皆有限时自终结；兜底只杀本轮登记的遗留件。
        foreach (var prop in _props)
        {
            try
            {
                if (!prop.HasExited)
                {
                    prop.Kill();
                }
                prop.Dispose();
            }
            catch (Exception)
            {
            }
        }
        base.Dispose();
    }

    // ---- fixture（沿 Unit 同款最小件形：身份/材料/签名/选项）----

    private void SetupProbes(
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? commands = null)
    {
        SyspkgReplay.EnvProbe = name =>
            env is { } table && table.TryGetValue(name, out var value) ? value : null;
        // CommandProbe 权威化——真实 PATH 全缺席化，spawn 由 SpawnProbe 接管。
        SyspkgReplay.CommandProbe = name =>
            commands is { } table && table.TryGetValue(name, out var path) ? path : null;
        SyspkgReplay.ExecProbe = (_, _) => 0;
    }

    private static Dictionary<string, string> TwoLegCommands => new()
    {
        ["dpkg"] = "/stub/dpkg",
        ["pkexec"] = "/stub/pkexec",
        ["sudo"] = "/stub/sudo",
    };

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

    private static string Sig(Protocol.UpdateKeyMaterial material, string path)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(material.ToPrivateParameters());
        return Convert.ToBase64String(Protocol.UpdateSignatureVerifier.ToP1363(
            ecdsa.SignData(File.ReadAllBytes(path), HashAlgorithmName.SHA256)));
    }

    private static string BootstrapperName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bundler-updater.exe" : "bundler-updater";

    private string InstallDir()
    {
        var dir = Path.Combine(Root, "install");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BootstrapperName), "stub-updater");
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

    private ApplyOptions Options() => new()
    {
        AppPath = AppFile,
        StagingRoot = StagingRoot,
        RollbackBackupDirectory = RetentionDir,
    };

    // 即时退道具：spawn 后在观察窗内以给定码退出。
    private Process ExitProp(int rc) => SpawnProp(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", $"/c exit {rc}")
            : ("/bin/sh", $"-c \"exit {rc}\""));

    // 窗外道具：有界轮询（30s 上限）本轮 gate 文件才退出——Apply 返回后由测试
    // 释放 gate，确定性覆盖"窗外退出"路径，不依赖固定 sleep 与观察窗的差值。
    // Windows 用 inbox powershell 轮询（timeout/choice 在重定向 stdin 下即刻
    // 退出，ping 会拉多余子代——都不作道具）；POSIX 用 sh 循环 sleep。
    private Process GatedExitProp(int rc, string gate)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var ps =
                "$d=0; while (-not (Test-Path '" + gate.Replace("'", "''") +
                "') -and $d -lt 300) { Start-Sleep -Milliseconds 100; $d++ }; exit " + rc;
            return SpawnProp(("powershell.exe", "-NoProfile -Command \"" + ps + "\""));
        }
        var sh =
            "n=0; while [ ! -f '" + gate.Replace("'", "'\\''") +
            "' ] && [ $n -lt 30 ]; do sleep 1; n=$((n+1)); done; exit " + rc;
        return SpawnProp(("/bin/sh", "-c \"" + sh + "\""));
    }

    private Process SpawnProp((string File, string Args) prop)
    {
        var process = Process.Start(new ProcessStartInfo(prop.File, prop.Args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _props.Add(process);
        return process;
    }

    private string RetainedFile => Path.Combine(RetentionDir, "myapp_2.0-1_amd64.deb");
    private string AppliedMarker => Path.Combine(RetentionDir, "applied");

    private bool SessionEnvRemains() =>
        Directory.Exists(StagingRoot) &&
        (Directory.EnumerateFiles(StagingRoot, "session.env", SearchOption.AllDirectories).Any() ||
         Directory.EnumerateDirectories(StagingRoot, "pkg-*").Any());

    private void AssertSessionEnvGone(string why)
    {
        // Exited 钩在 ThreadPool 异步触发——轮询至删余（窗内速退的内联清理路径下
        // 首次检查即过，轮询只是兼容窗外腿的异步时序）。
        for (var i = 0; i < 100 && SessionEnvRemains(); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(SessionEnvRemains(), why);
    }

    // ---- 已启动腿即终局：窗内任意 rc 原样返回，不换腿不重放 ----

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(42)]
    public void Apply_StartedLegExitsInWindow_ReturnsRcAsIs_NoReplay(int rc)
    {
        // pkexec+sudo 两腿在场：第一腿 spawn 即终局——窗内任意退出码（0 成功、
        // pkg-apply 3/4/5/6、pkexec 126 取消、127 鉴权失败、其他）都原样交还，
        // 不补拉第二腿；留存件/applied 标记按"最近交付工件"语义保留。
        SetupProbes(
            env: new Dictionary<string, string> { ["HOME"] = "/home/u" },
            commands: TwoLegCommands);
        var spawned = new List<ProcessStartInfo>();
        SyspkgReplay.SpawnProbe = startInfo =>
        {
            spawned.Add(startInfo);
            return ExitProp(rc);
        };
        var install = InstallDir();
        var artifact = Artifact();

        using var process = SyspkgReplay.Apply(Identity("deb"), artifact,
            Sig(Material, artifact), install, Options(), _log.Enqueue);

        Assert.Single(spawned);
        Assert.Equal("/stub/pkexec", spawned[0].FileName);
        Assert.True(process.WaitForExit(30_000), "道具腿应有界退出");
        Assert.Equal(rc, process.ExitCode);
        // 窗内速退走内联清理——session.env 与 pkg-* 目录不得残留。
        AssertSessionEnvGone("窗内退出腿的 env 件删余必须已执行");
        // 交付标记保留：取消/失败也不还原（不代表安装成功，供诊断与回滚）。
        Assert.True(File.Exists(RetainedFile) && File.Exists(RetainedFile + ".sig"));
        Assert.Equal("myapp_2.0-1_amd64.deb", File.ReadAllText(AppliedMarker).Trim());
        Assert.Contains(_log, line => line.Contains($"exited {rc}"));
    }

    // ---- 窗外退出同样终局：不补腿、Exited 钩负责留痕+清理 ----

    [Theory]
    [InlineData(126)]
    [InlineData(6)]
    public void Apply_LegExitsBeyondWindow_NoReplay_EnvCleanedOnExit(int rc)
    {
        // gate 闸道具：Apply 返回时腿必仍存活（2s 观察窗内道具等闸不退出，
        // 与调度停顿无关）；测试创建本轮 gate 件释放退出——窗外退出经
        // Exited 钩做同样的留痕+env 删余，绝不补拉下一腿。
        SetupProbes(
            env: new Dictionary<string, string> { ["HOME"] = "/home/u" },
            commands: TwoLegCommands);
        var gate = TempPath($"leg-gate-{rc}.release");
        var spawned = new List<ProcessStartInfo>();
        SyspkgReplay.SpawnProbe = startInfo =>
        {
            spawned.Add(startInfo);
            return GatedExitProp(rc, gate);
        };
        var install = InstallDir();
        var artifact = Artifact();

        using var process = SyspkgReplay.Apply(Identity("deb"), artifact,
            Sig(Material, artifact), install, Options(), _log.Enqueue);

        Assert.Single(spawned);
        Assert.False(process.HasExited, "gate 未释放时腿应仍存活");
        File.WriteAllText(gate, "go");
        Assert.True(process.WaitForExit(45_000), "gate 释放后道具应有界退出");
        Assert.Equal(rc, process.ExitCode);
        Assert.Single(spawned);   // 窗外退出仍不补拉第二腿
        AssertSessionEnvGone("窗外退出腿的 Exited 钩删余必须已执行");
        Assert.True(File.Exists(RetainedFile) && File.Exists(RetainedFile + ".sig"));
        Assert.Equal("myapp_2.0-1_amd64.deb", File.ReadAllText(AppliedMarker).Trim());
        Assert.Contains(_log, line => line.Contains($"exited {rc}"));
    }

    // ---- 唯一退化判据：spawn 抛异常才换下一腿 ----

    [Fact]
    public void Apply_SpawnThrowsOnFirstLeg_NextLegLaunchedForReal()
    {
        // 第一腿 spawn 抛（探测后工具被删/执行位丢失）→ 第二腿真拉起交付；
        // 已交付腿窗内 rc0 速退走内联清理。
        SetupProbes(
            env: new Dictionary<string, string> { ["HOME"] = "/home/u" },
            commands: TwoLegCommands);
        var spawned = new List<ProcessStartInfo>();
        SyspkgReplay.SpawnProbe = startInfo =>
        {
            spawned.Add(startInfo);
            return spawned.Count == 1
                ? throw new InvalidOperationException("spawn gone")
                : ExitProp(0);
        };
        var install = InstallDir();
        var artifact = Artifact();

        using var process = SyspkgReplay.Apply(Identity("deb"), artifact,
            Sig(Material, artifact), install, Options(), _log.Enqueue);

        Assert.Equal(2, spawned.Count);
        Assert.Equal("/stub/pkexec", spawned[0].FileName);
        Assert.Equal("/stub/sudo", spawned[1].FileName);
        Assert.StartsWith("\"--\"", spawned[1].Arguments);   // 裸 sudo 腿 "--" 前缀保留
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
        AssertSessionEnvGone("第二腿交付后 env 件删余必须已执行");
        Assert.Equal("myapp_2.0-1_amd64.deb", File.ReadAllText(AppliedMarker).Trim());
        Assert.Contains(_log, line => line.Contains("failed to spawn"));
        Assert.Contains(_log, line => line.Contains("trying next leg"));
    }
}

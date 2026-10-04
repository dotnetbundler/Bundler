// docker 容器腿：docker 可用性 → Skip；镜像缺失时尝试 pull、失败 → Skip（与脚本语义一致）。
internal static class DockerRunner
{
    private static bool? _available;

    public static bool IsAvailable
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    private static bool Probe()
    {
        if (!ExternalTools.Has("docker"))
        {
            return false;
        }
        try
        {
            var result = ProcessRunner.Run("docker", ["info"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Require()
        => Assert.SkipWhen(!IsAvailable, "SKIP: docker is unavailable on this host.");

    // 镜像确保可用（本地有或 pull 成功）→ true；不可用 → false（脚本"SKIP 单镜像继续"语义）。
    public static bool TryEnsureImage(string image)
    {
        if (!IsAvailable)
        {
            return false;
        }
        var inspect = ProcessRunner.Run("docker", ["image", "inspect", image],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
        if (inspect.ExitCode == 0)
        {
            return true;
        }
        var pull = ProcessRunner.Run("docker", ["pull", image],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });
        return pull.ExitCode == 0;
    }

    public static void RequireImage(string image)
    {
        Require();
        var inspect = ProcessRunner.Run("docker", ["image", "inspect", image],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
        if (inspect.ExitCode == 0)
        {
            return;
        }
        var pull = ProcessRunner.Run("docker", ["pull", image],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });
        Assert.SkipWhen(pull.ExitCode != 0,
            $"SKIP: docker image '{image}' could not be pulled (daemon present but image unavailable).");
    }

    // 平台探测：linux/arm64 容器在本机是否可跑（binfmt/qemu 注册即视为可跑）。
    private static bool? _arm64Runnable;
    public static bool CanRunArm64Containers
    {
        get
        {
            _arm64Runnable ??= ProbeArm64();
            return _arm64Runnable.Value;
        }
    }

    private static bool ProbeArm64()
    {
        if (!IsAvailable)
        {
            return false;
        }
        try
        {
            RequireImage("alpine:latest");
            var result = ProcessRunner.Run("docker",
                ["run", "--rm", "--platform", "linux/arm64", "alpine:latest", "uname", "-m"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(2) });
            return result.ExitCode == 0 && result.StdOut.Contains("aarch64");
        }
        catch
        {
            return false;
        }
    }

    public sealed record Mount(string HostPath, string ContainerPath, bool ReadOnly = true);

    // docker run --rm [-v host:guest[:ro]] image sh -c '<script>'。
    // platform 为空表示宿主原生架构；"linux/arm64" 走 binfmt/qemu。
    public static ProcessRunner.Result RunScript(string image, string script,
        IEnumerable<Mount>? mounts = null, string? platform = null,
        TimeSpan? timeout = null, string? extraArgs = null)
    {
        var args = new List<string> { "run", "--rm" };
        if (platform is not null)
        {
            args.AddRange(["--platform", platform]);
        }
        if (extraArgs is not null)
        {
            args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        if (mounts is not null)
        {
            foreach (var mount in mounts)
            {
                // docker 对不存在的 -v 源会在宿主自动建 root 属空目录——污染工作区
                // 且让后续 publish 撞 EACCES；挂载源缺失必须先于 docker run 失败。
                Assert.True(
                    File.Exists(mount.HostPath) || Directory.Exists(mount.HostPath),
                    $"docker mount source missing on host: {mount.HostPath}");
                args.AddRange(["-v", $"{mount.HostPath}:{mount.ContainerPath}{(mount.ReadOnly ? ":ro" : "")}"]);
            }
        }
        args.Add(image);
        args.AddRange(["sh", "-c", script]);
        return ProcessRunner.Run("docker", args,
            new ProcessRunner.Options { Timeout = timeout ?? TimeSpan.FromMinutes(10) });
    }

    public static ProcessRunner.Result CheckedScript(string image, string script,
        string what, IEnumerable<Mount>? mounts = null, string? platform = null,
        TimeSpan? timeout = null, string? extraArgs = null)
    {
        var result = RunScript(image, script, mounts, platform, timeout, extraArgs);
        ProcessRunner.AssertSuccess(result, what);
        return result;
    }
}

// 跨版本兼容腿：v1 冻件（alpha.83 真产，Fixtures/UpdateCompat/v1，出处见 PROVENANCE.md）
// 与当前代码的对握——旧装被新更新、新包被旧引导换。
// 断代不是失败：腿断言的是"兼容成立或干净拒绝"，有意断代时改断言并另冻 v2。
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text.Json.Nodes;
using DotNet.Bundler.Update;
using DotNet.Bundler.Updater;
using DotNet.Bundler.Updater.Bootstrap;
using Protocol = DotNet.Bundler.Updater.Protocol;
using UpdateKeyMaterial = DotNet.Bundler.Core.Update.UpdateKeyMaterial;


public sealed class UpdateCompatFixture : IDisposable
{
    // 惰性创建：宿主/冻件门禁在各用例体内 Skip，之后才允许占锁建目录。
    private readonly Lazy<IntegrationWorkspace> _ws = new(() => IntegrationWorkspace.Create(
        "update-compat-integration", "BundlerUpdateCompat"));

    public IntegrationWorkspace Ws => _ws.Value;

    public void Dispose()
    {
        if (_ws.IsValueCreated)
        {
            _ws.Value.Dispose();
        }
    }
}

public sealed class UpdateCompatTests : IClassFixture<UpdateCompatFixture>
{
    private readonly UpdateCompatFixture _fixture;
    private IntegrationWorkspace Ws => _fixture.Ws;

    public UpdateCompatTests(UpdateCompatFixture fixture) => _fixture = fixture;

    static string V1Dir => Path.Combine(RepositoryLayout.FixturesDirectory, "UpdateCompat", "v1");
    static string V1FeedJson => Path.Combine(V1Dir, "feed", "bundler-update-feed.stable.json");
    static string V1Zip => Path.Combine(V1Dir, "feed", "linux-x64", "zip",
        "hellov1-1.0.0-linux-x64.zip");
    static string V1Sh => Path.Combine(V1Dir, "bootstrap", "bundler-updater.sh");
    // linux 引导件收敛 musl 静态单件（static-pie 件 glibc 宿主照跑）——
    // linux-x86_64 目标经 musl 姊妹件解析，本腿直接取 musl 件跑真实 apply。
    static string CurrentAot => Path.Combine(RepositoryLayout.Root,
        "src", "Bundler.Updater.Bootstrap", "tools", "linux-musl-x86_64", "bundler-updater");

    // 每用例一个工作区子目录：IntegrationWorkspace 的身份标记+锁纪律，
    // 不另起 /tmp GUID 目录。
    string CaseRoot(string name)
    {
        var root = Ws.Combine(name);
        Directory.CreateDirectory(root);
        return root;
    }

    // 解出 v1 zip 载荷当"旧装"（侧车+内嵌 v1 引导件都在树里）。
    static string ExtractV1Install(string root)
    {
        Assert.SkipUnless(File.Exists(V1Zip), $"v1 frozen zip missing: {V1Zip}");
        var dir = Path.Combine(root, "v1zip");
        ZipFile.ExtractToDirectory(V1Zip, dir);
        var inner = Directory.GetDirectories(dir).Single();
        var install = Path.Combine(root, "install-v1");
        Directory.Move(inner, install);
        return install;
    }

    // 侧车 feedUrl/publicKey 指向腿内 feed 与运行期密钥对——冻件里烙的是生成机
    // 路径与 v1 密钥，须改指（私钥不入库，腿内签名材料用运行期生成的）。
    static void RetargetSidecar(string installDir, string feedJsonPath, string? publicKey = null)
    {
        var sidecar = Path.Combine(installDir, "bundler-update.json");
        var node = JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
        node["feedUrl"] = feedJsonPath;
        if (publicKey != null)
        {
            node["publicKey"] = publicKey;
        }
        File.WriteAllText(sidecar, node.ToJsonString());
    }

    static string WriteFeed(string dir, string channel, string version,
        UpdateKeyMaterial material, params Protocol.UpdateFeedArtifact[] artifacts)
    {
        Directory.CreateDirectory(dir);
        var feed = new Protocol.UpdateFeed
        {
            Version = version, Channel = channel,
            PublishedAt = "2026-10-02T00:00:00+00:00",
        };
        feed.Artifacts.AddRange(artifacts);
        var path = Path.Combine(dir, Protocol.UpdateFeed.FeedFileName(channel));
        using (var stream = File.Create(path))
        {
            new DataContractJsonSerializer(typeof(Protocol.UpdateFeed))
                .WriteObject(stream, feed);
        }
        File.WriteAllBytes(path + ".sig", EcdsaSigner.SignFile(path, material));
        return path;
    }

    static string WriteArtifact(string path, string topDir,
        params (string name, byte[] bytes)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry($"{topDir}/{name}");
            using var writer = entry.Open();
            writer.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    static string SignAndFeed(string feedDir, string channel, string version,
        UpdateKeyMaterial material, string artifact)
    {
        var name = Path.GetFileName(artifact);
        return WriteFeed(feedDir, channel, version, material,
            new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x64", Format = "zip",
                Url = name, File = name,
                Sha256 = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact))),
                Size = new FileInfo(artifact).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
            });
    }

    static void AssertSwapped(string install, string appContent)
    {
        Assert.Equal(appContent, File.ReadAllText(Path.Combine(install, "app")));
        Assert.False(Directory.Exists(install + ".bundler-backup"));
        Assert.False(File.Exists(install + ".bundler-swap"));
    }

    // 当前打包产物的载荷树形态：app + 身份侧车 + 引导件二进制 + 子目录件。
    static string CurrentShapePayload(string root)
    {
        Assert.SkipUnless(File.Exists(CurrentAot), $"current AOT bootstrapper missing: {CurrentAot}");
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "lib"));
        File.WriteAllText(Path.Combine(payload, "app"), "v2");
        File.WriteAllText(Path.Combine(payload, "bundler-update.json"),
            "{\"format\":\"zip\",\"rid\":\"linux-x86_64\",\"channel\":\"stable\"," +
            "\"feedUrl\":\"<leg-internal>\",\"publicKey\":\"<leg-internal>\"}");
        File.WriteAllText(Path.Combine(payload, "lib", "data.txt"), "v2-data");
        File.Copy(CurrentAot, Path.Combine(payload, "bundler-updater"));
        return payload;
    }

    [Fact]
    async Task V1Feed_ReadableAndVerifiable_ByCurrentClient()
    {
        Assert.SkipUnless(File.Exists(V1FeedJson), $"v1 frozen feed missing: {V1FeedJson}");
        var root = CaseRoot("v1feed");
        try
        {
            var install = ExtractV1Install(root);
            RetargetSidecar(install, V1FeedJson);
            // 装机报旧版本号 → 冻件清单被选中，下载+验签真 v1 工件（v1 公钥在侧车内）。
            var client = UpdateClient.FromInstallDirectory(install, "0.9.0");
            var info = await client.CheckForUpdateAsync();
            Assert.NotNull(info);
            var downloaded = await client.DownloadAsync(info, Path.Combine(root, "dl"));
            client.Verify(info, downloaded);
        }
        finally { Ws.Remove(root); }
    }

    [Fact]
    async Task V1Install_EndToEndSwap_ByCurrentFeed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !TestPlatform.IsMusl &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "v1 冻件的引导件是 linux-x86_64 AOT——端到端腿只在 linux-x86_64 宿主跑。");
        var root = CaseRoot("v1swap");
        try
        {
            var material = UpdateKeyMaterial.Generate();
            var install = ExtractV1Install(root);
            var feedDir = Path.Combine(root, "feed");
            var feedJson = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable"));
            // 当前形态载荷：新 app + 侧车（v1 公钥，后续轮次仍验得过）+ 当前引导件。
            var artifact = WriteArtifact(Path.Combine(feedDir, "app-2.0.0.zip"), "app-v2",
                ("app", "v2"u8.ToArray()),
                ("bundler-update.json", System.Text.Encoding.UTF8.GetBytes(
                    "{\"format\":\"zip\",\"rid\":\"linux-x86_64\",\"channel\":\"stable\"," +
                    $"\"feedUrl\":\"{feedJson}\",\"publicKey\":\"{material.PublicPointBase64()}\"}}")),
                ("bundler-updater", File.ReadAllBytes(CurrentAot)));
            SignAndFeed(feedDir, "stable", "2.0.0", material, artifact);
            RetargetSidecar(install, feedJson, material.PublicPointBase64());

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync())!;
            var downloaded = await client.DownloadAsync(info, Path.Combine(root, "dl"));
            client.Verify(info, downloaded);
            // 换包由装机内嵌的 v1 AOT 引导件执行——真实"旧装被新更新"链。
            var process = client.Apply(info, downloaded,
                new ApplyOptions { StagingRoot = Path.Combine(root, "staging") });
            Assert.True(TestProcessExit.WaitForExit(process, 90_000), "v1 bootstrapper did not exit in 90s");
            Assert.Equal(0, process.ExitCode);
            AssertSwapped(install, "v2");
        }
        finally { Ws.Remove(root); }
    }

    [Fact]
    void V1Payload_AppliesUnder_CurrentBootstrap()
    {
        var root = CaseRoot("v1payload");
        try
        {
            var payload = ExtractV1Install(Path.Combine(root, "p"));
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "stale.txt"), "old");
            var log = Path.Combine(root, "u.log");
            var rc = BootstrapPlan.Run(
                ["apply", "--install-dir", install, "--payload", payload, "--log", log]);
            Assert.Equal(0, rc);
            Assert.Equal("V1_PAYLOAD_FILE",
                File.ReadAllText(Path.Combine(install, "data.txt")).TrimEnd());
            Assert.True(File.Exists(Path.Combine(install, "bundler-update.json")));
            Assert.False(File.Exists(Path.Combine(install, "stale.txt")));
        }
        finally { Ws.Remove(root); }
    }

    [Fact]
    void V1ShBootstrap_Applies_CurrentPayload()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "v1 sh 引导件腿只在 POSIX 宿主跑。");
        Assert.SkipUnless(File.Exists(V1Sh), $"v1 frozen sh bootstrapper missing: {V1Sh}");
        var root = CaseRoot("v1sh");
        try
        {
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            // 当前真实载荷形态：app + 侧车 + 引导件 + 子目录。
            var payload = CurrentShapePayload(root);
            RunProcess("sh", [V1Sh, "apply", "--install-dir", install,
                "--payload", payload, "--log", Path.Combine(root, "u.log")]);
            AssertSwapped(install, "v2");
            Assert.Equal("v2-data", File.ReadAllText(Path.Combine(install, "lib", "data.txt")));
            Assert.True(File.Exists(Path.Combine(install, "bundler-update.json")));
        }
        finally { Ws.Remove(root); }
    }

    [Fact]
    void V1AotBootstrap_Applies_CurrentPayload()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !TestPlatform.IsMusl &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "v1 AOT 引导件是 linux-x86_64——只在 linux-x86_64 宿主跑。");
        Assert.SkipUnless(File.Exists(V1Zip), $"v1 frozen zip missing: {V1Zip}");
        var root = CaseRoot("v1aot");
        try
        {
            var binary = Path.Combine(root, "bundler-updater");
            using (var archive = ZipFile.OpenRead(V1Zip))
            {
                var entry = archive.Entries.Single(e =>
                    e.FullName.EndsWith("/bundler-updater", StringComparison.Ordinal));
                entry.ExtractToFile(binary);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(binary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            var payload = CurrentShapePayload(root);
            RunProcess(binary, ["apply", "--install-dir", install,
                "--payload", payload, "--log", Path.Combine(root, "u.log")]);
            AssertSwapped(install, "v2");
            Assert.Equal("v2-data", File.ReadAllText(Path.Combine(install, "lib", "data.txt")));
            Assert.True(File.Exists(Path.Combine(install, "bundler-update.json")));
        }
        finally { Ws.Remove(root); }
    }

    // 退出码断言只留这一处：非零时把 stdout/stderr 一并带进失败消息，
    // 调用方不再重复 Assert.Equal(0, rc)。
    static void RunProcess(string file, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = TestProcessExit.Start(start)
            ?? throw new InvalidOperationException($"spawn timed out: {file}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!TestProcessExit.WaitForExit(process, 90_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail($"{file} did not exit in 90s");
        }
        Task.WaitAll(stdout, stderr);
        Assert.True(process.ExitCode == 0,
            $"{file} rc={process.ExitCode}\nstdout:{stdout.Result}\nstderr:{stderr.Result}");
    }
}

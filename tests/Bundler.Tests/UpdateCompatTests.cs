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


public class UpdateCompatTests
{
    static string V1Dir => RepoPath("tests/Bundler.Tests/Fixtures/UpdateCompat/v1");
    static string V1FeedJson => Path.Combine(V1Dir, "feed", "bundler-update-feed.stable.json");
    static string V1Zip => Path.Combine(V1Dir, "feed", "linux-x64", "zip",
        "hellov1-1.0.0-linux-x64.zip");
    static string V1Sh => Path.Combine(V1Dir, "bootstrap", "bundler-updater.sh");
    static string CurrentAot => RepoPath(
        "src/Bundler.Updater.Bootstrap/tools/linux-x86_64/bundler-updater");

    // 解出 v1 zip 载荷当"旧装"（侧车+内嵌 v1 引导件都在树里）。
    static string ExtractV1Install(string root)
    {
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
    static async Task V1Feed_ReadableAndVerifiable_ByCurrentClient()
    {
        var root = CreateTempDirectory();
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
        finally { Cleanup(root); }
    }

    [Fact]
    static async Task V1Install_EndToEndSwap_ByCurrentFeed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "v1 冻件的引导件是 linux-x86_64 AOT——端到端腿只在 linux-x86_64 宿主跑。");
        var root = CreateTempDirectory();
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
            Assert.True(process.WaitForExit(90_000), "v1 bootstrapper did not exit in 90s");
            Assert.Equal(0, process.ExitCode);
            AssertSwapped(install, "v2");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void V1Payload_AppliesUnder_CurrentBootstrap()
    {
        var root = CreateTempDirectory();
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
        finally { Cleanup(root); }
    }

    [Fact]
    static void V1ShBootstrap_Applies_CurrentPayload()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "v1 sh 引导件腿只在 POSIX 宿主跑。");
        var root = CreateTempDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            // 当前真实载荷形态：app + 侧车 + 引导件 + 子目录。
            var payload = CurrentShapePayload(root);
            var rc = RunProcess("sh", [V1Sh, "apply", "--install-dir", install,
                "--payload", payload, "--log", Path.Combine(root, "u.log")]);
            Assert.Equal(0, rc);
            AssertSwapped(install, "v2");
            Assert.Equal("v2-data", File.ReadAllText(Path.Combine(install, "lib", "data.txt")));
            Assert.True(File.Exists(Path.Combine(install, "bundler-update.json")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    static void V1AotBootstrap_Applies_CurrentPayload()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "v1 AOT 引导件是 linux-x86_64——只在 linux-x86_64 宿主跑。");
        var root = CreateTempDirectory();
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
            var rc = RunProcess(binary, ["apply", "--install-dir", install,
                "--payload", payload, "--log", Path.Combine(root, "u.log")]);
            Assert.Equal(0, rc);
            AssertSwapped(install, "v2");
            Assert.Equal("v2-data", File.ReadAllText(Path.Combine(install, "lib", "data.txt")));
            Assert.True(File.Exists(Path.Combine(install, "bundler-update.json")));
        }
        finally { Cleanup(root); }
    }

    static int RunProcess(string file, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail($"{file} did not exit in 90s");
        }
        Task.WaitAll(stdout, stderr);
        Assert.True(process.ExitCode == 0,
            $"{file} rc={process.ExitCode}\nstdout:{stdout.Result}\nstderr:{stderr.Result}");
        return process.ExitCode;
    }

    static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Bundler.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "bundler-updatecompat-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { /* best-effort */ }
    }
}

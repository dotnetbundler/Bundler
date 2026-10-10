// CLI-C1 集成腿的 C# 移植：对应原 tests/Cli.Integration/Verify.sh。
// 覆盖点：CLI 构建 → validate/plan/bundle 全命令 → 退出码分级(0/1/2)
// → --json 机器可读输出 → quiet/verbose 日志档 → 'all' 展开 → bundler.json 驱动与覆盖
// → 未知键/缺失配置拒绝 → 后端失败 exit 1 → NativeAOT 端到端与裁剪断言（Linux）。
// JSON 断言改用 System.Text.Json（脚本时代 python3 的等价物）。
using System.Text.Json;

public sealed class CliFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; }
    public string CliDll { get; }
    public string PublishDir { get; }
    public string OutDir { get; }

    public CliFixture()
    {
        // 原脚本无 OS 门禁（非 Linux 上 appimage/AOT 子腿单独跳过），保持一致。
        ExternalTools.Require("dotnet");
        Ws = IntegrationWorkspace.Create("cli-integration", "BundlerCliIntegration");
        var cliProject = Path.Combine(RepositoryLayout.Root, "src", "Bundler.Cli", "Bundler.Cli.csproj");
        var cliOut = Ws.Combine("cli");
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["build", cliProject, "-c", "Release", "-o", cliOut]),
            "CLI build failed");
        CliDll = Path.Combine(cliOut, "bundler.dll");
        Assert.True(File.Exists(CliDll), $"CLI dll missing: {CliDll}");

        PublishDir = Ws.Combine("publish");
        OutDir = Ws.Combine("out");
        var fixtureProject = Path.Combine(RepositoryLayout.FixturesDirectory,
            "Cli", "BundlerCliIntegrationFixture.csproj");
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", fixtureProject, "-c", "Release", "-o", PublishDir]),
            "fixture publish failed");
        Assert.True(File.Exists(Path.Combine(PublishDir, MainExe)),
            "fixture executable missing");

        _native = new Lazy<string>(() =>
        {
            var aotDir = Ws.Combine("aot");
            var publish = Dotnet.Run(
                ["publish",
                 Path.Combine(RepositoryLayout.Root, "src", "Bundler.Cli", "Bundler.Cli.csproj"),
                 "-c", "Release", "-r", TestPlatform.LinuxRuntimeIdentifier, "-o", aotDir, "-v", "q"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) });
            ProcessRunner.AssertSuccess(publish, $"AOT publish failed:\n{publish.Output}");
            return Path.Combine(aotDir, "bundler");
        });
    }

    public string MainExe => "BundlerCliIntegrationFixture";

    // NativeAOT 发布很贵（分钟级），三个 AOT 用例共享同一份产物。
    private readonly Lazy<string> _native;

    public string NativeBinary => _native.Value;
    public string NativeDir => Path.GetDirectoryName(_native.Value)!;

    public IEnumerable<string> BaseArgs(string? outputDir = null) =>
    [
        "--input-dir", PublishDir, "--rid", "linux-x64",
        "--product-name", "CliFixture", "--identifier", "dev.example.cli",
        "--package-version", "1.0.0", "--main-executable", MainExe,
        "--output-dir", outputDir ?? OutDir,
    ];

    internal ProcessRunner.Result Cli(params string[] args)
        => ProcessRunner.Run("dotnet", [CliDll, .. args],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });

    public void Dispose() => Ws.Dispose();
}

public sealed class CliIntegrationTests : IClassFixture<CliFixture>
{
    private readonly CliFixture _f;

    public CliIntegrationTests(CliFixture fixture) => _f = fixture;

    private static string[] BasePlus(CliFixture f, params string[] extra)
        => [.. f.BaseArgs(), .. extra];

    [Fact]
    public void VersionAndHelp()
    {
        var version = _f.Cli("--version");
        ProcessRunner.AssertSuccess(version, "--version failed");
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^\d+\.\d+\.\d+"), version.StdOut);
        var help = _f.Cli("--help");
        ProcessRunner.AssertSuccess(help, "--help failed");
        Assert.Contains("Usage:", help.StdOut);
    }

    [Fact]
    public void UsageFailuresExit2()
    {
        Assert.Equal(2, _f.Cli().ExitCode);
        Assert.Equal(2, _f.Cli("badcmd").ExitCode);
        Assert.Equal(2, _f.Cli(["bundle", .. BasePlus(_f, "--formats", "bogus")]).ExitCode);
        Assert.Equal(2, _f.Cli(["bundle", .. BasePlus(_f, "--formats", "zip", "--unexpected-flag")]).ExitCode);
        Assert.Equal(2, _f.Cli(["bundle", .. BasePlus(_f, "--formats")]).ExitCode);
    }

    [Fact]
    public void ValidateValidInvalidAndMissingInput()
    {
        var ok = _f.Cli(["validate", .. BasePlus(_f, "--formats", "zip")]);
        ProcessRunner.AssertSuccess(ok, "validate must exit 0 on valid config");

        var jsonOut = _f.Ws.Combine("validate.json");
        var json = _f.Cli(["validate", .. BasePlus(_f, "--formats", "zip", "--json")]);
        ProcessRunner.AssertSuccess(json, "validate --json must exit 0");
        File.WriteAllText(jsonOut, json.StdOut);
        using var doc = JsonDocument.Parse(json.StdOut);
        Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("issues").EnumerateArray());

        var badBase = _f.BaseArgs().Select(a => a).ToList();
        badBase[badBase.IndexOf("--input-dir") + 1] = "/nonexistent-dir";
        var missing = _f.Cli(["validate", .. badBase, "--formats", "zip"]);
        Assert.Equal(2, missing.ExitCode);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex("inputDirectory|mainExecutable"),
            missing.StdErr);
    }

    [Fact]
    public void ValidateMatrixViolationExits2()
    {
        var result = _f.Cli(["validate", .. BasePlus(_f, "--formats", "nsis")]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not supported", result.StdErr);
    }

    [Fact]
    public void PlanHumanAndJsonOutput()
    {
        var formats = TestPlatform.IsLinux ? "deb,rpm,appimage,zip,targz" : "deb,rpm,zip,targz";
        var plan = _f.Cli(["plan", .. BasePlus(_f, "--formats", formats)]);
        ProcessRunner.AssertSuccess(plan, "plan must exit 0");
        foreach (var fmt in formats.Split(','))
        {
            Assert.Contains($"linux-x64 {fmt} ->", plan.StdOut);
        }
        var json = _f.Cli(["plan", .. BasePlus(_f, "--formats", "deb", "--json")]);
        ProcessRunner.AssertSuccess(json, "plan --json must exit 0");
        using var doc = JsonDocument.Parse(json.StdOut);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("deb", items[0].GetProperty("format").GetString());
        Assert.Equal("linux-x64", items[0].GetProperty("runtimeIdentifier").GetString());
    }

    [Fact]
    public void PlanAllExpandsToLinuxFormatsOnly()
    {
        var json = _f.Cli(["plan", .. BasePlus(_f, "--formats", "all", "--json")]);
        ProcessRunner.AssertSuccess(json, "plan all must exit 0");
        using var doc = JsonDocument.Parse(json.StdOut);
        var formats = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("format").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "deb", "rpm", "appimage", "zip", "targz" }, formats);
    }

    [Fact]
    public void BundleProducesArtifactsOnStdout()
    {
        var formats = TestPlatform.IsLinux ? "deb,rpm,appimage,zip,targz" : "deb,rpm,zip,targz";
        var result = _f.Cli(["bundle", .. BasePlus(_f, "--formats", formats)]);
        ProcessRunner.AssertSuccess(result, $"bundle must exit 0 (stderr: {result.StdErr})");
        var expected = new[]
        {
            "deb/clifixture_1.0.0-1_amd64.deb",
            "rpm/clifixture-1.0.0-1.x86_64.rpm",
            "zip/clifixture-1.0.0-linux-x64.zip",
            "targz/clifixture-1.0.0-linux-x64.tar.gz",
        };
        foreach (var rel in expected)
        {
            var abs = Path.Combine(_f.OutDir, "linux-x64", rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(abs), $"artifact missing: linux-x64/{rel}");
            Assert.Contains(abs, result.StdOut);
        }
        if (TestPlatform.IsLinux)
        {
            var appDir = Path.Combine(_f.OutDir, "linux-x64", "appimage");
            var appImage = Directory.EnumerateFiles(appDir, "*.AppImage").FirstOrDefault();
            Assert.NotNull(appImage);
            Assert.True(File.Exists(appImage + ".sha256"), "appimage sha256 sidecar missing");
        }
    }

    [Fact]
    public void BundleJsonIsCleanMachineOutput()
    {
        var outDir = _f.Ws.Combine("out-json");
        var result = _f.Cli(["bundle", .. _f.BaseArgs(outDir), "--formats", "zip", "--json"]);
        ProcessRunner.AssertSuccess(result, "bundle --json must exit 0");
        using var doc = JsonDocument.Parse(result.StdOut);
        var artifacts = doc.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Single(artifacts);
        Assert.Equal("zip", artifacts[0].GetProperty("format").GetString());
        Assert.EndsWith(".zip", artifacts[0].GetProperty("path").GetString());
        Assert.DoesNotContain("bundler:", result.StdOut);
    }

    [Fact]
    public void QuietSuppressesInfoVerboseKeepsIt()
    {
        var quiet = _f.Cli(["bundle", .. _f.BaseArgs(_f.Ws.Combine("out-quiet")),
            "--formats", "zip", "--quiet"]);
        ProcessRunner.AssertSuccess(quiet, "quiet bundle must exit 0");
        Assert.DoesNotContain("bundler:information", quiet.StdErr);
        var verbose = _f.Cli(["bundle", .. _f.BaseArgs(_f.Ws.Combine("out-verbose")),
            "--formats", "zip", "--verbose"]);
        ProcessRunner.AssertSuccess(verbose, "verbose bundle must exit 0");
        Assert.Contains("bundler:information", verbose.StdErr);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    [Fact]
    public void BackendFailureExits1()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SKIP: chmod-based failure leg is POSIX-only.");
        Assert.SkipWhen(TestPlatform.IsRoot, "SKIP: running as root bypasses chmod-based permission checks.");
        var emptyDir = _f.Ws.Combine("empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, _f.MainExe), "x");
        var locked = Path.Combine(emptyDir, "locked");
        File.WriteAllText(locked, "locked");
        File.SetUnixFileMode(locked, 0);
        try
        {
            var result = _f.Cli("bundle",
                "--input-dir", emptyDir, "--rid", "linux-x64", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", _f.MainExe,
                "--output-dir", _f.Ws.Combine("out-fail"));
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    [Fact]
    public void BundlerJsonDrivesBundle()
    {
        var cfgDir = _f.Ws.Combine("cfg");
        Directory.CreateDirectory(Path.Combine(cfgDir, "docs"));
        File.WriteAllText(Path.Combine(cfgDir, "docs", "readme.txt"), "readme-content");
        var cfgPath = Path.Combine(cfgDir, "bundler.json");
        File.WriteAllText(cfgPath, $$"""
            {
              "productName": "CfgApp",
              "identifier": "dev.example.cfg",
              "version": "2.0.0",
              "outputDirectory": "{{cfgDir.Replace("\\", "\\\\")}}/out",
              "targets": [{
                "runtimeIdentifier": "linux-x64",
                "inputDirectory": "{{_f.PublishDir.Replace("\\", "\\\\")}}",
                "mainExecutable": "{{_f.MainExe}}",
                "formats": ["zip"]
              }],
              "archive": {
                "archiveName": "from-config",
                "files": [{ "source": "docs/readme.txt", "destination": "docs/readme.txt" }]
              },
              "deb": { "section": "utils", "vendor": "Lin <lin@example.com>" }
            }
            """);
        var bundle = _f.Cli("bundle", "--config", cfgPath, "--quiet");
        ProcessRunner.AssertSuccess(bundle, "config-driven bundle must exit 0");
        var cfgZip = Path.Combine(cfgDir, "out", "linux-x64", "zip", "from-config.zip");
        Assert.True(File.Exists(cfgZip), "config archiveName must produce from-config.zip");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(cfgZip))
        {
            Assert.Contains(zip.Entries, e => e.FullName.EndsWith("/docs/readme.txt"));
        }

        var ovr = _f.Cli("plan", "--config", cfgPath, "--formats", "targz", "--json");
        ProcessRunner.AssertSuccess(ovr, "plan with --formats override must exit 0");
        using var doc = JsonDocument.Parse(ovr.StdOut);
        var formats = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("format").GetString()).ToArray();
        Assert.Equal(new[] { "targz" }, formats);

        var dotted = _f.Cli("bundle", "--config", cfgPath,
            "--archive.archive-name=dotted", "--quiet");
        ProcessRunner.AssertSuccess(dotted, "dotted-knob bundle must exit 0");
        Assert.True(File.Exists(Path.Combine(cfgDir, "out", "linux-x64", "zip", "dotted.zip")),
            "--archive.archive-name must override the config file");
    }

    [Fact]
    public void BundlerJsonUpdateSectionEmitsFeedAndSidecar()
    {
        var cfgDir = _f.Ws.Combine("cfg-update");
        Directory.CreateDirectory(cfgDir);
        var keyPath = Path.Combine(cfgDir, "update.key");
        var keygen = _f.Cli("update-keygen", "--key-file", keyPath, "--quiet");
        ProcessRunner.AssertSuccess(keygen, "update-keygen must exit 0");
        var cfgPath = Path.Combine(cfgDir, "bundler.json");
        File.WriteAllText(cfgPath, $$"""
            {
              "productName": "CfgApp",
              "identifier": "dev.example.cfg",
              "version": "2.0.0",
              "outputDirectory": "{{cfgDir.Replace("\\", "\\\\")}}/out",
              "targets": [{
                "runtimeIdentifier": "linux-x64",
                "inputDirectory": "{{_f.PublishDir.Replace("\\", "\\\\")}}",
                "mainExecutable": "{{_f.MainExe}}",
                "formats": ["zip"]
              }],
              "update": {
                "feedUrl": "https://updates.example.com/feed",
                "channel": "stable",
                "signingKeyFile": "{{keyPath.Replace("\\", "\\\\")}}",
                "notes": "e2e update section"
              }
            }
            """);
        var bundle = _f.Cli("bundle", "--config", cfgPath, "--quiet");
        ProcessRunner.AssertSuccess(bundle, "update-enabled bundle must exit 0");
        var outRoot = Path.Combine(cfgDir, "out");
        Assert.True(File.Exists(Path.Combine(outRoot, "bundler-update-feed.stable.json")),
            "update section must emit the channel feed at output root");
        var zip = Directory.EnumerateFiles(outRoot, "*.zip", SearchOption.AllDirectories).Single();
        Assert.True(File.Exists(zip + ".sig"), "update-adapted artifact must carry a .sig sidecar");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            var sidecar = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("bundler-update.json", StringComparison.Ordinal));
            Assert.NotNull(sidecar);
            using var reader = new StreamReader(sidecar.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            Assert.Equal("https://updates.example.com/feed",
                doc.RootElement.GetProperty("feedUrl").GetString());
            Assert.Equal("stable", doc.RootElement.GetProperty("channel").GetString());
        }
    }

    [Fact]
    public void UnknownConfigKeysAndMissingFileRejected()
    {
        var cfgDir = _f.Ws.Combine("cfg-reject");
        Directory.CreateDirectory(cfgDir);
        var goodPath = Path.Combine(cfgDir, "bundler.json");
        File.WriteAllText(goodPath, $$"""
            {
              "productName": "CfgApp",
              "identifier": "dev.example.cfg",
              "version": "2.0.0",
              "outputDirectory": "{{cfgDir.Replace("\\", "\\\\")}}/out",
              "targets": [{
                "runtimeIdentifier": "linux-x64",
                "inputDirectory": "{{_f.PublishDir.Replace("\\", "\\\\")}}",
                "mainExecutable": "{{_f.MainExe}}",
                "formats": ["zip"]
              }],
              "archive": { "archiveName": "from-config" }
            }
            """);
        var badPath = Path.Combine(cfgDir, "bad.json");
        File.WriteAllText(badPath, File.ReadAllText(goodPath).Replace("\"archive\"", "\"achive\""));
        Assert.Equal(2, _f.Cli("plan", "--config", badPath).ExitCode);
        Assert.Equal(2, _f.Cli("plan", "--config", goodPath, "--deb.bogus=1").ExitCode);
        Assert.Equal(2, _f.Cli("plan", "--config", Path.Combine(cfgDir, "missing.json")).ExitCode);
    }

    [Fact]
    public void AotPublishRunsEndToEnd()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: AOT native section is Linux-only.");
        var native = _f.NativeBinary;
        Assert.True(File.Exists(native), "AOT binary missing.");
        var magic = new byte[4];
        using (var stream = File.OpenRead(native))
        {
            stream.ReadExactly(magic);
        }
        Assert.Equal(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }, magic);

        var nativeVersion = ProcessRunner.Run(native, ["--version"]);
        ProcessRunner.AssertSuccess(nativeVersion, "native --version failed.");
        Assert.Contains(RepositoryLayout.PackageVersion.Split('+')[0].Split('-')[0], nativeVersion.StdOut);

        var nativeOut = _f.Ws.Combine("aot-out");
        var bundle = ProcessRunner.Run(native,
            ["bundle", .. _f.BaseArgs(nativeOut), "--formats", "zip", "--quiet"],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });
        ProcessRunner.AssertSuccess(bundle, "native bundle must exit 0");
        Assert.True(Directory.Exists(nativeOut)
            && Directory.EnumerateFiles(nativeOut, "*.zip", SearchOption.AllDirectories).Any(),
            "native bundle must produce a zip artifact");
    }

    [Fact]
    public void AotPublishTrimsHostRestrictedBackends()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: AOT section is Linux-only.");
        var aotDir = _f.NativeDir;
        foreach (var asm in new[] { "Wix", "MacDmg", "MacPkg" })
        {
            Assert.False(File.Exists(Path.Combine(aotDir, $"DotNet.Bundler.{asm}.pdb")),
                $"host-restricted backend {asm} must not be published on Linux");
        }
        Assert.True(File.Exists(Path.Combine(aotDir, "DotNet.Bundler.AppImage.pdb")),
            "linux-capable AppImage backend must still be published");
        ExternalTools.Require("strings");
        var strings = ProcessRunner.Run("strings", ["-n", "8", Path.Combine(aotDir, "bundler")]);
        ProcessRunner.AssertSuccess(strings, "strings failed.");
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"wix3141|candle\.exe|hdiutil"),
            strings.StdOut);
    }

    [Fact]
    public void AotHostRestrictedFormatsFailExplicitly()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: AOT section is Linux-only.");
        var native = _f.NativeBinary;
        foreach (var fmt in new[] { "msi", "dmg", "pkg" })
        {
            var result = ProcessRunner.Run(native,
                ["bundle", .. _f.BaseArgs(_f.Ws.Combine($"aot-{fmt}")), "--formats", fmt],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(5) });
            // 显式请求矩阵不兼容格式 = 用法错 rc=2（宿主门禁 PNSE 才走 rc=1）。
            Assert.Equal(2, result.ExitCode);
            Assert.Matches(
                new System.Text.RegularExpressions.Regex("not supported for linux-x64"),
                result.StdErr);
        }
    }
}

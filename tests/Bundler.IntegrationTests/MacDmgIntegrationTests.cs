// MAC-DMG-1/2/3/4 集成腿的 C# 移植：对应原 tests/MacOS.Dmg.Integration/Verify.sh。
// 覆盖点：nupkg → 中间 .app + .dmg → hdiutil attach/卷内容/detach → UDZO 变体 →
// SkipWindowLayout → EULA(udifrez 资源回读)+ad-hoc 签名+SLA 挂载门控 →
// quarantine 传播 → osx-x64 → 失败路径无残留 → API fixture。
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacDmgFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; }
    public string CacheDir { get; }
    public string FixtureProject { get; }
    public string MountRoot { get; }

    public string App { get; private set; } = "";
    public string Dmg { get; private set; } = "";
    public string UdzoDmg { get; private set; } = "";
    public string SkipDmg { get; private set; } = "";
    public string EulaDmg { get; private set; } = "";
    public string X64Dmg { get; private set; } = "";

    private readonly Lazy<bool> _init;

    public void Ensure() => _ = _init.Value;

    public MacDmgFixture()
    {
        Ws = IntegrationWorkspace.Create("macos-dmg-integration", "BundlerMacOSDmgIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        MountRoot = Ws.Combine("mount");
        FixtureProject = Path.Combine(RepositoryLayout.TestsDirectory,
            "MacOS.Dmg.Integration", "Fixture", "BundlerMacDmgIntegrationFixture.csproj");
        _init = new Lazy<bool>(Initialize);
    }

    private string BundlePath(string name, string rid = "osx-arm64")
        => Ws.Combine(name, rid, "dmg", "Bundler Mac DMG Fixture.dmg");

    private string PublishDmg(string name, params string[] extra)
        => PublishDmgForRid(name, "osx-arm64", extra);

    private string PublishDmgForRid(string name, string rid, params string[] extra)
    {
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", FixtureProject, "-c", "Release",
                $"-p:BundlerIntegrationOutput={Ws.Combine(name)}",
                "--packages", CacheDir, .. extra],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            $"dmg fixture publish '{name}' failed");
        var dmg = BundlePath(name, rid);
        Assert.True(File.Exists(dmg), $"The .dmg artifact is missing: {dmg}");
        return dmg;
    }

    private bool Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsMacOS, "SKIP: .dmg integration test requires macOS.");
        foreach (var tool in new[] { "hdiutil", "plutil", "unzip", "file" })
        {
            ExternalTools.Require(tool);
        }
        _ = RepositoryPackages.DirectoryPath;

        var bundleDir = Ws.Combine("bundle");
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", FixtureProject, "-c", "Release",
                $"-p:BundlerIntegrationOutput={bundleDir}", "--packages", CacheDir],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            "dmg fixture publish 'bundle' failed");
        App = Path.Combine(bundleDir, "osx-arm64", "app", "Bundler Mac DMG Fixture.app");
        Dmg = Path.Combine(bundleDir, "osx-arm64", "dmg", "Bundler Mac DMG Fixture.dmg");
        Assert.True(Directory.Exists(App), "The planner did not produce the intermediate .app.");
        Assert.True(File.Exists(Dmg), "The .dmg artifact is missing.");

        UdzoDmg = PublishDmg("bundle-udzo", "-p:BundlerTestDmgCompression=Udzo");
        SkipDmg = PublishDmg("bundle-skip", "-p:BundlerTestDmgSkipWindowLayout=true");
        EulaDmg = PublishDmg("bundle-eula",
            "-p:BundlerTestDmgSkipWindowLayout=true",
            "-p:BundlerTestDmgLicense=true",
            "-p:BundlerTestDmgSignIdentity=-");
        X64Dmg = PublishDmgForRid("bundle-x64", "osx-x64",
            "-p:RuntimeIdentifier=osx-x64",
            "-p:BundlerTestDmgSkipWindowLayout=true");
        return true;
    }

    // hdiutil attach → 断言 → detach；挂载点在 finally 里兜底卸载。
    public void WithMounted(string dmg, Action<string> assert)
    {
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("hdiutil",
                ["attach", dmg, "-nobrowse", "-readonly", "-mountpoint", MountRoot]),
            "hdiutil attach failed.");
        try
        {
            assert(MountRoot);
        }
        finally
        {
            ProcessRunner.Run("hdiutil", ["detach", MountRoot, "-force"]);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(MountRoot))
        {
            ProcessRunner.Run("hdiutil", ["detach", MountRoot, "-force"]);
        }
        Ws.Dispose();
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacDmgIntegrationTests : IClassFixture<MacDmgFixture>
{
    private readonly MacDmgFixture _f;

    public MacDmgIntegrationTests(MacDmgFixture fixture) => _f = fixture;

    [Fact]
    public void RepositoryPackagesCarryMacDmgBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[]
        {
            "DotNet.Bundler", "DotNet.Bundler.MSBuild",
            "DotNet.Bundler.MacApp", "DotNet.Bundler.MacDmg",
        })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("tasks/netstandard2.0/DotNet.Bundler.MacDmg.dll"));
    }

    [Fact]
    public void IntermediateAppLintsAndDmgExists()
    {
        _f.Ensure();
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("plutil",
                ["-lint", Path.Combine(_f.App, "Contents/Info.plist")]),
            "Intermediate .app Info.plist is invalid.");
    }

    [Fact]
    public void MountedVolumeContentsAndLaunch()
    {
        _f.Ensure();
        _f.WithMounted(_f.Dmg, mount =>
        {
            var mountedApp = Path.Combine(mount, "Bundler Mac DMG Fixture.app");
            Assert.True(Directory.Exists(mountedApp), "Mounted volume lacks the .app.");
            var link = Path.Combine(mount, "Applications");
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)
                || File.ResolveLinkTarget(link, false) is not null,
                "Mounted volume lacks the /Applications drop link.");
            Assert.Equal("/Applications",
                File.ResolveLinkTarget(link, false)?.FullName.TrimEnd('/') ?? "");
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run("plutil",
                    ["-lint", Path.Combine(mountedApp, "Contents/Info.plist")]),
                "The .app inside the mounted .dmg has a broken Info.plist.");

            // MAC-DMG-2 品牌件：背景图 + 卷标图标无条件存在；.DS_Store 需 GUI，缺席只记录。
            Assert.True(File.Exists(Path.Combine(mount, ".background", "bg.png")),
                "Mounted volume lacks .background/bg.png.");
            Assert.True(File.Exists(Path.Combine(mount, ".VolumeIcon.icns")),
                "Mounted volume lacks .VolumeIcon.icns.");

            var exe = Path.Combine(mountedApp, "Contents/MacOS/BundlerMacDmgIntegrationFixture");
            var run = ProcessRunner.Run(exe, []);
            ProcessRunner.AssertSuccess(run, "The .app inside the mounted .dmg did not run.");
            Assert.Contains("BundlerMacDmgIntegrationFixture", run.StdOut);
        });
        ProcessRunner.AssertSuccess(ProcessRunner.Run("hdiutil", ["verify", _f.Dmg]),
            "hdiutil verify failed on the produced .dmg.");
    }

    [Fact]
    public void UdzoVariant()
    {
        _f.Ensure();
        var info = ProcessRunner.Run("hdiutil", ["imageinfo", _f.UdzoDmg]);
        var format = info.StdOut.Split('\n')
            .FirstOrDefault(l => l.StartsWith("Format:"))?.Split(':')[1].Trim();
        Assert.Equal("UDZO", format);
        _f.WithMounted(_f.UdzoDmg, mount =>
            Assert.True(Directory.Exists(
                Path.Combine(mount, "Bundler Mac DMG Fixture.app")),
                "UDZO volume lacks the .app."));
    }

    [Fact]
    public void SkipWindowLayoutVariantVerifies()
    {
        _f.Ensure();
        ProcessRunner.AssertSuccess(ProcessRunner.Run("hdiutil", ["verify", _f.SkipDmg]),
            "hdiutil verify failed on the SkipWindowLayout .dmg.");
    }

    [Fact]
    public void EulaAndAdHocSignedVariant()
    {
        _f.Ensure();
        var derez = ProcessRunner.Run("hdiutil", ["udifderez", "-xml", _f.EulaDmg]);
        ProcessRunner.AssertSuccess(derez, "udifderez could not read the SLA resources back.");
        Assert.Contains("<key>LPic</key>", derez.StdOut);
        Assert.Contains("<key>STR#</key>", derez.StdOut);
        Assert.Contains("<key>TEXT</key>", derez.StdOut);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("codesign", ["--verify", "--verbose=2", _f.EulaDmg]),
            "codesign --verify failed on the ad-hoc signed .dmg.");
        var desc = ProcessRunner.Run("codesign", ["-dvvv", _f.EulaDmg]);
        Assert.Contains("Signature=adhoc", desc.Output);

        // SLA 挂载门控：stdin 关闭（无回答）必须被拒；回答 Y 才挂载。
        var denied = ProcessRunner.Run("hdiutil",
            ["attach", _f.EulaDmg, "-nobrowse", "-readonly", "-mountpoint", _f.MountRoot],
            new ProcessRunner.Options { StandardInput = "" });
        Assert.NotEqual(0, denied.ExitCode);
        var accepted = ProcessRunner.Run("hdiutil",
            ["attach", _f.EulaDmg, "-nobrowse", "-readonly", "-mountpoint", _f.MountRoot],
            new ProcessRunner.Options { StandardInput = "Y\n" });
        ProcessRunner.AssertSuccess(accepted,
            "The SLA image did not mount after accepting the license.");
        try
        {
            Assert.True(Directory.Exists(
                Path.Combine(_f.MountRoot, "Bundler Mac DMG Fixture.app")),
                "The EULA-mounted volume lacks the .app.");
        }
        finally
        {
            ProcessRunner.Run("hdiutil", ["detach", _f.MountRoot, "-force"]);
        }
    }

    [Fact]
    public void QuarantinePropagatesToCopiedApp()
    {
        _f.Ensure();
        var quarDmg = _f.Ws.Combine("quarantine.dmg");
        File.Copy(_f.Dmg, quarDmg);
        ProcessRunner.AssertSuccess(ProcessRunner.Run("xattr",
            ["-w", "com.apple.quarantine",
             "0181;00000000;curl;00000000-0000-0000-0000-000000000000", quarDmg]),
            "Failed to quarantine the dmg copy.");
        var quarApp = _f.Ws.Combine("quar-app.app");
        _f.WithMounted(quarDmg, mount =>
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run("cp",
                    ["-R", Path.Combine(mount, "Bundler Mac DMG Fixture.app"), quarApp]),
                "cp -R off the quarantined volume failed."));
        var attr = ProcessRunner.Run("xattr", ["-p", "com.apple.quarantine", quarApp]);
        Assert.Equal(0, attr.ExitCode);
    }

    [Fact]
    public void OsxX64VariantStructure()
    {
        _f.Ensure();
        _f.WithMounted(_f.X64Dmg, mount =>
        {
            var file = ProcessRunner.Run("file",
                [Path.Combine(mount,
                    "Bundler Mac DMG Fixture.app/Contents/MacOS/BundlerMacDmgIntegrationFixture")]);
            Assert.Contains("x86_64", file.StdOut);
        });
    }

    [Fact]
    public void InvalidCompressionFailsCleanly()
    {
        _f.Ensure();
        var badDir = _f.Ws.Combine("bundle-bad");
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={badDir}",
             "-p:BundlerTestDmgCompression=Bogus",
             "--packages", _f.CacheDir],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(badDir, "*.dmg", SearchOption.AllDirectories));
        var mounted = ProcessRunner.Run("hdiutil", ["info"]);
        Assert.DoesNotContain(_f.Ws.Root, mounted.StdOut);
    }

    [Fact]
    public void ApiFixtureProducesMountableDmg()
    {
        _f.Ensure();
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "MacDmgApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["MACDMG_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "MacDmgApiTests failed");
        var apiDmg = Path.Combine(output, "artifacts", "osx-arm64", "dmg", "DMG API Package Fixture.dmg");
        Assert.True(File.Exists(apiDmg), "The standalone API package did not create a .dmg.");
        var apiMount = _f.Ws.Combine("api-mount");
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("hdiutil", ["attach", apiDmg, "-nobrowse", "-mountpoint", apiMount]),
            "The API fixture .dmg failed to mount.");
        try
        {
            Assert.True(Directory.Exists(Path.Combine(apiMount, "DMG API Package Fixture.app"))
                && File.ResolveLinkTarget(Path.Combine(apiMount, "Applications"), false) is not null,
                "The API fixture .dmg volume lacks the .app or the /Applications link.");
        }
        finally
        {
            ProcessRunner.Run("hdiutil", ["detach", apiMount, "-force"]);
        }
    }
}

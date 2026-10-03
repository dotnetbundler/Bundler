// MAC-PKG-1/2 集成腿的 C# 移植：对应原 tests/MacOS.Pkg.Integration/Verify.sh。
// 覆盖点：nupkg → 中间 .app + .pkg → pkgutil --expand-full payload/PackageInfo →
// xar 结构 → installer -dominfo/-pkginfo → override 元数据 → distribution 变体
// （pages+domain+dominfo）→ scripts 收进组件包 → 假签名身份诚实失败 →
// per-user 真安装+收据+forget → 同 id v2 覆盖升级 → osx-x64 → 失败路径 → API fixture。
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacPkgFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; }
    public string CacheDir { get; }
    public string FixtureProject { get; }
    public string ExpandRoot { get; }

    public string App { get; private set; } = "";
    public string Pkg { get; private set; } = "";
    public string OverridePkg { get; private set; } = "";
    public string DistPkg { get; private set; } = "";
    public string ScriptsPkg { get; private set; } = "";
    public string V2Pkg { get; private set; } = "";
    public string X64Pkg { get; private set; } = "";

    private readonly Lazy<bool> _init;

    public void Ensure() => _ = _init.Value;

    public MacPkgFixture()
    {
        Ws = IntegrationWorkspace.Create("macos-pkg-integration", "BundlerMacOSPkgIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExpandRoot = Ws.Combine("expand");
        FixtureProject = Path.Combine(RepositoryLayout.TestsDirectory,
            "MacOS.Pkg.Integration", "Fixture", "BundlerMacPkgIntegrationFixture.csproj");
        _init = new Lazy<bool>(Initialize);
    }

    private string BundlePath(string name, string rid = "osx-arm64")
        => Ws.Combine(name, rid, "pkg", "Bundler Mac PKG Fixture.pkg");

    private string PublishPkg(string name, params string[] extra)
        => PublishPkg(name, "osx-arm64", extra);

    private string PublishPkg(string name, string rid, params string[] extra)
    {
        var ridArg = rid == "osx-x64" ? new[] { "-r", "osx-x64" } : [];
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", FixtureProject, "-c", "Release", .. ridArg,
                $"-p:BundlerIntegrationOutput={Ws.Combine(name)}",
                "--packages", CacheDir, .. extra],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            $"pkg fixture publish '{name}' failed");
        var pkg = BundlePath(name, rid);
        Assert.True(File.Exists(pkg), $"The .pkg artifact is missing: {pkg}");
        return pkg;
    }

    public string Expand(string pkg, string subdir)
    {
        var root = Path.Combine(ExpandRoot, subdir);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("pkgutil", ["--expand-full", pkg, root]),
            $"pkgutil --expand-full failed on {Path.GetFileName(pkg)}.");
        return root;
    }

    public static string XarList(string pkg)
    {
        var result = ProcessRunner.Run("xar", ["-tf", pkg]);
        ProcessRunner.AssertSuccess(result, "xar -tf failed.");
        return result.StdOut;
    }

    private bool Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsMacOS, "SKIP: .pkg integration test requires macOS.");
        foreach (var tool in new[] { "pkgutil", "xar", "installer", "plutil", "file" })
        {
            ExternalTools.Require(tool);
        }
        _ = RepositoryPackages.DirectoryPath;

        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", FixtureProject, "-c", "Release",
                $"-p:BundlerIntegrationOutput={Ws.Combine("bundle")}",
                "--packages", CacheDir],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            "pkg fixture publish 'bundle' failed");
        App = Path.Combine(Ws.Combine("bundle"), "osx-arm64", "app", "Bundler Mac PKG Fixture.app");
        Pkg = BundlePath("bundle");
        Assert.True(Directory.Exists(App), "The planner did not produce the intermediate .app.");
        Assert.True(File.Exists(Pkg), "The .pkg artifact is missing.");

        OverridePkg = PublishPkg("bundle-override",
            "-p:BundlerTestPkgIdentifier=com.example.custom.pkg",
            "-p:BundlerTestPkgVersion=9.9.9",
            "-p:BundlerTestPkgInstallLocation=/opt/bundler-test");
        DistPkg = PublishPkg("bundle-dist",
            "-p:BundlerTestPkgTitle=Fixture Installer",
            "-p:BundlerTestPkgWelcome=true",
            "-p:BundlerTestPkgConclusion=true",
            "-p:BundlerTestPkgLicense=true",
            "-p:BundlerTestPkgDomain=CurrentUserHome");
        ScriptsPkg = PublishPkg("bundle-scripts",
            "-p:BundlerTestPkgScripts=true",
            "-p:BundlerTestPkgDomain=CurrentUserHome");
        V2Pkg = PublishPkg("bundle-v2",
            "-p:BundlerTestPkgVersion=2.0.0",
            "-p:BundlerTestPkgDomain=CurrentUserHome");
        X64Pkg = PublishPkg("bundle-x64", "osx-x64");
        return true;
    }

    // installer 对 <relocate> bundle 会重定位到已存在的同 id .app——
    // 真安装前清掉工作区里的全部中间 .app（脚本同款清理）。
    public void RemoveIntermediateApps()
    {
        foreach (var app in Directory.EnumerateDirectories(Ws.Root, "*.app", SearchOption.AllDirectories))
        {
            Directory.Delete(app, recursive: true);
        }
    }

    public void Dispose() => Ws.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacPkgIntegrationTests : IClassFixture<MacPkgFixture>
{
    private readonly MacPkgFixture _f;
    private const string FixtureIdentifier = "com.dotnetbundler.macpkgintegrationfixture";

    public MacPkgIntegrationTests(MacPkgFixture fixture) => _f = fixture;

    [Fact]
    public void RepositoryPackagesCarryMacPkgBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[]
        {
            "DotNet.Bundler", "DotNet.Bundler.MSBuild",
            "DotNet.Bundler.MacApp", "DotNet.Bundler.MacPkg",
        })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("tasks/netstandard2.0/DotNet.Bundler.MacPkg.dll"));
    }

    [Fact]
    public void PayloadAndPackageInfo()
    {
        _f.Ensure();
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("plutil",
                ["-lint", Path.Combine(_f.App, "Contents/Info.plist")]),
            "Intermediate .app Info.plist is invalid.");

        var root = _f.Expand(_f.Pkg, "root");
        Assert.True(File.Exists(Path.Combine(root, "PackageInfo")),
            "The expanded package lacks PackageInfo.");
        var payload = Path.Combine(root, "Payload");
        var payloadApp = Path.Combine(payload, "Bundler Mac PKG Fixture.app");
        Assert.True(Directory.Exists(payloadApp), "The payload lacks the .app at its root.");
        Assert.True(File.Exists(Path.Combine(payload, "support", "helper.txt")),
            "The payload lacks the explicit BundlerPkgPayload item at support/helper.txt.");
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("plutil",
                ["-lint", Path.Combine(payloadApp, "Contents/Info.plist")]),
            "The .app inside the payload has a broken Info.plist.");

        var exe = Path.Combine(payloadApp, "Contents/MacOS/BundlerMacPkgIntegrationFixture");
        var run = ProcessRunner.Run(exe, []);
        ProcessRunner.AssertSuccess(run, "The .app inside the expanded payload did not run.");
        Assert.Contains("BundlerMacPkgIntegrationFixture", run.StdOut);

        var packageInfo = File.ReadAllText(Path.Combine(root, "PackageInfo"));
        Assert.Contains($"identifier=\"{FixtureIdentifier}\"", packageInfo);
        Assert.Contains("version=\"1.0.0\"", packageInfo);
        Assert.Contains("install-location=\"/Applications\"", packageInfo);
    }

    [Fact]
    public void XarStructureAndInstallerViews()
    {
        _f.Ensure();
        var listing = MacPkgFixture.XarList(_f.Pkg);
        Assert.Contains("PackageInfo", listing);
        Assert.Contains("Payload", listing);
        Assert.Contains("Bom", listing);

        var dominfo = ProcessRunner.Run("installer", ["-dominfo", "-pkg", _f.Pkg, "-plist"]);
        ProcessRunner.AssertSuccess(dominfo, "installer -dominfo could not parse the package.");
        Assert.Contains("<array/>", dominfo.StdOut);

        var pkginfo = ProcessRunner.Run("installer", ["-pkginfo", "-pkg", _f.Pkg]);
        Assert.Contains("Bundler Mac PKG Fixture", pkginfo.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverrideVariant()
    {
        _f.Ensure();
        var info = File.ReadAllText(Path.Combine(_f.Expand(_f.OverridePkg, "override"), "PackageInfo"));
        Assert.Contains("identifier=\"com.example.custom.pkg\"", info);
        Assert.Contains("version=\"9.9.9\"", info);
        Assert.Contains("install-location=\"/opt/bundler-test\"", info);
    }

    [Fact]
    public void DistributionVariant()
    {
        _f.Ensure();
        var listing = MacPkgFixture.XarList(_f.DistPkg);
        Assert.Contains("Distribution", listing);
        Assert.Contains("component.pkg", listing);
        Assert.Contains("Resources/welcome.txt", listing);
        Assert.Contains("Resources/conclusion.rtf", listing);
        Assert.Contains("Resources/license.txt", listing);

        var docDir = Path.Combine(_f.ExpandRoot, "dist-doc");
        Directory.CreateDirectory(docDir);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("xar", ["-xf", _f.DistPkg, "-C", docDir]),
            "Could not extract the Distribution document.");
        var doc = File.ReadAllText(Path.Combine(docDir, "Distribution"));
        Assert.Contains("<title>Fixture Installer</title>", doc);
        Assert.Contains("enable_currentUserHome=\"true\"", doc);

        var dominfo = ProcessRunner.Run("installer", ["-dominfo", "-pkg", _f.DistPkg, "-plist"]);
        ProcessRunner.AssertSuccess(dominfo, "installer -dominfo failed on the distribution .pkg.");
        Assert.Contains("currentuserhome", dominfo.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostinstallArchivedIntoComponentPackage()
    {
        _f.Ensure();
        var productDir = Path.Combine(_f.ExpandRoot, "scripts-product");
        Directory.CreateDirectory(productDir);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("xar", ["-xf", _f.ScriptsPkg, "-C", productDir]),
            "xar could not unpack the scripts variant .pkg.");
        var scripts = Path.Combine(productDir, "component.pkg", "Scripts");
        Assert.True(File.Exists(scripts),
            "pkgbuild did not archive a Scripts payload into the component package.");
        var cpioDir = Path.Combine(_f.ExpandRoot, "scripts-cpio");
        Directory.CreateDirectory(cpioDir);
        // Scripts 是 gzip cpio：先 GZipStream 解压成 .cpio，再让 cpio 从文件读（二进制不走字符串 stdin）。
        using var data = new System.IO.Compression.GZipStream(
            File.OpenRead(scripts), System.IO.Compression.CompressionMode.Decompress);
        var cpioPath = Path.Combine(cpioDir, "scripts.cpio");
        using (var outStream = File.Create(cpioPath))
        {
            data.CopyTo(outStream);
        }
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("sh", ["-c", "cpio -i --quiet < scripts.cpio"],
                new ProcessRunner.Options { WorkingDirectory = cpioDir }),
            "The Scripts cpio archive could not be unpacked.");
        Assert.True(File.Exists(Path.Combine(cpioDir, "postinstall")),
            "The Scripts archive does not contain the postinstall script.");
    }

    [Fact]
    public void BogusSigningIdentityFailsHonestly()
    {
        _f.Ensure();
        var badDir = _f.Ws.Combine("bundle-sign-fail");
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={badDir}",
             "-p:BundlerTestPkgSignIdentity=Nonexistent Installer Identity",
             "--packages", _f.CacheDir],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(badDir, "*.pkg", SearchOption.AllDirectories));
    }

    [Fact]
    public void PerUserInstallReceiptAndForget()
    {
        _f.Ensure();
        _f.RemoveIntermediateApps();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var marker = Path.Combine(home, ".bundler-pkg-postinstall-ran");
        File.Delete(marker);
        var install = ProcessRunner.Run("installer",
            ["-pkg", _f.ScriptsPkg, "-target", "CurrentUserHomeDirectory", "-dumplog"],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });
        ProcessRunner.AssertSuccess(install,
            $"installer could not install the distribution pkg into the home domain.\n{install.StdOut}");
        try
        {
            var homeApp = Path.Combine(home, "Applications", "Bundler Mac PKG Fixture.app");
            Assert.True(Directory.Exists(homeApp),
                "The per-user install did not place the .app under ~/Applications.");
            Assert.True(File.Exists(Path.Combine(home, "Applications", "support", "helper.txt")),
                "The per-user install did not place the payload helper under ~/Applications/support.");
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run("plutil",
                    ["-lint", Path.Combine(homeApp, "Contents/Info.plist")]),
                "The installed .app has a broken Info.plist.");
            var run = ProcessRunner.Run(
                Path.Combine(homeApp, "Contents/MacOS/BundlerMacPkgIntegrationFixture"), []);
            ProcessRunner.AssertSuccess(run, "The installed .app did not run.");
            Assert.Contains("BundlerMacPkgIntegrationFixture", run.StdOut);

            var pkgs = ProcessRunner.Run("pkgutil", ["--pkgs", "--volume", home]);
            Assert.Contains(FixtureIdentifier, pkgs.StdOut);
            var files = ProcessRunner.Run("pkgutil", ["--files", FixtureIdentifier, "--volume", home]);
            Assert.Contains("support/helper.txt", files.StdOut);
            Assert.True(File.Exists(marker),
                "The postinstall script was not executed during the real install.");
        }
        finally
        {
            ProcessRunner.Run("pkgutil", ["--forget", FixtureIdentifier, "--volume", home]);
            File.Delete(marker);
        }
    }

    [Fact]
    public void OverwriteInstallUpgradesReceipt()
    {
        _f.Ensure();
        _f.RemoveIntermediateApps();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // 先装 v1（scripts 变体同 identifier）再覆盖 v2，收据版本必须到 2.0.0。
        foreach (var pkg in new[] { _f.ScriptsPkg, _f.V2Pkg })
        {
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run("installer",
                    ["-pkg", pkg, "-target", "CurrentUserHomeDirectory", "-dumplog"],
                    new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) }),
                $"installer failed on {Path.GetFileName(pkg)}.");
        }
        try
        {
            var info = ProcessRunner.Run("pkgutil",
                ["--pkg-info-plist", FixtureIdentifier, "--volume", home]);
            ProcessRunner.AssertSuccess(info, "pkgutil --pkg-info-plist failed after upgrade.");
            Assert.Contains("<string>2.0.0</string>", info.StdOut);
        }
        finally
        {
            ProcessRunner.Run("pkgutil", ["--forget", FixtureIdentifier, "--volume", home]);
            var homeApp = Path.Combine(home, "Applications", "Bundler Mac PKG Fixture.app");
            if (Directory.Exists(homeApp))
            {
                Directory.Delete(homeApp, recursive: true);
            }
            var support = Path.Combine(home, "Applications", "support");
            if (Directory.Exists(support))
            {
                Directory.Delete(support, recursive: true);
            }
        }
    }

    [Fact]
    public void OsxX64PayloadIsX64MachO()
    {
        _f.Ensure();
        var root = _f.Expand(_f.X64Pkg, "x64");
        var exe = Path.Combine(root, "Payload", "Bundler Mac PKG Fixture.app",
            "Contents", "MacOS", "BundlerMacPkgIntegrationFixture");
        Assert.True(File.Exists(exe), "The osx-x64 payload lacks the main executable.");
        var file = ProcessRunner.Run("file", [exe]);
        Assert.Contains("x86_64", file.StdOut);
    }

    [Fact]
    public void RelativeInstallLocationFailsCleanly()
    {
        _f.Ensure();
        var badDir = _f.Ws.Combine("bundle-bad");
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={badDir}",
             "-p:BundlerTestPkgInstallLocation=relative/path",
             "--packages", _f.CacheDir],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(badDir, "*.pkg", SearchOption.AllDirectories));
    }

    [Fact]
    public void ApiFixtureProducesExpandablePkg()
    {
        _f.Ensure();
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "MacPkgApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["MACPKG_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "MacPkgApiTests failed");
        var apiPkg = Path.Combine(output, "artifacts", "osx-arm64", "pkg", "PKG API Package Fixture.pkg");
        Assert.True(File.Exists(apiPkg), "The standalone API package did not create a .pkg.");
        var root = _f.Expand(apiPkg, "api");
        Assert.True(Directory.Exists(Path.Combine(root, "Payload", "PKG API Package Fixture.app")),
            "The API fixture .pkg payload lacks the .app bundle.");
    }
}

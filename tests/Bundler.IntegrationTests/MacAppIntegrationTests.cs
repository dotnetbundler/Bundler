// MAC-APP-1/2/3 集成腿的 C# 移植：对应原 tests/MacOS.App.Integration/Verify.sh。
// 覆盖点：nupkg → bundle 结构 → plutil 全键 → 桌面集成键（doc type/UTI/URL scheme/ATS/
// 调用方合并键）→ lipo → 启动 → LaunchServices 注册/文件关联/scheme 唤起/~/Applications
// 拷入拷出 → 重建确定性 → ad-hoc 签名链 → 缺证书失败 → osx-x64 结构 → 隔离首启
// → LSMinimumSystemVersion → v1→v2 原地升级 → 卸载=删目录 → API fixture。
using System.Text.Json;

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacAppFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; }
    public string CacheDir { get; }
    public string FixtureProject { get; }
    public string FixtureDir { get; }
    public string IconPng { get; private set; } = "";
    public string? Dylib { get; private set; }
    public bool DylibIsFat { get; private set; }

    public string App { get; private set; } = "";
    public string SignedApp { get; private set; } = "";
    public string X64App { get; private set; } = "";
    public string MinVerApp { get; private set; } = "";
    public string UpgradeApp { get; private set; } = "";

    public const string LsRegister =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    // 重初始化惰性执行：fixture 构造器里 Skip 在 xUnit v3/MTP 下会被记成 error，
    // 必须由各用例先调 Ensure()（Skip 异常在测试体内抛才计为 skipped）。
    private readonly Lazy<bool> _init;

    public void Ensure() => _ = _init.Value;

    public MacAppFixture()
    {
        Ws = IntegrationWorkspace.Create("macos-app-integration", "BundlerMacOSAppIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        FixtureDir = Path.Combine(RepositoryLayout.TestsDirectory, "MacOS.App.Integration", "Fixture");
        FixtureProject = Path.Combine(FixtureDir, "BundlerMacAppIntegrationFixture.csproj");
        _init = new Lazy<bool>(Initialize);
    }

    private bool Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsMacOS, "SKIP: .app integration test requires macOS.");
        foreach (var tool in new[] { "plutil", "unzip", "lipo", "shasum" })
        {
            ExternalTools.Require(tool);
        }
        Assert.True(File.Exists(LsRegister), "lsregister is unavailable on this host.");
        _ = RepositoryPackages.DirectoryPath;

        // 512x512 PNG（仅签名头+IHDR；后端只需位图尺寸）
        IconPng = Ws.Combine("icon-512.png");
        File.WriteAllBytes(IconPng,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAgAAAAIACAYAAAD0eNT6AAAAAXNSR0IArs4c6Q=="));

        // clang 可用时编 fat dylib（arm64+x86_64），两个产物都能过架构校验。
        var dylib = Ws.Combine("libfixture.dylib");
        if (ExternalTools.Has("clang"))
        {
            var src = Ws.Combine("fixture.c");
            File.WriteAllText(src, "int bundler_fixture(void){return 0;}\n");
            var fat = ProcessRunner.Run("clang",
                ["-dynamiclib", "-arch", "arm64", "-arch", "x86_64", "-o", dylib, src]);
            if (fat.ExitCode != 0)
            {
                ProcessRunner.AssertSuccess(
                    ProcessRunner.Run("clang", ["-dynamiclib", "-o", dylib, src]),
                    "clang dylib build failed.");
                DylibIsFat = false;
            }
            else
            {
                var lipo = ProcessRunner.Run("lipo", ["-info", dylib]);
                DylibIsFat = lipo.StdOut.Contains("x86_64");
            }
            Dylib = dylib;
        }

        App = Publish("bundle", "osx-arm64", BundlerAppPath("bundle", "osx-arm64"));
        AssertNoBundledPackageRestore();

        var entitlements = Ws.Combine("entitlements.plist");
        File.WriteAllText(entitlements, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>com.apple.security.cs.allow-jit</key>
                <true/>
                <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
                <true/>
                <key>com.apple.security.cs.disable-library-validation</key>
                <true/>
            </dict>
            </plist>
            """ + "\n");
        SignedApp = Publish("signed-output", "osx-arm64",
            BundlerAppPath("signed-output", "osx-arm64"),
            "-p:BundlerTestSignIdentity=-",
            "-p:BundlerTestHardenedRuntime=true",
            $"-p:BundlerTestEntitlementsFile={entitlements}");

        var x64Args = Dylib is null || !DylibIsFat
            ? []
            : new[] { $"-p:BundlerTestDylib={Dylib}" };
        X64App = PublishWithArgs("x64-output", "osx-x64",
            BundlerAppPath("x64-output", "osx-x64"), x64Args);

        MinVerApp = Publish("minver-output", "osx-arm64",
            BundlerAppPath("minver-output", "osx-arm64"),
            "-p:BundlerTestMinSystemVersion=99.0");

        UpgradeApp = Publish("upgrade-output", "osx-arm64",
            BundlerAppPath("upgrade-output", "osx-arm64"),
            "-p:BundlerTestBuildVersion=2026.9.2");
        return true;
    }

    private string BundlerAppPath(string output, string rid)
        => Ws.Combine(output, rid, "app", "Bundler Mac Integration Fixture.app");

    private IEnumerable<string> CommonArgs(string name)
    {
        yield return $"-p:BundlerIntegrationOutput={Ws.Combine(name)}";
        yield return $"-p:BundlerTestIcon={IconPng}";
        if (Dylib is not null)
        {
            yield return $"-p:BundlerTestDylib={Dylib}";
        }
        yield return $"-p:RestorePackagesPath={CacheDir}";
    }

    private string Publish(string name, string rid, string expectedApp, params string[] extra)
        => PublishWithArgs(name, rid, expectedApp, extra);

    private string PublishWithArgs(string name, string rid, string expectedApp, IEnumerable<string> extra)
    {
        var ridArg = rid == "osx-x64" ? new[] { "-p:RuntimeIdentifier=osx-x64" } : [];
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", FixtureProject, "-c", "Release", "--force",
                .. ridArg, .. CommonArgs(name), .. extra],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            $"mac-app fixture publish '{name}' failed");
        Assert.True(Directory.Exists(expectedApp), $"The .app bundle was not produced at {expectedApp}");
        return expectedApp;
    }

    // 项目引用消费的契约证据：fixture restore 不得引入 DotNet.Bundler nupkg。
    private void AssertNoBundledPackageRestore()
    {
        var assets = Path.Combine(FixtureDir, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), "Fixture restore assets are missing.");
        Assert.DoesNotContain("\"DotNet.Bundler", File.ReadAllText(assets));
    }

    public void Dispose() => Ws.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacAppIntegrationTests : IClassFixture<MacAppFixture>
{
    private readonly MacAppFixture _f;
    private static readonly JsonDocumentOptions Doc = new();

    public MacAppIntegrationTests(MacAppFixture fixture) => _f = fixture;

    [Fact]
    public void RepositoryPackagesCarryMacAppBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.MacApp" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var macEntries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MacApp.{version}.nupkg"));
        Assert.Contains(macEntries,
            e => e.EndsWith("lib/netstandard2.0/DotNet.Bundler.MacApp.dll"));
        var msbuildEntries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(msbuildEntries,
            e => e.EndsWith("tasks/netstandard2.0/DotNet.Bundler.MacApp.dll"));
    }

    [Fact]
    public void BundleStructure()
    {
        _f.Ensure();
        foreach (var required in new[]
        {
            "Contents/Info.plist", "Contents/PkgInfo",
            "Contents/MacOS/BundlerMacIntegrationFixture",
            "Contents/MacOS/BundlerMacIntegrationFixture.dll",
            "Contents/Resources/docs/readme.txt",
            "Contents/Resources/FixtureIcon.icns",
            "Contents/SharedSupport/shared.txt",
        })
        {
            Assert.True(File.Exists(Path.Combine(_f.App, required)),
                $"Missing bundle entry: {required}");
        }
        if (_f.Dylib is not null)
        {
            Assert.True(File.Exists(Path.Combine(_f.App, "Contents/Frameworks/libfixture.dylib")),
                "Missing Contents/Frameworks/libfixture.dylib");
        }
        Assert.Equal("APPL????", File.ReadAllText(Path.Combine(_f.App, "Contents/PkgInfo")));
        var icns = new byte[4];
        using (var stream = File.OpenRead(Path.Combine(_f.App, "Contents/Resources/FixtureIcon.icns")))
        {
            stream.ReadExactly(icns);
        }
        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(icns));
    }

    [Fact]
    public void InfoPlistLintsAndCarriesExpectedKeys()
    {
        _f.Ensure();
        var plist = Path.Combine(_f.App, "Contents/Info.plist");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("plutil", ["-lint", plist]),
            "plutil -lint rejected Info.plist.");
        var dump = ProcessRunner.Run("plutil", ["-p", plist]);
        ProcessRunner.AssertSuccess(dump, "plutil -p failed.");
        var expected = new Dictionary<string, string>
        {
            ["CFBundleIdentifier"] = "com.dotnetbundler.macintegrationfixture",
            ["CFBundleName"] = "MacFixture",
            ["CFBundleDisplayName"] = "Bundler Mac Fixture",
            ["CFBundleExecutable"] = "BundlerMacIntegrationFixture",
            ["CFBundlePackageType"] = "APPL",
            ["CFBundleShortVersionString"] = "1.0.0",
            ["CFBundleVersion"] = "2026.9.1",
            ["CFBundleIconFile"] = "FixtureIcon.icns",
            ["LSMinimumSystemVersion"] = "11.0",
            ["LSApplicationCategoryType"] = "public.app-category.utilities",
            ["NSHumanReadableCopyright"] = "Copyright DotNet.Bundler Tests",
        };
        foreach (var (key, value) in expected)
        {
            Assert.Contains($"\"{key}\" => \"{value}\"", dump.StdOut);
        }
    }

    [Fact]
    public void DesktopIntegrationKeys()
    {
        _f.Ensure();
        var plist = Path.Combine(_f.App, "Contents/Info.plist");

        var docTypes = PlistExtract(plist, "CFBundleDocumentTypes");
        var doc = Assert.Single(docTypes.EnumerateArray());
        Assert.Equal("hifix", Assert.Single(
            doc.GetProperty("CFBundleTypeExtensions").EnumerateArray()).GetString());
        Assert.Equal("HiFix Document", doc.GetProperty("CFBundleTypeName").GetString());
        Assert.Equal("Editor", doc.GetProperty("CFBundleTypeRole").GetString());
        Assert.Equal("Owner", doc.GetProperty("LSHandlerRank").GetString());
        Assert.Equal("com.dotnetbundler.hifix", Assert.Single(
            doc.GetProperty("LSItemContentTypes").EnumerateArray()).GetString());

        var utis = PlistExtract(plist, "UTExportedTypeDeclarations");
        var uti = Assert.Single(utis.EnumerateArray());
        Assert.Equal("com.dotnetbundler.hifix", uti.GetProperty("UTTypeIdentifier").GetString());
        Assert.Equal("public.data", Assert.Single(
            uti.GetProperty("UTTypeConformsTo").EnumerateArray()).GetString());
        var tags = uti.GetProperty("UTTypeTagSpecification");
        Assert.Equal("hifix", Assert.Single(
            tags.GetProperty("public.filename-extension").EnumerateArray()).GetString());
        Assert.Equal("application/x-hifix",
            tags.GetProperty("public.mime-type").GetString());

        var urlTypes = PlistExtract(plist, "CFBundleURLTypes");
        var url = Assert.Single(urlTypes.EnumerateArray());
        Assert.Equal("hifix", Assert.Single(
            url.GetProperty("CFBundleURLSchemes").EnumerateArray()).GetString());
        Assert.Equal("HiFix Link", url.GetProperty("CFBundleURLName").GetString());
        Assert.Equal("Viewer", url.GetProperty("CFBundleTypeRole").GetString());

        var ats = PlistExtract(plist, "NSAppTransportSecurity.NSExceptionDomains");
        var domain = ats.GetProperty("bundler.invalid");
        Assert.True(domain.GetProperty("NSExceptionAllowsInsecureHTTPLoads").GetBoolean());
        Assert.True(domain.GetProperty("NSIncludesSubdomains").GetBoolean());

        var dump = ProcessRunner.Run("plutil", ["-p", plist]);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(@"""NSSupportsSuddenTermination"" => (true|1)"),
            dump.StdOut);
        Assert.Contains("\"HiFixCustomKey\" => \"from-extra-plist\"", dump.StdOut);
    }

    [Fact]
    public void MachOArchitectureIsArm64()
    {
        _f.Ensure();
        var lipo = ProcessRunner.Run("lipo",
            ["-info", Path.Combine(_f.App, "Contents/MacOS/BundlerMacIntegrationFixture")]);
        Assert.Contains("arm64", lipo.StdOut);
    }

    [Fact]
    public void ExecutableRuns()
    {
        _f.Ensure();
        var exe = Path.Combine(_f.App, "Contents/MacOS/BundlerMacIntegrationFixture");
        Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute),
            "The Mach-O main executable lost its executable bit.");
        var run = ProcessRunner.Run(exe, ["marker-a", "marker b"]);
        ProcessRunner.AssertSuccess(run, "The .app executable did not run.");
        Assert.Contains("BundlerMacIntegrationFixture:marker-a,marker b", run.StdOut);
    }

    [Fact]
    public void LaunchServicesDispatchChain()
    {
        _f.Ensure();
        // open -W 可用性探测（无头宿主 LaunchServices 可能缺席）→ 缺席则整腿 Skip。
        var probe = ProcessRunner.Run("open", ["-n", "-W", _f.App],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
        Assert.SkipWhen(probe.ExitCode != 0,
            "SKIP: open -W unavailable on this host (headless LaunchServices).");

        ProcessRunner.AssertSuccess(
            ProcessRunner.Run(MacAppFixture.LsRegister, ["-f", _f.App]),
            "lsregister -f failed to register the bundle.");
        var registered = false;
        for (var i = 0; i < 20 && !registered; i++)
        {
            var dump = ProcessRunner.Run(MacAppFixture.LsRegister, ["-dump"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
            registered = dump.StdOut.Contains("com.dotnetbundler.macintegrationfixture");
            if (!registered)
            {
                Thread.Sleep(500);
            }
        }
        Assert.True(registered, "lsregister -dump does not list the bundle identifier.");

        var marker = Path.Combine(_f.App, "Contents", ".launch-marker");
        var sample = _f.Ws.Combine("sample.hifix");
        File.WriteAllText(sample, "hifix-payload\n");
        // 路由腿不用 -W 等退出（进程秒退时 open 的 kqueue 附加会偶发 No such process），
        // open 返回后轮询启动标记——标记才是"路由成功"的真实断言。
        File.Delete(marker);
        ProcessRunner.AssertSuccess(ProcessRunner.Run("open", [sample]),
            "open <file> did not route to the registered app.");
        WaitFor.Until(() => File.Exists(marker),
            "open <file> returned success but the app never ran (no launch marker).");

        File.Delete(marker);
        ProcessRunner.AssertSuccess(ProcessRunner.Run("open", ["hifix://ping"]),
            "open <scheme>:// did not route to the registered app.");
        WaitFor.Until(() => File.Exists(marker),
            "open <scheme>:// returned success but the app never ran (no launch marker).");

        // ~/Applications 拖放式安装
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory.CreateDirectory(Path.Combine(home, "Applications"));
        var copied = Path.Combine(home, "Applications", "Bundler Mac Integration Fixture.app");
        try
        {
            CopyDirectory(_f.App, copied);
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run(MacAppFixture.LsRegister, ["-f", copied]),
                "lsregister failed to register the ~/Applications copy.");
            var copiedMarker = Path.Combine(copied, "Contents", ".launch-marker");
            File.Delete(copiedMarker);
            ProcessRunner.AssertSuccess(ProcessRunner.Run("open", ["-W", copied]),
                "open failed for the ~/Applications copy.");
            Assert.True(File.Exists(copiedMarker), "The ~/Applications copy never ran.");
        }
        finally
        {
            ProcessRunner.Run(MacAppFixture.LsRegister, ["-u", copied]);
            if (Directory.Exists(copied))
            {
                Directory.Delete(copied, recursive: true);
            }
        }
    }

    [Fact]
    public void RebuildInfoPlistIsDeterministic()
    {
        _f.Ensure();
        var plist = Path.Combine(_f.App, "Contents/Info.plist");
        var before = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plist)));
        var args = new List<string>
        {
            "publish", _f.FixtureProject, "-c", "Release", "--force",
            $"-p:BundlerIntegrationOutput={_f.Ws.Combine("bundle")}",
            $"-p:BundlerTestIcon={_f.IconPng}",
            $"-p:RestorePackagesPath={_f.CacheDir}",
        };
        if (_f.Dylib is not null)
        {
            args.Add($"-p:BundlerTestDylib={_f.Dylib}");
        }
        ProcessRunner.AssertSuccess(
            Dotnet.Run(args, new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) }),
            "rebuild publish failed");
        var after = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plist)));
        Assert.Equal(before, after);
    }

    [Fact]
    public void AdHocSigningChain()
    {
        _f.Ensure();
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("codesign", ["--verify", "--deep", "--strict", _f.SignedApp]),
            "codesign --verify rejected the ad-hoc signed bundle.");
        var authority = ProcessRunner.Run("codesign", ["-dv", "--verbose=4", _f.SignedApp]);
        Assert.Contains("Signature=adhoc", authority.Output);

        var exe = Path.Combine(_f.SignedApp, "Contents/MacOS/BundlerMacIntegrationFixture");
        var run = ProcessRunner.Run(exe, ["signed-launch"]);
        ProcessRunner.AssertSuccess(run, "The ad-hoc signed bundle did not launch.");
        Assert.Contains("signed-launch", run.StdOut);
        var marker = Path.Combine(_f.SignedApp, "Contents", ".launch-marker");
        Assert.True(File.Exists(marker), "The signed app did not write its launch marker.");
        File.Delete(marker);
    }

    [Fact]
    public void MissingCertificateFailsBeforeTooling()
    {
        _f.Ensure();
        var output = _f.Ws.Combine("badcert-output");
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release", "--force",
             $"-p:BundlerIntegrationOutput={output}",
             $"-p:BundlerTestIcon={_f.IconPng}",
             $"-p:BundlerTestSigningCertificate={_f.Ws.Combine("missing.p12")}",
             $"-p:RestorePackagesPath={_f.CacheDir}"],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(15) });
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(
            Path.Combine(output, "osx-arm64", "app", "Bundler Mac Integration Fixture.app")),
            "A failed signing run left a pseudo-success .app.");
        Assert.Matches(
            new System.Text.RegularExpressions.Regex("TemporaryCertificatePath|temporary certificate",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            result.Output);
    }

    [Fact]
    public void OsxX64StructureOnly()
    {
        _f.Ensure();
        var exe = Path.Combine(_f.X64App, "Contents/MacOS/BundlerMacIntegrationFixture");
        var lipo = ProcessRunner.Run("lipo", ["-info", exe]);
        Assert.Contains("x86_64", lipo.StdOut);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("plutil", ["-lint", Path.Combine(_f.X64App, "Contents/Info.plist")]),
            "osx-x64 Info.plist failed lint.");
        // Rosetta 启动是环境项：能跑更好，跑不了不失败（脚本同款口径）。
        _ = ProcessRunner.Run("/usr/bin/arch", ["-x86_64", exe],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
    }

    [Fact]
    public void QuarantineGatekeeperBehavior()
    {
        _f.Ensure();
        var probe = ProcessRunner.Run("open", ["-n", "-W", _f.App],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
        Assert.SkipWhen(probe.ExitCode != 0,
            "SKIP: open -W unavailable on this host (headless LaunchServices).");

        var quarantined = Path.Combine(_f.Ws.Combine("quarantine"), "Bundler Mac Integration Fixture.app");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantined)!);
        CopyDirectory(_f.App, quarantined);
        var stamp = ((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString("x");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("xattr",
            ["-w", "com.apple.quarantine", $"0081;{stamp};Verify;", quarantined]),
            "Failed to set com.apple.quarantine.");
        var listed = ProcessRunner.Run("xattr", ["-l", quarantined]);
        Assert.Contains("com.apple.quarantine", listed.StdOut);

        // Gatekeeper 弹窗会挂起 open -W：限时等待，放行/拦下/超时挂起都算"被拦"的合法观察结果。
        try
        {
            _ = ProcessRunner.Run("open", ["-W", quarantined],
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
        }
        catch (TimeoutException)
        {
            // 挂起被杀属预期分支，脚本同样不判定具体结果。
        }
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("xattr", ["-d", "com.apple.quarantine", quarantined]),
            "com.apple.quarantine could not be removed.");
    }

    [Fact]
    public void MinSystemVersionRejectedByLaunchServices()
    {
        _f.Ensure();
        var probe = ProcessRunner.Run("open", ["-n", "-W", _f.App],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
        Assert.SkipWhen(probe.ExitCode != 0,
            "SKIP: open -W unavailable on this host (headless LaunchServices).");
        var dump = ProcessRunner.Run("plutil",
            ["-p", Path.Combine(_f.MinVerApp, "Contents/Info.plist")]);
        Assert.Contains("\"LSMinimumSystemVersion\" => \"99.0\"", dump.StdOut);
        var open = ProcessRunner.Run("open", ["-W", _f.MinVerApp],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
        Assert.NotEqual(0, open.ExitCode);
    }

    [Fact]
    public void InPlaceUpgradeKeepsRegistration()
    {
        _f.Ensure();
        var probe = ProcessRunner.Run("open", ["-n", "-W", _f.App],
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
        Assert.SkipWhen(probe.ExitCode != 0,
            "SKIP: open -W unavailable on this host (headless LaunchServices).");

        var dump = ProcessRunner.Run("plutil",
            ["-p", Path.Combine(_f.UpgradeApp, "Contents/Info.plist")]);
        Assert.Contains("\"CFBundleVersion\" => \"2026.9.2\"", dump.StdOut);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var copied = Path.Combine(home, "Applications", "Bundler Mac Integration Fixture.app");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "Applications"));
            if (Directory.Exists(copied))
            {
                Directory.Delete(copied, recursive: true);
            }
            CopyDirectory(_f.App, copied);
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run(MacAppFixture.LsRegister, ["-f", copied]),
                "v1 registration failed.");
            var marker = Path.Combine(copied, "Contents", ".launch-marker");
            File.Delete(marker);
            ProcessRunner.AssertSuccess(ProcessRunner.Run("open", ["-W", copied]),
                "v1 launch failed.");
            Assert.True(File.Exists(marker), "v1 launch marker missing.");

            Directory.Delete(copied, recursive: true);
            CopyDirectory(_f.UpgradeApp, copied);
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run(MacAppFixture.LsRegister, ["-f", copied]),
                "v2 registration failed.");
            File.Delete(marker);
            ProcessRunner.AssertSuccess(ProcessRunner.Run("open", ["-W", copied]),
                "v2 launch after in-place replacement failed.");
            Assert.True(File.Exists(marker), "v2 launch marker missing after upgrade.");

            var lsDump = ProcessRunner.Run(MacAppFixture.LsRegister, ["-dump"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(60) });
            Assert.Contains("com.dotnetbundler.macintegrationfixture", lsDump.StdOut);
        }
        finally
        {
            ProcessRunner.Run(MacAppFixture.LsRegister, ["-u", copied]);
            if (Directory.Exists(copied))
            {
                Directory.Delete(copied, recursive: true);
            }
        }
    }

    [Fact]
    public void ApiFixtureProducesLintableApp()
    {
        _f.Ensure();
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "MacAppApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["MACAPP_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "MacAppApiTests failed");
        var plist = Path.Combine(output, "artifacts", "osx-arm64", "app",
            "Mac API Package Fixture.app", "Contents", "Info.plist");
        Assert.True(File.Exists(plist), "The standalone API package did not create an .app.");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("plutil", ["-lint", plist]),
            "API fixture Info.plist failed lint.");
        var dump = ProcessRunner.Run("plutil", ["-p", plist]);
        Assert.Contains("\"CFBundleExecutable\" => \"ApiFixture\"", dump.StdOut);
    }

    [Fact]
    public void UninstallIsPlainDelete()
    {
        _f.Ensure();
        // 卸载=删 .app 目录；脚本最后删掉产物 app——测试里建一个副本验证删除语义。
        var copy = _f.Ws.Combine("delete-me", "Bundler Mac Integration Fixture.app");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        CopyDirectory(_f.App, copy);
        Directory.Delete(copy, recursive: true);
        Assert.False(Directory.Exists(copy), "Deleting the .app left residue.");
    }

    private static JsonElement PlistExtract(string plistPath, string key)
    {
        var result = ProcessRunner.Run("plutil", ["-extract", key, "json", "-o", "-", plistPath]);
        ProcessRunner.AssertSuccess(result, $"plutil -extract {key} failed.");
        return JsonDocument.Parse(result.StdOut, Doc).RootElement.Clone();
    }

    private static void CopyDirectory(string source, string destination)
    {
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("cp", ["-R", source, destination]), "cp -R failed.");
    }
}

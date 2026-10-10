// LINUX-DEB-1..4 集成腿的 C# 移植：对应原 tests/Linux.Deb.Integration/Verify.sh。
// 覆盖点：nupkg → ar 成员 → sha256 → control 元数据/md5sums → 载荷清单与模式
// → .desktop/icons/metainfo/changelog/copyright → 真实 dpkg -i/-r/-P（conffile 语义）
// → maintainer scripts / systemd / override / semver / metadata / desktop-override
// → 三类失败变体 → API fixture → arm64 结构 → lintian 豁免清单 → docker 矩阵。
using System.Text.RegularExpressions;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DebFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; private set; } = null!;
    public string CacheDir { get; private set; } = null!;
    public string ExtractRoot { get; private set; } = null!;
    public string FixtureProject { get; private set; } = null!;
    public string FixtureDir { get; private set; } = null!;

    public string DefaultDeb { get; private set; } = null!;
    public string OverrideDeb { get; private set; } = null!;
    public string SemverDeb { get; private set; } = null!;
    public string MetadataDeb { get; private set; } = null!;
    public string DesktopOverrideDeb { get; private set; } = null!;
    public string ScriptsDeb { get; private set; } = null!;
    public string SystemdDeb { get; private set; } = null!;
    public string UpgradeDeb { get; private set; } = null!;
    public string Arm64Deb { get; private set; } = null!;

    private readonly Lazy<bool> _init;

    public DebFixture() => _init = new Lazy<bool>(() => { Initialize(); return true; });

    public bool Ensure() => _init.Value;

    private void Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: deb integration test requires a Linux host.");
        foreach (var tool in new[] { "ar", "tar", "md5sum", "sha256sum", "dpkg-deb", "unzip", "gzip" })
        {
            ExternalTools.Require(tool);
        }
        Ws = IntegrationWorkspace.Create("linux-deb-integration", "BundlerLinuxDebIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExtractRoot = Ws.Combine("extract");
        FixtureDir = Path.Combine(RepositoryLayout.FixturesDirectory, "Deb");
        FixtureProject = Path.Combine(FixtureDir, "BundlerDebIntegrationFixture.csproj");
        _ = RepositoryPackages.DirectoryPath;

        Publish("bundle");
        DefaultDeb = RequireDeb(Ws.Combine("bundle", "bundler-deb-fixture_1.0.0-1_amd64.deb"));

        Publish("override",
            "-p:BundlerTestDebPackageName=custom-fixture",
            "-p:BundlerTestDebVersion=2:9.9.9-5",
            "-p:BundlerTestDebVendor=Custom Maintainer <m@example.com>",
            "-p:BundlerTestDebInstallRoot=/opt/custom-fixture",
            "-p:BundlerTestDebBinLink=custom-fixture-cli");
        OverrideDeb = RequireDeb(Ws.Combine("override", "custom-fixture_9.9.9-5_amd64.deb"));

        Publish("semver", "-p:BundlerVersion=2.5.0-beta.3+build.1");
        SemverDeb = RequireDeb(Ws.Combine("semver",
            "bundler-deb-fixture_2.5.0~beta.3+build.1-1_amd64.deb"));

        Publish("metadata",
            "-p:BundlerTestDebDepends=libc6 (>= 2.35)%3Blibssl3",
            "-p:BundlerTestDebRecommends=ca-certificates",
            "-p:BundlerTestDebProvides=virtual-fixture",
            "-p:BundlerTestDebConflicts=legacy-fixture",
            "-p:BundlerTestDebReplaces=legacy-fixture",
            "-p:BundlerTestDebSection=utils",
            "-p:BundlerTestDebPriority=extra");
        MetadataDeb = RequireDeb(Ws.Combine("metadata",
            "bundler-deb-fixture_1.0.0-1_amd64.deb"));

        Publish("desktop-override",
            $"-p:BundlerTestDebDesktopFile={FixtureDir}/Assets/custom.desktop");
        DesktopOverrideDeb = RequireDeb(Ws.Combine("desktop-override",
            "bundler-deb-fixture_1.0.0-1_amd64.deb"));

        Publish("scripts",
            $"-p:BundlerTestDebPostinstFile={FixtureDir}/Assets/postinst.sh",
            $"-p:BundlerTestDebPrermFile={FixtureDir}/Assets/prerm.sh",
            $"-p:BundlerTestDebPostrmFile={FixtureDir}/Assets/postrm.sh");
        ScriptsDeb = RequireDeb(Ws.Combine("scripts",
            "bundler-deb-fixture_1.0.0-1_amd64.deb"));

        Publish("systemd",
            $"-p:BundlerTestDebSystemdServiceFile={FixtureDir}/Assets/fixture.service",
            $"-p:BundlerTestDebPostinstFile={FixtureDir}/Assets/postinst.sh");
        SystemdDeb = RequireDeb(Ws.Combine("systemd",
            "bundler-deb-fixture_1.0.0-1_amd64.deb"));

        Publish("upgrade", "-p:BundlerVersion=1.0.1");
        UpgradeDeb = RequireDeb(Ws.Combine("upgrade",
            "bundler-deb-fixture_1.0.1-1_amd64.deb"));

        Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine("arm64")}",
             "--packages", CacheDir, "-r", "linux-arm64", "-p:BundlerTarget=linux-aarch64"],
            "linux-aarch64 publish failed", noRestore: false);
        var arm64 = Directory.EnumerateFiles(Ws.Combine("arm64"), "*_arm64.deb",
                SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(arm64);
        Arm64Deb = arm64;
    }

    public void Publish(string name, params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"deb fixture publish '{name}' failed", noRestore: false);

    private string RequireDeb(string path)
    {
        Assert.True(File.Exists(path), $"The .deb artifact is missing: {path}");
        return path;
    }

    public string ControlDir(string key) => Path.Combine(ExtractRoot, "control", key);
    public string DataDir(string key) => Path.Combine(ExtractRoot, "data", key);

    public void Dispose() => Ws?.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DebIntegrationTests : IClassFixture<DebFixture>
{
    private readonly DebFixture _f;

    public DebIntegrationTests(DebFixture fixture)
    {
        _f = fixture;
        _f.Ensure();
    }

    // 宿主 dpkg 的架构口径非 amd64（Alpine apk 版报 musl-linux-amd64）或缺失时，
    // amd64 .deb 装不上且载荷为 glibc 链接也跑不了——真装覆盖由 docker 发行版矩阵腿承担。
    private static void SkipWhenHostDpkgNotAmd64()
    {
        // 非 dpkg 系宿主连进程都起不来——缺失同样算不可装，Skip 而非 ERROR。
        ProcessRunner.Result arch;
        try
        {
            arch = ProcessRunner.Run("dpkg", ["--print-architecture"]);
        }
        catch (Exception)
        {
            Assert.Skip("SKIP: host has no dpkg; amd64 packages cannot be installed here.");
            return;
        }
        Assert.SkipWhen(arch.ExitCode != 0 || arch.StdOut.Trim() != "amd64",
            $"SKIP: host dpkg architecture is '{(arch.ExitCode == 0 ? arch.StdOut.Trim() : "unavailable")}', " +
            "amd64 packages cannot be installed here.");
    }

    [Fact]
    public void RepositoryPackagesCarryDebBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.Deb" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("DotNet.Bundler.Deb.dll"));
    }

    [Fact]
    public void ArMemberListingAndParsability()
    {
        var listing = DebTools.ArList(_f.DefaultDeb);
        Assert.Contains("debian-binary", listing);
        Assert.Contains("control.tar.gz", listing);
        Assert.Contains("data.tar.gz", listing);
        // dpkg-deb 能解析 fsys tar 流即结构合法
        var parse = ProcessRunner.Run("dpkg-deb", ["--fsys-tarfile", _f.DefaultDeb]);
        ProcessRunner.AssertSuccess(parse, "dpkg-deb cannot parse the archive.");
    }

    [Fact]
    public void Sha256SidecarMatches()
    {
        Assert.True(File.Exists(_f.DefaultDeb + ".sha256"), "The .sha256 sidecar is missing.");
        var result = ProcessRunner.Run("sha256sum", ["-c", Path.GetFileName(_f.DefaultDeb + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(_f.DefaultDeb)! });
        ProcessRunner.AssertSuccess(result, "The .sha256 sidecar does not match the .deb.");
    }

    [Fact]
    public void ControlMetadata()
    {
        var dir = _f.ControlDir("default");
        DebTools.ExtractControl(_f.DefaultDeb, dir);
        var control = File.ReadAllText(Path.Combine(dir, "control"));
        foreach (var field in new[]
        {
            "Package: bundler-deb-fixture", "Version: 1.0.0-1", "Architecture: amd64",
            "Maintainer: DotNet.Bundler Tests <tests@example.com>", "Priority: optional",
            "Section: utils", "Homepage: https://example.com/deb-fixture",
            "Installed-Size:", "Description: Disposable .deb integration-test fixture.",
        })
        {
            Assert.Contains(field, control);
        }
        Assert.True(File.Exists(Path.Combine(dir, "md5sums")), "control archive lacks md5sums.");
        Assert.Contains("new Debian package", DebTools.Info(_f.DefaultDeb));
    }

    [Fact]
    public void PayloadListingAndModes()
    {
        var listing = DebTools.ListContents(_f.DefaultDeb);
        Assert.Contains("usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture", listing);
        Assert.Contains("usr/lib/bundler-deb-fixture/docs/readme.txt", listing);
        Assert.Contains(
            "usr/bin/bundler-deb-fixture -> ../lib/bundler-deb-fixture/BundlerDebIntegrationFixture",
            listing);
        Assert.Matches(
            new Regex(@"^-rwxr-xr-x .*usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture$",
                RegexOptions.Multiline),
            listing);
        Assert.Contains("usr/share/applications/bundler-deb-fixture.desktop", listing);
    }

    [Fact]
    public void DesktopIntegrationPayload()
    {
        var dataDir = _f.DataDir("default");
        DebTools.ExtractData(_f.DefaultDeb, dataDir);
        var desktopFile = Path.Combine(dataDir, "usr/share/applications/bundler-deb-fixture.desktop");
        Assert.True(File.Exists(desktopFile), "The generated .desktop file is missing.");
        var lines = File.ReadAllLines(desktopFile);
        foreach (var line in new[]
        {
            "Type=Application", "Name=Bundler Deb Fixture",
            "Comment=Disposable .deb integration-test fixture.", "Exec=bundler-deb-fixture %u",
            "Icon=bundler-deb-fixture", "Terminal=false", "Categories=Utility;Development;",
            "MimeType=application/x-bundler-fixture;x-scheme-handler/bdlfixture;",
        })
        {
            Assert.Contains(line, lines);
        }
        Assert.True(File.Exists(Path.Combine(dataDir,
            "usr/share/icons/hicolor/48x48/apps/bundler-deb-fixture.png")), "hicolor 48x48 icon missing.");
        Assert.True(File.Exists(Path.Combine(dataDir,
            "usr/share/icons/hicolor/48x48@2/apps/bundler-deb-fixture.png")), "hicolor @2 icon missing.");
        Assert.True(File.Exists(Path.Combine(dataDir,
            "usr/share/metainfo/bundler-deb-fixture.metainfo.xml")), "metainfo missing.");
        var changelogDebian = Path.Combine(dataDir,
            "usr/share/doc/bundler-deb-fixture/changelog.Debian.gz");
        Assert.True(File.Exists(changelogDebian), "changelog.Debian.gz missing.");
        var stanza = ProcessRunner.Run("zcat", [changelogDebian]);
        ProcessRunner.AssertSuccess(stanza, "zcat changelog.Debian.gz failed.");
        Assert.Contains("bundler-deb-fixture (1.0.0-1) unstable", stanza.StdOut);
        var copyright = Path.Combine(dataDir, "usr/share/doc/bundler-deb-fixture/copyright");
        Assert.True(File.Exists(copyright), "copyright file missing.");
        Assert.Equal("MIT License", File.ReadLines(copyright).First());
        var changelog = Path.Combine(dataDir, "usr/share/doc/bundler-deb-fixture/changelog.gz");
        Assert.True(File.Exists(changelog), "changelog.gz missing.");
        var changelogText = ProcessRunner.Run("gzip", ["-dc", changelog]);
        ProcessRunner.AssertSuccess(changelogText, "gzip -dc changelog.gz failed.");
        Assert.Contains("# Changelog", changelogText.StdOut);
        Assert.True(File.Exists(Path.Combine(dataDir, "etc/bundler-deb-fixture/defaults.conf")),
            "The BundlerDebFile /etc entry is missing.");
    }

    [Fact]
    public void GeneratedDesktopFileValidates()
    {
        ExternalTools.Require("desktop-file-validate");
        var dataDir = _f.DataDir("validate");
        DebTools.ExtractData(_f.DefaultDeb, dataDir);
        var desktopFile = Path.Combine(dataDir, "usr/share/applications/bundler-deb-fixture.desktop");
        var result = ProcessRunner.Run("desktop-file-validate", [desktopFile]);
        ProcessRunner.AssertSuccess(result, "desktop-file-validate rejects the generated file.");
    }

    [Fact]
    public void ExtractedExecutableRuns()
    {
        SkipWhenHostDpkgNotAmd64();
        var dataDir = _f.DataDir("run");
        DebTools.ExtractData(_f.DefaultDeb, dataDir);
        var app = Path.Combine(dataDir, "usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture");
        Assert.True(File.Exists(app), "Extracted executable missing.");
        var mode = File.GetUnixFileMode(app);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "Extracted executable is not executable.");
        var run = ProcessRunner.Run(app, []);
        ProcessRunner.AssertSuccess(run, "The extracted app did not run.");
        Assert.StartsWith("BundlerDebIntegrationFixture:", run.StdOut);
        Assert.True(File.Exists(Path.Combine(dataDir, "usr/bin/bundler-deb-fixture")),
            "usr/bin symlink is missing in the extracted tree.");
    }

    [Fact]
    public void Md5sumsMatchExtractedPayload()
    {
        var dataDir = _f.DataDir("md5");
        var controlDir = _f.ControlDir("md5");
        DebTools.ExtractData(_f.DefaultDeb, dataDir);
        DebTools.ExtractControl(_f.DefaultDeb, controlDir);
        var result = ProcessRunner.Run("md5sum",
            ["-c", Path.Combine(controlDir, "md5sums")],
            new ProcessRunner.Options { WorkingDirectory = dataDir });
        ProcessRunner.AssertSuccess(result, "md5sums do not match the extracted payload.");
    }

    [Fact]
    public void OverrideVariantControlAndSymlink()
    {
        var dir = _f.ControlDir("override");
        DebTools.ExtractControl(_f.OverrideDeb, dir);
        var control = File.ReadAllText(Path.Combine(dir, "control"));
        Assert.Contains("Package: custom-fixture", control);
        Assert.Contains("Version: 2:9.9.9-5", control);
        Assert.Contains("Maintainer: Custom Maintainer <m@example.com>", control);
        var listing = DebTools.ListContents(_f.OverrideDeb);
        Assert.Contains("opt/custom-fixture/BundlerDebIntegrationFixture", listing);
        Assert.Contains(
            "usr/bin/custom-fixture-cli -> /opt/custom-fixture/BundlerDebIntegrationFixture",
            listing);
    }

    [Fact]
    public void SemverVariantMapsPrerelease()
    {
        var dir = _f.ControlDir("semver");
        DebTools.ExtractControl(_f.SemverDeb, dir);
        var control = File.ReadAllText(Path.Combine(dir, "control"));
        Assert.Contains("Version: 2.5.0~beta.3+build.1-1", control);
    }

    [Fact]
    public void MetadataVariantRelations()
    {
        var dir = _f.ControlDir("metadata");
        DebTools.ExtractControl(_f.MetadataDeb, dir);
        var control = File.ReadAllText(Path.Combine(dir, "control"));
        foreach (var field in new[]
        {
            "Depends: libc6 (>= 2.35), libssl3", "Recommends: ca-certificates",
            "Provides: virtual-fixture", "Conflicts: legacy-fixture",
            "Replaces: legacy-fixture", "Section: utils", "Priority: extra",
        })
        {
            Assert.Contains(field, control);
        }
    }

    [Fact]
    public void DesktopOverrideVariant()
    {
        var dataDir = _f.DataDir("desktop-override");
        DebTools.ExtractData(_f.DesktopOverrideDeb, dataDir);
        var desktopFile = Path.Combine(dataDir, "usr/share/applications/bundler-deb-fixture.desktop");
        Assert.True(File.Exists(desktopFile), "The override .desktop is missing.");
        Assert.Contains("Name=Bundler Deb Fixture Custom", File.ReadAllLines(desktopFile));
        if (ExternalTools.Has("desktop-file-validate"))
        {
            ProcessRunner.AssertSuccess(
                ProcessRunner.Run("desktop-file-validate", [desktopFile]),
                "desktop-file-validate rejects the override .desktop.");
        }
    }

    [Fact]
    public void FailureVariantsRejectInvalidKnobs()
    {
        // invalid priority
        var failPriority = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("fail-priority")}",
             "-p:BundlerTestDebPriority=ultra", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, failPriority.ExitCode);
        Assert.Matches(new Regex("priority", RegexOptions.IgnoreCase), failPriority.Output);
        // invalid categories
        var failCategories = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("fail-categories")}",
             "-p:BundlerTestDebCategories=Not A Category!", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, failCategories.ExitCode);
        Assert.Matches(new Regex("categor", RegexOptions.IgnoreCase), failCategories.Output);
    }

    [Fact]
    public void RelativeInstallRootFailsAndLeavesNoDeb()
    {
        var name = "failure";
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine(name)}",
             "-p:BundlerTestDebInstallRoot=relative/path", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Matches(new Regex("install root", RegexOptions.IgnoreCase), result.Output);
        var debDir = _f.Ws.Combine(name);
        var leftovers = Directory.Exists(debDir)
            ? Directory.EnumerateFiles(debDir, "*.deb").ToArray()
            : [];
        Assert.Empty(leftovers);
    }

    [Fact]
    public void ApiFixtureProducesParsableDeb()
    {
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "DebApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["DEB_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "DebApiTests failed");
        var apiDeb = Path.Combine(output, "artifacts", "api-fixture_1.0.0-1_amd64.deb");
        Assert.True(File.Exists(apiDeb), "The direct-API fixture produced no .deb.");
        DebTools.Info(apiDeb);
    }

    [Fact]
    public void ScriptsVariantControlMembers()
    {
        var dir = _f.ControlDir("scripts");
        DebTools.ExtractControl(_f.ScriptsDeb, dir);
        foreach (var member in new[] { "postinst", "prerm", "postrm" })
        {
            var path = Path.Combine(dir, member);
            Assert.True(File.Exists(path), $"control archive lacks {member}.");
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, mode);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal((byte)'#', bytes[0]);
            Assert.Equal((byte)'!', bytes[1]);
        }
        Assert.False(File.Exists(Path.Combine(dir, "preinst")), "Unset preinst must not be packed.");
        var conffiles = Path.Combine(dir, "conffiles");
        Assert.True(File.Exists(conffiles), "conffiles member missing in scripts variant.");
        Assert.Contains("/etc/bundler-deb-fixture/defaults.conf", File.ReadAllLines(conffiles));
    }

    [Fact]
    public void SystemdVariantSynthesizesDaemonReload()
    {
        var controlDir = _f.ControlDir("systemd");
        var dataDir = _f.DataDir("systemd");
        DebTools.ExtractControl(_f.SystemdDeb, controlDir);
        DebTools.ExtractData(_f.SystemdDeb, dataDir);
        var unit = Path.Combine(dataDir, "usr/lib/systemd/system/bundler-deb-fixture.service");
        Assert.True(File.Exists(unit), "The systemd unit is missing from the payload.");
        Assert.Contains("ExecStart=/usr/bin/bundler-deb-fixture", File.ReadAllLines(unit));
        var postinst = File.ReadAllLines(Path.Combine(controlDir, "postinst"));
        Assert.Contains("systemctl daemon-reload || true", postinst);
        Assert.Contains(postinst, l => l.Contains("bundler-deb-fixture-postinst.ran"));
    }

    [Fact]
    [Trait("Requires", "localinstall")]
    public void RealDpkgInstallRemoveAndConffileSemantics()
    {
        SkipWhenHostDpkgNotAmd64();
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") != "1",
            "真装会写系统包库；置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1 才跑。");
        ElevatedRunner.RequireSudo("real dpkg -i/-r not exercised.");
        try
        {
            ProcessRunner.AssertSuccess(
                ElevatedRunner.Sudo("dpkg", "-i", _f.DefaultDeb), "dpkg -i failed.");
            var status = ProcessRunner.Run("dpkg", ["-s", "bundler-deb-fixture"]);
            ProcessRunner.AssertSuccess(status, "dpkg -s failed after install.");
            Assert.Contains("Status: install ok installed", status.StdOut);
            Assert.Contains("Version: 1.0.0-1", status.StdOut);
            Assert.True(File.Exists("/usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture"));
            var installedExe = ProcessRunner.Run("/usr/bin/bundler-deb-fixture", []);
            ProcessRunner.AssertSuccess(installedExe, "The installed symlink did not launch the app.");
            Assert.StartsWith("BundlerDebIntegrationFixture:", installedExe.StdOut);
            Assert.Matches(new Regex("^[0-9]+$"), DebTools.Field(_f.DefaultDeb, "Installed-Size"));

            var installedFiles = ProcessRunner.Run("dpkg", ["-L", "bundler-deb-fixture"]);
            ProcessRunner.AssertSuccess(installedFiles, "dpkg -L failed.");
            var lines = installedFiles.StdOut.Split('\n').Select(l => l.Trim()).ToArray();
            foreach (var path in new[]
            {
                "/usr/share/applications/bundler-deb-fixture.desktop",
                "/usr/share/icons/hicolor/48x48/apps/bundler-deb-fixture.png",
                "/usr/share/icons/hicolor/48x48@2/apps/bundler-deb-fixture.png",
                "/usr/share/metainfo/bundler-deb-fixture.metainfo.xml",
                "/usr/share/doc/bundler-deb-fixture/copyright",
                "/usr/share/doc/bundler-deb-fixture/changelog.gz",
                "/etc/bundler-deb-fixture/defaults.conf",
            })
            {
                Assert.Contains(path, lines);
                Assert.True(File.Exists(path), $"Installed path missing: {path}");
            }
            var installedDesktop = File.ReadAllLines("/usr/share/applications/bundler-deb-fixture.desktop");
            Assert.Contains("Exec=bundler-deb-fixture %u", installedDesktop);
            if (ExternalTools.Has("desktop-file-validate"))
            {
                ProcessRunner.AssertSuccess(
                    ProcessRunner.Run("desktop-file-validate",
                        ["/usr/share/applications/bundler-deb-fixture.desktop"]),
                    "desktop-file-validate rejects the installed .desktop.");
            }

            // conffile 语义：/etc DebFile 自动登记 conffile → -r 保留 → -P 清除
            var conffiles = ProcessRunner.Run("dpkg-query", ["-W", "-f=${Conffiles}", "bundler-deb-fixture"]);
            ProcessRunner.AssertSuccess(conffiles, "dpkg-query Conffiles failed.");
            Assert.Contains("etc/bundler-deb-fixture/defaults.conf", conffiles.StdOut);
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("/bin/sh", "-c",
                "echo 'local_edit=1' > /etc/bundler-deb-fixture/defaults.conf"), "conffile edit failed.");
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("dpkg", "-r", "bundler-deb-fixture"),
                "dpkg -r failed.");
            Assert.False(File.Exists("/usr/lib/bundler-deb-fixture"));
            Assert.False(File.Exists("/usr/bin/bundler-deb-fixture"));
            Assert.False(File.Exists("/usr/share/applications/bundler-deb-fixture.desktop"));
            Assert.Equal("local_edit=1",
                File.ReadAllLines("/etc/bundler-deb-fixture/defaults.conf").First());
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("dpkg", "-P", "bundler-deb-fixture"),
                "dpkg -P failed.");
            Assert.False(Directory.Exists("/etc/bundler-deb-fixture"), "dpkg -P did not purge the conffile.");
            var after = ProcessRunner.Run("dpkg", ["-s", "bundler-deb-fixture"]);
            Assert.NotEqual(0, after.ExitCode);
        }
        finally
        {
            ElevatedRunner.Sudo("dpkg", "-P", "bundler-deb-fixture");
            ElevatedRunner.Sudo("rm", "-rf", "/etc/bundler-deb-fixture");
        }
    }

    [Fact]
    [Trait("Requires", "localinstall")]
    public void MaintainerScriptsRunUnderRealDpkg()
    {
        SkipWhenHostDpkgNotAmd64();
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") != "1",
            "真装会写系统包库；置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1 才跑。");
        ElevatedRunner.RequireSudo("maintainer-script leg not exercised.");
        var markers = new[]
        {
            "/var/lib/bundler-deb-fixture-postinst.ran", "/tmp/bundler-deb-fixture-postinst.ran",
            "/tmp/bundler-deb-fixture-prerm.ran", "/tmp/bundler-deb-fixture-postrm.ran",
        };
        try
        {
            ElevatedRunner.Sudo("rm", ["-f", .. markers]);
            ProcessRunner.AssertSuccess(
                ElevatedRunner.Sudo("dpkg", "-i", _f.ScriptsDeb), "dpkg -i (scripts) failed.");
            Assert.True(File.Exists("/var/lib/bundler-deb-fixture-postinst.ran")
                || File.Exists("/tmp/bundler-deb-fixture-postinst.ran"),
                "postinst did not run (no marker file).");
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("dpkg", "-r", "bundler-deb-fixture"),
                "dpkg -r (scripts) failed.");
            Assert.True(File.Exists("/tmp/bundler-deb-fixture-prerm.ran"), "prerm did not run.");
            Assert.True(File.Exists("/tmp/bundler-deb-fixture-postrm.ran"), "postrm did not run.");
        }
        finally
        {
            ElevatedRunner.Sudo("dpkg", "-P", "bundler-deb-fixture");
            ElevatedRunner.Sudo("rm", ["-f", .. markers]);
            ElevatedRunner.Sudo("rm", "-rf", "/etc/bundler-deb-fixture");
        }
    }

    [Fact]
    [Trait("Requires", "localinstall")]
    public void UpgradePreservesModifiedConffile()
    {
        SkipWhenHostDpkgNotAmd64();
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") != "1",
            "真装会写系统包库；置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1 才跑。");
        ElevatedRunner.RequireSudo("upgrade leg not exercised.");
        try
        {
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("dpkg", "-i", _f.DefaultDeb),
                "dpkg -i v1.0.0 failed.");
            ProcessRunner.AssertSuccess(ElevatedRunner.Sudo("/bin/sh", "-c",
                "echo 'user_custom=42' > /etc/bundler-deb-fixture/defaults.conf"), "conffile edit failed.");
            ProcessRunner.AssertSuccess(
                ElevatedRunner.Sudo("dpkg", "-i", "--force-confold", _f.UpgradeDeb),
                "Upgrade dpkg -i v1.0.1 failed.");
            var status = ProcessRunner.Run("dpkg", ["-s", "bundler-deb-fixture"]);
            ProcessRunner.AssertSuccess(status, "dpkg -s failed after upgrade.");
            Assert.Contains("Version: 1.0.1-1", status.StdOut);
            Assert.Equal("user_custom=42",
                File.ReadAllLines("/etc/bundler-deb-fixture/defaults.conf").First());
        }
        finally
        {
            ElevatedRunner.Sudo("dpkg", "-P", "bundler-deb-fixture");
            ElevatedRunner.Sudo("rm", "-rf", "/etc/bundler-deb-fixture");
        }
    }

    [Fact]
    public void Arm64PackageStructure()
    {
        var info = DebTools.Info(_f.Arm64Deb);
        Assert.Contains("Architecture: arm64", info);
        var listing = DebTools.ListContents(_f.Arm64Deb);
        Assert.Contains("usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture", listing);
    }

    [Fact]
    public void LintianGateHonoursExemptionList()
    {
        ExternalTools.Require("lintian");
        var exemptionsFile = Path.Combine(
            RepositoryLayout.FixturesDirectory, "Deb", "lintian-exemptions.txt");
        var exemptions = File.ReadAllLines(exemptionsFile)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet();
        var result = ProcessRunner.Run("lintian", [_f.DefaultDeb]); // 退出码非零也照常解析
        var tags = result.Output.Split('\n')
            .Select(l => Regex.Match(l, @"^[EW]: [^:]*: ([^ ]*)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var unexpected = tags.Except(exemptions).ToArray();
        Assert.True(unexpected.Length == 0,
            $"lintian reported non-exempt tags: {string.Join(' ', unexpected)}\n{result.Output}");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerMatrixInstallRunRemove()
    {
        // 容器内跑 dpkg 与宿主是否有 dpkg 无关；只要求宿主是 amd64（镜像是 amd64 包）。
        Assert.SkipWhen(
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
                System.Runtime.InteropServices.Architecture.X64,
            "SKIP: docker matrix leg installs amd64 packages; non-x64 host cannot run them.");
        const string containerScript = """
            set -e
            dpkg -i /tmp/pkg.deb >/dev/null
            dpkg -s bundler-deb-fixture | grep -q "Status: install ok installed"
            bundler-deb-fixture smoke | grep -q "BundlerDebIntegrationFixture:smoke"
            test -f /etc/bundler-deb-fixture/defaults.conf
            dpkg -r bundler-deb-fixture >/dev/null
            test -f /etc/bundler-deb-fixture/defaults.conf
            ! dpkg -s bundler-deb-fixture >/dev/null 2>&1
            dpkg -P bundler-deb-fixture >/dev/null 2>&1 || true
            test ! -e /etc/bundler-deb-fixture/defaults.conf
            """;
        foreach (var image in new[] { "debian:stable", "ubuntu:latest" })
        {
            DockerRunner.RequireImage(image);
            var result = DockerRunner.RunScript(image, containerScript,
                [new DockerRunner.Mount(_f.DefaultDeb, "/tmp/pkg.deb")]);
            ProcessRunner.AssertSuccess(result, $"docker {image} install/run/remove failed.");
        }
    }
}

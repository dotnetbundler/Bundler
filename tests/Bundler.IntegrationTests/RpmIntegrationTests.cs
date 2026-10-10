// LINUX-RPM-1..4 集成腿的 C# 移植：对应原 tests/Linux.Rpm.Integration/Verify.sh。
// 覆盖点：nupkg → rpm -qip/--queryformat 逐字段（自依赖/rpmlib/关系字段三件套/
// LICENSE/GROUP/URL/FILEFLAGS %doc%license%config(noreplace)）→ cpio 载荷核对
// → 目录归属（自有叶子占有/共享目录不占有）→ 变体 ×9 → 失败变体 ×4
// → arm64 → deb;rpm 扇出 → docker fedora/rocky/leap 实装与 scriptlet/%config/rpm -U 语义
// → gpg 签名 rpm -K → rpmlint 豁免闸。
using System.Text.RegularExpressions;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RpmFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; private set; } = null!;
    public string CacheDir { get; private set; } = null!;
    public string ExtractRoot { get; private set; } = null!;
    public string FixtureProject { get; private set; } = null!;
    public string FixtureDir { get; private set; } = null!;

    public string DefaultRpm { get; private set; } = null!;
    public string OverridesRpm { get; private set; } = null!;
    public string PrereleaseRpm { get; private set; } = null!;
    public string MetadataRpm { get; private set; } = null!;
    public string DesktopOverrideRpm { get; private set; } = null!;
    public string ScriptsRpm { get; private set; } = null!;
    public string ConfigRpm { get; private set; } = null!;
    public string ConfigV2Rpm { get; private set; } = null!;
    public string Arm64Rpm { get; private set; } = null!;
    public string FanoutRpm { get; private set; } = null!;
    public string FanoutDeb { get; private set; } = null!;

    private readonly Lazy<bool> _init;

    public RpmFixture() => _init = new Lazy<bool>(() => { Initialize(); return true; });

    public bool Ensure() => _init.Value;

    private void Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: rpm integration test requires a Linux host.");
        foreach (var tool in new[] { "sha256sum", "unzip", "gzip" })
        {
            ExternalTools.Require(tool);
        }
        Ws = IntegrationWorkspace.Create("linux-rpm-integration", "BundlerLinuxRpmIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExtractRoot = Ws.Combine("extract");
        FixtureDir = Path.Combine(RepositoryLayout.FixturesDirectory, "Rpm");
        FixtureProject = Path.Combine(FixtureDir, "BundlerRpmIntegrationFixture.csproj");
        _ = RepositoryPackages.DirectoryPath;

        Publish("default");
        DefaultRpm = RequireRpm(Ws.Combine("default", "linux-x64", "rpm"), "bundler-rpm-fixture-1.0.0-1.x86_64.rpm");

        Publish("overrides",
            "-p:BundlerTestRpmPackageName=my-rpm-app",
            "-p:BundlerTestRpmVersion=9.9",
            "-p:BundlerTestRpmRelease=7.el9",
            "-p:BundlerTestRpmEpoch=2",
            "-p:BundlerTestRpmArchitecture=noarch",
            "-p:BundlerTestRpmInstallRoot=/opt/myapp",
            "-p:BundlerTestRpmBinLink=none");
        OverridesRpm = RequireRpm(Ws.Combine("overrides", "linux-x64", "rpm"), "my-rpm-app-9.9-7.el9.noarch.rpm");

        Publish("prerelease", "-p:BundlerVersion=1.0.0-alpha.2");
        PrereleaseRpm = RequireRpm(Ws.Combine("prerelease", "linux-x64", "rpm"),
            "bundler-rpm-fixture-1.0.0-0.1.alpha.2.x86_64.rpm");

        Publish("metadata",
            "-p:BundlerTestRpmDepends=libpng%3Bzlib >= 1.2",
            "-p:BundlerTestRpmProvides=bundler-plugin = 2.0",
            "-p:BundlerTestRpmConflicts=old-bundler",
            "-p:BundlerTestRpmObsoletes=bundler-rpm-legacy < 1.0",
            "-p:BundlerTestRpmRecommends=bundler-extras",
            "-p:BundlerTestRpmSuggests=bundler-docs >= 0.9",
            "-p:BundlerTestRpmLicense=MIT OR Apache-2.0",
            "-p:BundlerTestRpmGroup=Applications/Engineering",
            "-p:BundlerTestRpmUrl=https://example.com/rpm-override");
        MetadataRpm = RequireRpm(Ws.Combine("metadata", "linux-x64", "rpm"));

        Publish("desktop-override", $"-p:BundlerTestRpmDesktopFile={FixtureDir}/Assets/custom.desktop");
        DesktopOverrideRpm = RequireRpm(Ws.Combine("desktop-override", "linux-x64", "rpm"));

        Publish("scripts", "-p:BundlerTestRpmScripts=1", "-p:BundlerTestRpmSystemd=1");
        ScriptsRpm = RequireRpm(Ws.Combine("scripts", "linux-x64", "rpm"));

        Publish("configx", "-p:BundlerTestRpmConfigLocations=/usr/lib/bundler-rpm-fixture/docs/readme.txt");
        ConfigRpm = RequireRpm(Ws.Combine("configx", "linux-x64", "rpm"));
        Publish("configx-v2",
            "-p:BundlerTestRpmConfigLocations=/usr/lib/bundler-rpm-fixture/docs/readme.txt",
            "-p:BundlerTestRpmRelease=2");
        ConfigV2Rpm = RequireRpm(Ws.Combine("configx-v2", "linux-x64", "rpm"));

        Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine("arm64")}",
             "--packages", CacheDir, "-r", "linux-arm64"],
            "linux-arm64 publish failed", noRestore: false);
        Arm64Rpm = Directory.EnumerateFiles(Ws.Combine("arm64"), "*.aarch64.rpm",
            SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("No *.aarch64.rpm produced for linux-arm64.");

        Publish("fanout", "-p:BundlerTestFormats=deb%3Brpm");
        FanoutRpm = RequireRpm(Ws.Combine("fanout", "linux-x64", "rpm"), "*.rpm");
        FanoutDeb = RequireRpm(Ws.Combine("fanout", "linux-x64", "deb"), "*.deb");
    }

    public void Publish(string name, params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"rpm fixture publish '{name}' failed", noRestore: false);

    // 带 env 的 publish——口令类值走环境变量（MSBuild 自动导入为同名属性），不进 argv。
    public void Publish(string name, Dictionary<string, string?> environment,
        params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"rpm fixture publish '{name}' failed", noRestore: false, environment);

    private static string RequireRpm(string dir, string pattern = "*.rpm")
    {
        var match = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern).FirstOrDefault()
            : null;
        Assert.NotNull(match);
        return match;
    }

    public void Dispose() => Ws?.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RpmIntegrationTests : IClassFixture<RpmFixture>
{
    private readonly RpmFixture _f;

    public RpmIntegrationTests(RpmFixture fixture)
    {
        _f = fixture;
        _f.Ensure();
    }

    [Fact]
    public void RepositoryPackagesCarryRpmBackendAndSigningDependency()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.Rpm" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("DotNet.Bundler.Rpm.dll"));
        Assert.Contains(entries, e => e.EndsWith("BouncyCastle.Cryptography.dll"));
    }

    [Fact]
    public void DefaultRpmNameAndSha256Sidecar()
    {
        Assert.True(File.Exists(_f.DefaultRpm + ".sha256"), "Missing sha256 sidecar.");
        var check = ProcessRunner.Run("sha256sum",
            ["-c", Path.GetFileName(_f.DefaultRpm + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(_f.DefaultRpm)! });
        ProcessRunner.AssertSuccess(check, "sha256 sidecar mismatch.");
    }

    [Fact]
    public void RpmMetadataFields()
    {
        RpmTools.RequireRpm();
        var info = RpmTools.Info(_f.DefaultRpm);
        Assert.Contains("Name        : bundler-rpm-fixture", info);
        Assert.Contains("Version     : 1.0.0", info);
        Assert.Contains("Release     : 1", info);
        Assert.Contains("Architecture: x86_64", info);
        Assert.Equal("cpio", RpmTools.Field(_f.DefaultRpm, "PAYLOADFORMAT"));
        Assert.Equal("gzip", RpmTools.Field(_f.DefaultRpm, "PAYLOADCOMPRESSOR"));
        Assert.Equal("DotNet.Bundler Tests", RpmTools.Field(_f.DefaultRpm, "VENDOR"));

        var provides = RpmTools.Query(_f.DefaultRpm, "-qp", "--provides");
        Assert.Contains("bundler-rpm-fixture = 1.0.0-1", provides);
        var requires = RpmTools.Query(_f.DefaultRpm, "-qp", "--requires");
        Assert.Contains("rpmlib(CompressedFileNames)", requires);

        var files = RpmTools.ListFilesVerbose(_f.DefaultRpm);
        Assert.Matches(new Regex(@"drwxr-xr-x.*/usr/lib/bundler-rpm-fixture"), files);
        Assert.Contains("/usr/bin/bundler-rpm-fixture -> ", files);
    }

    [Fact]
    public void FreedesktopDocLicenseAndOwnershipPlacement()
    {
        RpmTools.RequireRpm();
        var filelist = RpmTools.ListFiles(_f.DefaultRpm);
        foreach (var expected in new[]
        {
            "/usr/share/applications/bundler-rpm-fixture.desktop",
            "/usr/share/icons/hicolor/48x48/apps/bundler-rpm-fixture.png",
            "/usr/share/icons/hicolor/48x48@2/apps/bundler-rpm-fixture.png",
            "/usr/share/metainfo/bundler-rpm-fixture.metainfo.xml",
            "/usr/share/doc/bundler-rpm-fixture/changelog.gz",
            "/usr/share/licenses/bundler-rpm-fixture/LICENSE.txt",
            "/etc/bundler-rpm-fixture/defaults.conf",
        })
        {
            Assert.Contains(expected, filelist);
        }

        // 目录归属：包自有叶子目录须占有，共享系统目录不得占有。
        var dirs = RpmTools.ListFilesVerbose(_f.DefaultRpm)
            .Split('\n')
            .Where(l => l.StartsWith('d'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
            .ToArray();
        Assert.Contains("/usr/share/licenses/bundler-rpm-fixture", dirs);
        Assert.Contains("/usr/share/doc/bundler-rpm-fixture", dirs);
        Assert.Contains("/etc/bundler-rpm-fixture", dirs);
        foreach (var shared in new[]
        {
            "/etc", "/usr/share", "/usr/share/applications", "/usr/share/doc",
            "/usr/share/icons", "/usr/share/metainfo",
        })
        {
            Assert.DoesNotContain(shared, dirs);
        }

        var flags = RpmTools.Query(_f.DefaultRpm, "-qp", "--queryformat", "[%{FILENAMES} %{FILEFLAGS}\n]");
        Assert.Contains("/usr/share/licenses/bundler-rpm-fixture/LICENSE.txt 128", flags);
        Assert.Contains("/usr/share/doc/bundler-rpm-fixture/changelog.gz 2", flags);
        Assert.Equal("Unspecified", RpmTools.Field(_f.DefaultRpm, "GROUP"));
        Assert.Equal("https://example.com/rpm-fixture", RpmTools.Field(_f.DefaultRpm, "URL"));
    }

    [Fact]
    public void GeneratedDesktopFileValidates()
    {
        RpmTools.RequireCpio();
        ExternalTools.Require("desktop-file-validate");
        var dest = Path.Combine(_f.ExtractRoot, "payload-validate");
        RpmTools.ExtractPayload(_f.DefaultRpm, dest);
        var desktop = Path.Combine(dest, "usr/share/applications/bundler-rpm-fixture.desktop");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("desktop-file-validate", [desktop]),
            "The generated .desktop fails desktop-file-validate.");
    }

    [Fact]
    public void CpioPayloadContentsAndBinaryRuns()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        RpmTools.RequireCpio();
        var dest = Path.Combine(_f.ExtractRoot, "payload");
        RpmTools.ExtractPayload(_f.DefaultRpm, dest);
        var exe = Path.Combine(dest, "usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture");
        Assert.True(File.Exists(exe), "Executable missing from the payload.");
        Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.Exists(Path.Combine(dest, "usr/lib/bundler-rpm-fixture/docs/readme.txt")),
            "Resource file missing from the payload.");
        var link = Path.Combine(dest, "usr/bin/bundler-rpm-fixture");
        Assert.True(File.Exists(link) || new FileInfo(link).LinkTarget != null,
            "/usr/bin symlink missing.");
        Assert.Equal("../lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture",
            new FileInfo(link).LinkTarget);
        var run = ProcessRunner.Run(exe, ["hello"],
            new ProcessRunner.Options { WorkingDirectory = dest });
        ProcessRunner.AssertSuccess(run, "Payload binary did not run.");
        Assert.Contains("BundlerRpmIntegrationFixture:hello", run.StdOut);
    }

    [Fact]
    public void OverridesVariant()
    {
        Assert.Equal("my-rpm-app-9.9-7.el9.noarch.rpm", Path.GetFileName(_f.OverridesRpm));
        RpmTools.RequireRpm();
        Assert.Equal("2", RpmTools.Field(_f.OverridesRpm, "EPOCHNUM"));
        Assert.Equal("noarch", RpmTools.Field(_f.OverridesRpm, "ARCH"));
        var files = RpmTools.ListFilesVerbose(_f.OverridesRpm);
        Assert.Contains("/opt/myapp/", files);
        Assert.DoesNotContain("/usr/bin/", files);
    }

    [Fact]
    public void PrereleaseVariantMapsToRpmRelease()
    {
        Assert.Equal("bundler-rpm-fixture-1.0.0-0.1.alpha.2.x86_64.rpm",
            Path.GetFileName(_f.PrereleaseRpm));
        RpmTools.RequireRpm();
        Assert.Equal("1.0.0", RpmTools.Field(_f.PrereleaseRpm, "VERSION"));
        Assert.Equal("0.1.alpha.2", RpmTools.Field(_f.PrereleaseRpm, "RELEASE"));
    }

    [Fact]
    public void MetadataRelationsVariant()
    {
        RpmTools.RequireRpm();
        var req = RpmTools.Query(_f.MetadataRpm, "-qp", "--requires");
        Assert.Contains("libpng", req);
        Assert.Matches(new Regex(@"zlib +>= +1\.2"), req);
        var prov = RpmTools.Query(_f.MetadataRpm, "-qp", "--provides");
        Assert.Matches(new Regex(@"bundler-plugin += +2\.0"), prov);
        Assert.Contains("old-bundler", RpmTools.Query(_f.MetadataRpm, "-qp", "--conflicts"));
        Assert.Contains("bundler-rpm-legacy", RpmTools.Query(_f.MetadataRpm, "-qp", "--obsoletes"));
        Assert.Contains("bundler-extras", RpmTools.Query(_f.MetadataRpm, "-qp", "--recommends"));
        Assert.Contains("bundler-docs", RpmTools.Query(_f.MetadataRpm, "-qp", "--suggests"));
        Assert.Equal("MIT OR Apache-2.0", RpmTools.Field(_f.MetadataRpm, "LICENSE"));
        Assert.Equal("Applications/Engineering", RpmTools.Field(_f.MetadataRpm, "GROUP"));
        Assert.Equal("https://example.com/rpm-override", RpmTools.Field(_f.MetadataRpm, "URL"));
    }

    [Fact]
    public void DesktopOverrideVariant()
    {
        RpmTools.RequireCpio();
        var dest = Path.Combine(_f.ExtractRoot, "desktop-override");
        RpmTools.ExtractPayload(_f.DesktopOverrideRpm, dest);
        var desktop = Path.Combine(dest, "usr/share/applications/bundler-rpm-fixture.desktop");
        Assert.Contains("Name=Bundler Rpm Fixture Custom", File.ReadAllText(desktop));
        if (ExternalTools.Has("desktop-file-validate"))
        {
            ProcessRunner.AssertSuccess(ProcessRunner.Run("desktop-file-validate", [desktop]),
                "The override .desktop fails validation.");
        }
    }

    [Fact]
    public void FailureVariantsLeaveNoArtifact()
    {
        // 非法包名 → publish 必须失败且无残留 .rpm
        var failName = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("failure")}",
             "-p:BundlerTestRpmPackageName=Bad Name", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, failName.ExitCode);
        var failDir = _f.Ws.Combine("failure");
        var leftovers = Directory.Exists(failDir)
            ? Directory.EnumerateFiles(failDir, "*.rpm", SearchOption.AllDirectories).ToArray()
            : [];
        Assert.Empty(leftovers);

        var badDep = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("baddep")}",
             "-p:BundlerTestRpmDepends=foo != 1.0", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badDep.ExitCode);

        var badFile = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("badfile")}",
             "-p:BundlerTestRpmBadFile=1", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badFile.ExitCode);
    }

    [Fact]
    public void ScriptletFailureVariants()
    {
        var badScript = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("badscript")}",
             "-p:BundlerTestRpmCrlfScript=1", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badScript.ExitCode);
        var badCfg = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("badcfg")}",
             "-p:BundlerTestRpmConfigLocations=/usr/lib/bundler-rpm-fixture/missing.conf",
             "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badCfg.ExitCode);
    }

    [Fact]
    public void ScriptsVariantEmitsScriptletsAndDaemonReload()
    {
        RpmTools.RequireRpm();
        Assert.Equal("/bin/sh", RpmTools.Field(_f.ScriptsRpm, "PREINPROG"));
        var scripts = RpmTools.Scripts(_f.ScriptsRpm);
        Assert.Contains("echo prein", scripts);
        Assert.Contains("echo postun", scripts);
        Assert.Contains("daemon-reload", scripts);
    }

    [Fact]
    public void ConfigNoreplaceVariantCarriesFlags17()
    {
        RpmTools.RequireRpm();
        var flags = RpmTools.Query(_f.ConfigRpm, "-qp", "--queryformat", "[%{FILENAMES} %{FILEFLAGS}\n]");
        Assert.Matches(new Regex(@"/etc/bundler-rpm-fixture/defaults\.conf +17"), flags);
        Assert.Matches(new Regex(@"/usr/lib/bundler-rpm-fixture/docs/readme\.txt +17"), flags);
        Assert.Equal("gzip", RpmTools.Field(_f.ConfigRpm, "PAYLOADCOMPRESSOR"));
    }

    [Fact]
    public void Arm64PackageStructure()
    {
        RpmTools.RequireRpm();
        Assert.Equal("aarch64", RpmTools.Field(_f.Arm64Rpm, "ARCH"));
        Assert.Contains("/usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture",
            RpmTools.ListFiles(_f.Arm64Rpm));
    }

    [Fact]
    public void DebRpmFanoutProducesBothArtifacts()
    {
        Assert.EndsWith(".rpm", _f.FanoutRpm);
        Assert.EndsWith(".deb", _f.FanoutDeb);
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerMatrixInstallQueryRunRemove()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        const string containerScript = """
            set -e
            rpm -i /tmp/pkg.rpm
            rpm -q bundler-rpm-fixture | grep -q bundler-rpm-fixture
            rpm -ql bundler-rpm-fixture | grep -q /usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture
            /usr/bin/bundler-rpm-fixture probe | grep -q probe
            for path in \
                /usr/share/applications/bundler-rpm-fixture.desktop \
                /usr/share/icons/hicolor/48x48/apps/bundler-rpm-fixture.png \
                /usr/share/metainfo/bundler-rpm-fixture.metainfo.xml \
                /usr/share/doc/bundler-rpm-fixture/changelog.gz \
                /usr/share/licenses/bundler-rpm-fixture/LICENSE.txt \
                /etc/bundler-rpm-fixture/defaults.conf; do
                rpm -ql bundler-rpm-fixture | grep -qx "$path"
                test -e "$path"
            done
            rpm -qd bundler-rpm-fixture | grep -q changelog.gz
            rpm -V bundler-rpm-fixture
            rpm -e bundler-rpm-fixture
            test ! -e /usr/lib/bundler-rpm-fixture
            test ! -L /usr/bin/bundler-rpm-fixture
            test ! -e /usr/share/licenses/bundler-rpm-fixture
            test ! -e /usr/share/doc/bundler-rpm-fixture
            test ! -e /etc/bundler-rpm-fixture
            """;
        var ranAny = false;
        foreach (var image in new[] { "fedora:latest", "rockylinux:9", "opensuse/leap:latest" })
        {
            if (!DockerRunner.TryEnsureImage(image))
            {
                continue; // 脚本语义：单镜像不可用记 SKIP 继续下一镜像
            }
            ranAny = true;
            var result = DockerRunner.RunScript(image, containerScript,
                [new DockerRunner.Mount(_f.DefaultRpm, "/tmp/pkg.rpm")]);
            ProcessRunner.AssertSuccess(result, $"rpm -i/-e failed in {image}.");
        }
        Assert.SkipWhen(!ranAny,
            "SKIP: no rpm-family docker image available (pull failed for all).");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerScriptletConfigAndUpgradeSemantics()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        DockerRunner.RequireImage("fedora:latest");
        var result = DockerRunner.RunScript("fedora:latest",
            """
            set -e
            rpm -i /tmp/scripts.rpm
            grep -qx prein /tmp/bundler-rpm-scripts.log
            grep -qx postin /tmp/bundler-rpm-scripts.log
            grep -c . /tmp/bundler-rpm-scripts.log | grep -qx 2
            test -f /usr/lib/systemd/system/bundler-rpm-fixture.service
            rpm -e bundler-rpm-fixture
            grep -qx preun /tmp/bundler-rpm-scripts.log
            grep -qx postun /tmp/bundler-rpm-scripts.log
            rpm -i /tmp/config.rpm
            echo changed > /etc/bundler-rpm-fixture/defaults.conf
            rpm -e bundler-rpm-fixture
            test -f /etc/bundler-rpm-fixture/defaults.conf.rpmsave
            grep -qx changed /etc/bundler-rpm-fixture/defaults.conf.rpmsave
            rpm -i /tmp/config.rpm
            echo upgraded > /etc/bundler-rpm-fixture/defaults.conf
            rpm -U /tmp/config-v2.rpm
            rpm -q bundler-rpm-fixture | grep -q 1.0.0-2
            grep -qx upgraded /etc/bundler-rpm-fixture/defaults.conf
            test ! -e /etc/bundler-rpm-fixture/defaults.conf.rpmnew
            rpm -e bundler-rpm-fixture
            """,
            [new DockerRunner.Mount(_f.ScriptsRpm, "/tmp/scripts.rpm"),
             new DockerRunner.Mount(_f.ConfigRpm, "/tmp/config.rpm"),
             new DockerRunner.Mount(_f.ConfigV2Rpm, "/tmp/config-v2.rpm")]);
        ProcessRunner.AssertSuccess(result,
            "Scriptlet/config/upgrade semantics failed in fedora:latest.");
    }

    [Fact]
    public void RpmSigningVerifiesUnderIsolatedRpmdb()
    {
        RpmTools.RequireRpm();
        ExternalTools.Require("gpg");
        var signDir = Path.Combine(_f.ExtractRoot, "signing");
        var rpmdb = Path.Combine(signDir, "rpmdb");
        var gnupg = Path.Combine(signDir, "gnupg");
        Directory.CreateDirectory(rpmdb);
        Directory.CreateDirectory(gnupg);
        File.SetUnixFileMode(gnupg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keygen = Path.Combine(signDir, "keygen.txt");
        File.WriteAllText(keygen, """
            Key-Type: RSA
            Key-Length: 2048
            Name-Real: Bundler Test
            Name-Email: bundler-test@example.com
            Expire-Date: 0
            Passphrase: bundler-test-pass
            %commit
            """ + "\n");
        var env = new ProcessRunner.Options
        {
            Environment = new Dictionary<string, string?> { ["GNUPGHOME"] = gnupg },
            Timeout = TimeSpan.FromMinutes(2),
        };
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("gpg", ["--batch", "--gen-key", keygen], env),
            "gpg test key generation failed.");
        var secretAsc = Path.Combine(signDir, "signing-key.asc");
        var passFile = Path.Combine(signDir, "sign.pass");
        File.WriteAllText(passFile, "bundler-test-pass");
        // 路径走位置参数不经插值：含引号/空格的路径不会破串或注入。
        var exportSecret = ProcessRunner.Run("/bin/sh",
            ["-c", "gpg --batch --yes --pinentry-mode loopback --passphrase-file \"$1\" --export-secret-keys --armor bundler-test@example.com > \"$2\"",
             "bundler-sh", passFile, secretAsc], env);
        ProcessRunner.AssertSuccess(exportSecret, "gpg secret key export failed.");
        var pubAsc = Path.Combine(signDir, "signing-key.pub.asc");
        var exportPub = ProcessRunner.Run("/bin/sh",
            ["-c", "gpg --batch --export --armor bundler-test@example.com > \"$1\"",
             "bundler-sh", pubAsc], env);
        ProcessRunner.AssertSuccess(exportPub, "gpg public key export failed.");

        _f.Publish("signed",
            new Dictionary<string, string?> { ["BundlerRpmSigningKeyPassphrase"] = "bundler-test-pass" },
            $"-p:BundlerRpmSigningKeyFile={secretAsc}");
        var signedRpm = Directory.EnumerateFiles(_f.Ws.Combine("signed"), "*.rpm",
            SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(signedRpm);

        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("rpm", ["--dbpath", rpmdb, "--initdb"]),
            "rpm --initdb failed.");
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("rpm", ["--dbpath", rpmdb, "--import", pubAsc]),
            "rpm --import of the test public key failed.");
        var signedCheck = ProcessRunner.Run("rpm", ["--dbpath", rpmdb, "-K", signedRpm]);
        Assert.Contains("signatures OK", signedCheck.StdOut);
        var unsignedCheck = ProcessRunner.Run("rpm", ["--dbpath", rpmdb, "-K", _f.DefaultRpm]);
        Assert.DoesNotContain("signatures OK", unsignedCheck.StdOut);
    }

    [Fact]
    public void RpmlintGateHonoursExemptionList()
    {
        ExternalTools.Require("rpmlint");
        var exemptionsFile = Path.Combine(
            RepositoryLayout.FixturesDirectory, "Rpm", "rpmlint-exemptions.txt");
        var exemptions = File.ReadAllLines(exemptionsFile)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet();
        var result = ProcessRunner.Run("rpmlint", [_f.DefaultRpm]);
        var tags = result.Output.Split('\n')
            .Select(l => Regex.Match(l, @"^[^ ]*: [EW]: ([^ ]*)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var unexpected = tags.Except(exemptions).ToArray();
        Assert.True(unexpected.Length == 0,
            $"rpmlint reported non-exempt tags: {string.Join(' ', unexpected)}\n{result.Output}");
    }
}

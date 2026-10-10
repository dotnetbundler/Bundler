// ARCHIVE-1 集成腿的 C# 移植：对应原 tests/Archive.Integration/Verify.sh。
// 覆盖点逐腿对应：nupkg 结构 → publish → 命名/sha256 → unzip/zipinfo/tar 模式断言
// → 真实解包运行 → python3 跨实现读取 → 覆盖/映射/失败/扇出/跨 OS → API fixture 腿。
using System.Diagnostics;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ArchiveFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; private set; } = null!;
    public string CacheDir { get; private set; } = null!;
    public string ExtractRoot { get; private set; } = null!;
    public string FixtureProject { get; private set; } = null!;

    public const string Stem = "bundler-archive-fixture-1.0.0-linux-x86_64";
    public const string Exe = "BundlerArchiveIntegrationFixture";

    public string DefaultZip { get; private set; } = null!;
    public string DefaultTgz { get; private set; } = null!;
    public string OverrideZip { get; private set; } = null!;
    public string NamedZip { get; private set; } = null!;
    public string FilesZip { get; private set; } = null!;
    public string FilesTgz { get; private set; } = null!;
    public string FanoutDir { get; private set; } = null!;
    public string CrossWinDir { get; private set; } = null!;
    public string CrossOsxDir { get; private set; } = null!;

    private readonly Lazy<bool> _init;

    public ArchiveFixture() => _init = new Lazy<bool>(() => { Initialize(); return true; });

    public bool Ensure() => _init.Value;

    private void Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: archive integration test requires a Linux host.");
        foreach (var tool in new[] { "sha256sum", "unzip", "zipinfo", "tar" })
        {
            ExternalTools.Require(tool);
        }
        Ws = IntegrationWorkspace.Create("archive-integration", "BundlerArchiveIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExtractRoot = Ws.Combine("extract");
        FixtureProject = Path.Combine(RepositoryLayout.FixturesDirectory,
            "Archive", "BundlerArchiveIntegrationFixture.csproj");
        _ = RepositoryPackages.DirectoryPath;
        Publish("default");
        DefaultZip = SingleFile(Ws.Combine("default"), "*.zip");
        DefaultTgz = SingleFile(Ws.Combine("default"), "*.tar.gz");

        Publish("override",
            "-p:BundlerTestArchivePackageName=acme-tool",
            "-p:BundlerTestArchiveVersion=2.3.4",
            "-p:BundlerTestFormats=zip");
        OverrideZip = SingleFile(Ws.Combine("override"), "*.zip");

        Publish("named", "-p:BundlerTestArchiveName=custom-stem", "-p:BundlerTestFormats=zip");
        NamedZip = SingleFile(Ws.Combine("named"), "custom-stem.zip");

        Publish("files", "-p:BundlerTestArchiveFiles=1", "-p:BundlerTestFormats=zip%3Btargz");
        FilesZip = SingleFile(Ws.Combine("files"), "*.zip");
        FilesTgz = SingleFile(Ws.Combine("files"), "*.tar.gz");

        Publish("fanout", "-p:BundlerTestFormats=deb%3Brpm%3Bappimage%3Bzip%3Btargz");
        FanoutDir = Ws.Combine("fanout");

        foreach (var rid in new[] { "windows-x86_64", "macos-arm64" })
        {
            Dotnet.Publish(FixtureProject, "Release",
            [
                $"-p:BundlerIntegrationOutput={Ws.Combine("cross-" + rid)}",
                "-p:BundlerTestFormats=zip",
                "--packages", CacheDir,
                "-r", TestPlatform.ToPublishRid(rid), $"-p:BundlerTarget={rid}",
            ], $"cross publish for {rid} failed", noRestore: false);
        }
        CrossWinDir = Ws.Combine("cross-windows-x86_64");
        CrossOsxDir = Ws.Combine("cross-macos-arm64");
    }

    public void Publish(string name, params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"archive fixture publish '{name}' failed", noRestore: false);

    public static string SingleFile(string dir, string pattern)
    {
        var matches = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern).ToArray()
            : [];
        Assert.True(matches.Length >= 1, $"No '{pattern}' artifact under {dir}");
        return matches[0];
    }

    public void Dispose() => Ws?.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ArchiveIntegrationTests : IClassFixture<ArchiveFixture>
{
    private readonly ArchiveFixture _f;

    public ArchiveIntegrationTests(ArchiveFixture fixture)
    {
        _f = fixture;
        _f.Ensure();
    }

    [Fact]
    public void RepositoryPackagesCarryArchiveBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.Archive" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("DotNet.Bundler.Archive.dll"));
    }

    [Fact]
    public void DefaultPublishProducesZipAndTarGzWithExpectedNames()
    {
        Assert.Equal($"{ArchiveFixture.Stem}.zip", Path.GetFileName(_f.DefaultZip));
        Assert.Equal($"{ArchiveFixture.Stem}.tar.gz", Path.GetFileName(_f.DefaultTgz));
    }

    [Fact]
    public void ZipListingAndModes()
    {
        var zip = _f.DefaultZip;
        var stem = ArchiveFixture.Stem;
        var exe = ArchiveFixture.Exe;
        Assert.True(File.Exists(zip + ".sha256"), $"{zip}: missing sha256 sidecar.");
        var sidecar = ProcessRunner.Run("sha256sum", ["-c", Path.GetFileName(zip + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(zip)! });
        ProcessRunner.AssertSuccess(sidecar, $"{zip}: sha256 sidecar mismatch.");

        var listing = ProcessRunner.Run("unzip", ["-l", zip]);
        ProcessRunner.AssertSuccess(listing, $"unzip -l failed on {zip}");
        Assert.Matches(new System.Text.RegularExpressions.Regex($" {stem}/$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($" {stem}/{exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);

        var modes = ProcessRunner.Run("zipinfo", ["-l", zip]);
        ProcessRunner.AssertSuccess(modes, $"zipinfo -l failed on {zip}");
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^-rwxr-xr-x .* {stem}/{exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), modes.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^-rw-r--r-- .* {stem}/{exe}\\.dll$", System.Text.RegularExpressions.RegexOptions.Multiline), modes.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^drwxr-xr-x .* {stem}/$", System.Text.RegularExpressions.RegexOptions.Multiline), modes.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^lrwxrwxrwx .* {stem}/{exe}\\.link$", System.Text.RegularExpressions.RegexOptions.Multiline), modes.StdOut);
    }

    [Fact]
    public void TarGzListingAndModes()
    {
        var tgz = _f.DefaultTgz;
        var stem = ArchiveFixture.Stem;
        var exe = ArchiveFixture.Exe;
        Assert.True(File.Exists(tgz + ".sha256"), $"{tgz}: missing sha256 sidecar.");
        var sidecar = ProcessRunner.Run("sha256sum", ["-c", Path.GetFileName(tgz + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(tgz)! });
        ProcessRunner.AssertSuccess(sidecar, $"{tgz}: sha256 sidecar mismatch.");

        var listing = ProcessRunner.Run("tar", ["-tzvf", tgz]);
        ProcessRunner.AssertSuccess(listing, $"tar -tzvf failed on {tgz}");
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^drwxr-xr-x .* {stem}/$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^-rwxr-xr-x .* {stem}/{exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^-rw-r--r-- .* {stem}/{exe}\\.dll$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^lrwxrwxrwx .* {stem}/{exe}\\.link -> \\./{exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
    }

    [Fact]
    public void UnzippedPayloadRunsWithExecBitAndSymlink()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        var root = Path.Combine(_f.ExtractRoot, "zip");
        Directory.CreateDirectory(root);
        var unzip = ProcessRunner.Run("unzip", ["-q", _f.DefaultZip, "-d", root]);
        ProcessRunner.AssertSuccess(unzip, "unzip extraction failed.");
        AssertExtractedRuns(root);
    }

    [Fact]
    public void PythonCrossImplementationReadsBothFormats()
    {
        ExternalTools.Require("python3");
        var script = """
            import sys, tarfile, zipfile
            zip_path, tgz_path, stem, exe = sys.argv[1:5]
            z = zipfile.ZipFile(zip_path)
            names = z.namelist()
            assert f"{stem}/{exe}" in names, "zipfile: payload entry missing"
            info = z.getinfo(f"{stem}/{exe}")
            assert (info.external_attr >> 16) & 0o111 != 0, "zipfile: exec bits not visible"
            assert z.read(f"{stem}/{exe}.link") == b"./" + exe.encode(), "zipfile: symlink content mismatch"
            t = tarfile.open(tgz_path, "r:gz")
            t_names = t.getnames()
            assert f"{stem}/{exe}" in t_names, "tarfile: payload entry missing"
            sym = t.getmember(f"{stem}/{exe}.link")
            assert sym.issym() and sym.linkname == f"./{exe}", "tarfile: symlink member mismatch"
            """;
        var result = ProcessRunner.Run("python3",
            ["-c", script, _f.DefaultZip, _f.DefaultTgz, ArchiveFixture.Stem, ArchiveFixture.Exe]);
        ProcessRunner.AssertSuccess(result, "python3 cross-implementation read failed");
    }

    [Fact]
    public void TarExtractedPayloadRunsWithExecBitAndSymlink()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        var root = Path.Combine(_f.ExtractRoot, "targz");
        Directory.CreateDirectory(root);
        var tar = ProcessRunner.Run("tar", ["-xzf", _f.DefaultTgz, "-C", root]);
        ProcessRunner.AssertSuccess(tar, "tar -xzf extraction failed.");
        AssertExtractedRuns(root);
    }

    [Fact]
    public void OverrideVariantUsesCustomPackageNameVersion()
    {
        var zip = _f.OverrideZip;
        Assert.Equal("acme-tool-2.3.4-linux-x86_64.zip", Path.GetFileName(zip));
        var listing = ProcessRunner.Run("unzip", ["-l", zip]);
        ProcessRunner.AssertSuccess(listing, "override: unzip -l failed.");
        Assert.Matches(new System.Text.RegularExpressions.Regex($" acme-tool-2\\.3\\.4-linux-x86_64/{ArchiveFixture.Exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
    }

    [Fact]
    public void ArchiveNameOverrideUsesCustomTopLevelDirectory()
    {
        var listing = ProcessRunner.Run("unzip", ["-l", _f.NamedZip]);
        ProcessRunner.AssertSuccess(listing, "named: unzip -l failed.");
        Assert.Matches(new System.Text.RegularExpressions.Regex($" custom-stem/{ArchiveFixture.Exe}$", System.Text.RegularExpressions.RegexOptions.Multiline), listing.StdOut);
    }

    [Fact]
    public void ArchiveFileMappingLandsInBothFormats()
    {
        var stem = ArchiveFixture.Stem;
        var zipListing = ProcessRunner.Run("unzip", ["-l", _f.FilesZip]);
        ProcessRunner.AssertSuccess(zipListing, "files: unzip -l failed.");
        Assert.Matches(new System.Text.RegularExpressions.Regex($" {stem}/extras/defaults\\.conf$", System.Text.RegularExpressions.RegexOptions.Multiline), zipListing.StdOut);

        var tarListing = ProcessRunner.Run("tar", ["-tzvf", _f.FilesTgz]);
        ProcessRunner.AssertSuccess(tarListing, "files: tar -tzvf failed.");
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^-rw-r--r-- .* {stem}/extras/defaults\\.conf$", System.Text.RegularExpressions.RegexOptions.Multiline), tarListing.StdOut);

        var root = Path.Combine(_f.ExtractRoot, "files");
        Directory.CreateDirectory(root);
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("unzip", ["-q", _f.FilesZip, "-d", root]),
            "files: unzip extraction failed.");
        var content = File.ReadAllText(Path.Combine(root, stem, "extras", "defaults.conf")).Trim();
        Assert.Equal("fixture=defaults", content);
    }

    [Fact]
    public void AbsoluteArchiveFileDestinationFailsPublish()
    {
        var badDir = _f.Ws.Combine("badfile");
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={badDir}",
             "-p:BundlerTestArchiveBadFile=1",
             "--packages", _f.CacheDir]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("BundlerArchiveFile destination", result.Output, StringComparison.OrdinalIgnoreCase);
        var leftovers = Directory.Exists(badDir)
            ? Directory.EnumerateFiles(badDir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".zip") || f.EndsWith(".tar.gz")).ToArray()
            : [];
        Assert.Empty(leftovers);
    }

    [Fact]
    public void MultiFormatFanoutProducesAllFormats()
    {
        foreach (var pattern in new[] { "*.deb", "*.rpm", "*.AppImage", "*.zip", "*.tar.gz" })
        {
            Assert.True(
                Directory.EnumerateFiles(_f.FanoutDir, pattern, SearchOption.AllDirectories).Any(),
                $"fanout: {pattern} missing under {_f.FanoutDir}.");
        }
    }

    [Fact]
    public void CrossOsPublishProducesZipForWindowsAndMac()
    {
        Assert.True(Directory.EnumerateFiles(_f.CrossWinDir, "*.zip").Any(),
            "cross: no zip produced for windows-x86_64.");
        Assert.True(Directory.EnumerateFiles(_f.CrossOsxDir, "*.zip").Any(),
            "cross: no zip produced for macos-arm64.");
    }

    [Fact]
    public void ApiFixtureProducesArchives()
    {
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "ArchiveApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["ARCHIVE_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "ArchiveApiTests failed");
        var artifacts = Path.Combine(output, "artifacts");
        Assert.True(Directory.EnumerateFiles(artifacts, "*.zip", SearchOption.AllDirectories).Any(),
            "API fixture did not produce a .zip.");
        Assert.True(Directory.EnumerateFiles(artifacts, "*.tar.gz", SearchOption.AllDirectories).Any(),
            "API fixture did not produce a .tar.gz.");
    }

    private static void AssertExtractedRuns(string root)
    {
        var stem = ArchiveFixture.Stem;
        var exe = ArchiveFixture.Exe;
        var top = Path.Combine(root, stem);
        Assert.True(Directory.Exists(top), $"extracted top-level directory {stem} missing.");
        var payload = Path.Combine(top, exe);
        Assert.True(File.Exists(payload), $"extracted payload {exe} missing.");
        Assert.True(File.GetUnixFileMode(payload).HasFlag(UnixFileMode.UserExecute),
            $"extracted payload {exe} is not executable.");
        Assert.True(File.Exists(Path.Combine(top, exe + ".dll")), $"extracted payload {exe}.dll missing.");
        var link = Path.Combine(top, exe + ".link");
        var info = new FileInfo(link);
        Assert.True(info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint),
            $"extracted {exe}.link is not a symlink.");
        Assert.Equal($"./{exe}", info.LinkTarget);
        var run = ProcessRunner.Run(payload, ["probe"]);
        ProcessRunner.AssertSuccess(run, "extracted payload failed to run.");
        Assert.Equal("BundlerArchiveIntegrationFixture:probe", run.StdOut.Trim());
    }
}

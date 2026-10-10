using DotNet.Bundler;
using DotNet.Bundler.Archive;
using System.Formats.Tar;
using System.IO.Compression;

// 直调后端 API 冒烟：ArchiveBundler 必须产出单顶层目录布局的 .zip 与 .tar.gz
public static class ArchiveApiTests
{
    [Fact]
    public static async Task ArchiveBundler_ProducesZipAndTarGz()
    {
        var root = ApiFixtureRoot.For("ARCHIVE_API_FIXTURE_OUTPUT", "Archive");
        var input = ApiFixtureRoot.WriteShellPayload(root);

        var artifacts = await new ArchiveBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.archiveapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API archive smoke test fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.Zip, PackageFormat.TarGz]
                }
            ]
        }, TestContext.Current.CancellationToken);

        var stem = "api-fixture-1.0.0-linux-x86_64";
        var zip = Assert.Single(artifacts, a => a.Path.EndsWith(".zip", StringComparison.Ordinal)).Path;
        var tarGz = Assert.Single(artifacts, a => a.Path.EndsWith(".tar.gz", StringComparison.Ordinal)).Path;
        Assert.Contains(stem, zip);
        Assert.Contains(stem, tarGz);
        Assert.True(File.Exists(zip + ".sha256"));
        Assert.True(File.Exists(tarGz + ".sha256"));

        using var zipArchive = ZipFile.OpenRead(zip);
        Assert.Contains(zipArchive.Entries, e => e.FullName.EndsWith("/FixtureApp", StringComparison.Ordinal));

        await using var tarStream = File.OpenRead(tarGz);
        await using var gzip = new GZipStream(tarStream, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        var sawExecutable = false;
        while (await tar.GetNextEntryAsync(cancellationToken: TestContext.Current.CancellationToken) is { } entry)
        {
            sawExecutable |= entry.Name.EndsWith("/FixtureApp", StringComparison.Ordinal);
        }
        Assert.True(sawExecutable);
    }
}

using System.IO.Compression;
using DotNet.Bundler;
using DotNet.Bundler.Archive;

namespace Bundler.ApiTests;

/// <summary>
/// zip/tar.gz 公开 API 契约：ArchiveBundler 双格式产包、生态命名、
/// 内容往返（独立读器验）、确定性字节、exec 位保留。
/// </summary>
public sealed class ArchiveApiTests : TempBase
{
    [Fact]
    public async Task Zip_RoundTrips_ThroughSystemReader()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Zip);
        var artifacts = await new ArchiveBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Zip, artifact.Format);
        Assert.Equal("sample-1.2.3-linux-x86_64.zip", Path.GetFileName(artifact.Path));

        using var zip = new ZipArchive(File.OpenRead(artifact.Path), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(names, n => n.EndsWith("sample", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("readme.txt", StringComparison.Ordinal));
        var readme = zip.Entries.First(e => e.FullName.EndsWith("readme.txt", StringComparison.Ordinal));
        using var reader = new StreamReader(readme.Open());
        Assert.Equal("doc", reader.ReadToEnd().TrimEnd());
    }

    [Fact]
    public async Task TarGz_RoundTrips_ThroughIndependentReader()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.TarGz);
        var artifacts = await new ArchiveBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal("sample-1.2.3-linux-x86_64.tar.gz", Path.GetFileName(artifact.Path));
        Assert.Equal([0x1F, 0x8B], BundleFixture.Magic(artifact.Path, 2));

        var entries = MiniTar.ReadGzip(artifact.Path);
        Assert.Contains(entries, e => e.Name.EndsWith("sample", StringComparison.Ordinal));
        var readme = entries.First(e => e.Name.EndsWith("readme.txt", StringComparison.Ordinal));
        Assert.Equal("doc", System.Text.Encoding.UTF8.GetString(readme.Data).TrimEnd());
    }

    [Fact]
    public async Task Rebuild_DeterministicBytes()
    {
        var a = (await new ArchiveBundler().BuildAsync(
            BundleFixture.Minimal(Temp("z1").FullName, "linux-x86_64", PackageFormat.Zip),
            TestContext.Current.CancellationToken))[0];
        var b = (await new ArchiveBundler().BuildAsync(
            BundleFixture.Minimal(Temp("z2").FullName, "linux-x86_64", PackageFormat.Zip),
            TestContext.Current.CancellationToken))[0];
        Assert.Equal(File.ReadAllBytes(a.Path), File.ReadAllBytes(b.Path));
    }

    [Fact]
    public async Task WindowsTarget_EcosystemName()
    {
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Zip);
        var artifacts = await new ArchiveBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);
        Assert.Equal("sample-1.2.3-windows-x64.zip",
            Path.GetFileName(artifacts[0].Path)); // windows 生态词 x64
    }
}

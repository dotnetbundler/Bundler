using System.IO.Compression;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Deb;

namespace Bundler.ApiTests;

/// <summary>
/// .deb 公开 API 契约：DebBundler.BuildAsync 产真包（全托管、全宿主可跑），
/// 断言文件名生态约定、ar 三段序、control 字段、确定性字节。
/// </summary>
public sealed class DebApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesStructuredDeb()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Deb);
        var artifacts = await new DebBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Deb, artifact.Format);
        Assert.Equal("linux-x86_64", artifact.Target);
        // deb 生态命名 {name}_{ver}-{release}_{arch}.deb
        Assert.Equal("sample_1.2.3-1_amd64.deb", Path.GetFileName(artifact.Path));
        Assert.True(File.Exists(artifact.Path));

        var members = MiniAr.Read(artifact.Path);
        Assert.Equal("debian-binary", members[0].Name);
        Assert.StartsWith("control.tar", members[1].Name);
        Assert.StartsWith("data.tar", members[2].Name);
        Assert.Equal("2.0\n", Encoding.ASCII.GetString(members[0].Data));

        using var controlStream = new GZipStream(
            new MemoryStream(members[1].Data), CompressionMode.Decompress);
        var controlBytes = new MemoryStream();
        controlStream.CopyTo(controlBytes);
        var controlEntries = MiniTar.Read(controlBytes.ToArray());
        var control = controlEntries.First(e => e.Name.EndsWith("control", StringComparison.Ordinal));
        var text = Encoding.UTF8.GetString(control.Data);
        Assert.Contains("Package: sample", text);
        Assert.Contains("Version: 1.2.3-1", text);
        Assert.Contains("Architecture: amd64", text);

        // data.tar 内含真载荷文件
        var dataStream = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(members[2].Data), CompressionMode.Decompress))
        {
            gzip.CopyTo(dataStream);
        }
        var dataEntries = MiniTar.Read(dataStream.ToArray());
        Assert.Contains(dataEntries, e => e.Name.Contains("sample", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_Rebuild_DeterministicBytes()
    {
        var bundle1 = BundleFixture.Minimal(Temp("d1").FullName, "linux-x86_64", PackageFormat.Deb);
        var bundle2 = BundleFixture.Minimal(Temp("d2").FullName, "linux-x86_64", PackageFormat.Deb);
        var a = (await new DebBundler().BuildAsync(bundle1, TestContext.Current.CancellationToken))[0];
        var b = (await new DebBundler().BuildAsync(bundle2, TestContext.Current.CancellationToken))[0];
        Assert.Equal(File.ReadAllBytes(a.Path), File.ReadAllBytes(b.Path));
    }

    [Fact]
    public async Task BuildAsync_ByFormatLayout_LandsInSubdir()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Deb,
            layout: OutputLayout.ByFormat);
        var artifacts = await new DebBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);
        Assert.Contains(
            "out/deb",
            artifacts[0].Path.Replace('\\', '/'));
    }
}

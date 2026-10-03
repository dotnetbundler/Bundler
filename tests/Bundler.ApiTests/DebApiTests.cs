using DotNet.Bundler;
using DotNet.Bundler.Deb;
using System.Text;

// 直调后端 API 冒烟：DebBundler 必须产出真实 .deb（ar 归档魔数）
public static class DebApiTests
{
    [Fact]
    public static async Task DebBundler_ProducesDeb()
    {
        var root = ApiFixtureRoot.For("DEB_API_FIXTURE_OUTPUT", "Deb");
        var input = ApiFixtureRoot.WriteShellPayload(root);

        var artifacts = await new DebBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.debapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API .deb smoke test fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "linux-x64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.Deb]
                }
            ]
        }, TestContext.Current.CancellationToken);

        var deb = Assert.Single(artifacts).Path;
        var expected = Path.Combine(root, "artifacts", "linux-x64", "deb",
            "api-fixture_1.0.0-1_amd64.deb");
        Assert.Equal(expected, deb);
        Assert.True(File.Exists(deb));

        var magic = new byte[8];
        await using var stream = File.OpenRead(deb);
        Assert.Equal(8, await stream.ReadAsync(magic, TestContext.Current.CancellationToken));
        Assert.Equal("!<arch>\n", Encoding.ASCII.GetString(magic));
    }
}

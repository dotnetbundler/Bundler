using DotNet.Bundler;
using DotNet.Bundler.Rpm;

// 直调后端 API 冒烟：RpmBundler 必须产出带 rpm lead 魔数的真实 .rpm
public static class RpmApiTests
{
    [Fact]
    public static async Task RpmBundler_ProducesRpm()
    {
        var root = ApiFixtureRoot.For("RPM_API_FIXTURE_OUTPUT", "Rpm");
        var input = ApiFixtureRoot.WriteShellPayload(root);

        var artifacts = await new RpmBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.rpmapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API .rpm smoke test fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.Rpm]
                }
            ]
        }, TestContext.Current.CancellationToken);

        var rpm = Assert.Single(artifacts).Path;
        var expected = Path.Combine(root, "artifacts",
            "api-fixture-1.0.0-1.x86_64.rpm");
        Assert.Equal(expected, rpm);
        Assert.True(File.Exists(rpm));

        var magic = new byte[4];
        await using var stream = File.OpenRead(rpm);
        Assert.Equal(4, await stream.ReadAsync(magic, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0xED, 0xAB, 0xEE, 0xDB }, magic);
    }
}

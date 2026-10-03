using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;

// 直调后端 API 冒烟：AlpineApkBundler 必须产出真实 .apk（拼接 gzip 流，1f 8b 08 魔数）
public static class AlpineApkApiTests
{
    [Fact]
    public static async Task AlpineApkBundler_ProducesApk()
    {
        var root = ApiFixtureRoot.For("APK_API_FIXTURE_OUTPUT", "AlpineApk");
        var input = ApiFixtureRoot.WriteShellPayload(root);

        var artifacts = await new AlpineApkBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.alpineapkapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API .apk smoke test fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "linux-musl-x64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.AlpineApk]
                }
            ]
        }, TestContext.Current.CancellationToken);

        var apk = Assert.Single(artifacts).Path;
        var expected = Path.Combine(root, "artifacts", "linux-musl-x64", "apk",
            "api-fixture-1.0.0-r0.apk");
        Assert.Equal(expected, apk);
        Assert.True(File.Exists(apk));

        var magic = new byte[3];
        await using var stream = File.OpenRead(apk);
        Assert.Equal(3, await stream.ReadAsync(magic, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0x1f, 0x8b, 0x08 }, magic);
    }
}

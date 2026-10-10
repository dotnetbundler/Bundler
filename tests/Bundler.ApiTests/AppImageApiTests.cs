using DotNet.Bundler;
using DotNet.Bundler.AppImage;

// 直调后端 API 冒烟：AppImageBundler 必须驱动内嵌 appimagetool 产出真实 .AppImage ELF
public static class AppImageApiTests
{
    [Fact]
    public static async Task AppImageBundler_ProducesAppImage()
    {
        Assert.SkipUnless(TestPlatform.IsLinux, "AppImage requires a Linux host.");

        var root = ApiFixtureRoot.For("APPIMAGE_API_FIXTURE_OUTPUT", "AppImage");
        var input = ApiFixtureRoot.WriteShellPayload(root);

        var artifacts = await new AppImageBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.appimageapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API .AppImage smoke test fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.AppImage]
                }
            ]
        }, TestContext.Current.CancellationToken);

        var appImage = Assert.Single(artifacts).Path;
        var expected = Path.Combine(root, "artifacts",
            "api-fixture-1.0.0-x86_64.AppImage");
        Assert.Equal(expected, appImage);
        Assert.True(File.Exists(appImage));

        var magic = await File.ReadAllBytesAsync(appImage, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, magic.Take(4).ToArray());
        Assert.True(File.Exists(appImage + ".sha256"));
    }
}

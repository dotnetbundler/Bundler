using DotNet.Bundler;
using DotNet.Bundler.Nsis;

// 直调后端 API 冒烟：NsisBundler 必须驱动内嵌 makensis 产出真实 setup.exe
// （musl 宿主跑不了内嵌 glibc makensis——与 Bundler.Tests 的 5 个既有边界失败同一根因，此处显式门禁）
public static class NsisApiTests
{
    [Fact]
    public static async Task NsisBundler_ProducesSetup()
    {
        Assert.SkipUnless(!TestPlatform.IsMusl, "Bundled makensis is glibc-linked; musl hosts cannot run it.");

        var root = ApiFixtureRoot.For("NSIS_API_FIXTURE_OUTPUT", "Nsis");
        var cache = Environment.GetEnvironmentVariable("NSIS_API_FIXTURE_CACHE") ??
            Path.Combine(Path.GetTempPath(), "DotNet.Bundler.ApiTests", "tool-cache");
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"), "package-api-fixture", TestContext.Current.CancellationToken);

        var request = new BundleConfiguration
        {
            ProductName = "NSIS API Package Fixture",
            Identifier = "com.dotnetbundler.nsisapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };

        var artifacts = await new NsisBundler(
            options: new NsisBundlerOptions { ToolCacheDirectory = cache })
            .BuildAsync(request, TestContext.Current.CancellationToken);

        var setup = Assert.Single(artifacts).Path;
        Assert.True(File.Exists(setup));
    }
}

using DotNet.Bundler;
using DotNet.Bundler.Wix;

// 直调后端 API 冒烟：WixBundler 必须产出真实 .msi（工具缓存目录由环境变量或共享临时目录供给）
public static class MsiApiTests
{
    [Fact]
    public static async Task WixBundler_ProducesMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "Msi requires a Windows host.");

        var root = ApiFixtureRoot.For("MSI_API_FIXTURE_OUTPUT", "Msi");
        var cache = Environment.GetEnvironmentVariable("MSI_API_FIXTURE_CACHE") ??
            Path.Combine(Path.GetTempPath(), "DotNet.Bundler.ApiTests", "tool-cache");
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"), "package-api-fixture", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "example.demo"), "package-resource", TestContext.Current.CancellationToken);

        var request = new BundleConfiguration
        {
            ProductName = "MSI API Package Fixture",
            Identifier = "com.dotnetbundler.msiapifixture",
            Version = "1.0.0",
            Publisher = "Bundler Tests",
            OutputDirectory = Path.Combine(root, "artifacts"),
            FileAssociations = [new BundleFileAssociationConfiguration { Extensions = ["demo"], Name = "API demo" }],
            UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["msi-api-demo"], Name = "API link" }],
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture.exe",
                    Formats = [PackageFormat.Msi]
                }
            ]
        };

        var artifacts = await new WixBundler(
            new WixBundleConfiguration
            {
                StartMenuShortcut = true, DesktopShortcut = true,
                InstallDirectorySelection = true, AddToPath = true, UninstallShortcut = true,
                Languages = ["en-US"], AllowDowngrades = false
            },
            new WixBundlerOptions { ToolCacheDirectory = cache })
            .BuildAsync(request, TestContext.Current.CancellationToken);

        var msi = Assert.Single(artifacts).Path;
        Assert.Equal(PackageFormat.Msi, artifacts[0].Format);
        Assert.True(File.Exists(msi));
    }
}

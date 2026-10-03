using DotNet.Bundler;
using DotNet.Bundler.MacPkg;

// 直调后端 API 冒烟：MacPkgBundler 必须产出 .pkg 且同时留下中间 .app
public static class MacPkgApiTests
{
    [Fact]
    public static async Task MacPkgBundler_ProducesPkg()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "Pkg requires a macOS host.");

        var root = ApiFixtureRoot.For("MACPKG_API_FIXTURE_OUTPUT", "MacPkg");
        var input = await ApiFixtureRoot.WriteMachOPayloadAsync(root);

        var request = new BundleConfiguration
        {
            ProductName = "PKG API Package Fixture",
            Identifier = "com.dotnetbundler.macpkgapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "osx-arm64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture",
                    Formats = [PackageFormat.Pkg]
                }
            ]
        };

        var artifacts = await new MacPkgBundler(
            pkgConfiguration: new MacPkgBundleConfiguration
            {
                Identifier = "com.dotnetbundler.macpkgapifixture.pkg"
            }).BuildAsync(request, TestContext.Current.CancellationToken);

        var pkg = Assert.Single(artifacts, a => a.Format == PackageFormat.Pkg).Path;
        Assert.EndsWith(".pkg", pkg);
        Assert.True(File.Exists(pkg));
        Assert.Contains(artifacts, a => a.Format == PackageFormat.App);
    }
}

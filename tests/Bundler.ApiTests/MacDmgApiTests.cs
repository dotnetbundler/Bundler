using DotNet.Bundler;
using DotNet.Bundler.MacDmg;

// 直调后端 API 冒烟：MacDmgBundler 必须产出 .dmg 且同时留下中间 .app
public static class MacDmgApiTests
{
    [Fact]
    public static async Task MacDmgBundler_ProducesDmg()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "Dmg requires a macOS host.");

        var root = ApiFixtureRoot.For("MACDMG_API_FIXTURE_OUTPUT", "MacDmg");
        var input = await ApiFixtureRoot.WriteMachOPayloadAsync(root);

        var request = new BundleConfiguration
        {
            ProductName = "DMG API Package Fixture",
            Identifier = "com.dotnetbundler.macdmgapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "osx-arm64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture",
                    Formats = [PackageFormat.Dmg]
                }
            ]
        };

        var artifacts = await new MacDmgBundler(
            dmgConfiguration: new MacDmgBundleConfiguration
            {
                VolumeName = "DMG API Fixture",
                SkipWindowLayout = true
            }).BuildAsync(request, TestContext.Current.CancellationToken);

        var dmg = Assert.Single(artifacts, a => a.Format == PackageFormat.Dmg).Path;
        Assert.EndsWith(".dmg", dmg);
        Assert.True(File.Exists(dmg));
        Assert.Contains(artifacts, a => a.Format == PackageFormat.App);
    }
}

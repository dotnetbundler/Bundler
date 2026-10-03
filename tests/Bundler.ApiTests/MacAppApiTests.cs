using DotNet.Bundler;
using DotNet.Bundler.MacApp;

// 直调后端 API 冒烟：MacAppBundler 必须产出 .app 且文件关联/协议/合并 plist 全落进 Info.plist
public static class MacAppApiTests
{
    [Fact]
    public static async Task MacAppBundler_ProducesAppBundle()
    {
        var root = ApiFixtureRoot.For("MACAPP_API_FIXTURE_OUTPUT", "MacApp");
        var input = await ApiFixtureRoot.WriteMachOPayloadAsync(root);

        var request = new BundleConfiguration
        {
            ProductName = "Mac API Package Fixture",
            Identifier = "com.dotnetbundler.macapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "osx-arm64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture",
                    Formats = [PackageFormat.App]
                }
            ],
            FileAssociations = [new BundleFileAssociationConfiguration
            {
                Extensions = ["apifix"], Name = "Api Fixture Document", MimeType = "application/x-apifix"
            }],
            UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["apifix"], Name = "Api Fixture Link" }]
        };

        var settings = new MacAppBundleConfiguration
        {
            ExceptionDomain = "bundler.invalid",
            DocumentTypes = [new MacAppDocumentTypeConfiguration
            {
                Extensions = ["apifix"], Rank = MacAppHandlerRank.Owner,
                ExportedTypeIdentifier = "com.dotnetbundler.apifix", ExportedTypeConformsTo = ["public.data"]
            }],
            InfoPlistXml = "<dict><key>ApiMergedKey</key><string>merged</string></dict>"
        };

        var artifacts = await new MacAppBundler(settings).BuildAsync(request, TestContext.Current.CancellationToken);
        var app = Assert.Single(artifacts).Path;
        var plistPath = Path.Combine(app, "Contents", "Info.plist");
        Assert.True(File.Exists(plistPath));

        var plistXml = await File.ReadAllTextAsync(plistPath, TestContext.Current.CancellationToken);
        foreach (var requiredKey in new[]
                 {
                     "CFBundleDocumentTypes", "CFBundleURLTypes", "UTExportedTypeDeclarations",
                     "NSAppTransportSecurity", "ApiMergedKey"
                 })
        {
            Assert.Contains($"<key>{requiredKey}</key>", plistXml);
        }
    }
}

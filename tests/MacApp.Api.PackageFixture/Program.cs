using DotNet.Bundler;
using DotNet.Bundler.MacApp;

if (args.Length != 1)
{
    throw new ArgumentException(
        $"Expected an output directory; received {args.Length}: {string.Join(" | ", args)}");
}

var output = Path.GetFullPath(args[0]);
var input = Path.Combine(output, "publish");
Directory.CreateDirectory(input);
// A minimal Mach-O header is enough: the backend checks the magic bytes, not a runnable binary.
var executable = new byte[64];
executable[0] = 0xCF; executable[1] = 0xFA; executable[2] = 0xED; executable[3] = 0xFE;
// arm64 cputype (0x0100000C, little-endian) so the architecture check accepts it.
executable[4] = 0x0C; executable[5] = 0x00; executable[6] = 0x00; executable[7] = 0x01;
await File.WriteAllBytesAsync(Path.Combine(input, "ApiFixture"), executable);
await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.dll"), "package-api-fixture");

var request = new BundleConfiguration
{
    ProductName = "Mac API Package Fixture",
    Identifier = "com.dotnetbundler.macapifixture",
    Version = "1.0.0",
    OutputDirectory = Path.Combine(output, "artifacts"),
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

var artifacts = await new MacAppBundler(settings).BuildAsync(request);
if (artifacts.Count != 1 ||
    !File.Exists(Path.Combine(artifacts[0].Path, "Contents", "Info.plist")))
{
    throw new InvalidOperationException("The package-based standalone API did not create an .app bundle.");
}

var plistXml = await File.ReadAllTextAsync(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
foreach (var requiredKey in new[]
         {
             "CFBundleDocumentTypes", "CFBundleURLTypes", "UTExportedTypeDeclarations",
             "NSAppTransportSecurity", "ApiMergedKey"
         })
{
    if (!plistXml.Contains($"<key>{requiredKey}</key>", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The standalone API build missed the MAC-APP-2 key {requiredKey}.");
    }
}

Console.WriteLine($"Created {artifacts[0].Path}");

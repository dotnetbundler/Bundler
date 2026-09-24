using DotNet.Bundler;
using DotNet.Bundler.Wix;

if (args.Length != 2)
{
    throw new ArgumentException($"Expected output and tool-cache directories; received {args.Length}.");
}

var output = Path.GetFullPath(args[0]);
var cache = Path.GetFullPath(args[1]);
var input = Path.Combine(output, "publish");
Directory.CreateDirectory(input);
await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"), "package-api-fixture");
await File.WriteAllTextAsync(Path.Combine(input, "example.demo"), "package-resource");

var request = new BundleConfiguration
{
    ProductName = "MSI API Package Fixture",
    Identifier = "com.dotnetbundler.msiapifixture",
    Version = "1.0.0",
    Publisher = "Bundler Tests",
    OutputDirectory = Path.Combine(output, "artifacts"),
    FileAssociations = [new BundleFileAssociationConfiguration { Extensions = ["demo"], Name = "API demo" }],
    UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["msi-api-demo"], Name = "API link" }],
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = input,
            MainExecutable = "ApiFixture.exe",
            Formats = [PackageFormat.Msi]
        }
    ]
};

var artifacts = await new WixBundler(
    new WixBundleConfiguration { StartMenuShortcut = true, DesktopShortcut = true },
    new WixBundlerOptions { ToolCacheDirectory = cache })
    .BuildAsync(request);
if (artifacts.Count != 1 || artifacts[0].Format != PackageFormat.Msi ||
    !File.Exists(artifacts[0].Path))
{
    throw new InvalidOperationException("The package-based standalone MSI API did not create an installer.");
}

Console.WriteLine($"Created {artifacts[0].Path}");

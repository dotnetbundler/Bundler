using DotNet.Bundler;
using DotNet.Bundler.Wix;

if (args.Length is not (2 or 6))
{
    throw new ArgumentException($"Expected output/cache or output/cache/target/app-version/MSI-version/downgrade arguments; received {args.Length}.");
}

var output = Path.GetFullPath(args[0]);
var cache = Path.GetFullPath(args[1]);
var target = args.Length == 6 ? args[2] : "win-x64";
var appVersion = args.Length == 6 ? args[3] : "1.0.0";
var msiVersion = args.Length == 6 ? args[4] : null;
var allowDowngrades = args.Length == 6 && bool.Parse(args[5]);
var input = Path.Combine(output, "publish");
Directory.CreateDirectory(input);
await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"), "package-api-fixture");
await File.WriteAllTextAsync(Path.Combine(input, "example.demo"), "package-resource");

var request = new BundleConfiguration
{
    ProductName = "MSI API Package Fixture",
    Identifier = "com.dotnetbundler.msiapifixture",
    Version = appVersion,
    Publisher = "Bundler Tests",
    OutputDirectory = Path.Combine(output, "artifacts"),
    FileAssociations = [new BundleFileAssociationConfiguration { Extensions = ["demo"], Name = "API demo" }],
    UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["msi-api-demo"], Name = "API link" }],
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = target,
            InputDirectory = input,
            MainExecutable = "ApiFixture.exe",
            Formats = [PackageFormat.Msi]
        }
    ]
};

var artifacts = await new WixBundler(
    new WixBundleConfiguration { StartMenuShortcut = true, DesktopShortcut = true,
        MsiVersion = msiVersion, AllowDowngrades = allowDowngrades },
    new WixBundlerOptions { ToolCacheDirectory = cache })
    .BuildAsync(request);
if (artifacts.Count != 1 || artifacts[0].Format != PackageFormat.Msi ||
    !File.Exists(artifacts[0].Path))
{
    throw new InvalidOperationException("The package-based standalone MSI API did not create an installer.");
}

Console.WriteLine($"Created {artifacts[0].Path}");

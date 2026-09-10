using Bundler.Core.Configuration;
using Bundler.Core.Models;
using DotNet.Bundler.Nsis;

if (args.Length != 2)
{
    throw new ArgumentException(
        $"Expected output and tool-cache directories; received {args.Length}: {string.Join(" | ", args)}");
}

var output = Path.GetFullPath(args[0]);
var cache = Path.GetFullPath(args[1]);
var input = Path.Combine(output, "publish");
Directory.CreateDirectory(input);
await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"), "package-api-fixture");

var request = new BundleConfiguration
{
    ProductName = "NSIS API Package Fixture",
    Identifier = "com.dotnetbundler.nsisapifixture",
    Version = "1.0.0",
    OutputDirectory = Path.Combine(output, "artifacts"),
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
    .BuildAsync(request);
if (artifacts.Count != 1 || !File.Exists(artifacts[0].Path))
{
    throw new InvalidOperationException("The package-based standalone API did not create an installer.");
}

Console.WriteLine($"Created {artifacts[0].Path}");

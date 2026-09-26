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
    ]
};

var artifacts = await new MacAppBundler().BuildAsync(request);
if (artifacts.Count != 1 ||
    !File.Exists(Path.Combine(artifacts[0].Path, "Contents", "Info.plist")))
{
    throw new InvalidOperationException("The package-based standalone API did not create an .app bundle.");
}

Console.WriteLine($"Created {artifacts[0].Path}");

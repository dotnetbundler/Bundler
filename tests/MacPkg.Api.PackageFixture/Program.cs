using DotNet.Bundler;
using DotNet.Bundler.MacPkg;

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
    ProductName = "PKG API Package Fixture",
    Identifier = "com.dotnetbundler.macpkgapifixture",
    Version = "1.0.0",
    OutputDirectory = Path.Combine(output, "artifacts"),
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
    }).BuildAsync(request);

var pkg = artifacts.SingleOrDefault(artifact => artifact.Format == PackageFormat.Pkg);
if (pkg is null || !File.Exists(pkg.Path) || !pkg.Path.EndsWith(".pkg", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The package-based standalone API did not create a .pkg installer.");
}
if (!artifacts.Any(artifact => artifact.Format == PackageFormat.App))
{
    throw new InvalidOperationException("A .pkg request must also produce the intermediate .app artifact.");
}

Console.WriteLine($"Created {pkg.Path}");

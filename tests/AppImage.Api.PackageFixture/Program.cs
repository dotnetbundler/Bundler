using DotNet.Bundler;
using DotNet.Bundler.AppImage;

// Direct-API smoke test for the NuGet-packaged AppImage bundler: a fake publish
// directory is written to disk, then AppImageBundler must drive the embedded
// appimagetool into a real .AppImage ELF.
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP: AppImage requires a Linux host.");
    return;
}
var root = Environment.GetEnvironmentVariable("APPIMAGE_API_FIXTURE_OUTPUT") ??
    Path.Combine(Path.GetTempPath(), "AppImage.Api.PackageFixture");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");

var artifacts = await new AppImageBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "Api Fixture",
    Identifier = "com.dotnetbundler.appimageapifixture",
    Publisher = "DotNet.Bundler Tests",
    Version = "1.0.0",
    Description = "Direct-API .AppImage smoke test fixture.",
    OutputDirectory = Path.Combine(root, "artifacts"),
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "linux-x64",
            InputDirectory = input,
            MainExecutable = "FixtureApp",
            Formats = [PackageFormat.AppImage]
        }
    ]
});

var appImage = artifacts.Single().Path;
var expected = Path.Combine(root, "artifacts", "linux-x64", "appimage",
    "api-fixture_1.0.0_amd64.AppImage");
if (appImage != expected || !File.Exists(appImage))
{
    throw new InvalidOperationException($"Unexpected .AppImage artifact path: {appImage}");
}

var magic = File.ReadAllBytes(appImage).Take(4).ToArray();
if (magic[0] != 0x7F || magic[1] != 'E' || magic[2] != 'L' || magic[3] != 'F')
{
    throw new InvalidOperationException("The artifact is not an ELF file.");
}
if (!File.Exists(appImage + ".sha256"))
{
    throw new InvalidOperationException("The sha256 sidecar is missing.");
}
Console.WriteLine($"OK: {appImage}");

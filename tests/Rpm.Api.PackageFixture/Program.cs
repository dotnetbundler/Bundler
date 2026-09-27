using DotNet.Bundler;
using DotNet.Bundler.Rpm;

// Direct-API smoke test for the NuGet-packaged Rpm bundler: a fake publish
// directory is written to disk, then RpmBundler must emit a real .rpm archive.
var root = Environment.GetEnvironmentVariable("RPM_API_FIXTURE_OUTPUT") ??
    Path.Combine(Path.GetTempPath(), "Rpm.Api.PackageFixture");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");

var artifacts = await new RpmBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "Api Fixture",
    Identifier = "com.dotnetbundler.rpmapifixture",
    Publisher = "DotNet.Bundler Tests",
    Version = "1.0.0",
    Description = "Direct-API .rpm smoke test fixture.",
    OutputDirectory = Path.Combine(root, "artifacts"),
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "linux-x64",
            InputDirectory = input,
            MainExecutable = "FixtureApp",
            Formats = [PackageFormat.Rpm]
        }
    ]
});

var rpm = artifacts.Single().Path;
var expected = Path.Combine(root, "artifacts", "linux-x64", "rpm",
    "api-fixture-1.0.0-1.x86_64.rpm");
if (rpm != expected || !File.Exists(rpm))
{
    throw new InvalidOperationException($"Unexpected .rpm artifact path: {rpm}");
}

using var stream = File.OpenRead(rpm);
var magic = new byte[4];
if (stream.Read(magic, 0, 4) != 4 ||
    magic[0] != 0xED || magic[1] != 0xAB || magic[2] != 0xEE || magic[3] != 0xDB)
{
    throw new InvalidOperationException("The artifact does not have the rpm lead magic.");
}

Console.WriteLine($"Rpm.Api.PackageFixture produced {rpm}");

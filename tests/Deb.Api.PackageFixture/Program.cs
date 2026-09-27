using DotNet.Bundler;
using DotNet.Bundler.Deb;

// Direct-API smoke test for the NuGet-packaged Deb bundler: a fake publish
// directory is written to disk, then DebBundler must emit a real .deb archive.
var root = Environment.GetEnvironmentVariable("DEB_API_FIXTURE_OUTPUT") ??
    Path.Combine(Path.GetTempPath(), "Deb.Api.PackageFixture");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");

var artifacts = await new DebBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "Api Fixture",
    Identifier = "com.dotnetbundler.debapifixture",
    Publisher = "DotNet.Bundler Tests",
    Version = "1.0.0",
    Description = "Direct-API .deb smoke test fixture.",
    OutputDirectory = Path.Combine(root, "artifacts"),
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "linux-x64",
            InputDirectory = input,
            MainExecutable = "FixtureApp",
            Formats = [PackageFormat.Deb]
        }
    ]
});

var deb = artifacts.Single().Path;
var expected = Path.Combine(root, "artifacts", "linux-x64", "deb",
    "api-fixture_1.0.0-1_amd64.deb");
if (deb != expected || !File.Exists(deb))
{
    throw new InvalidOperationException($"Unexpected .deb artifact path: {deb}");
}

using var stream = File.OpenRead(deb);
var magic = new byte[8];
if (stream.Read(magic, 0, 8) != 8 ||
    System.Text.Encoding.ASCII.GetString(magic) != "!<arch>\n")
{
    throw new InvalidOperationException("The artifact is not an ar archive.");
}

Console.WriteLine($"Deb.Api.PackageFixture produced {deb}");

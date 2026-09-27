using DotNet.Bundler;
using DotNet.Bundler.Archive;
using System.IO.Compression;

// Direct-API smoke test for the NuGet-packaged Archive bundler: a fake publish
// directory is written to disk, then ArchiveBundler must produce a real .zip
// and .tar.gz with the single top-level directory layout.
var root = Environment.GetEnvironmentVariable("ARCHIVE_API_FIXTURE_OUTPUT") ??
    Path.Combine(Path.GetTempPath(), "Archive.Api.PackageFixture");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");

var artifacts = await new ArchiveBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "Api Fixture",
    Identifier = "com.dotnetbundler.archiveapifixture",
    Publisher = "DotNet.Bundler Tests",
    Version = "1.0.0",
    Description = "Direct-API archive smoke test fixture.",
    OutputDirectory = Path.Combine(root, "artifacts"),
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "linux-x64",
            InputDirectory = input,
            MainExecutable = "FixtureApp",
            Formats = [PackageFormat.Zip, PackageFormat.TarGz]
        }
    ]
});

var stem = "api-fixture-1.0.0-linux-x64";
var zip = artifacts.Single(a => a.Path.EndsWith(".zip", StringComparison.Ordinal));
var tarGz = artifacts.Single(a => a.Path.EndsWith(".tar.gz", StringComparison.Ordinal));
if (!zip.Path.Contains(stem, StringComparison.Ordinal) ||
    !tarGz.Path.Contains(stem, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"unexpected artifact names: {zip.Path}, {tarGz.Path}");
}
if (!File.Exists(zip.Path + ".sha256") || !File.Exists(tarGz.Path + ".sha256"))
{
    throw new InvalidOperationException("sha256 sidecar missing.");
}
using (var archive = ZipFile.OpenRead(zip.Path))
{
    if (archive.GetEntry(stem + "/FixtureApp") is null)
    {
        throw new InvalidOperationException("zip is missing the payload under the top-level directory.");
    }
}
Console.WriteLine($"Archive.Api.PackageFixture OK: {Path.GetFileName(zip.Path)}, {Path.GetFileName(tarGz.Path)}");

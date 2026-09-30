using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;

// Direct-API smoke test for the NuGet-packaged AlpineApk bundler: a fake
// publish directory is written to disk, then AlpineApkBundler must emit a
// real .apk archive (concatenated gzip streams starting with 1f 8b).
var root = Environment.GetEnvironmentVariable("APK_API_FIXTURE_OUTPUT") ??
    Path.Combine(Path.GetTempPath(), "AlpineApk.Api.PackageFixture");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");

var artifacts = await new AlpineApkBundler().BuildAsync(new BundleConfiguration
{
    ProductName = "Api Fixture",
    Identifier = "com.dotnetbundler.alpineapkapifixture",
    Publisher = "DotNet.Bundler Tests",
    Version = "1.0.0",
    Description = "Direct-API .apk smoke test fixture.",
    OutputDirectory = Path.Combine(root, "artifacts"),
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "linux-musl-x64",
            InputDirectory = input,
            MainExecutable = "FixtureApp",
            Formats = [PackageFormat.AlpineApk]
        }
    ]
});

var apk = artifacts.Single().Path;
var expected = Path.Combine(root, "artifacts", "linux-musl-x64", "apk",
    "api-fixture-1.0.0-r0.apk");
if (apk != expected || !File.Exists(apk))
{
    throw new InvalidOperationException($"Unexpected .apk artifact path: {apk}");
}

var magic = new byte[3];
using (var stream = File.OpenRead(apk))
{
    if (stream.Read(magic, 0, 3) != 3 ||
        magic[0] != 0x1f || magic[1] != 0x8b || magic[2] != 0x08)
    {
        throw new InvalidOperationException("The artifact is not a gzip stream.");
    }
}

Console.WriteLine($"AlpineApk.Api.PackageFixture produced {apk}");
Console.WriteLine($"BundlerAlpineApkIntegrationFixture:{string.Join(",", args)}");

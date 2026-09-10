using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;
using Bundler.Core.Validation;

var tests = new (string Name, Action Test)[]
{
    ("Parses supported desktop RIDs", ParsesSupportedDesktopRids),
    ("Rejects incompatible formats", RejectsIncompatibleFormats),
    ("Adds app dependency before DMG", AddsAppDependencyBeforeDmg),
    ("Rejects executable paths outside input", RejectsExecutablePathEscape)
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ParsesSupportedDesktopRids()
{
    string[] rids = ["win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"];
    Assert(rids.All(rid => BundleTarget.TryParse(rid, out _)), "One or more supported RIDs failed to parse.");
    Assert(!BundleTarget.TryParse("android-arm64", out _), "A mobile RID was accepted.");
}

static void RejectsIncompatibleFormats()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "linux-x64",
        InputDirectory = "unused",
        Formats = [PackageFormat.Msi]
    });

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Message.Contains("not supported", StringComparison.Ordinal)),
        "Linux MSI should have failed validation.");
}

static void AddsAppDependencyBeforeDmg()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "osx-arm64",
        InputDirectory = "unused",
        Formats = [PackageFormat.Dmg]
    });

    var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
    Assert(plan.Items.Count == 2, "DMG plan should contain an app and a DMG step.");
    Assert(plan.Items[0].Format == PackageFormat.App && plan.Items[0].Intermediate,
        "The intermediate app step must be first.");
    Assert(plan.Items[1].Format == PackageFormat.Dmg && !plan.Items[1].Intermediate,
        "The requested DMG step must be last.");
}

static void RejectsExecutablePathEscape()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "win-x64",
        InputDirectory = "unused",
        MainExecutable = "../Other.exe",
        Formats = [PackageFormat.Nsis]
    });

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Path.EndsWith("mainExecutable", StringComparison.Ordinal)),
        "An executable outside inputDirectory should have failed validation.");
}

static BundleConfiguration ValidConfiguration(BundleTargetConfiguration target) => new()
{
    ProductName = "ExampleApp",
    Identifier = "com.example.app",
    Version = "1.0.0",
    OutputDirectory = "artifacts",
    Targets = [target]
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

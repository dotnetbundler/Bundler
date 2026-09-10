using Bundler.Core.Configuration;
using Bundler.Core.Backends;
using Bundler.Core.Backends.Windows;
using Bundler.Core.Models;
using Bundler.Core.Planning;
using Bundler.Core.Tools;
using Bundler.Core.Templates;
using Bundler.Core.Validation;

var tests = new (string Name, Func<Task> Test)[]
{
    ("Parses supported desktop RIDs", () => RunSync(ParsesSupportedDesktopRids)),
    ("Rejects incompatible formats", () => RunSync(RejectsIncompatibleFormats)),
    ("Adds app dependency before DMG", () => RunSync(AddsAppDependencyBeforeDmg)),
    ("Rejects executable paths outside input", () => RunSync(RejectsExecutablePathEscape)),
    ("Verifies and extracts bundled NSIS", VerifiesAndExtractsBundledNsis),
    ("Writes a valid Windows uninstall command", () => RunSync(WritesValidWindowsUninstallCommand)),
    ("Rejects unknown template variables", () => RunSync(RejectsUnknownTemplateVariables)),
    ("Runs backends through the common pipeline", RunsBackendsThroughCommonPipeline)
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
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

static async Task VerifiesAndExtractsBundledNsis()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var repositoryRoot = RepositoryRoot();
    var archive = Path.Combine(repositoryRoot, "third_party", "nsis", "nsis-3.12.zip");
    var cache = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var compiler = await NsisToolResolver.ResolveAsync(archive, cache);
        Assert(File.Exists(compiler), "The verified NSIS archive did not produce makensis.exe.");
    }
    finally
    {
        if (Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}

static void WritesValidWindowsUninstallCommand()
{
    var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(input);
    try
    {
        File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "test");
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = input,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            input,
            "ExampleApp.exe",
            "output",
            false);

        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(template, configuration, item, "setup.exe", "ExampleApp");
        Assert(script.Contains("\"UninstallString\" '\"$INSTDIR\\Uninstall.exe\"'", StringComparison.Ordinal),
            "UninstallString must contain ordinary quotes around the executable path.");
        Assert(!script.Contains("'$\"$INSTDIR", StringComparison.Ordinal),
            "UninstallString must not write NSIS escape markers into the registry.");
    }
    finally
    {
        Directory.Delete(input, recursive: true);
    }
}

static void RejectsUnknownTemplateVariables()
{
    try
    {
        TemplateRenderer.Render("{{known}} {{missing}}", new Dictionary<string, string> { ["known"] = "value" });
        throw new InvalidOperationException("An unknown template variable should have failed rendering.");
    }
    catch (InvalidDataException exception)
    {
        Assert(exception.Message.Contains("missing", StringComparison.Ordinal),
            "The template error should identify the missing variable.");
    }
}

static async Task RunsBackendsThroughCommonPipeline()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    var output = Path.Combine(root, "artifacts");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "test");

    var backend = new RecordingBackend();
    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            OutputDirectory = output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };

        var artifacts = await new BundlePipeline([backend]).BuildAsync(configuration);
        Assert(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "The common pipeline should return an existing backend artifact.");
        Assert(backend.WorkDirectory is not null && !Directory.Exists(backend.WorkDirectory),
            "The common pipeline should clean its backend work directory.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

static Task RunSync(Action action)
{
    action();
    return Task.CompletedTask;
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

file sealed class RecordingBackend : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Nsis;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;
    public string? WorkDirectory { get; private set; }

    public async Task<BundleArtifact> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default)
    {
        WorkDirectory = context.WorkDirectory;
        var path = Path.Combine(context.Item.OutputDirectory, "recording-installer.exe");
        await File.WriteAllTextAsync(path, "artifact", cancellationToken);
        return new BundleArtifact(Format, context.Item.Target.RuntimeIdentifier, path);
    }
}

using DotNet.Bundler.Cli;

namespace Bundler.UnitTests.Cli;

/// <summary>
/// bundler.json 解析与 CLI 覆盖合并面。全部经 --config 指到临时文件，
/// 不依赖 cwd 发现（发现路径由 CliProgram/init 侧覆盖）。
/// </summary>
public sealed class CliConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bundler-ut-" + Guid.NewGuid().ToString("N"));

    public CliConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "bundler.json");
        File.WriteAllText(path, json);
        return path;
    }

    private CliResolvedConfiguration Resolve(string json, params string[] extraArgs)
    {
        var config = WriteConfig(json);
        var args = new List<string> { "bundle", "--config", config };
        args.AddRange(extraArgs);
        return CliConfig.Resolve(CliArguments.Parse(args.ToArray()));
    }

    [Fact]
    public void Resolve_PackageVersion_IsVersionAlias()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "packageVersion": "2.5.0",
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in",
                             "formats": ["deb"] } ] }
            """);
        Assert.Equal("2.5.0", resolved.Bundle.Version);
    }

    [Fact]
    public void Resolve_PackageVersionAndVersion_Rejected()
    {
        Assert.Throws<CliUsageException>(() => Resolve("""
            { "version": "1.0", "packageVersion": "2.0" }
            """));
    }

    [Fact]
    public void Resolve_UnknownTopLevelKey_RejectedWithSuggestion()
    {
        var ex = Assert.Throws<CliUsageException>(() => Resolve("""
            { "productName": "X", "Productname": "oops" }
            """));
        Assert.Contains("did you mean 'productName'", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownTargetKey_Rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Resolve("""
            { "productName": "X", "targets": [ { "target": "linux-x86_64",
              "inputDirectory": "in", "formats": ["deb"], "arch": "x64" } ] }
            """));
        Assert.Contains("targets[0].arch", ex.Message);
    }

    [Fact]
    public void Resolve_CliOverride_BeatsFileValue()
    {
        var resolved = Resolve("""
            { "productName": "FileName", "identifier": "com.x", "version": "1.0",
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in",
                             "formats": ["deb"] } ] }
            """, "--product-name", "CliName");
        Assert.Equal("CliName", resolved.Bundle.ProductName);
    }

    [Fact]
    public void Resolve_FormatKnob_MergesIntoSection()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "deb": { "section": "utils" },
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in",
                             "formats": ["deb"] } ] }
            """, "--deb.vendor=ACME Corp");
        Assert.Equal("ACME Corp", resolved.Deb?.Vendor);
        Assert.Equal("utils", resolved.Deb?.Section);
    }

    [Fact]
    public void Resolve_SectionPaths_RelativeToConfigDir()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "deb": { "postInstallFile": "scripts/post.sh" },
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in",
                             "formats": ["deb"] } ] }
            """);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "scripts/post.sh")),
            resolved.Deb?.PostInstallFile);
    }

    [Fact]
    public void Resolve_InfersVersionAndIdentifier()
    {
        var resolved = Resolve("""
            { "productName": "X", "targets": [ { "target": "linux-x86_64",
              "inputDirectory": "in", "formats": ["deb"] } ] }
            """);
        Assert.Equal("0.1.0", resolved.Bundle.Version);
        Assert.Equal("dev.bundler.x", resolved.Bundle.Identifier);
        Assert.Contains(resolved.InferredNotes, n => n.StartsWith("version="));
        Assert.Contains(resolved.InferredNotes, n => n.StartsWith("identifier="));
    }

    [Fact]
    public void Resolve_InfersFormatsPerTargetOs()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in" } ] }
            """);
        Assert.Equal(
            [DotNet.Bundler.PackageFormat.Deb, DotNet.Bundler.PackageFormat.Zip],
            resolved.Bundle.Targets[0].Formats);
    }

    [Fact]
    public void Resolve_Strict_MissingTarget_Rejected()
    {
        Assert.Throws<CliUsageException>(() => Resolve("""
            { "productName": "X", "targets": [ { "inputDirectory": "in",
              "formats": ["deb"] } ] }
            """, "--strict"));
    }

    [Fact]
    public void Resolve_MissingFormats_Rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Resolve("""
            { "productName": "X", "targets": [ { "target": "linux-x86_64",
              "inputDirectory": "in" } ] }
            """, "--strict"));
        Assert.Contains("Formats are required", ex.Message);
    }

    [Fact]
    public void Resolve_FormatsAll_ExpandsPerTarget()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "targets": [ { "target": "windows-x86_64", "inputDirectory": "in" } ] }
            """, "--formats", "all");
        var formats = resolved.Bundle.Targets[0].Formats;
        Assert.Contains(DotNet.Bundler.PackageFormat.Nsis, formats);
        Assert.Contains(DotNet.Bundler.PackageFormat.Msi, formats);
        Assert.DoesNotContain(DotNet.Bundler.PackageFormat.Deb, formats);
    }

    [Fact]
    public void Resolve_TargetLevelCli_WritesIntoTargets0()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "targets": [ { "target": "linux-x86_64", "formats": ["deb"] } ] }
            """, "--main-executable", "run.sh");
        Assert.Equal("run.sh", resolved.Bundle.Targets[0].MainExecutable);
    }

    [Fact]
    public void Resolve_MissingConfigFile_Rejected()
    {
        var parsed = CliArguments.Parse(
            ["bundle", "--config", Path.Combine(_dir, "absent.json")]);
        Assert.Throws<CliUsageException>(() => CliConfig.Resolve(parsed));
    }

    [Fact]
    public void Resolve_NoConfigPlusConfig_Rejected()
    {
        var parsed = CliArguments.Parse(
            ["bundle", "--config", WriteConfig("{}"), "--no-config"]);
        Assert.Throws<CliUsageException>(() => CliConfig.Resolve(parsed));
    }

    [Fact]
    public void Resolve_ExplicitNull_TreatedAsAbsent()
    {
        var resolved = Resolve("""
            { "productName": "X", "identifier": "com.x", "version": "1.0",
              "publisher": null,
              "targets": [ { "target": "linux-x86_64", "inputDirectory": "in",
                             "formats": ["deb"] } ] }
            """);
        Assert.Null(resolved.Bundle.Publisher);
    }

    [Fact]
    public void Resolve_MainExecutable_InferredFromSingleExe()
    {
        var input = Path.Combine(_dir, "input");
        Directory.CreateDirectory(input);
        if (OperatingSystem.IsWindows())
        {
            // Windows 宿主只对 windows 目标的 .exe 做推断
            File.WriteAllText(Path.Combine(input, "run.exe"), "");
            var resolved = Resolve($$"""
                { "productName": "X", "identifier": "com.x", "version": "1.0",
                  "targets": [ { "target": "windows-x86_64",
                    "inputDirectory": {{JsonEscape(input)}},
                    "formats": ["nsis"] } ] }
                """);
            Assert.Equal("run.exe", resolved.Bundle.Targets[0].MainExecutable);
        }
        else
        {
            var run = Path.Combine(input, "run");
            File.WriteAllText(run, "#!/bin/sh\n");
            File.SetUnixFileMode(run,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(input, "data.txt"), "");
            var resolved = Resolve($$"""
                { "productName": "X", "identifier": "com.x", "version": "1.0",
                  "targets": [ { "target": "linux-x86_64",
                    "inputDirectory": {{JsonEscape(input)}},
                    "formats": ["deb"] } ] }
                """);
            Assert.Equal("run", resolved.Bundle.Targets[0].MainExecutable);
        }
    }

    private static string JsonEscape(string path) =>
        "\"" + path.Replace("\\", "\\\\") + "\"";
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Cli;

public static class CliProgram
{
    /// <summary>Exit codes: 0 success, 1 backend/IO failure, 2 usage or validation failure.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return RunInner(args, stdout, stderr);
        }
        catch (CliUsageException exception)
        {
            stderr.WriteLine($"bundler: {exception.Message}");
            PrintUsage(stderr);
            return 2;
        }
        catch (BundleValidationException exception)
        {
            stderr.WriteLine("bundler: configuration is invalid:");
            foreach (var issue in exception.Issues)
            {
                stderr.WriteLine($"  {issue.Path}: {issue.Message}");
            }
            return 2;
        }
        catch (Exception exception) when (exception is IOException or JsonException or
            InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException)
        {
            stderr.WriteLine($"bundler: {exception.Message}");
            return 1;
        }
    }

    private static int RunInner(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var parsed = CliArguments.Parse(args);

        if (parsed.ShowVersion)
        {
            stdout.WriteLine(CliVersion());
            return 0;
        }

        if (parsed.Help || parsed.Command.Length == 0)
        {
            PrintUsage(parsed.Help ? stdout : stderr);
            return parsed.Help ? 0 : 2;
        }

        if (parsed.Command is not ("validate" or "plan" or "bundle"))
        {
            throw new CliUsageException($"Unknown command '{parsed.Command}'.");
        }

        var logger = new CliBundleLogger(stderr, parsed.Quiet, parsed.Verbose);
        var resolved = CliConfig.Resolve(parsed);
        var configuration = resolved.Bundle;
        return parsed.Command switch
        {
            "validate" => RunValidate(configuration, parsed, stdout, stderr),
            "plan" => RunPlan(configuration, parsed, stdout),
            "bundle" => RunBundle(configuration, resolved, parsed, stdout, logger),
            _ => 2
        };
    }



    internal static IReadOnlyList<PackageFormat> ParseFormats(string value, string rid)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            throw new CliUsageException("--formats must list at least one format.");
        }

        var parsed = names.Select(ParseFormat).Distinct().ToArray();
        if (parsed.Any(format => format == AllFormatsSentinel))
        {
            if (!BundleTarget.TryParse(rid, out var target) || target is null)
            {
                throw new CliUsageException(
                    $"Unknown rid '{rid}'. Supported: win-x86, win-x64, win-arm64, osx, osx-x64, osx-arm64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64.");
            }
            return Enum.GetValues<PackageFormat>()
                .Where(format => DesktopTargetMatrix.Supports(target, format))
                .ToArray();
        }
        return parsed;
    }

    // 'all' is not a real PackageFormat; represented out-of-band via the sentinel.
    private const PackageFormat AllFormatsSentinel = (PackageFormat)(-1);

    private static PackageFormat ParseFormat(string name) => name.ToLowerInvariant() switch
    {
        "nsis" => PackageFormat.Nsis,
        "msi" => PackageFormat.Msi,
        "app" => PackageFormat.App,
        "dmg" => PackageFormat.Dmg,
        "pkg" => PackageFormat.Pkg,
        "deb" => PackageFormat.Deb,
        "rpm" => PackageFormat.Rpm,
        "appimage" => PackageFormat.AppImage,
        "zip" => PackageFormat.Zip,
        "targz" or "tar.gz" => PackageFormat.TarGz,
        "alpineapk" or "apk" => PackageFormat.AlpineApk,
        "all" => AllFormatsSentinel,
        _ => throw new CliUsageException(
            $"Unknown format '{name}'. Supported: nsis, msi, app, dmg, pkg, deb, rpm, appimage, zip, targz, alpineapk, all.")
    };

    private static int RunValidate(
        BundleConfiguration configuration, CliArguments parsed, TextWriter stdout, TextWriter stderr)
    {
        var issues = BundleConfigurationValidator.Validate(configuration);
        if (parsed.Json)
        {
            WriteJson(stdout, new JsonObject
            {
                ["valid"] = issues.Count == 0,
                ["issues"] = new JsonArray(issues.Select(issue => (JsonNode)new JsonObject
                {
                    ["path"] = issue.Path,
                    ["message"] = issue.Message
                }).ToArray())
            });
        }
        else if (issues.Count == 0)
        {
            stdout.WriteLine("Configuration is valid.");
        }
        else
        {
            foreach (var issue in issues)
            {
                stderr.WriteLine($"  {issue.Path}: {issue.Message}");
            }
        }
        return issues.Count == 0 ? 0 : 2;
    }

    private static int RunPlan(BundleConfiguration configuration, CliArguments parsed, TextWriter stdout)
    {
        var plan = BundlePlanner.Create(configuration);
        if (parsed.Json)
        {
            WriteJson(stdout, new JsonObject
            {
                ["items"] = new JsonArray(plan.Items.Select(item => (JsonNode)new JsonObject
                {
                    ["runtimeIdentifier"] = item.Target.RuntimeIdentifier,
                    ["format"] = FormatName(item.Format),
                    ["inputDirectory"] = item.InputDirectory,
                    ["outputDirectory"] = item.OutputDirectory,
                    ["mainExecutable"] = item.MainExecutable,
                    ["intermediate"] = item.Intermediate
                }).ToArray())
            });
        }
        else
        {
            foreach (var item in plan.Items)
            {
                var suffix = item.Intermediate ? " (intermediate)" : "";
                stdout.WriteLine(
                    $"{item.Target.RuntimeIdentifier} {FormatName(item.Format)} -> {item.OutputDirectory}{suffix}");
            }
        }
        return 0;
    }

    private static int RunBundle(
        BundleConfiguration configuration, CliResolvedConfiguration resolved,
        CliArguments parsed, TextWriter stdout, IBundleLogger logger)
    {
        var formats = configuration.Targets.SelectMany(t => t.Formats).Distinct().ToArray();
        var artifacts = new List<BundleArtifact>();
        foreach (var format in formats)
        {
            var single = SingleFormatConfiguration(configuration, format);
            var produced = FormatDispatcher.BuildAsync(format, single, resolved, logger)
                .GetAwaiter().GetResult();
            artifacts.AddRange(produced);
        }

        if (parsed.Json)
        {
            WriteJson(stdout, new JsonObject
            {
                ["outputDirectory"] = configuration.OutputDirectory,
                ["artifacts"] = new JsonArray(artifacts.Select(artifact => (JsonNode)new JsonObject
                {
                    ["format"] = FormatName(artifact.Format),
                    ["runtimeIdentifier"] = artifact.RuntimeIdentifier,
                    ["path"] = artifact.Path
                }).ToArray())
            });
        }
        else
        {
            foreach (var artifact in artifacts)
            {
                stdout.WriteLine(artifact.Path);
            }
        }
        return 0;
    }

    private static BundleConfiguration SingleFormatConfiguration(
        BundleConfiguration source, PackageFormat format) => new()
        {
            ProductName = source.ProductName,
            Identifier = source.Identifier,
            Version = source.Version,
            Publisher = source.Publisher,
            Description = source.Description,
            Homepage = source.Homepage,
            Copyright = source.Copyright,
            LicenseFile = source.LicenseFile,
            OutputDirectory = source.OutputDirectory,
            Icons = source.Icons,
            Resources = source.Resources,
            FileAssociations = source.FileAssociations,
            UrlProtocols = source.UrlProtocols,
            Targets = source.Targets.Select(target => new BundleTargetConfiguration
            {
                RuntimeIdentifier = target.RuntimeIdentifier,
                InputDirectory = target.InputDirectory,
                MainExecutable = target.MainExecutable,
                SigningFiles = target.SigningFiles,
                Formats = [format]
            }).ToArray()
        };

    private static string FormatName(PackageFormat format) => format switch
    {
        PackageFormat.TarGz => "targz",
        PackageFormat.AlpineApk => "apk",
        _ => format.ToString().ToLowerInvariant()
    };

    private static string CliVersion() =>
        typeof(CliProgram).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliProgram).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static void WriteJson(TextWriter stdout, JsonObject payload) =>
        stdout.WriteLine(payload.ToJsonString());

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  bundler validate|plan|bundle --input-dir <dir> --rid <rid> --formats <csv>");
        writer.WriteLine("      --product-name <name> --identifier <id> --package-version <ver>");
        writer.WriteLine("      [--output-dir <dir>] [--main-executable <name>] [--json] [--quiet|--verbose]");
        writer.WriteLine("  bundler --version | --help");
        writer.WriteLine("Formats: nsis"
#if BUNDLER_HOST_WINDOWS
            + " msi"
#endif
            + " app"
#if BUNDLER_HOST_MACOS
            + " dmg pkg"
#endif
            + " deb rpm"
#if BUNDLER_HOST_LINUX
            + " appimage"
#endif
            + " zip targz alpineapk all");
        writer.WriteLine("RIDs:    win-x86 win-x64 win-arm64 osx osx-x64 osx-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64");
        writer.WriteLine("Exit codes: 0 success, 1 packaging/IO failure, 2 usage or validation failure");
    }
}

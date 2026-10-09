using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.Update;

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

        if (parsed.Command is "update-keygen")
        {
            var keyPath = parsed.RequiredOption("key-file");
            var material = UpdateKeyMaterial.Generate();
            material.Save(keyPath);
            stdout.WriteLine(keyPath);
            stdout.WriteLine(material.PublicPointBase64());
            return 0;
        }

        if (parsed.Command is not ("validate" or "plan" or "bundle"))
        {
            throw new CliUsageException($"Unknown command '{parsed.Command}'.");
        }

        var logger = new CliBundleLogger(stderr, parsed.Quiet, parsed.Verbose);
        var resolved = CliConfig.Resolve(parsed);
        var mergedDirectory = (string?)null;
        try
        {
            var configuration = ApplyInputDirectories(resolved.Bundle, parsed, out mergedDirectory);
            return parsed.Command switch
            {
                "validate" => RunValidate(configuration, resolved, parsed, stdout, stderr, logger),
                "plan" => RunPlan(configuration, parsed, stdout),
                "bundle" => RunBundle(configuration, resolved, parsed, stdout, stderr, logger),
                _ => 2
            };
        }
        finally
        {
            if (mergedDirectory is not null && Directory.Exists(mergedDirectory))
            {
                Directory.Delete(mergedDirectory, recursive: true);
            }
        }
    }

    // Repeated --input-dir feeds one target: a single value wins as before; two or more
    // directories are universal-merged (fat Mach-O + dedup rules) into a temp payload dir.
    private static BundleConfiguration ApplyInputDirectories(
        BundleConfiguration source, CliArguments parsed, out string? mergedDirectory)
    {
        mergedDirectory = null;
        if (parsed.InputDirectories.Count <= 1 || source.Targets.Count == 0)
        {
            return source;
        }
        var dirs = parsed.InputDirectories.Select(Path.GetFullPath).ToArray();
        var input = Path.Combine(Path.GetTempPath(), "bundler-universal-" + Guid.NewGuid().ToString("N"));
        mergedDirectory = input; // assign before Merge so the finally cleanup also covers merge failures
        MacUniversalPayloadMerger.Merge(dirs, input);

        var first = source.Targets[0];
        var targets = source.Targets.ToArray();
        targets[0] = new BundleTargetConfiguration
        {
            RuntimeIdentifier = first.RuntimeIdentifier,
            InputDirectory = input,
            MainExecutable = first.MainExecutable,
            SigningFiles = first.SigningFiles,
            Formats = first.Formats
        };
        return new BundleConfiguration
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
            Update = source.Update,
            Targets = targets
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
        "alpineapk" => PackageFormat.AlpineApk,
        "all" => AllFormatsSentinel,
        _ => throw new CliUsageException(
            $"Unknown format '{name}'. Supported: nsis, msi, app, dmg, pkg, deb, rpm, appimage, zip, targz, alpineapk, all.")
    };

    private static int RunValidate(
        BundleConfiguration configuration, CliResolvedConfiguration resolved,
        CliArguments parsed, TextWriter stdout, TextWriter stderr, IBundleLogger logger)
    {
        var issues = BundleConfigurationValidator.Validate(configuration).ToList();
        // 各格式旋钮一并验证；宿主门禁格式不是配置错误，跳过。
        foreach (var format in configuration.Targets.SelectMany(t => t.Formats).Distinct())
        {
            try
            {
                FormatDispatcher.Create(format, resolved, logger)
                    .Validate(SingleFormatConfiguration(configuration, format));
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (Exception exception) when (exception is not CliUsageException)
            {
                issues.Add(new ValidationIssue(FormatName(format), exception.Message));
            }
        }
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
        CliArguments parsed, TextWriter stdout, TextWriter stderr, IBundleLogger logger)
    {
        var formats = configuration.Targets.SelectMany(t => t.Formats).Distinct().ToArray();
        var artifacts = new List<BundleArtifact>();
        var failures = new List<(PackageFormat Format, string Reason)>();

        // 统一预检：共享配置与各格式旋钮在扇出前一次验全；配置类错误聚合
        // 报完即 rc=2；宿主门禁格式不进预检，留构建趟按逐格式容错处理。
        var validationErrors = new List<(string Path, string Message)>();
        try
        {
            BundlePlanner.Create(configuration);
        }
        catch (BundleValidationException validation)
        {
            foreach (var issue in validation.Issues)
            {
                // rid×format 矩阵不兼容（"X is not supported for Y"）属显式请求的
                // 用法错——归聚合配置错 rc=2；宿主门禁（PNSE）才走逐格式容错 rc=1。
                validationErrors.Add((issue.Path, issue.Message));
            }
        }
        var pending = new List<(PackageFormat Format, BundleConfiguration Single, IFormatBundler? Bundler)>();
        foreach (var format in formats)
        {
            var single = SingleFormatConfiguration(configuration, format);
            try
            {
                var bundler = FormatDispatcher.Create(format, resolved, logger);
                bundler.Validate(single);
                pending.Add((format, single, bundler));
            }
            catch (PlatformNotSupportedException)
            {
                pending.Add((format, single, null));
            }
            catch (Exception exception) when (exception is not CliUsageException)
            {
                validationErrors.Add((FormatName(format), exception.Message));
            }
        }
        if (validationErrors.Count > 0)
        {
            foreach (var (path, message) in validationErrors)
            {
                stderr.WriteLine($"  {path}: {message}");
            }
            return 2;
        }

        foreach (var (format, single, bundler) in pending)
        {
            try
            {
                var produced = (bundler ?? FormatDispatcher.Create(format, resolved, logger))
                    .BuildAsync(single)
                    .GetAwaiter().GetResult();
                artifacts.AddRange(produced);
            }
            catch (Exception exception) when (exception is not BundleValidationException and not CliUsageException)
            {
                // 逐格式独立失败：单格式失败不拖累其余格式，末位汇总非零 rc；
                // 校验拒绝仍是 rc=2 用法层错误，直接向上传播。
                failures.Add((format, exception.Message));
                logger.Log(BundleLogLevel.Warning,
                    $"format '{FormatName(format)}' failed: {exception.Message}");
            }
        }

        var updateArtifacts = configuration.Update is null || artifacts.Count == 0
            ? Array.Empty<string>()
            : UpdateManifestEmitter.EmitAsync(configuration, artifacts)
                .GetAwaiter().GetResult();

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
                }).ToArray()),
                ["updateArtifacts"] = new JsonArray(updateArtifacts.Select(path => (JsonNode)JsonValue.Create(path)).ToArray())
            });
        }
        else
        {
            foreach (var artifact in artifacts)
            {
                stdout.WriteLine(artifact.Path);
            }
            foreach (var path in updateArtifacts)
            {
                stdout.WriteLine(path);
            }
        }

        if (failures.Count > 0)
        {
            stderr.WriteLine($"bundler: {failures.Count} format(s) failed: " +
                string.Join("; ", failures.Select(f => $"{FormatName(f.Format)} ({f.Reason})")));
            return 1;
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
            Update = source.Update,
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
        writer.WriteLine("  bundler validate|plan|bundle --input-dir <dir> [--input-dir <dir>...] --rid <rid> --formats <csv>");
        writer.WriteLine("      (--input-dir repeated merges the dirs into a universal payload, e.g. osx-x64 + osx-arm64)");
        writer.WriteLine("      --product-name <name> --identifier <id> --package-version <ver>");
        writer.WriteLine("      [--output-dir <dir>] [--main-executable <name>] [--json] [--quiet|--verbose]");
        writer.WriteLine("  Paths in --config resolve relative to the config file; paths given on the command line resolve relative to the working directory.");
        writer.WriteLine("  bundler update-keygen --key-file <path>   generate an update signing key; prints key path and public key");
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

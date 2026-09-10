using System.Text.RegularExpressions;
using Bundler.Core.Configuration;
using Bundler.Core.Models;

namespace Bundler.Core.Validation;

public static class BundleConfigurationValidator
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$",
        RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);

    public static IReadOnlyList<ValidationIssue> Validate(
        BundleConfiguration configuration,
        bool checkFileSystem = true)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }
        var issues = new List<ValidationIssue>();

        Required(configuration.ProductName, "productName", issues);
        Required(configuration.Identifier, "identifier", issues);
        Required(configuration.Version, "version", issues);
        Required(configuration.OutputDirectory, "outputDirectory", issues);

        if (!string.IsNullOrWhiteSpace(configuration.Identifier) &&
            !IdentifierPattern.IsMatch(configuration.Identifier))
        {
            issues.Add(new("identifier", "Must be a reverse-DNS identifier such as 'com.example.app'."));
        }

        if (!string.IsNullOrWhiteSpace(configuration.Version) &&
            !VersionPattern.IsMatch(configuration.Version))
        {
            issues.Add(new("version", "Must be a SemVer-like version such as '1.2.3' or '1.2.3-beta.1'."));
        }

        if (configuration.Targets.Count == 0)
        {
            issues.Add(new("targets", "At least one desktop target is required."));
        }

        for (var index = 0; index < configuration.Targets.Count; index++)
        {
            ValidateTarget(configuration, configuration.Targets[index], index, checkFileSystem, issues);
        }

        ValidatePaths(configuration.Icons, "icons", checkFileSystem, issues);
        for (var index = 0; index < configuration.Resources.Count; index++)
        {
            var resource = configuration.Resources[index];
            Required(resource.Source, $"resources[{index}].source", issues);
            Required(resource.TargetPath, $"resources[{index}].targetPath", issues);
            if (!string.IsNullOrWhiteSpace(resource.TargetPath) &&
                (Path.IsPathRooted(resource.TargetPath) ||
                 resource.TargetPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Contains("..", StringComparer.Ordinal)))
            {
                issues.Add(new($"resources[{index}].targetPath", "Must stay inside the installation directory."));
            }

            if (checkFileSystem && !string.IsNullOrWhiteSpace(resource.Source) &&
                !File.Exists(resource.Source) && !Directory.Exists(resource.Source))
            {
                issues.Add(new($"resources[{index}].source", $"Path does not exist: {resource.Source}"));
            }
        }
        ValidateDuplicateOutputs(configuration, issues);
        return issues;
    }

    private static void ValidateTarget(
        BundleConfiguration configuration,
        BundleTargetConfiguration targetConfiguration,
        int index,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        var path = $"targets[{index}]";
        Required(targetConfiguration.InputDirectory, $"{path}.inputDirectory", issues);
        if (!BundleTarget.TryParse(targetConfiguration.RuntimeIdentifier, out var target))
        {
            issues.Add(new($"{path}.runtimeIdentifier", "Supported RIDs: win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, linux-arm64."));
            return;
        }

        if (targetConfiguration.Formats.Count == 0)
        {
            issues.Add(new($"{path}.formats", "At least one package format is required."));
        }

        foreach (var format in targetConfiguration.Formats.Distinct())
        {
            if (!DesktopTargetMatrix.Supports(target!.OperatingSystem, format))
            {
                issues.Add(new($"{path}.formats", $"{format} is not supported for {target.OperatingSystem}."));
            }
        }

        var configuredExecutable = targetConfiguration.MainExecutable;
        if (!string.IsNullOrWhiteSpace(configuredExecutable) &&
            (Path.IsPathRooted(configuredExecutable) ||
             configuredExecutable!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .Contains("..", StringComparer.Ordinal)))
        {
            issues.Add(new($"{path}.mainExecutable", "Must stay inside inputDirectory."));
        }

        if (!checkFileSystem || string.IsNullOrWhiteSpace(targetConfiguration.InputDirectory))
        {
            return;
        }

        if (!Directory.Exists(targetConfiguration.InputDirectory))
        {
            issues.Add(new($"{path}.inputDirectory", $"Directory does not exist: {targetConfiguration.InputDirectory}"));
            return;
        }

        var executableName = targetConfiguration.MainExecutable ??
            (target!.OperatingSystem == DesktopOperatingSystem.Windows
                ? $"{configuration.ProductName}.exe"
                : configuration.ProductName);
        var executablePath = Path.Combine(targetConfiguration.InputDirectory, executableName);
        if (!File.Exists(executablePath))
        {
            issues.Add(new($"{path}.mainExecutable", $"Main executable does not exist: {executablePath}"));
        }
    }

    private static void ValidateDuplicateOutputs(
        BundleConfiguration configuration,
        List<ValidationIssue> issues)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var targetIndex = 0; targetIndex < configuration.Targets.Count; targetIndex++)
        {
            var target = configuration.Targets[targetIndex];
            foreach (var format in target.Formats)
            {
                var key = $"{target.RuntimeIdentifier}:{format}";
                if (!outputs.Add(key))
                {
                    issues.Add(new($"targets[{targetIndex}].formats", $"Duplicate output requested: {key}."));
                }
            }
        }
    }

    private static void ValidatePaths(
        IReadOnlyList<string> paths,
        string propertyName,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        if (!checkFileSystem)
        {
            return;
        }

        for (var index = 0; index < paths.Count; index++)
        {
            if (!File.Exists(paths[index]) && !Directory.Exists(paths[index]))
            {
                issues.Add(new($"{propertyName}[{index}]", $"Path does not exist: {paths[index]}"));
            }
        }
    }

    private static void Required(string value, string path, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(path, "Value is required."));
        }
    }
}

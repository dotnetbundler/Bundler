using System.Text.RegularExpressions;
using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public static class BundleConfigurationValidator
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$",
        RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);
    private static readonly Regex FileExtensionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_+-]{0,63}$",
        RegexOptions.Compiled);
    private static readonly Regex UrlSchemePattern = new(
        "^[A-Za-z][A-Za-z0-9+.-]{0,63}$",
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

        if (!string.IsNullOrWhiteSpace(configuration.Homepage) &&
            (!Uri.TryCreate(configuration.Homepage, UriKind.Absolute, out var homepage) ||
             (homepage.Scheme != Uri.UriSchemeHttp && homepage.Scheme != Uri.UriSchemeHttps)))
        {
            issues.Add(new("homepage", "Must be an absolute HTTP or HTTPS URL."));
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
        ValidateOptionalFile(configuration.LicenseFile, "licenseFile", [".txt", ".rtf"], checkFileSystem, issues);
        for (var index = 0; index < configuration.Resources.Count; index++)
        {
            var resource = configuration.Resources[index];
            Required(resource.Source, $"resources[{index}].source", issues);
            Required(resource.TargetPath, $"resources[{index}].targetPath", issues);
            if (!string.IsNullOrWhiteSpace(resource.TargetPath) &&
                (IsRootedInstallerPath(resource.TargetPath) ||
                 resource.TargetPath.Split(new[] { '/', '\\' }, StringSplitOptions.None)
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
        ValidateFileAssociations(configuration.FileAssociations, issues);
        ValidateUrlProtocols(configuration.UrlProtocols, issues);
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
            issues.Add(new($"{path}.runtimeIdentifier", "Supported RIDs: win-x86, win-x64, win-arm64, osx, osx-x64, osx-arm64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64."));
            return;
        }

        if (targetConfiguration.Formats.Count == 0)
        {
            issues.Add(new($"{path}.formats", "At least one package format is required."));
        }

        foreach (var format in targetConfiguration.Formats.Distinct())
        {
            if (!DesktopTargetMatrix.Supports(target!, format))
            {
                issues.Add(new($"{path}.formats", $"{format} is not supported for {target!.RuntimeIdentifier}."));
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

        if (!string.IsNullOrWhiteSpace(targetConfiguration.InputDirectory))
        {
            var inputDirectory = Path.GetFullPath(targetConfiguration.InputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outputDirectory = Path.GetFullPath(configuration.OutputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (outputDirectory.StartsWith(inputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outputDirectory, inputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("outputDirectory", "Must not be inside a target inputDirectory."));
            }
        }

        for (var signingIndex = 0; signingIndex < targetConfiguration.SigningFiles.Count; signingIndex++)
        {
            var signingFile = targetConfiguration.SigningFiles[signingIndex];
            if (string.IsNullOrWhiteSpace(signingFile) || Path.IsPathRooted(signingFile) ||
                signingFile.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Contains("..", StringComparer.Ordinal))
            {
                issues.Add(new($"{path}.signingFiles[{signingIndex}]", "Must be a non-empty path inside inputDirectory."));
            }
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
        for (var signingIndex = 0; signingIndex < targetConfiguration.SigningFiles.Count; signingIndex++)
        {
            var signingFile = targetConfiguration.SigningFiles[signingIndex];
            if (!string.IsNullOrWhiteSpace(signingFile) && !Path.IsPathRooted(signingFile))
            {
                var signingPath = Path.Combine(targetConfiguration.InputDirectory, signingFile);
                if (!File.Exists(signingPath))
                {
                    issues.Add(new($"{path}.signingFiles[{signingIndex}]", $"Signing file does not exist: {signingPath}"));
                }
            }
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

    private static void ValidateFileAssociations(
        IReadOnlyList<BundleFileAssociationConfiguration> associations,
        List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var associationIndex = 0; associationIndex < associations.Count; associationIndex++)
        {
            var association = associations[associationIndex];
            if (association.Extensions.Count == 0)
            {
                issues.Add(new($"fileAssociations[{associationIndex}].extensions", "At least one file extension is required."));
            }

            for (var extensionIndex = 0; extensionIndex < association.Extensions.Count; extensionIndex++)
            {
                var configured = association.Extensions[extensionIndex].Trim();
                var extension = configured.StartsWith(".", StringComparison.Ordinal) ? configured.Substring(1) : configured;
                var path = $"fileAssociations[{associationIndex}].extensions[{extensionIndex}]";
                if (!FileExtensionPattern.IsMatch(extension))
                {
                    issues.Add(new(path, "Must be a 1-64 character extension containing only letters, digits, '_', '+', or '-'."));
                }
                else if (!seen.Add(extension))
                {
                    issues.Add(new(path, $"Duplicate file extension: {configured}."));
                }
            }
        }
    }

    private static void ValidateUrlProtocols(
        IReadOnlyList<BundleUrlProtocolConfiguration> protocols,
        List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var protocolIndex = 0; protocolIndex < protocols.Count; protocolIndex++)
        {
            var protocol = protocols[protocolIndex];
            if (protocol.Schemes.Count == 0)
            {
                issues.Add(new($"urlProtocols[{protocolIndex}].schemes", "At least one URL scheme is required."));
            }

            for (var schemeIndex = 0; schemeIndex < protocol.Schemes.Count; schemeIndex++)
            {
                var scheme = protocol.Schemes[schemeIndex].Trim();
                var path = $"urlProtocols[{protocolIndex}].schemes[{schemeIndex}]";
                if (!UrlSchemePattern.IsMatch(scheme))
                {
                    issues.Add(new(path, "Must be a 1-64 character URI scheme beginning with a letter."));
                }
                else if (!seen.Add(scheme))
                {
                    issues.Add(new(path, $"Duplicate URL scheme: {scheme}."));
                }
            }
        }
    }

    private static bool IsRootedInstallerPath(string path) =>
        path.StartsWith("/", StringComparison.Ordinal) ||
        path.StartsWith("\\", StringComparison.Ordinal) ||
        (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private static void ValidateOptionalFile(
        string? path,
        string propertyName,
        IReadOnlyCollection<string> extensions,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new(propertyName, $"Supported file extensions: {string.Join(", ", extensions)}."));
        }

        if (checkFileSystem && !File.Exists(path))
        {
            issues.Add(new(propertyName, $"File does not exist: {path}"));
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

using System.Text.RegularExpressions;
using DotNet.Bundler;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Core;

public static class BundleConfigurationValidator
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$",
        RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
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

        ValidateIcons(configuration.Icons, checkFileSystem, issues);
        ValidateOptionalFile(configuration.LicenseFile, "licenseFile", [".txt", ".rtf"], checkFileSystem, issues);
        for (var index = 0; index < configuration.Resources.Count; index++)
        {
            var resource = configuration.Resources[index];
            Required(resource.Source, $"resources[{index}].source", issues);
            Required(resource.Destination, $"resources[{index}].targetPath", issues);
            if (!string.IsNullOrWhiteSpace(resource.Destination) &&
                (IsRootedInstallerPath(resource.Destination) ||
                 resource.Destination.Split(new[] { '/', '\\' }, StringSplitOptions.None)
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
        ValidateUpdateSection(configuration.Update, checkFileSystem, issues);
        ValidateDuplicateOutputs(configuration, issues);
        return issues;
    }

    // update 节与格式节同级：开启后 feed/通道/签名旋钮在扇出前一次验全，
    // 不留到发射期才 InvalidOperationException。
    private static readonly Regex UpdateChannelPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static void ValidateUpdateSection(
        UpdateBundleConfiguration? update,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        if (update is null)
        {
            return;
        }

        // feed 空 = 装出来的身份旁车带空 feed——更新链静默断。
        if (string.IsNullOrWhiteSpace(update.FeedUrl))
        {
            issues.Add(new("update.feedUrl", "Value is required when update is enabled."));
        }

        if (!string.IsNullOrWhiteSpace(update.Channel) &&
            !UpdateChannelPattern.IsMatch(update.Channel))
        {
            // 通道名直接进清单文件名 bundler-update-feed.<channel>.json。
            issues.Add(new("update.channel", "Must be a filename-safe token (letters, digits, '.', '_', '-')."));
        }

        var signingKeyFile = update.SigningKeyFile is { Length: > 0 } keyPath ? keyPath : null;
        if (signingKeyFile is null)
        {
            issues.Add(new("update.signingKeyFile", "Value is required — update signatures are mandatory."));
        }
        else if (checkFileSystem && !File.Exists(signingKeyFile))
        {
            issues.Add(new("update.signingKeyFile", $"File does not exist: {signingKeyFile}"));
        }

        var publicKey = update.PublicKey is { Length: > 0 } point ? point : null;
        if (publicKey is not null && !IsEcP256Point(publicKey))
        {
            issues.Add(new("update.publicKey", "Must be a base64 uncompressed P-256 point (65 bytes, 0x04 prefix)."));
        }
        else if (publicKey is not null && signingKeyFile is not null &&
            checkFileSystem && File.Exists(signingKeyFile))
        {
            // 旁车嵌 publicKey、清单用 signingKeyFile 私钥签——两钥不符时装出来的
            // 客户端会拿错的公钥验签：合法更新被拒、他钥签名反被信。扇出前拦下。
            try
            {
                var derived = UpdateKeyMaterial.Load(signingKeyFile).PublicPointBase64();
                if (!string.Equals(derived, publicKey.Trim(), StringComparison.Ordinal))
                {
                    issues.Add(new("update.publicKey",
                        "Must match the public half of update.signingKeyFile."));
                }
            }
            // netstandard2.0 腿无 System.Text.Json 引用——按全名匹配其 JsonException。
            catch (Exception error) when (error is IOException or InvalidOperationException
                or System.Runtime.Serialization.SerializationException
                or UnauthorizedAccessException or FormatException or ArgumentException
                || error.GetType().FullName == "System.Text.Json.JsonException")
            {
                issues.Add(new("update.signingKeyFile",
                    $"Cannot be parsed as an ec-p256 key file: {error.Message}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(update.BootstrapperDirectory) &&
            checkFileSystem && !Directory.Exists(update.BootstrapperDirectory))
        {
            issues.Add(new("update.bootstrapperDirectory", $"Directory does not exist: {update.BootstrapperDirectory}"));
        }
    }

    private static bool IsEcP256Point(string base64)
    {
        // netstandard2.0 无 TryFromBase64String——异常即非法输入。
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == 65 && bytes[0] == 0x04;
        }
        catch (FormatException)
        {
            return false;
        }
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
        if (configuredExecutable is { } && configuredExecutable.Trim().Length == 0 &&
            configuredExecutable.Length > 0)
        {
            issues.Add(new($"{path}.mainExecutable",
                "Must not be blank; omit the knob to use the default executable."));
        }
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
            // 宿主 FS 语义：Windows/macOS 不敏感、Linux 敏感（与 ZipToolCache.PathComparer 同规）。
            var comparison = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux)
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (outputDirectory.StartsWith(inputDirectory + Path.DirectorySeparatorChar, comparison) ||
                string.Equals(outputDirectory, inputDirectory, comparison))
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
            // 注入的中间 App 项同样占输出槽：dmg/pkg 未显式带 app 时 planner 会
            // 补一个中间 app——同 rid 双 target 判定不能漏它。
            var formats = target.Formats.Concat(
                target.Formats.Any(format => format is PackageFormat.Dmg or PackageFormat.Pkg) &&
                !target.Formats.Contains(PackageFormat.App)
                    ? new[] { PackageFormat.App }
                    : []);
            foreach (var format in formats)
            {
                var key = $"{target.RuntimeIdentifier}:{format}";
                if (!outputs.Add(key))
                {
                    issues.Add(new($"targets[{targetIndex}].formats", $"Duplicate output requested: {key}."));
                }
            }
        }
    }

    private static void ValidateIcons(
        IReadOnlyList<string> icons,
        bool checkFileSystem,
        List<ValidationIssue> issues)
    {
        for (var index = 0; index < icons.Count; index++)
        {
            // 图标类型因格式而异（nsis .ico / mac .png/.icns/.car 或 Icon.icon 目录集）——
            // 共享层只验存在；文件/目录形态与类型校验在各格式的 Validate 里。
            if (checkFileSystem && !File.Exists(icons[index]) && !Directory.Exists(icons[index]))
            {
                issues.Add(new($"icons[{index}]", $"Path does not exist: {icons[index]}"));
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

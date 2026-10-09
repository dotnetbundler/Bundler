using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Nsis;

public sealed class NsisBundler : IFormatBundler
{
    public static IReadOnlyList<string> SupportedLanguages { get; } =
        NsisLanguageCatalog.Definitions.Select(language => language.Name).ToArray();

    private readonly NsisBundleConfiguration _configuration;
    private readonly NsisBundlerOptions _options;

    public NsisBundler(
        NsisBundleConfiguration? configuration = null,
        NsisBundlerOptions? options = null)
    {
        _configuration = configuration ?? new NsisBundleConfiguration();
        _options = options ?? new NsisBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (!SemanticVersion.TryParse(bundle.Version, out var semanticVersion))
        {
            throw new ArgumentException(
                $"NSIS package version '{bundle.Version}' must be a valid SemVer 2.0 version.",
                nameof(bundle));
        }
        if (!semanticVersion!.TryGetWindowsNumericVersion(out _))
        {
            throw new ArgumentException(
                $"NSIS package version '{bundle.Version}' has a numeric component outside the Windows range 0-65535.",
                nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats)
                .Any(format => format != PackageFormat.Nsis))
        {
            // Nsis=0 是枚举默认值：FirstOrDefault 空集时恰落 0，必须用 Any 判存在。
            var unsupported = bundle.Targets.SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Nsis);
            throw new NotSupportedException(
                $"DotNet.Bundler.Nsis accepts NSIS targets only; '{unsupported}' requires another backend package.");
        }
        if (_options.Signer is null && bundle.Targets.Any(target => target.SigningFiles.Count > 0))
        {
            throw new ArgumentException(
                "Payload signing files were configured, but no bundle signer was provided.",
                nameof(bundle));
        }

        ValidateConfiguration(_configuration);
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        var cache = _options.ResolveToolCacheDirectory();
        var embedded = await NsisEmbeddedResources.MaterializeAsync(cache, cancellationToken);
        var toolset = !string.IsNullOrWhiteSpace(_options.CompilerPath)
            ? new NsisToolset(
                Path.GetFullPath(_options.CompilerPath!),
                string.IsNullOrWhiteSpace(_options.DataDirectory)
                    ? null
                    : Path.GetFullPath(_options.DataDirectory!))
            : await NsisToolResolver.ResolveAsync(
                _options.ToolsetArchivePath ?? embedded.ToolsetArchivePath,
                cache,
                cancellationToken);
        var template = Path.GetFullPath(_options.TemplatePath ?? embedded.TemplatePath);
        var languages = Path.GetFullPath(_options.LanguageDirectory ?? embedded.LanguageDirectory);

        var backend = new NsisBundleBackend(
            toolset,
            template,
            languages,
            embedded.PluginDirectory,
            _configuration,
            _options.Signer);
        return await new BundlePipeline([backend], _options.Logger).BuildAsync(bundle, cancellationToken);
    }

    private static void ValidateConfiguration(NsisBundleConfiguration settings)
    {
        if (!Enum.IsDefined(typeof(NsisCompression), settings.Compression))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Compression,
                "Unknown NSIS compression mode.");
        }

        if (settings.Languages.Count == 0)
        {
            throw new ArgumentException("At least one NSIS language is required.", nameof(settings));
        }

        ValidateOptionalFile(settings.InstallerIcon, ".ico", nameof(settings.InstallerIcon));
        ValidateOptionalFile(settings.UninstallerIcon, ".ico", nameof(settings.UninstallerIcon));
        ValidateOptionalFile(settings.HeaderImage, ".bmp", nameof(settings.HeaderImage));
        ValidateOptionalFile(settings.SidebarImage, ".bmp", nameof(settings.SidebarImage));
        ValidateOptionalFile(settings.UninstallerHeaderImage, ".bmp", nameof(settings.UninstallerHeaderImage));
        ValidateOptionalFile(settings.InstallerHooks, ".nsh", nameof(settings.InstallerHooks));
        foreach (var file in settings.CustomLanguageFiles.Values)
        {
            ValidateOptionalFile(file, ".nsh", nameof(settings.CustomLanguageFiles));
        }
        var selectedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in settings.Languages)
        {
            var canonical = NsisLanguageCatalog.Resolve(language).Name;
            if (!selectedLanguages.Add(canonical))
            {
                throw new ArgumentException(
                    $"NSIS language '{canonical}' is selected more than once.",
                    nameof(settings));
            }
        }
        var customizedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in settings.CustomLanguageFiles.Keys)
        {
            var canonical = NsisLanguageCatalog.Resolve(language).Name;
            if (!selectedLanguages.Contains(canonical))
            {
                throw new ArgumentException(
                    $"Custom NSIS language file '{language}' does not correspond to a selected language.",
                    nameof(settings));
            }
            if (!customizedLanguages.Add(canonical))
            {
                throw new ArgumentException(
                    $"NSIS language '{canonical}' has more than one custom language file.",
                    nameof(settings));
            }
        }
        ValidateMsiCodes(settings.LegacyMsiProductCodes, nameof(settings.LegacyMsiProductCodes));
        ValidateMsiCodes(settings.LegacyMsiUpgradeCodes, nameof(settings.LegacyMsiUpgradeCodes));
        ValidateShortcuts(settings.Shortcuts);
    }

    private static void ValidateShortcuts(NsisShortcutConfiguration shortcuts)
    {
        if (shortcuts is null)
        {
            throw new ArgumentNullException(nameof(shortcuts));
        }
        ValidateRelativeInstalledPath(shortcuts.WorkingDirectory, nameof(shortcuts.WorkingDirectory), allowCurrentDirectory: true);
        ValidateRelativeInstalledPath(shortcuts.Icon, nameof(shortcuts.Icon), allowCurrentDirectory: false);
        ValidateRelativeInstalledPath(shortcuts.StartMenuFolder, nameof(shortcuts.StartMenuFolder), allowCurrentDirectory: true);
        foreach (var executable in shortcuts.LegacyMainExecutables)
        {
            ValidateRelativeInstalledPath(executable, nameof(shortcuts.LegacyMainExecutables), allowCurrentDirectory: false);
        }
        foreach (var productName in shortcuts.LegacyProductNames)
        {
            if (string.IsNullOrWhiteSpace(productName) || WindowsFileNames.ContainsInvalidCharacter(productName))
            {
                throw new ArgumentException("Legacy shortcut product names must be valid file names.", nameof(shortcuts));
            }
        }

        var appUserModelId = shortcuts.AppUserModelId;
        if (appUserModelId is not null &&
            (string.IsNullOrWhiteSpace(appUserModelId) || appUserModelId.Length > 128 || appUserModelId.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException(
                "Shortcut AppUserModelId must be non-empty, contain no whitespace, and be at most 128 characters.",
                nameof(shortcuts));
        }
    }

    private static void ValidateRelativeInstalledPath(string? value, string propertyName, bool allowCurrentDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || (allowCurrentDirectory && value == "."))
        {
            return;
        }
        var components = value!.Split('\\', '/');
        if (Path.IsPathRooted(value!) || components.Contains(".."))
        {
            throw new ArgumentException($"{propertyName} must be a path relative to the installed application directory.", propertyName);
        }
        if (components.Any(component => component.Length == 0 || WindowsFileNames.ContainsInvalidCharacter(component)))
        {
            throw new ArgumentException($"{propertyName} contains an invalid Windows path component.", propertyName);
        }
    }

    private static void ValidateMsiCodes(IReadOnlyList<string> codes, string propertyName)
    {
        var normalized = new List<string>();
        foreach (var code in codes)
        {
            if (!Guid.TryParse(code, out var parsed))
            {
                throw new ArgumentException($"{propertyName} contains an invalid MSI GUID: '{code}'.", propertyName);
            }
            normalized.Add(parsed.ToString("B"));
        }

        if (string.Join(";", normalized.Distinct(StringComparer.OrdinalIgnoreCase)).Length > 1023)
        {
            throw new ArgumentException(
                $"{propertyName} exceeds the NSIS runtime string limit after normalization.",
                propertyName);
        }
    }

    private static void ValidateOptionalFile(string? path, string extension, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{propertyName} must be a '{extension}' file.", propertyName);
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{propertyName} was not found.", path);
        }
    }
}

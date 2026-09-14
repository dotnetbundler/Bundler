using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Nsis;

public sealed class NsisBundler
{
    private readonly NsisBundleConfiguration _configuration;
    private readonly NsisBundlerOptions _options;

    public NsisBundler(
        NsisBundleConfiguration? configuration = null,
        NsisBundlerOptions? options = null)
    {
        _configuration = configuration ?? new NsisBundleConfiguration();
        _options = options ?? new NsisBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        var unsupported = bundle.Targets
            .SelectMany(target => target.Formats)
            .FirstOrDefault(format => format != PackageFormat.Nsis);
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Nsis))
        {
            throw new NotSupportedException(
                $"DotNet.Bundler.Nsis accepts NSIS targets only; '{unsupported}' requires another backend package.");
        }

        ValidateConfiguration(_configuration);
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

        var backend = new NsisBundleBackend(toolset, template, languages, _configuration);
        return await new BundlePipeline([backend], _options.Logger).BuildAsync(bundle, cancellationToken);
    }

    private static void ValidateConfiguration(NsisBundleConfiguration settings)
    {
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

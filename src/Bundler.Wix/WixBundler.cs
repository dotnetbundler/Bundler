using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Wix;

public sealed class WixBundler
{
    private readonly WixBundleConfiguration _settings;
    private readonly WixBundlerOptions _options;

    public WixBundler(WixBundleConfiguration? configuration = null, WixBundlerOptions? options = null)
    {
        _settings = configuration ?? new WixBundleConfiguration();
        _options = options ?? new WixBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WiX 3.14.1 MSI builds require a Windows host.");
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Msi))
        {
            throw new NotSupportedException("DotNet.Bundler.Wix accepts MSI targets only.");
        }
        if (bundle.Targets.Any(target => target.SigningFiles.Count > 0) && _options.Signer is null)
        {
            throw new ArgumentException("MSI signing files require a configured Windows signer.");
        }
        if (!Enum.IsDefined(typeof(WixPackageLanguage), _settings.Language))
        {
            throw new ArgumentOutOfRangeException(nameof(_settings.Language));
        }
        if (_settings.Codepage < 0 || _settings.Codepage is 65000 or 65001)
        {
            throw new ArgumentException("MSI requires a Windows ANSI code page; UTF-7/UTF-8 are not supported by WiX 3 MSI UI.");
        }
        if (_settings.Language == WixPackageLanguage.ChineseSimplified && _settings.EffectiveCodepage != 936)
            throw new ArgumentException("Simplified Chinese MSI UI requires Windows code page 936.");
        if (!string.IsNullOrWhiteSpace(bundle.LicenseFile) &&
            !Path.GetExtension(bundle.LicenseFile).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MSI interactive license UI requires an RTF license file.");

        foreach (var target in bundle.Targets)
        {
            WixIdentity.Create(bundle.Identifier, bundle.Version, target.RuntimeIdentifier,
                _settings.InstallScope, _settings.UpgradeCode, _settings.Language, _settings.MsiVersion);
        }
        var toolset = await WixToolsetResolver.ResolveAsync(
            _options.ResolveToolCacheDirectory(), _options.ToolsetArchivePath, cancellationToken);
        return await new BundlePipeline(
            [new WixBundleBackend(toolset, _settings, _options.Signer)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}

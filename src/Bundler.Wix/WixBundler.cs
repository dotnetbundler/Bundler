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
        var languages = _settings.ResolveLanguages();
        _settings.ValidateExtensionSurface();
        if (_settings.Codepage < 0 || _settings.Codepage is 65000 or 65001)
        {
            throw new ArgumentException("MSI requires a Windows ANSI code page; UTF-7/UTF-8 are not supported by WiX 3 MSI UI.");
        }
        foreach (var language in languages)
        {
            var localeFile = _settings.LocaleFiles.FirstOrDefault(pair =>
                pair.Key.Equals(language.Culture, StringComparison.OrdinalIgnoreCase)).Value;
            if (localeFile is not null)
            {
                WixLocale.ReadCallerStrings(localeFile, language, _settings.EffectiveCodepage(language));
            }
        }
        if (!string.IsNullOrWhiteSpace(bundle.LicenseFile) &&
            !Path.GetExtension(bundle.LicenseFile).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MSI interactive license UI requires an RTF license file.");
        CheckBitmap(_settings.BannerBitmap, "MSI banner bitmap requires a 493x58 .bmp file.", 493, 58);
        CheckBitmap(_settings.DialogBitmap, "MSI dialog bitmap requires a 503x314 .bmp file.", 503, 314);

        foreach (var target in bundle.Targets)
        foreach (var language in languages)
        {
            WixIdentity.Create(bundle.Identifier, bundle.Version, target.RuntimeIdentifier,
                _settings.InstallScope, _settings.UpgradeCode, language, _settings.MsiVersion);
        }
        var toolset = await WixToolsetResolver.ResolveAsync(
            _options.ResolveToolCacheDirectory(), _options.ToolsetArchivePath, cancellationToken);
        return await new BundlePipeline(
            [new WixBundleBackend(toolset, _settings, _options.Signer)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }

    private static void CheckBitmap(string? path, string message, int expectedWidth, int expectedHeight)
    {
        if (path is null) return;
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !Path.GetExtension(path).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(message);
        var header = new byte[26];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
                throw new ArgumentException(message);
        }
        var validHeader = header[0] == 'B' && header[1] == 'M' &&
            BitConverter.ToInt32(header, 10) >= 14 &&
            BitConverter.ToUInt32(header, 14) >= 40 &&
            BitConverter.ToInt32(header, 18) == expectedWidth &&
            Math.Abs(BitConverter.ToInt32(header, 22)) == expectedHeight;
        if (!validHeader)
            throw new ArgumentException(message);
    }
}

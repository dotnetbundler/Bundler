using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacApp;

public sealed class MacAppBundler : IFormatBundler
{
    private readonly MacAppBundleConfiguration _configuration;
    private readonly MacAppBundlerOptions _options;

    public MacAppBundler(
        MacAppBundleConfiguration? configuration = null,
        MacAppBundlerOptions? options = null)
    {
        _configuration = configuration ?? new MacAppBundleConfiguration();
        _options = options ?? new MacAppBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.App))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.App);
            throw new NotSupportedException(
                $"DotNet.Bundler.MacApp accepts App targets only; '{unsupported}' requires another backend package.");
        }
        if (bundle.LicenseFile is not null)
        {
            throw new NotSupportedException(
                ".app has no license-file payload contract; managed installation arrives with MAC-PKG.");
        }
        if (bundle.Targets.Any(target => target.SigningFiles.Count > 0))
        {
            throw new NotSupportedException(
                ".app signing covers every Mach-O inside the bundle automatically; " +
                "per-file SigningFiles has no macOS meaning. Use MacAppBundleConfiguration.Signing.");
        }
        ValidateConfiguration(_configuration, bundle);
    }

    // dmg/pkg 的构建管线内嵌 .app 阶段，入口统一预检经此方法覆盖同一套
    // 旋钮校验；LicenseFile 与 per-file SigningFiles 的拒绝是 .app 格式独有
    // 契约（dmg/pkg 合法消费 license），不在共享面内。
    internal static void ValidateConfiguration(
        MacAppBundleConfiguration configuration, BundleConfiguration bundle)
    {
        foreach (var target in bundle.Targets)
        {
            if (!string.IsNullOrWhiteSpace(target.MainExecutable) &&
                (target.MainExecutable!.Contains('/') || target.MainExecutable.Contains('\\')))
            {
                throw new ArgumentException(
                    "The .app main executable must be a file directly inside the input directory; " +
                    $"got '{target.MainExecutable}'.");
            }
        }
        // Fail on invalid metadata, mappings, integration config, and icons before touching
        // the file system.
        MacAppMetadata.Resolve(bundle, configuration);
        MacAppBundleBackend.ResolveContentsMappings(configuration);
        MacAppDesktopIntegration.ResolveDocumentTypes(bundle, configuration);
        MacAppDesktopIntegration.ResolveUrlTypes(bundle, configuration);
        if (configuration.ExceptionDomain is { } domain && domain.Trim().Length == 0)
        {
            throw new ArgumentException("ExceptionDomain must not be empty.");
        }
        if (configuration.InfoPlistFile is not null && configuration.InfoPlistXml is not null)
        {
            throw new ArgumentException("InfoPlistFile and InfoPlistXml are mutually exclusive.");
        }
        if (configuration.InfoPlistFile is { } plistFile && !File.Exists(Path.GetFullPath(plistFile)))
        {
            throw new FileNotFoundException("The caller Info.plist does not exist.", plistFile);
        }
        foreach (var framework in configuration.FrameworkDirectories)
        {
            var name = Path.GetFileName(framework);
            if (!name.EndsWith(".framework", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Contents/Frameworks accepts .framework bundles and .dylib files only: {framework}");
            }
        }
        if (bundle.Icons.Any(icon =>
                !icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".car", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".icon", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                ".app icons accept .icns/.png bitmaps, an Icon Composer .icon directory, or a .car file.");
        }
        if (bundle.Icons.Count(icon => icon.EndsWith(".car", StringComparison.OrdinalIgnoreCase)) > 1 ||
            bundle.Icons.Count(icon => icon.EndsWith(".icon", StringComparison.OrdinalIgnoreCase)) > 1)
        {
            throw new ArgumentException(".app icons accept a single .car or .icon input.");
        }
        // 签名放最后：它含宿主门禁（PNSE），先验完所有配置类旋钮才不会
        // 被宿主门遮住——非 mac 宿主上配置错仍应按配置错误聚合而非逐格式容错。
        MacAppSigning.Validate(configuration.Signing);
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        return await new BundlePipeline(
            [new MacAppBundleBackend(_configuration)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}

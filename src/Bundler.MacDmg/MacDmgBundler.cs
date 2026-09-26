using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacDmg;

public sealed class MacDmgBundler
{
    private readonly MacDmgBundleConfiguration _dmgConfiguration;
    private readonly MacAppBundleConfiguration _appConfiguration;
    private readonly MacDmgBundlerOptions _options;

    public MacDmgBundler(
        MacDmgBundleConfiguration? dmgConfiguration = null,
        MacAppBundleConfiguration? appConfiguration = null,
        MacDmgBundlerOptions? options = null)
    {
        _dmgConfiguration = dmgConfiguration ?? new MacDmgBundleConfiguration();
        _appConfiguration = appConfiguration ?? new MacAppBundleConfiguration();
        _options = options ?? new MacDmgBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Dmg))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Dmg);
            throw new NotSupportedException(
                $"DotNet.Bundler.MacDmg accepts Dmg targets only; '{unsupported}' requires another backend package.");
        }
        var isMacOs = MacDmgBundleBackend.HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!isMacOs)
        {
            throw new NotSupportedException(
                ".dmg creation requires a macOS host (hdiutil/osascript are not cross-host).");
        }
        if (_dmgConfiguration.VolumeName is { } volumeName && volumeName.Trim().Length == 0)
        {
            throw new ArgumentException("The .dmg volume name must not be empty.");
        }
        return await new BundlePipeline(
            [
                new MacAppBundleBackend(_appConfiguration),
                new MacDmgBundleBackend(_dmgConfiguration)
            ],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}

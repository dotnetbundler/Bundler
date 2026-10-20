using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacPkg;

public sealed class MacPkgBundler : IFormatBundler
{
    private readonly MacPkgBundleConfiguration _pkgConfiguration;
    private readonly MacAppBundleConfiguration _appConfiguration;
    private readonly MacPkgBundlerOptions _options;

    public MacPkgBundler(
        MacPkgBundleConfiguration? pkgConfiguration = null,
        MacAppBundleConfiguration? appConfiguration = null,
        MacPkgBundlerOptions? options = null)
    {
        _pkgConfiguration = pkgConfiguration ?? new MacPkgBundleConfiguration();
        _appConfiguration = appConfiguration ?? new MacAppBundleConfiguration();
        _options = options ?? new MacPkgBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.Any(target => target is null))
        {
            throw new ArgumentException("Targets must not contain null elements.", nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Pkg))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Pkg);
            throw new NotSupportedException(
                $"DotNet.Bundler.MacPkg accepts Pkg targets only; '{unsupported}' requires another backend package.");
        }
        var isMacOs = MacPkgBundleBackend.HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!isMacOs)
        {
            throw new PlatformNotSupportedException(
                ".pkg creation requires a macOS host (pkgbuild/productbuild are not cross-host).");
        }
        // 构建管线内嵌 .app 阶段，其旋钮一并预检。
        MacAppBundler.ValidateConfiguration(_appConfiguration, bundle);
        MacPkgBundleBackend.ValidateConfiguration(_pkgConfiguration, bundle);
        // pkg 级公证提交的是内嵌 .app 的容器——内件代码签名未开 hardened
        // runtime 时 notary 服务端拒收，前置拦在扇出前。
        if (_pkgConfiguration.Signing?.Notarize == true &&
            _appConfiguration.Signing?.HardenedRuntime != true)
        {
            throw new ArgumentException(
                ".pkg notarization requires the bundled .app signing to enable HardenedRuntime — " +
                "Apple's notary service rejects the inner code signature without it.");
        }
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        return await new BundlePipeline(
            [
                new MacAppBundleBackend(_appConfiguration),
                new MacPkgBundleBackend(_pkgConfiguration)
            ],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}

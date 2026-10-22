using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// codesign (inside-out) + explicit notarization for the produced .app.
/// Everything is opt-in: an empty <see cref="MacAppSigningConfiguration"/> leaves the bundle
/// unsigned. Signing requires a macOS host; notarization requires a real identity (not "-").
/// Credential/keychain/tool-argument machinery is shared via <see cref="MacSigningSupport"/>.
/// </summary>
internal static class MacAppSigning
{
    /// <summary>Bundle directories whose contents codesign treats as nested code.</summary>
    private static readonly string[] CodeDirectoryNames =
        ["MacOS", "Frameworks", "PlugIns", "Helpers", "XPCServices", "Libraries", "SharedFrameworks"];

    /// <summary>Apple 嵌套 bundle 形态：按 bundle 维度签名而不是按散文件签。</summary>
    private static readonly string[] NestedBundleExtensions =
        [".app", ".appex", ".bundle", ".framework", ".xpc", ".qlgenerator", ".plugin", ".safariextension"];

    internal static bool Configured(MacAppSigningConfiguration signing) =>
        signing.Identity is not null || !string.IsNullOrEmpty(signing.TemporaryCertificateFile);

    /// <summary>Preflight checks; the bundler calls this before any payload work.</summary>
    internal static void Validate(MacAppSigningConfiguration signing)
    {
        var signingRequested = Configured(signing);
        MacSigningSupport.ValidateIdentityAndCertificate(
            signing.Identity, signing.TemporaryCertificateFile, allowAdHoc: true, ".app");
        if (signing.EntitlementsFile is { Length: > 0 } entitlements &&
            !File.Exists(Path.GetFullPath(entitlements)))
        {
            throw new FileNotFoundException("EntitlementsFile does not exist.", entitlements);
        }
        MacSigningSupport.ValidateNotarizationKnobs(
            signing.Notarize, signingRequested, adHocIdentity: signing.Identity == "-",
            signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId,
            signing.SkipStapling, signing.NotaryWait,
            "Notarization requires a real signing identity; ad-hoc signatures cannot be notarized.");
        // 公证服务拒收未开 hardened runtime 的代码签名——上传必败，前置拦在预检。
        if (signing.Notarize && !signing.HardenedRuntime)
        {
            throw new ArgumentException(
                "Notarization requires Signing.HardenedRuntime — Apple's notary service rejects signatures without it.");
        }
        if (signingRequested && !(MacAppBundleBackend.HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
        {
            throw new PlatformNotSupportedException(".app signing requires a macOS host (codesign).");
        }
    }

    /// <summary>Sign the staged .app inside-out, verify, then notarize when enabled.</summary>
    internal static async Task RunAsync(
        BundleBuildContext context, string appPath, string mainExecutable,
        MacAppSigningConfiguration signing, CancellationToken cancellationToken)
    {
        // Provenance/quarantine xattrs are stripped unconditionally (signing or not): payload
        // trees copied from the network or other machines carry com.apple.quarantine and
        // FinderInfo attrs that would otherwise ride into the bundle and fail Gatekeeper
        // checks or codesign's detritus audit on the user's machine.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await ToolRunner.RunAsync(
                "xattr", ["-crs", appPath], context.WorkDirectory, cancellationToken);
        }
        if (!Configured(signing))
        {
            return;
        }
        var logger = context.Logger;
        MacSigningSupport.TemporaryKeychain? keychain = null;
        string identity;
        try
        {
            (identity, keychain) = await MacSigningSupport.ResolveIdentityAsync(
                context.WorkDirectory, logger, signing.Identity, signing.TemporaryCertificateFile,
                signing.TemporaryCertificatePassword, cancellationToken);

            logger.Log(BundleLogLevel.Information,
                identity == "-"
                    ? "Signing the .app ad-hoc (codesign -s -)."
                    : $"Signing the .app with identity '{identity}'.");

            var contentsDirectory = Path.Combine(appPath, "Contents");
            var mainExecutablePath = Path.Combine(contentsDirectory, "MacOS", mainExecutable);
            // Inside-out order: every regular file under the code-bearing directories must be
            // signed before the main executable (which seals the whole bundle). codesign treats
            // anything under MacOS/Frameworks/... as nested code — including managed .dlls, which
            // are not Mach-O but still get rejected as unsigned subcomponents.
            var nestedBundles = FindNestedBundles(contentsDirectory);
            var nestedFiles = Directory
                .EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories)
                .Where(file => CodeDirectoryNames.Any(dir =>
                    file.StartsWith(
                        Path.Combine(contentsDirectory, dir) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
                // 嵌套 bundle 内的文件归该 bundle 的签名管，不按顶层散文件签。
                .Where(file => !nestedBundles.Any(bundle =>
                    file.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .Where(file => !string.Equals(file, mainExecutablePath, StringComparison.Ordinal))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();

            foreach (var file in nestedFiles)
            {
                await ToolRunner.RunAsync("codesign",
                    CodesignArguments(identity, signing, keychain, entitlements: false, file),
                    context.WorkDirectory, cancellationToken);
            }
            foreach (var nested in nestedBundles)
            {
                await SignNestedBundleAsync(
                    context, signing, identity, keychain, nested, cancellationToken);
            }
            await ToolRunner.RunAsync("codesign",
                CodesignArguments(identity, signing, keychain, entitlements: true, mainExecutablePath),
                context.WorkDirectory, cancellationToken);
            await ToolRunner.RunAsync("codesign",
                CodesignArguments(identity, signing, keychain, entitlements: true, appPath),
                context.WorkDirectory, cancellationToken);

            await ToolRunner.RunAsync("codesign",
                ["--verify", "--deep", "--strict", "--verbose=4", appPath],
                context.WorkDirectory, cancellationToken);
            if (identity != "-")
            {
                var assessment = await ToolRunner.TryRunAsync("spctl",
                    ["-a", "-t", "execute", "-vv", appPath],
                    context.WorkDirectory, cancellationToken);
                if (assessment is { ExitCode: 0 })
                {
                    logger.Log(BundleLogLevel.Information, "spctl accepted the signed bundle.");
                }
                else
                {
                    logger.Log(BundleLogLevel.Warning,
                        "spctl rejected the bundle (expected for self-signed or untrusted " +
                        "identities): " + (assessment?.StandardError ?? assessment?.StandardOutput ?? "unavailable"));
                }
            }
        }
        finally
        {
            if (keychain is not null)
            {
                await keychain.DisposeAsync(context.WorkDirectory, cancellationToken);
            }
        }

        if (signing.Notarize)
        {
            await NotarizeAppAsync(context, appPath, signing, cancellationToken);
        }
    }

    /// <summary>codesign argument list; separated for test assertion.</summary>
    internal static IReadOnlyList<string> CodesignArguments(
        string identity, MacAppSigningConfiguration signing,
        MacSigningSupport.TemporaryKeychain? keychain, bool entitlements, string target) =>
        MacSigningSupport.CodesignArguments(
            identity, signing.HardenedRuntime, signing.EntitlementsFile, entitlements, keychain, target);

    /// <summary>嵌套 bundle（.app/.framework/…）的 inside-out 递归签名：内部散文件→更深的嵌套→bundle 根。</summary>
    private static async Task SignNestedBundleAsync(
        BundleBuildContext context, MacAppSigningConfiguration signing, string identity,
        MacSigningSupport.TemporaryKeychain? keychain, string bundlePath, CancellationToken cancellationToken)
    {
        // .app 的载荷在 Contents/ 下；.framework 无 Contents，载荷在
        // Versions/Current 下（符号链接，枚举时自然穿透）；扁平嵌套包退回包根。
        var contentsDirectory = new[]
            {
                Path.Combine(bundlePath, "Contents"),
                Path.Combine(bundlePath, "Versions", "Current"),
                bundlePath,
            }
            .First(Directory.Exists);
        {
            var deeper = FindNestedBundles(contentsDirectory);
            var loose = Directory
                .EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories)
                .Where(file => CodeDirectoryNames.Any(dir =>
                    file.StartsWith(
                        Path.Combine(contentsDirectory, dir) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
                .Where(file => !deeper.Any(bundle =>
                    file.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
            foreach (var file in loose)
            {
                await ToolRunner.RunAsync("codesign",
                    CodesignArguments(identity, signing, keychain, entitlements: false, file),
                    context.WorkDirectory, cancellationToken);
            }
            foreach (var nested in deeper)
            {
                await SignNestedBundleAsync(context, signing, identity, keychain, nested, cancellationToken);
            }
        }
        // bundle 根签名封住其主可执行与资源——不叠 entitlements（属主 app 的旋钮）。
        await ToolRunner.RunAsync("codesign",
            CodesignArguments(identity, signing, keychain, entitlements: false, bundlePath),
            context.WorkDirectory, cancellationToken);
    }

    /// <summary>找出 Contents/ 下的顶层嵌套 bundle；更深一层的由递归自己发现。</summary>
    private static IReadOnlyList<string> FindNestedBundles(string contentsDirectory)
    {
        var bundles = new List<string>();
        var claimed = new List<string>();
        foreach (var directory in Directory
            .EnumerateDirectories(contentsDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (claimed.Any(bundle =>
                    directory.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                continue;
            }
            if (NestedBundleExtensions.Any(extension =>
                    directory.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                bundles.Add(directory);
                claimed.Add(directory);
            }
        }
        return bundles;
    }

    /// <summary>ditto → shared notary submit/staple. The zip is always cleaned up.</summary>
    private static async Task NotarizeAppAsync(
        BundleBuildContext context, string appPath, MacAppSigningConfiguration signing,
        CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(
            context.WorkDirectory, Path.GetFileName(appPath) + ".notarize.zip");
        try
        {
            context.Logger.Log(BundleLogLevel.Information, "Archiving the bundle for notarization (ditto -c -k --keepParent).");
            await ToolRunner.RunAsync(
                "ditto", ["-c", "-k", "--keepParent", appPath, zipPath],
                context.WorkDirectory, cancellationToken);
            await MacSigningSupport.NotarizeAsync(
                context, zipPath, stapleTarget: appPath,
                signing.NotaryWait, signing.SkipStapling, signing.KeychainProfile,
                signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
                signing.AppleId, signing.ApplePassword, signing.AppleTeamId,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }
}

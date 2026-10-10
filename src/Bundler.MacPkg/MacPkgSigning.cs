using DotNet.Bundler;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacPkg;

/// <summary>
/// Optional Developer ID Installer signing + notarization for the produced .pkg.
/// There is no ad-hoc signing for installer packages: a real identity (or an imported
/// certificate) signs via <c>pkgbuild --sign</c>/<c>productsign --sign</c>, and the
/// signed package itself is the artifact Gatekeeper/notarytool acts on.
/// </summary>
internal static class MacPkgSigning
{
    internal static bool Configured(MacPkgSigningConfiguration signing) =>
        signing.Identity is not null || !string.IsNullOrEmpty(signing.TemporaryCertificateFile);

    /// <summary>Preflight checks; the bundler calls this before any payload work.</summary>
    internal static void Validate(MacPkgSigningConfiguration signing)
    {
        if (signing.Identity is not null && !string.IsNullOrEmpty(signing.TemporaryCertificateFile))
        {
            throw new ArgumentException(
                "MacPkg Signing.Identity and TemporaryCertificateFile are mutually exclusive.");
        }
        if (signing.Identity is { Length: 0 } or "-")
        {
            throw new ArgumentException(
                "MacPkg Signing.Identity must be a real Developer ID Installer identity; " +
                ".pkg has no ad-hoc signature equivalent.");
        }
        if (signing.TemporaryCertificateFile is { Length: > 0 } certificatePath &&
            !File.Exists(Path.GetFullPath(certificatePath)))
        {
            throw new FileNotFoundException(
                "MacPkg Signing.TemporaryCertificateFile does not exist.", certificatePath);
        }
        if (signing.Notarize)
        {
            if (!Configured(signing))
            {
                throw new ArgumentException(
                    ".pkg notarization requires a signing identity; the signed package " +
                    "is what notarytool and Gatekeeper evaluate.");
            }
            MacAppSigning.ResolveCredentials(
                signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiIssuer,
                signing.AppleId, signing.ApplePassword, signing.AppleTeamId);
        }
        else if (signing.SkipStapling || !signing.NotaryWait)
        {
            throw new ArgumentException("SkipStapling/NotaryWait require Notarize to be enabled.");
        }
    }

    /// <summary>
    /// Open a temporary keychain when a certificate is supplied, else return the configured
    /// identity. The caller disposes the returned keychain.
    /// </summary>
    internal static async Task<(string Identity, MacAppSigning.TemporaryKeychain? Keychain)>
        ResolveIdentityAsync(
            BundleBuildContext context, MacPkgSigningConfiguration signing,
            CancellationToken cancellationToken)
    {
        if (signing.TemporaryCertificateFile is { Length: > 0 } certificatePath)
        {
            var keychain = await MacAppSigning.TemporaryKeychain.CreateAsync(
                context.WorkDirectory, Path.GetFullPath(certificatePath),
                signing.TemporaryCertificatePassword ?? "", context.Logger, cancellationToken);
            return (keychain.Identity, keychain);
        }
        return (signing.Identity!, null);
    }

    /// <summary>pkgbuild flags appended when the component package is signed at build time.</summary>
    internal static IReadOnlyList<string> PkgbuildSignArguments(
        string identity, MacAppSigning.TemporaryKeychain? keychain)
    {
        var arguments = new List<string> { "--sign", identity, "--timestamp" };
        if (keychain is not null)
        {
            arguments.Add("--keychain");
            arguments.Add(keychain.Path);
        }
        return arguments;
    }

    /// <summary>Sign a product archive in place via productsign (unsigned → signed rename).</summary>
    internal static async Task SignProductAsync(
        BundleBuildContext context, string packagePath, string identity,
        MacAppSigning.TemporaryKeychain? keychain, CancellationToken cancellationToken)
    {
        var unsignedPath = Path.Combine(context.WorkDirectory, "unsigned.pkg");
        File.Move(packagePath, unsignedPath);
        var arguments = new List<string> { "--sign", identity };
        if (keychain is not null)
        {
            arguments.Add("--keychain");
            arguments.Add(keychain.Path);
        }
        arguments.Add(unsignedPath);
        arguments.Add(packagePath);
        await MacPkgProcessRunner.RunAsync(
            "productsign", arguments, context.WorkDirectory, cancellationToken);
        File.Delete(unsignedPath);
    }

    /// <summary>notarytool submit → stapler on the .pkg itself (no ditto: pkg is a flat archive).</summary>
    internal static async Task NotarizeAsync(
        BundleBuildContext context, string packagePath, MacPkgSigningConfiguration signing,
        CancellationToken cancellationToken)
    {
        var credentials = MacAppSigning.ResolveCredentials(
            signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId);
        var submitArguments = new List<string> { "notarytool", "submit", packagePath };
        submitArguments.AddRange(credentials);
        submitArguments.Add("--output-format");
        submitArguments.Add("json");
        if (signing.NotaryWait)
        {
            submitArguments.Add("--wait");
        }
        context.Logger.Log(BundleLogLevel.Information,
            signing.NotaryWait
                ? "Submitting the .pkg to notarytool and waiting for the verdict."
                : "Submitting the .pkg to notarytool without waiting (stapling is skipped).");
        var result = await MacPkgProcessRunner.TryRunAsync(
            "xcrun", submitArguments, context.WorkDirectory, cancellationToken)
            ?? throw new InvalidOperationException(
                "xcrun/notarytool is unavailable; notarization requires Xcode 13+ (host macOS 11.3+).");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"notarytool submission failed.{Environment.NewLine}" +
                $"{result.StandardOutput}{result.StandardError}".TrimEnd());
        }
        var submissionId = MacAppSigning.NotarySubmissionId(result.StandardOutput);
        if (submissionId is not null)
        {
            context.Logger.Log(BundleLogLevel.Information, $"notarytool submission id: {submissionId}");
        }
        if (!signing.NotaryWait || signing.SkipStapling)
        {
            return;
        }
        await MacPkgProcessRunner.RunAsync(
            "xcrun", ["stapler", "staple", packagePath],
            context.WorkDirectory, cancellationToken);
        context.Logger.Log(BundleLogLevel.Information, "stapler attached the notarization ticket.");
    }
}

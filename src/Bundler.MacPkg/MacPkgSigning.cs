using DotNet.Bundler;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacPkg;

/// <summary>
/// Optional Developer ID Installer signing + notarization for the produced .pkg.
/// There is no ad-hoc signing for installer packages: a real identity (or an imported
/// certificate) signs via <c>pkgbuild --sign</c>/<c>productsign --sign</c>, and the
/// signed package itself is the artifact Gatekeeper/notarytool acts on.
/// Credential/keychain/tool-argument machinery is shared via <see cref="MacSigningSupport"/>.
/// </summary>
internal static class MacPkgSigning
{
    internal static bool Configured(MacPkgSigningConfiguration signing) =>
        signing.Identity is not null || !string.IsNullOrEmpty(signing.TemporaryCertificateFile);

    /// <summary>Preflight checks; the bundler calls this before any payload work.</summary>
    internal static void Validate(MacPkgSigningConfiguration signing)
    {
        MacSigningSupport.ValidateIdentityAndCertificate(
            signing.Identity, signing.TemporaryCertificateFile, allowAdHoc: false, ".pkg");
        MacSigningSupport.ValidateNotarizationKnobs(
            signing.Notarize, Configured(signing), adHocIdentity: false,
            signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId,
            signing.SkipStapling, signing.NotaryWait,
            ".pkg notarization requires a signing identity; the signed package " +
            "is what notarytool and Gatekeeper evaluate.");
    }

    /// <summary>
    /// Open a temporary keychain when a certificate is supplied, else return the configured
    /// identity. The caller disposes the returned keychain.
    /// </summary>
    internal static Task<(string Identity, MacSigningSupport.TemporaryKeychain? Keychain)>
        ResolveIdentityAsync(
            BundleBuildContext context, MacPkgSigningConfiguration signing,
            CancellationToken cancellationToken) =>
        MacSigningSupport.ResolveIdentityAsync(
            context.WorkDirectory, context.Logger, signing.Identity,
            signing.TemporaryCertificateFile, signing.TemporaryCertificatePassword,
            cancellationToken);

    /// <summary>Sign a product archive in place via productsign (unsigned → signed rename).</summary>
    internal static async Task SignProductAsync(
        BundleBuildContext context, string packagePath, string identity,
        MacSigningSupport.TemporaryKeychain? keychain, CancellationToken cancellationToken)
    {
        var unsignedPath = Path.Combine(context.WorkDirectory, "unsigned.pkg");
        File.Move(packagePath, unsignedPath);
        var arguments = new List<string>(
            MacSigningSupport.ProductsignArguments(identity, keychain))
        {
            unsignedPath,
            packagePath
        };
        await ToolRunner.RunAsync(
            "productsign", arguments, context.WorkDirectory, cancellationToken);
        File.Delete(unsignedPath);
    }

    /// <summary>notarytool submit → stapler on the .pkg itself (no ditto: pkg is a flat archive).</summary>
    internal static Task NotarizeAsync(
        BundleBuildContext context, string packagePath, MacPkgSigningConfiguration signing,
        CancellationToken cancellationToken) =>
        MacSigningSupport.NotarizeAsync(
            context, packagePath, stapleTarget: packagePath,
            signing.NotaryWait, signing.SkipStapling, signing.KeychainProfile,
            signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId,
            cancellationToken);
}

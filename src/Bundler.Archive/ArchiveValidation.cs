using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Archive;

/// <summary>
/// The archive configuration validation shared by <see cref="ArchiveBundler.Validate"/>
/// and the build path: package/archive names, mapped file knobs, and the
/// per-target architecture token all raise at validate time exactly the
/// exceptions Build would produce.
/// </summary>
internal static class ArchiveValidation
{
    internal static void Settings(ArchiveBundleConfiguration settings, BundleConfiguration bundle)
    {
        ArchiveIdentity.PackageName(bundle, settings);
        if (settings.ArchiveName is { } name)
        {
            ArchiveIdentity.ValidateStem(name);
        }
        foreach (var file in settings.Files ?? [])
        {
            ArchiveTree.ValidateDestination(file.Destination);
            ArchiveTree.RequireExisting(file.Source);
        }
        foreach (var target in bundle.Targets)
        {
            if (ArtifactNaming.ArchToken(BundleTarget.Parse(target.Target), PackageFormat.Zip) is null)
            {
                throw new ArgumentException(
                    $"Architecture of target '{target.Target}' is not supported for archives.");
            }
        }
    }
}

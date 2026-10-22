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
        if (settings.ArchiveName is { } name)
        {
            ArchiveIdentity.ValidateStem(name);
        }
        foreach (var file in settings.Files ?? [])
        {
            ArchiveTree.ValidateDestination(file.Destination);
            ArchiveTree.RequireExisting(file.Source);
        }
        var packageName = ArchiveIdentity.PackageName(bundle, settings);
        var version = settings.Version ?? bundle.Version;
        foreach (var target in bundle.Targets)
        {
            var parsed = BundleTarget.Parse(target.Target);
            if (ArtifactNaming.ArchToken(parsed, PackageFormat.Zip) is null)
            {
                throw new ArgumentException(
                    $"Architecture of target '{target.Target}' is not supported for archives.");
            }
            // version 参与默认 stem 组成——含 '/'、空白或 '..' 的版本在 Build 才炸；
            // 把合成 stem 的校验提前到 Validate，与显式 ArchiveName 同缝。
            ArchiveIdentity.ArchiveStem(settings, packageName, version, parsed);
        }
    }
}

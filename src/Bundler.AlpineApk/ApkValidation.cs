using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// The apk configuration validation shared by <see cref="AlpineApkBundler.Validate"/>
/// and <see cref="ApkPackageWriter.Build"/>: <see cref="Settings"/> checks
/// everything independent of the build tree, <see cref="Target"/> resolves and
/// validates one target's parameters. Both throw at validate time exactly the
/// exceptions Build would raise.
/// </summary>
internal static class ApkValidation
{
    /// <summary>Per-target resolved parameters, already validated.</summary>
    internal sealed record Resolved(
        string PackageName,
        string PkgVer,
        string Architecture,
        string InstallRoot,
        string BinLink,
        string Origin,
        string Description,
        string Url,
        string License,
        string BuildDate);

    /// <summary>
    /// Target-independent checks: script files (exist, non-empty, LF, no BOM),
    /// the signing key/passphrase pair, list fields, single-line .PKGINFO
    /// fields, and every referenced file knob source on disk.
    /// </summary>
    internal static void Settings(AlpineApkBundleConfiguration settings, BundleConfiguration bundle)
    {
        foreach (var (path, name) in ApkPackageWriter.ScriptKnobs(settings))
        {
            if (path is not null)
            {
                ApkPackageWriter.ReadScript(path, name);
            }
        }

        var hasKey = settings.Signing.KeyFile is { Length: > 0 };
        if (settings.Signing.Passphrase is { Length: > 0 } && !hasKey)
        {
            throw new ArgumentException(
                "Signing.Passphrase requires Signing.KeyFile to point at a PEM RSA private key.");
        }
        if (hasKey)
        {
            if (!File.Exists(settings.Signing.KeyFile!))
            {
                throw new FileNotFoundException(
                    $"The .apk signing key does not exist: {settings.Signing.KeyFile}",
                    settings.Signing.KeyFile);
            }
            // 成员名推导的可空基名检查与构建同链。
            ApkPackageWriter.SignatureMemberName(settings.Signing.KeyFile!);
        }

        foreach (var depend in settings.Depends ?? [])
        {
            ApkIdentity.ValidateListEntry(depend, nameof(settings.Depends));
        }
        foreach (var provide in settings.Provides ?? [])
        {
            ApkIdentity.ValidateListEntry(provide, nameof(settings.Provides));
        }
        foreach (var trigger in settings.Triggers ?? [])
        {
            if (!trigger.StartsWith("/", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Trigger directories must be absolute paths: '{trigger}'.");
            }
            ApkIdentity.ValidateListEntry(trigger, nameof(settings.Triggers));
        }
        if (settings.ExtraPkgInfo is not null)
        {
            foreach (var pair in settings.ExtraPkgInfo)
            {
                if (!pair.Key.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ||
                    pair.Key.Length == 0)
                {
                    throw new ArgumentException($"Invalid .PKGINFO key: '{pair.Key}'.");
                }
                if (BuiltInFields.Contains(pair.Key, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"The .PKGINFO key '{pair.Key}' collides with a built-in field.");
                }
            }
        }

        // .PKGINFO fields are single-line — surface the shape check early.
        foreach (var (knob, value) in new (string, string?)[]
        {
            ("Description", settings.Description), ("Url", settings.Url),
            ("License", settings.License), ("Origin", settings.Origin)
        })
        {
            if (value is { } v && v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new ArgumentException($"The .apk {knob} must be a single line.");
            }
        }

        foreach (var file in settings.Files ?? [])
        {
            FreedesktopFiles.NormalizeAbsoluteDestination(file.Destination, "alpineapk");
            var source = Path.GetFullPath(file.Source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"The .apk mapped file does not exist: {source}", source);
            }
            if (!UnixFileTypes.IsRegularFile(source))
            {
                throw new ArgumentException(
                    $"The .apk mapped file must be a regular file: {source}");
            }
        }
    }

    /// <summary>
    /// Resolves the per-target parameters exactly as <see cref="ApkPackageWriter.Build"/>
    /// consumes them — the returned values are already valid.
    /// </summary>
    internal static Resolved Target(
        AlpineApkBundleConfiguration settings, BundleConfiguration bundle, BundleTarget target)
    {
        var packageName = settings.PackageName ?? ApkIdentity.SanitizeName(bundle.ProductName);
        ApkIdentity.ValidateName(packageName);
        var upstream = ApkIdentity.MapVersion(settings.Version ?? bundle.Version);
        var release = settings.Release ?? "0";
        ApkIdentity.ValidateNonNegativeInteger(release, "Release");
        var architecture = settings.Architecture ?? ApkIdentity.MapArchitecture(target);
        ApkIdentity.ValidateArchitecture(architecture);
        var buildDate = settings.BuildDate ?? "0";
        ApkIdentity.ValidateNonNegativeInteger(buildDate, "BuildDate");
        var binLink = settings.BinLink ?? packageName;
        // 与 deb/rpm/appimage 同规："none" 是关闭语义，不产字面 usr/bin/none 链接。
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
        }
        if (binLink.Length > 0)
        {
            binLink = LinuxLinkName.Validate(binLink, "AlpineApkBinLink");
        }
        var description = settings.Description ?? bundle.Description ?? "";
        if (description.Length == 0)
        {
            description = bundle.ProductName;
        }
        // .PKGINFO 字段单行——校验落在解析后值上，bundle.Description/Homepage
        // 回退源同样被门住（否则 validate 过、Build 才在 Field() 抛）。
        var url = settings.Url ?? bundle.Homepage ?? "";
        var origin = settings.Origin ?? packageName;
        var license = settings.License ?? "";
        foreach (var (knob, value) in new (string, string)[]
        {
            ("Description", description), ("Url", url),
            ("License", license), ("Origin", origin)
        })
        {
            if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new ArgumentException($"The .apk {knob} must be a single line.");
            }
        }
        return new Resolved(
            packageName, upstream + "-r" + release, architecture,
            "usr/lib/" + packageName, binLink,
            origin, description, url, license, buildDate);
    }

    internal static readonly string[] BuiltInFields =
    [
        "pkgname", "pkgver", "pkgdesc", "url", "arch", "origin", "license",
        "depend", "provides", "triggers", "builddate", "size", "datahash"
    ];
}

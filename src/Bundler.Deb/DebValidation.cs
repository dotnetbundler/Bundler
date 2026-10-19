using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Deb;

/// <summary>
/// The deb configuration validation shared by the public facade
/// (<see cref="DebBundler.Validate"/>) and <see cref="DebPackageWriter.Build"/>:
/// <see cref="Settings"/> checks everything that does not depend on the build
/// tree, <see cref="Target"/> resolves and validates one target's parameters.
/// Both throw the same exceptions at validate time that Build would raise.
/// </summary>
internal static class DebValidation
{
    /// <summary>Per-target resolved parameters, already validated.</summary>
    internal sealed record Resolved(
        string PackageName,
        string Version,
        string Architecture,
        string InstallRoot,
        string BinLink,
        string Maintainer);

    /// <summary>
    /// Target-independent checks: script files (exist, LF, shebang), control
    /// field shapes (priority/section/relations/homepage), and every referenced
    /// file knob source existing on disk.
    /// </summary>
    internal static void Settings(DebBundleConfiguration settings, BundleConfiguration bundle)
    {
        foreach (var (file, knob) in new (string?, string)[]
        {
            (settings.PreInstallFile, "PreInstallFile"),
            (settings.PostInstallFile, "PostInstallFile"),
            (settings.PreUninstallFile, "PreUninstallFile"),
            (settings.PostUninstallFile, "PostUninstallFile")
        })
        {
            if (file is { Length: > 0 })
            {
                DebPackageWriter.ReadScript(file, knob);
            }
        }

        var priority = (settings.Priority ?? "optional").Trim();
        if (priority is not ("required" or "important" or "standard" or "optional" or "extra"))
        {
            throw new ArgumentException(
                $"'{priority}' is not a valid Debian priority (required|important|standard|optional|extra).");
        }
        if (settings.Section is { Length: > 0 } section &&
            !SectionPattern.IsMatch(section.Trim()))
        {
            throw new ArgumentException(
                $"'{section.Trim()}' is not a valid Debian archive section (lowercase letters, digits, '+', '-', '.'; optional '/' subsections).");
        }
        foreach (var (field, clauses) in new (string, IReadOnlyList<string>?)[]
        {
            ("Depends", settings.Depends), ("Recommends", settings.Recommends),
            ("Provides", settings.Provides), ("Conflicts", settings.Conflicts),
            ("Replaces", settings.Replaces)
        })
        {
            if (clauses is { Count: > 0 } && clauses.Any(
                clause => clause.Trim().Length == 0 || clause.IndexOfAny(new[] { '\r', '\n' }) >= 0))
            {
                throw new ArgumentException($"The .deb {field} entries must be non-empty single lines.");
            }
        }
        if (!string.IsNullOrWhiteSpace(bundle.Homepage) &&
            bundle.Homepage!.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("The homepage must be a single line.");
        }

        // Referenced files must exist; destinations must be canonical absolute paths.
        foreach (var (file, knob) in new (string?, string)[]
        {
            (settings.DesktopFile, "DesktopFile"),
            (settings.MetainfoFile, "MetainfoFile"),
            (settings.ChangelogFile, "ChangelogFile"),
            (settings.SystemdServiceFile, "SystemdServiceFile"),
            (bundle.LicenseFile, "LicenseFile")
        })
        {
            if (file is { Length: > 0 })
            {
                FreedesktopFiles.RequireExisting(file, knob);
            }
        }
        foreach (var icon in bundle.Icons)
        {
            FreedesktopFiles.RequireExisting(icon, "Icons");
        }
        foreach (var file in settings.Files ?? [])
        {
            FreedesktopFiles.NormalizeAbsoluteDestination(file.Destination, "deb");
            FreedesktopFiles.RequireExisting(file.Source, "DebFile");
        }
        foreach (var path in settings.ConfigLocations ?? [])
        {
            DebPackageWriter.ValidateConffileDestination(path, "Conffiles");
        }
    }

    /// <summary>
    /// Resolves the per-target parameters exactly as <see cref="DebPackageWriter.Build"/>
    /// consumes them — the returned values are already valid.
    /// </summary>
    internal static Resolved Target(
        DebBundleConfiguration settings, BundleConfiguration bundle, BundleTarget target)
    {
        var packageName = settings.PackageName ?? DebName.Sanitize(bundle.ProductName);
        DebName.Validate(packageName);
        var version = DebVersion.Map(bundle.Version, settings);
        var architecture = settings.Architecture ?? MapArchitecture(target);
        ValidateArchitecture(architecture);
        var installRoot = NormalizeInstallRoot(settings.InstallRoot, packageName);
        var binLink = settings.BinLink ?? packageName;
        // 与 rpm/appimage/apk 同规："none" 是关闭语义，不产字面 usr/bin/none 链接。
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
        }
        if (binLink.Length > 0)
        {
            binLink = LinuxLinkName.Validate(binLink, "DebBinLink");
        }
        var maintainer = settings.Vendor ?? bundle.Publisher ?? bundle.Identifier;
        if (string.IsNullOrWhiteSpace(maintainer) || maintainer.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "The .deb maintainer must be a non-empty single line; set Publisher or Vendor.");
        }
        return new Resolved(packageName, version, architecture, installRoot, binLink, maintainer);
    }

    private static string MapArchitecture(BundleTarget target) =>
        ArtifactNaming.ArchToken(target, PackageFormat.Deb)
        ?? throw new NotSupportedException(
            $"No Debian architecture mapping for {target.Architecture}; set Architecture explicitly.");

    private static void ValidateArchitecture(string architecture)
    {
        if (!architecture.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-') ||
            architecture.Length == 0)
        {
            throw new ArgumentException(
                $"'{architecture}' is not a valid Debian architecture (lowercase letters, digits, '-').");
        }
    }

    // Returns the tar-relative path (no leading '/'), e.g. "usr/lib/pkg".
    private static string NormalizeInstallRoot(string? installRoot, string packageName)
    {
        var root = string.IsNullOrWhiteSpace(installRoot)
            ? "/usr/lib/" + packageName
            : installRoot!.Trim();
        if (!root.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The .deb install root must be an absolute path: '{root}'.");
        }
        var normalized = root.Trim('/');
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                $"The .deb install root contains an invalid segment: '{root}'.");
        }
        return normalized;
    }

    private static readonly System.Text.RegularExpressions.Regex SectionPattern = new(
        "^[a-z0-9][a-z0-9+.-]*(/[a-z0-9][a-z0-9+.-]*)*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}

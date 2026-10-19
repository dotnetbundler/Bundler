using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// The rpm configuration validation shared by <see cref="RpmBundler.Validate"/>
/// and <see cref="RpmPackageWriter.Build"/>: <see cref="Settings"/> checks
/// everything independent of the build tree, <see cref="Target"/> resolves and
/// validates one target's parameters. Both throw at validate time exactly the
/// exceptions Build would raise.
/// </summary>
internal static class RpmValidation
{
    /// <summary>Per-target resolved parameters, already validated.</summary>
    internal sealed record Resolved(
        string PackageName,
        RpmVersion.Mapped Version,
        string Architecture,
        string InstallRoot,
        string BinLink,
        string Vendor);

    /// <summary>
    /// Target-independent checks: single-line header fields, the signing
    /// key/passphrase pair, scriptlet files (exist + LF) and *Program knobs,
    /// dependency clause syntax, and every referenced file knob source on disk.
    /// </summary>
    internal static void Settings(RpmBundleConfiguration settings, BundleConfiguration bundle)
    {
        var vendor = settings.Vendor ?? bundle.Publisher ?? bundle.Identifier;
        if (string.IsNullOrWhiteSpace(vendor) || vendor.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "The .rpm vendor must be a non-empty single line; set Publisher or Vendor.");
        }
        foreach (var (knob, value) in new[]
        {
            ("License", settings.License), ("Group", settings.Group),
            ("Url", (settings.Url ?? bundle.Homepage))
        })
        {
            if (value is { } v && v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new ArgumentException($"The .rpm {knob} must be a single line.");
            }
        }

        ValidateSigning(settings);

        foreach (var (file, program, knob) in new (string?, string?, string)[]
        {
            (settings.PreInstallFile, settings.PreInstallProgram, "PreInstall"),
            (settings.PostInstallFile, settings.PostInstallProgram, "PostInstall"),
            (settings.PreUninstallFile, settings.PreUninstallProgram, "PreUninstall"),
            (settings.PostUninstallFile, settings.PostUninstallProgram, "PostUninstall")
        })
        {
            if (file is { Length: > 0 })
            {
                RpmPackageWriter.ReadScriptlet(file, knob + "File");
            }
            if (program is { } p &&
                (p.Trim().Length == 0 || p.IndexOfAny(new[] { '\r', '\n' }) >= 0))
            {
                throw new ArgumentException(
                    $"The .rpm {knob}Program must be a non-empty single line.");
            }
        }

        // Dependency clauses are parsed into the header — surface syntax errors
        // at validate time, not mid-build.
        foreach (var (field, clauses) in new (string, IReadOnlyList<string>?)[]
        {
            ("Depends", settings.Depends), ("Provides", settings.Provides),
            ("Conflicts", settings.Conflicts), ("Obsoletes", settings.Obsoletes),
            ("Recommends", settings.Recommends), ("Suggests", settings.Suggests)
        })
        {
            foreach (var clause in clauses ?? [])
            {
                RpmDependency.Parse(clause);
            }
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
            FreedesktopFiles.NormalizeAbsoluteDestination(file.Destination, "rpm");
            FreedesktopFiles.RequireExisting(file.Source, "RpmFile");
        }
        foreach (var path in settings.ConfigLocations ?? [])
        {
            FreedesktopFiles.NormalizeAbsoluteDestination(path, "rpm");
        }
    }

    /// <summary>
    /// Resolves the per-target parameters exactly as <see cref="RpmPackageWriter.Build"/>
    /// consumes them — the returned values are already valid.
    /// </summary>
    internal static Resolved Target(
        RpmBundleConfiguration settings, BundleConfiguration bundle, BundleTarget target)
    {
        var packageName = settings.PackageName ?? RpmName.Sanitize(bundle.ProductName);
        RpmName.Validate(packageName);
        var mapped = RpmVersion.Map(bundle.Version, settings);
        var architecture = settings.Architecture ?? RpmPackageWriter.MapArchitecture(target);
        ValidateArchitecture(architecture);
        var installRoot = NormalizeInstallRoot(settings.InstallRoot, packageName);
        var binLink = settings.BinLink ?? packageName;
        // 与 deb/appimage/apk 同规："none" 是关闭语义，不产字面 usr/bin/none 链接。
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
        }
        if (binLink.Length > 0)
        {
            binLink = LinuxLinkName.Validate(binLink, "RpmBinLink");
        }
        var vendor = settings.Vendor ?? bundle.Publisher ?? bundle.Identifier;
        if (string.IsNullOrWhiteSpace(vendor) || vendor.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "The .rpm vendor must be a non-empty single line; set Publisher or Vendor.");
        }
        return new Resolved(packageName, mapped, architecture, installRoot, binLink, vendor!);
    }

    private static void ValidateSigning(RpmBundleConfiguration settings)
    {
        var hasKey = settings.Signing.KeyFile is { Length: > 0 };
        var hasPassphrase = settings.Signing.Passphrase is { Length: > 0 };
        if (hasPassphrase && !hasKey)
        {
            throw new ArgumentException(
                "Signing.Passphrase requires Signing.KeyFile to point at an OpenPGP secret key.");
        }
        if (hasKey && !File.Exists(settings.Signing.KeyFile!))
        {
            throw new ArgumentException(
                $"Signing.KeyFile '{settings.Signing.KeyFile}' does not exist.");
        }
    }

    private static void ValidateArchitecture(string architecture)
    {
        if (architecture.Length == 0 || !architecture.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"'{architecture}' is not a valid RPM architecture (letters, digits, '_').");
        }
    }

    private static string NormalizeInstallRoot(string? installRoot, string packageName)
    {
        var root = string.IsNullOrWhiteSpace(installRoot)
            ? "/usr/lib/" + packageName
            : installRoot!.Trim();
        // 与 deb 同规：绝对路径 + 每段非空、非 . / ..、不含空白。
        if (!root.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The .rpm install root must be an absolute path: '{root}'.");
        }
        var normalized = root.Trim('/');
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(segment =>
                segment is "" or "." or ".." || segment.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException(
                $"The .rpm install root contains an invalid segment: '{root}'.");
        }
        // 调用方按 installRoot + "/" + relative 拼绝对路径——前导斜杠必须保留。
        return "/" + normalized;
    }
}

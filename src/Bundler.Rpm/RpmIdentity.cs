using System.Text;
using System.Text.RegularExpressions;

namespace DotNet.Bundler.Rpm;

/// <summary>RPM package-name normalization and validation.</summary>
internal static class RpmName
{
    // RPM names: no whitespace; allowed letters, digits, '+', '-', '.', '_'.
    private static readonly Regex ValidName = new(
        "^[A-Za-z0-9][A-Za-z0-9+._-]*$", RegexOptions.Compiled);

    internal static string Sanitize(string productName)
    {
        var builder = new StringBuilder(productName.Length);
        var pendingSeparator = false;
        foreach (var raw in productName.Trim().ToLowerInvariant())
        {
            var c = raw == '_' ? '-' : raw;
            var valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c == '+' || c == '-' || c == '.';
            if (valid)
            {
                if (pendingSeparator && builder.Length > 0 && builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
                pendingSeparator = false;
                builder.Append(c);
            }
            else
            {
                pendingSeparator = builder.Length > 0;
            }
        }
        var name = builder.ToString().Trim('-', '+', '.');
        if (!ValidName.IsMatch(name))
        {
            throw new ArgumentException(
                $"Cannot derive an RPM package name from product name '{productName}'; " +
                "set an explicit package name (RpmBundleConfiguration.PackageName / BundlerRpmPackageName).");
        }
        return name;
    }

    internal static void Validate(string packageName)
    {
        if (!ValidName.IsMatch(packageName) || packageName.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"'{packageName}' is not a valid RPM package name: " +
                "use letters, digits, '+', '-', '.', '_' with no whitespace, " +
                "start with an alphanumeric and be at least two characters long.");
        }
    }
}

/// <summary>SemVer → RPM version/release mapping and validation.</summary>
internal static class RpmVersion
{
    // RPM version/release must not contain '-'; allowed: letters, digits, '.', '_', '+'.
    private static readonly Regex ValidField = new(
        "^[0-9A-Za-z.+_]+$", RegexOptions.Compiled);

    internal sealed class Mapped
    {
        internal string Version = "";
        internal string Release = "1";
        internal int Epoch;
    }

    internal static Mapped Map(string semver, RpmBundleConfiguration settings)
    {
        if (settings.Version is { Length: > 0 } explicitVersion)
        {
            ValidateField(explicitVersion, "Version");
            if (settings.Release is { Length: > 0 } rel)
            {
                ValidateField(rel, "Release");
            }
            return new Mapped
            {
                Version = explicitVersion,
                Release = settings.Release is { Length: > 0 } r ? r : "1",
                Epoch = ParseEpoch(settings)
            };
        }

        var core = semver.Trim();
        var build = "";
        var buildIndex = core.IndexOf('+');
        if (buildIndex >= 0)
        {
            build = core.Substring(buildIndex + 1);
            core = core.Substring(0, buildIndex);
        }
        var dashIndex = core.IndexOf('-');
        var version = dashIndex >= 0 ? core.Substring(0, dashIndex) : core;
        var prerelease = dashIndex >= 0 ? core.Substring(dashIndex + 1) : null;

        if (version.Length == 0 || !char.IsDigit(version[0]))
        {
            throw new ArgumentException(
                $"'{semver}' cannot map to an RPM version: the release part must start with a digit.");
        }
        ValidateField(version, "Version");

        string release;
        if (settings.Release is { Length: > 0 } explicitRelease)
        {
            ValidateField(explicitRelease, "Release");
            release = explicitRelease;
        }
        else if (prerelease is not null)
        {
            // Fedora convention: prereleases get Release 0.<n>.<label> so they sort
            // before the release ("0.x" < "1"). '-' inside the label becomes '.'.
            var label = prerelease.Replace('-', '.').Replace('+', '.');
            release = "0.1." + label;
        }
        else
        {
            release = "1";
        }
        if (build.Length > 0)
        {
            release += "+" + build.Replace('-', '.');
        }
        return new Mapped { Version = version, Release = release, Epoch = ParseEpoch(settings) };
    }

    private static int ParseEpoch(RpmBundleConfiguration settings)
    {
        if (settings.Epoch is not { Length: > 0 } epoch)
        {
            return 0;
        }
        if (!epoch.All(char.IsDigit) || !int.TryParse(epoch, out var value) || value < 0)
        {
            throw new ArgumentException(
                $"'{epoch}' is not a valid RPM epoch (non-negative digits only).");
        }
        return value;
    }

    private static void ValidateField(string value, string field)
    {
        if (!ValidField.IsMatch(value) || value.Contains('-'))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid RPM {field}: use letters, digits, '.', '_', '+' — never '-'.");
        }
    }

    // EVR string used in PROVIDES/dependency tags: [epoch:]version-release.
    internal static string Evr(Mapped mapped)
    {
        var evr = mapped.Version + "-" + mapped.Release;
        return mapped.Epoch > 0 ? mapped.Epoch + ":" + evr : evr;
    }
}

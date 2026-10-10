using System.Text;
using System.Text.RegularExpressions;

namespace DotNet.Bundler.Deb;

/// <summary>Debian package-name normalization and validation.</summary>
internal static class DebName
{
    private static readonly Regex ValidName = new(
        "^[a-z0-9][a-z0-9+.-]+$", RegexOptions.Compiled);

    // Kebab-cases a product name into a valid Debian package name.
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
                $"Cannot derive a Debian package name from product name '{productName}'; " +
                "set an explicit package name (DebBundleConfiguration.PackageName / BundlerDebPackageName).");
        }
        return name;
    }

    internal static void Validate(string packageName)
    {
        if (!ValidName.IsMatch(packageName))
        {
            throw new ArgumentException(
                $"'{packageName}' is not a valid Debian package name: " +
                "use lowercase letters, digits, '+', '-', '.', start with an alphanumeric, " +
                "and be at least two characters long.");
        }
    }
}

/// <summary>SemVer → Debian version mapping and version validation.</summary>
internal static class DebVersion
{
    // Debian policy shape: [epoch:]upstream[-revision]; upstream must start with a digit.
    private static readonly Regex ValidVersion = new(
        "^(?:[0-9]+:)?[0-9][0-9A-Za-z.+~]*(?:-[0-9A-Za-z.+~]+)?$", RegexOptions.Compiled);

    private static readonly Regex ValidRevision = new(
        "^[0-9A-Za-z.+~]+$", RegexOptions.Compiled);

    internal static string Map(string semver, DebBundleConfiguration settings)
    {
        if (settings.Version is { Length: > 0 } explicitVersion)
        {
            Validate(explicitVersion);
            return explicitVersion;
        }

        var upstream = MapUpstream(semver);
        var revision = settings.Release ?? "1";
        if (revision.Length > 0 && !ValidRevision.IsMatch(revision))
        {
            throw new ArgumentException(
                $"'{revision}' is not a valid Debian revision (allowed: letters, digits, '+', '.', '~').");
        }
        var version = revision.Length > 0 ? upstream + "-" + revision : upstream;
        if (settings.Epoch is { Length: > 0 } epoch)
        {
            if (!epoch.All(char.IsDigit))
            {
                throw new ArgumentException($"'{epoch}' is not a valid Debian epoch (digits only).");
            }
            version = epoch + ":" + version;
        }
        Validate(version);
        return version;
    }

    // SemVer "1.2.3-alpha.1+build" → Debian upstream "1.2.3~alpha.1+build": '~' makes a
    // prerelease sort *before* its release, which a plain '-' cannot express.
    private static string MapUpstream(string semver)
    {
        var core = semver.Trim();
        var build = "";
        var buildIndex = core.IndexOf('+');
        if (buildIndex >= 0)
        {
            build = core.Substring(buildIndex);
            core = core.Substring(0, buildIndex);
        }
        var dashIndex = core.IndexOf('-');
        var upstream = dashIndex >= 0
            ? core.Substring(0, dashIndex) + "~" + core.Substring(dashIndex + 1)
            : core;
        return upstream + build;
    }

    internal static void Validate(string version)
    {
        if (!ValidVersion.IsMatch(version))
        {
            throw new ArgumentException(
                $"'{version}' is not a valid Debian version " +
                "(shape [epoch:]upstream[-revision]; upstream starts with a digit; " +
                "allowed characters are letters, digits, '.', '+', '~').");
        }
    }

    // The file name drops any epoch prefix (epochs are not filesystem-friendly and
    // Debian archive tools never include them in .deb file names).
    internal static string FileNameVersion(string version)
    {
        var colon = version.IndexOf(':');
        return colon >= 0 ? version.Substring(colon + 1) : version;
    }
}

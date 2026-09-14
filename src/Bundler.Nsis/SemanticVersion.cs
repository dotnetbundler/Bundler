namespace DotNet.Bundler.Nsis;

// Implements the SemVer 2.0 precedence rules without converting numeric identifiers
// to integers, so versions with identifiers larger than Int64 remain comparable.
internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(string major, string minor, string patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    private string Major { get; }
    private string Minor { get; }
    private string Patch { get; }
    private string[] Prerelease { get; }

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var text = value!;
        if (text != text.Trim()) return false;

        var buildSeparator = text.IndexOf('+');
        var withoutBuild = buildSeparator < 0 ? text : text.Substring(0, buildSeparator);
        var build = buildSeparator < 0 ? null : text.Substring(buildSeparator + 1);
        if (build is not null && !IsIdentifierList(build, forbidLeadingZeroes: false))
        {
            return false;
        }

        var prereleaseSeparator = withoutBuild.IndexOf('-');
        var core = prereleaseSeparator < 0
            ? withoutBuild
            : withoutBuild.Substring(0, prereleaseSeparator);
        var prereleaseText = prereleaseSeparator < 0
            ? null
            : withoutBuild.Substring(prereleaseSeparator + 1);
        if (prereleaseText is not null && !IsIdentifierList(prereleaseText, forbidLeadingZeroes: true))
        {
            return false;
        }

        var coreParts = core.Split('.');
        if (coreParts.Length != 3 || coreParts.Any(part => !IsNumericIdentifier(part, forbidLeadingZeroes: true)))
        {
            return false;
        }

        version = new SemanticVersion(
            coreParts[0],
            coreParts[1],
            coreParts[2],
            prereleaseText is null ? Array.Empty<string>() : prereleaseText.Split('.'));
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareNumeric(Major, other.Major);
        if (result == 0) result = CompareNumeric(Minor, other.Minor);
        if (result == 0) result = CompareNumeric(Patch, other.Patch);
        if (result != 0) return result;

        // A release has higher precedence than any prerelease of the same core version.
        if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
        if (other.Prerelease.Length == 0) return -1;

        for (var index = 0; index < Math.Min(Prerelease.Length, other.Prerelease.Length); index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = IsNumericIdentifier(left, forbidLeadingZeroes: false);
            var rightNumeric = IsNumericIdentifier(right, forbidLeadingZeroes: false);
            if (leftNumeric && rightNumeric)
            {
                result = CompareNumeric(left, right);
            }
            else if (leftNumeric != rightNumeric)
            {
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left, right);
            }

            if (result != 0) return Math.Sign(result);
        }

        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    public bool TryGetWindowsNumericVersion(out string numericVersion)
    {
        numericVersion = string.Empty;
        if (!ushort.TryParse(Major, out var major) ||
            !ushort.TryParse(Minor, out var minor) ||
            !ushort.TryParse(Patch, out var patch))
        {
            return false;
        }

        numericVersion = $"{major}.{minor}.{patch}.0";
        return true;
    }

    private static int CompareNumeric(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return left.Length.CompareTo(right.Length);
        }
        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool IsIdentifierList(string value, bool forbidLeadingZeroes)
    {
        var parts = value.Split('.');
        return parts.Length > 0 && parts.All(part =>
            part.Length > 0 &&
            part.All(character => IsAsciiLetterOrDigit(character) || character == '-') &&
            !(forbidLeadingZeroes && IsNumericIdentifier(part, forbidLeadingZeroes: false) &&
              part.Length > 1 && part[0] == '0'));
    }

    private static bool IsNumericIdentifier(string value, bool forbidLeadingZeroes) =>
        value.Length > 0 &&
        value.All(character => character is >= '0' and <= '9') &&
        (!forbidLeadingZeroes || value.Length == 1 || value[0] != '0');

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

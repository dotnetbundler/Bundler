namespace DotNet.Bundler.Updater;

/// <summary>
/// 语义化版本轻量比较：major.minor.patch 数值序 + 预发布后缀（空=正式版高于任何预发布）。
/// 不依赖 NuGet.Versioning——够用且零外部包。
/// </summary>
internal readonly struct UpdateVersion : IComparable<UpdateVersion>
{
    internal readonly int Major;
    internal readonly int Minor;
    internal readonly int Patch;
    internal readonly string Prerelease;

    private UpdateVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major; Minor = minor; Patch = patch; Prerelease = prerelease;
    }

    internal static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var core = text!.Trim();
        // build metadata（+之后全部）不参与比较——先整串截掉再拆预发布，
        // 否则 1.2.3-beta+build2 会把 +build 留在预发布段里当差异。
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core.Substring(0, plus);
        }
        var prerelease = "";
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = core.Substring(dash + 1);
            core = core.Substring(0, dash);
        }
        var parts = core.Split('.');
        if (parts.Length < 2 || parts.Length > 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return false;
        }
        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }
        version = new UpdateVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        // semver 优先级：正式版高于同核预发布；都是预发布按段比——
        // 数值段比数值、数值段低于字母段、前缀相同短列更低（beta.10 > beta.2）。
        if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
        if (other.Prerelease.Length == 0) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < a.Length || i < b.Length; i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var numericA = int.TryParse(a[i], out var numberA);
            var numericB = int.TryParse(b[i], out var numberB);
            if (numericA && numericB)
            {
                var numeric = numberA.CompareTo(numberB);
                if (numeric != 0) return numeric;
            }
            else if (numericA)
            {
                return -1;
            }
            else if (numericB)
            {
                return 1;
            }
            else
            {
                var ordinal = string.CompareOrdinal(a[i], b[i]);
                if (ordinal != 0) return ordinal;
            }
        }
        return 0;
    }
}

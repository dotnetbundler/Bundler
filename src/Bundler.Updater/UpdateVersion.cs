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
        var prerelease = "";
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = core.Substring(dash + 1);
            core = core.Substring(0, dash);
        }
        var plus = core.IndexOf('+'); // build metadata 不参与比较
        if (plus >= 0)
        {
            core = core.Substring(0, plus);
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
        // 正式版高于同核预发布；都是预发布按字符串序（够用粒度）。
        if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
        if (other.Prerelease.Length == 0) return -1;
        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }
}

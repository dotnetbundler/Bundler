using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Alpine package identity: name/version/architecture normalization and
/// validation. apk package names are kebab-style tokens and apk versions
/// never contain '-', so the mapping mirrors the .deb identity helpers.
/// </summary>
internal static class ApkIdentity
{
    internal static string SanitizeName(string productName)
    {
        var lowered = productName.ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }
        var name = builder.ToString().Trim('-');
        while (name.Contains("--"))
        {
            name = name.Replace("--", "-");
        }
        return name;
    }

    internal static void ValidateName(string name)
    {
        if (name.Length == 0 ||
            !char.IsLetterOrDigit(name[0]) ||
            !char.IsLetterOrDigit(name[name.Length - 1]) ||
            !name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '-'))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid Alpine package name ([A-Za-z0-9][A-Za-z0-9._+-]* ending in an alphanumeric).");
        }
    }

    /// <summary>
    /// Maps a SemVer-ish version to an apk upstream version: '-' (which splits
    /// the release component) is not allowed and becomes '_'; everything else
    /// must already be in the apk character set.
    /// </summary>
    internal static string MapVersion(string version)
    {
        var mapped = version.Replace('-', '_');
        if (mapped.Length == 0 ||
            !char.IsLetterOrDigit(mapped[0]) ||
            !mapped.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '~'))
        {
            throw new ArgumentException(
                $"'{version}' is not a valid Alpine upstream version ([A-Za-z0-9][A-Za-z0-9._+~]*, no '-').");
        }
        return mapped;
    }

    internal static string MapArchitecture(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X64 => "x86_64",
        CpuArchitecture.Arm64 => "aarch64",
        CpuArchitecture.X86 => "x86",
        _ => throw new NotSupportedException(
            $"No Alpine architecture mapping for {architecture}; set Architecture explicitly.")
    };

    internal static void ValidateArchitecture(string architecture)
    {
        if (architecture.Length == 0 ||
            !architecture.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"'{architecture}' is not a valid Alpine architecture (lowercase letters, digits, '_').");
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace DotNet.Bundler.AppImage;

internal static class AppImageIdentity
{
    /// <summary>kebab-case normalization shared shape with the deb/rpm backends.</summary>
    internal static string PackageName(string? value, string productName)
    {
        var raw = value ?? productName;
        var normalized = Regex.Replace(raw.Trim(), @"[^A-Za-z0-9+._-]+", "-")
            .Trim('-', '.', '_', '+')
            .ToLowerInvariant();
        if (normalized.Length < 2 ||
            normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '+' or '-' or '.' or '_')))
        {
            throw new ArgumentException(
                $"The AppImage package name must be ≥2 chars of [A-Za-z0-9+._-], got '{raw}'.");
        }
        return normalized;
    }

    /// <summary>
    /// appimagetool <c>ARCH</c> value for a target
    /// (x86_64/aarch64/i686 naming). The <see cref="AppImageBundleConfiguration.Architecture"/>
    /// override is already in this namespace; <c>amd64</c> is normalized to
    /// <c>x86_64</c>.
    /// </summary>
    internal static string EnvironmentArchitecture(BundleTarget target) =>
        target.Architecture switch
        {
            CpuArchitecture.X64 => "x86_64",
            CpuArchitecture.Arm64 => "aarch64",
            CpuArchitecture.X86 => "i686",
            _ => throw new ArgumentException(
                $"Cannot map target '{target.Target}' to an AppImage architecture (x86_64/aarch64/i686).")
        };

    /// <summary>Normalize an <see cref="AppImageBundleConfiguration.Architecture"/> override.</summary>
    internal static string NormalizeArchitecture(string value)
    {
        var arch = value.Trim() == "amd64" ? "x86_64" : value.Trim();
        // i686 无 runtime 随包：收窄到随包供应的两架构，避免配置面承诺兑现不了。
        if (arch is not ("x86_64" or "aarch64"))
        {
            throw new ArgumentException(
                $"BundlerAppImageArchitecture must be one of x86_64/aarch64 (or amd64), got '{value}'.");
        }
        return arch;
    }


}

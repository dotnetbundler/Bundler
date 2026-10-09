using System.Text;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Archive;

internal static class ArchiveIdentity
{
    /// <summary>Package name: knob override or ProductName normalized to kebab-case.</summary>
    internal static string PackageName(BundleConfiguration bundle, ArchiveBundleConfiguration settings)
    {
        var name = settings.PackageName ?? Kebab(bundle.ProductName);
        if (name.Length == 0 || name.Any(c => c == '/' || c == '\\' || c == ':' || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException(
                $"Archive package name must be a single safe name; got '{name}'.");
        }
        return name;
    }

    /// <summary>
    /// The archive base name and its single top-level directory
    /// (<c>&lt;name&gt;-&lt;version&gt;-&lt;rid&gt;</c> by default).
    /// </summary>
    internal static string ArchiveStem(
        ArchiveBundleConfiguration settings, string packageName, string version, BundlePlanItem item)
    {
        var stem = settings.ArchiveName ?? $"{packageName}-{version}-{item.Target.RuntimeIdentifier}";
        // 与 PackageName 同字符集：':'与空白同样会破坏归档顶层目录名。
        if (stem.Length == 0 || stem is "." or ".." || stem.Contains("..") ||
            stem.Any(c => c is '/' or '\\' or ':' || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException(
                $"ArchiveName must be a single safe path segment; got '{stem}'.");
        }
        return stem;
    }

    private static string Kebab(string productName)
    {
        var builder = new StringBuilder(productName.Length);
        foreach (var c in productName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[builder.Length - 1] != '-')
            {
                builder.Append('-');
            }
        }
        return builder.ToString().Trim('-');
    }
}

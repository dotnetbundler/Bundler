using System.Text;

namespace DotNet.Bundler.Core;

/// <summary>
/// Shared Linux package-name normalization used by the deb/rpm backends:
/// lowercase, '_'→'-', runs of disallowed characters collapse to one '-',
/// trim leading/trailing separators. Callers keep their own charset
/// validation and error text.
/// </summary>
internal static class PackageNames
{
    internal static string SanitizeLinuxName(string productName)
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
        return builder.ToString().Trim('-', '+', '.');
    }
}

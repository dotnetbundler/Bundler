namespace DotNet.Bundler.Wix;

internal static class WixPackagePaths
{
    internal static string NormalizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) ||
            target.Contains(":") || target.Contains("\0"))
        {
            throw new ArgumentException("MSI target paths must be non-empty relative paths.");
        }
        var parts = target.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part == "." || part == ".." ||
                              part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)))
        {
            throw new ArgumentException("MSI target path contains an unsafe segment: " + target);
        }
        return string.Join("/", parts);
    }
}

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
                              part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) ||
                              part.IndexOfAny(InvalidCharacters) >= 0 || part.Any(char.IsControl) ||
                              IsReservedDeviceName(part)))
        {
            throw new ArgumentException("MSI target path contains an unsafe segment: " + target);
        }
        return string.Join("/", parts);
    }

    private static readonly char[] InvalidCharacters = "<>:\"/\\|?*".ToCharArray();

    // Windows 设备保留名按首个 '.' 前的基名判定（con.tar.gz 同样被拒绝）。
    private static bool IsReservedDeviceName(string part)
    {
        var name = part.Split('.')[0];
        return name is { Length: >= 3 } && ReservedDeviceNames.Contains(
            name, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] ReservedDeviceNames =
        ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5",
         "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4",
         "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

}

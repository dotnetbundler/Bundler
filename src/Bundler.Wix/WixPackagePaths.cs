using DotNet.Bundler.Core;

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
        // 段判据与 NSIS 同簿——非法字符/控制字符/设备保留名/尾点尾空格由
        // WindowsFileNames.IsInvalidName 一处收口。
        if (parts.Any(part => part.Length == 0 || part == "." || part == ".." ||
                              WindowsFileNames.IsInvalidName(part)))
        {
            throw new ArgumentException("MSI target path contains an unsafe segment: " + target);
        }
        return string.Join("/", parts);
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Nsis.Plugin;

internal static class MsiProducts
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const string VersionProperty = "VersionString";

    internal static string? FindFirst(string productCodes, string upgradeCodes, string displayName, string publisher) =>
        FindAll(productCodes, upgradeCodes, displayName, publisher).FirstOrDefault();

    internal static string? GetNewestVersion(string productCodes, string upgradeCodes, string displayName, string publisher)
    {
        SemanticVersion? newest = null;
        string? newestText = null;
        foreach (var productCode in FindAll(productCodes, upgradeCodes, displayName, publisher))
        {
            var versionText = GetVersion(productCode);
            if (!SemanticVersion.TryParse(versionText ?? string.Empty, out var version))
            {
                // 任一版本无法比较时返回空值，让安装器采用保守策略。
                return null;
            }
            if (newest is null || version!.CompareTo(newest) > 0)
            {
                newest = version;
                newestText = versionText;
            }
        }

        return newestText;
    }

    private static IEnumerable<string> FindAll(string productCodes, string upgradeCodes, string displayName, string publisher)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var productCode in ParseCodes(productCodes))
        {
            if (visited.Add(productCode) && GetVersion(productCode) is not null)
            {
                yield return productCode;
            }
        }

        foreach (var upgradeCode in ParseCodes(upgradeCodes))
        {
            for (uint index = 0; ; index++)
            {
                var buffer = new StringBuilder(39);
                var result = MsiEnumRelatedProducts(upgradeCode, 0, index, buffer);
                if (result == ErrorNoMoreItems)
                {
                    break;
                }
                if (result != ErrorSuccess)
                {
                    break;
                }

                var productCode = NormalizeCode(buffer.ToString());
                if (productCode is not null && visited.Add(productCode) && GetVersion(productCode) is not null)
                {
                    yield return productCode;
                }
            }
        }

        // 可选的按卸载注册项名称/发布者匹配（Tauri 对齐能力，显式启用时才非空）。
        foreach (var productCode in FindByUninstallEntry(displayName, publisher))
        {
            if (visited.Add(productCode) && GetVersion(productCode) is not null)
            {
                yield return productCode;
            }
        }
    }

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const uint KeyWow64_64Key = 0x0100;
    private const uint KeyRead = 0x20019;
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

    private static IEnumerable<string> FindByUninstallEntry(string displayName, string publisher)
    {
        if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(publisher))
        {
            yield break;
        }

        // 32 位插件进程下默认视图只看到 WOW6432 注册项：
        // HKCU 原生视图承载 per-user x64 MSI，HKLM 原生视图承载 per-machine x64 MSI。
        var views = new (IntPtr Root, uint Flags)[]
        {
            (HkeyCurrentUser, 0),
            (HkeyCurrentUser, KeyWow64_64Key),
            (HkeyLocalMachine, 0),
            (HkeyLocalMachine, KeyWow64_64Key),
        };

        foreach (var (root, flags) in views)
        {
            foreach (var productCode in EnumerateUninstallView(root, flags, displayName, publisher))
            {
                yield return productCode;
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallView(
        IntPtr root, uint flags, string displayName, string publisher)
    {
        if (RegOpenKeyEx(root, UninstallKeyPath, 0, KeyRead | flags, out var parent) != ErrorSuccess)
        {
            yield break;
        }

        try
        {
            var name = new StringBuilder(256);
            for (uint index = 0; ; index++)
            {
                name.Length = 0;
                var nameLength = (uint)name.Capacity;
                if (RegEnumKeyEx(parent, index, name, ref nameLength, IntPtr.Zero, null, IntPtr.Zero, out _) != ErrorSuccess)
                {
                    yield break;
                }

                var subPath = UninstallKeyPath + "\\" + name;
                if (RegOpenKeyEx(root, subPath, 0, KeyRead | flags, out var sub) != ErrorSuccess)
                {
                    continue;
                }

                try
                {
                    var entryName = QueryString(sub, "DisplayName");
                    var entryPublisher = QueryString(sub, "Publisher");
                    var uninstallString = QueryString(sub, "UninstallString");
                    if (entryName is null || entryPublisher is null || uninstallString is null ||
                        !entryName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                        !entryPublisher.Equals(publisher, StringComparison.OrdinalIgnoreCase) ||
                        uninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (ExtractProductCode(uninstallString) is { } code)
                    {
                        yield return code;
                    }
                }
                finally
                {
                    RegCloseKey(sub);
                }
            }
        }
        finally
        {
            RegCloseKey(parent);
        }
    }

    private static string? ExtractProductCode(string uninstallString)
    {
        var open = uninstallString.IndexOf('{');
        var close = open >= 0 ? uninstallString.IndexOf('}', open + 1) : -1;
        return open >= 0 && close > open
            ? NormalizeCode(uninstallString.Substring(open, close - open + 1))
            : null;
    }

    private static string? QueryString(IntPtr key, string valueName)
    {
        uint type = 0;
        uint length = 0;
        // REG_SZ=1；MSI 把 UninstallString/ModifyPath 写成 REG_EXPAND_SZ=2，两者都按字符串处理。
        if (RegQueryValueEx(key, valueName, IntPtr.Zero, ref type, null, ref length) != ErrorSuccess ||
            (type != 1 && type != 2) || length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        return RegQueryValueEx(key, valueName, IntPtr.Zero, ref type, buffer, ref length) == ErrorSuccess
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern uint RegOpenKeyEx(
        IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr resultKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
    private static extern uint RegEnumKeyEx(
        IntPtr hKey, uint index, StringBuilder name, ref uint nameLength,
        IntPtr reserved, StringBuilder? className, IntPtr classNameLength, out long lastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern uint RegQueryValueEx(
        IntPtr hKey, string valueName, IntPtr reserved, ref uint type,
        byte[]? data, ref uint dataLength);

    [DllImport("advapi32.dll")]
    private static extern uint RegCloseKey(IntPtr hKey);

    internal static string? GetVersion(string productCode)
    {
        var normalized = NormalizeCode(productCode);
        return normalized is null ? null : GetProductInfo(normalized, VersionProperty);
    }

    private static IEnumerable<string> ParseCodes(string value)
    {
        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeCode(item);
            if (normalized is not null)
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizeCode(string value) =>
        Guid.TryParse(value, out var code) ? code.ToString("B").ToUpperInvariant() : null;

    private static string? GetProductInfo(string productCode, string property)
    {
        uint length = 0;
        var result = MsiGetProductInfo(productCode, property, null, ref length);
        if (result == ErrorSuccess && length == 0)
        {
            return string.Empty;
        }
        if (result != ErrorSuccess && result != ErrorMoreData)
        {
            return null;
        }

        length++;
        var buffer = new StringBuilder(checked((int)length));
        result = MsiGetProductInfo(productCode, property, buffer, ref length);
        return result == ErrorSuccess ? buffer.ToString() : null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(
        string upgradeCode,
        uint reserved,
        uint productIndex,
        StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(
        string productCode,
        string property,
        StringBuilder? valueBuffer,
        ref uint valueBufferLength);
}

using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Nsis.Plugin;

internal static class MsiProducts
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const string VersionProperty = "VersionString";

    internal static string? FindFirst(string productCodes, string upgradeCodes) =>
        FindAll(productCodes, upgradeCodes).FirstOrDefault();

    internal static string? GetNewestVersion(string productCodes, string upgradeCodes)
    {
        SemanticVersion? newest = null;
        string? newestText = null;
        foreach (var productCode in FindAll(productCodes, upgradeCodes))
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

    private static IEnumerable<string> FindAll(string productCodes, string upgradeCodes)
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
    }

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

using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Wix;

// Minimal Windows Installer read API used to verify that an expert-mode MSI
// still carries the Bundler-derived identity after light produced it.
internal static class MsiIdentityProbe
{
    internal static void VerifyExpertMsi(string path, WixIdentity identity, WixLanguageInfo language,
        WixInstallScope scope)
    {
        var productCode = ReadProperty(path, "ProductCode");
        if (!productCode.Equals(identity.ProductCode.ToString("B").ToUpperInvariant(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expert MSI ProductCode '{productCode}' does not match the Bundler identity " +
                $"'{identity.ProductCode:B}'; the template must use the Bundler.* candle variables.");
        }
        var upgradeCode = ReadUpgradeCode(path);
        if (!upgradeCode.Equals(identity.UpgradeCode.ToString("B").ToUpperInvariant(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expert MSI UpgradeCode '{upgradeCode}' does not match the Bundler identity " +
                $"'{identity.UpgradeCode:B}'; upgrade continuity would break.");
        }
        var productLanguage = ReadProperty(path, "ProductLanguage");
        if (!productLanguage.Equals(language.Lcid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expert MSI ProductLanguage '{productLanguage}' does not match '{language.Culture}' " +
                $"LCID {language.Lcid}.");
        }
        var isPerMachine = ReadProperty(path, "ALLUSERS") == "1" ||
            string.IsNullOrEmpty(ReadProperty(path, "MSIINSTALLPERUSER"));
        if (isPerMachine != (scope == WixInstallScope.PerMachine))
        {
            throw new InvalidOperationException(
                $"Expert MSI install scope does not match configured scope '{scope}'.");
        }
    }

    private static string ReadProperty(string path, string name)
    {
        return ReadValue(path,
            "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'") ?? "";
    }

    private static string ReadUpgradeCode(string path)
    {
        return ReadValue(path, "SELECT `UpgradeCode` FROM `Upgrade`") ?? "";
    }

    private static string? ReadValue(string path, string sql)
    {
        Check(MsiOpenDatabase(path, IntPtr.Zero, out var database));
        try
        {
            Check(MsiDatabaseOpenView(database, sql, out var view));
            try
            {
                var result = MsiViewExecute(view, IntPtr.Zero);
                Check(result);
                result = MsiViewFetch(view, out var record);
                if (result == 259) return null;
                Check(result);
                try
                {
                    var text = new StringBuilder(1024);
                    var length = (uint)text.Capacity;
                    Check(MsiRecordGetString(record, 1, text, ref length));
                    return text.ToString();
                }
                finally { MsiCloseHandle(record); }
            }
            finally
            {
                MsiViewClose(view);
                MsiCloseHandle(view);
            }
        }
        finally { MsiCloseHandle(database); }
    }

    private static void Check(uint code)
    {
        if (code != 0)
            throw new InvalidOperationException("Windows Installer database API returned " + code + ".");
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string sql, out IntPtr view);
    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")]
    private static extern uint MsiViewClose(IntPtr view);
    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);
}

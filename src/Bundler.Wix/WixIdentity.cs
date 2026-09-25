using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNet.Bundler.Wix;

public sealed record WixIdentity(Guid UpgradeCode, Guid ProductCode, string ProductVersion)
{
    private static readonly Guid NamespaceId = new("D9024E41-FDAB-535D-8AD9-3F961EB95FAB");
    private static readonly Regex VersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant);

    public static WixIdentity Create(
        string identifier,
        string version,
        string runtimeIdentifier,
        WixInstallScope scope,
        string? upgradeCode = null,
        WixPackageLanguage language = WixPackageLanguage.English,
        string? msiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("An MSI identifier is required.", nameof(identifier));
        }
        if (scope is not (WixInstallScope.CurrentUser or WixInstallScope.PerMachine))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        if (language is not (WixPackageLanguage.English or WixPackageLanguage.ChineseSimplified))
            throw new ArgumentOutOfRangeException(nameof(language));
        if (runtimeIdentifier != "win-x86" && runtimeIdentifier != "win-x64" && runtimeIdentifier != "win-arm64")
        {
            throw new NotSupportedException($"MSI target '{runtimeIdentifier}' is not supported.");
        }

        var versionToMap = msiVersion ?? version;
        var match = VersionPattern.Match(versionToMap ?? "");
        if (!match.Success ||
            !uint.TryParse(match.Groups[1].Value, out var major) || major > 255 ||
            !uint.TryParse(match.Groups[2].Value, out var minor) || minor > 255 ||
            !uint.TryParse(match.Groups[3].Value, out var build) || build > 65535)
        {
            throw new ArgumentException(
                "MSI requires a stable major.minor.patch version (major/minor 0-255, patch 0-65535); pre-release and build metadata are not supported.",
                msiVersion is null ? nameof(version) : nameof(msiVersion));
        }

        var normalizedIdentifier = identifier.ToLowerInvariant();
        var family = $"{normalizedIdentifier}|{scope}|{runtimeIdentifier}" +
            (language == WixPackageLanguage.English ? "" : "|zh-CN");
        Guid familyCode;
        if (upgradeCode is null)
        {
            familyCode = Uuid5(NamespaceId, "upgrade|" + family);
        }
        else if (!Guid.TryParse(upgradeCode, out familyCode) || familyCode == Guid.Empty)
        {
            throw new ArgumentException("UpgradeCode must be a non-empty GUID.", nameof(upgradeCode));
        }

        var productVersion = $"{major}.{minor}.{build}";
        var productLanguage = language == WixPackageLanguage.English ? 1033 : 2052;
        var productCode = Uuid5(NamespaceId, $"product|{familyCode:D}|{productVersion}|{productLanguage}");
        return new WixIdentity(familyCode, productCode, productVersion);
    }

    internal static Guid ComponentCode(string identifier, string runtimeIdentifier, WixInstallScope scope, string relativePath,
        WixPackageLanguage language = WixPackageLanguage.English) =>
        Uuid5(NamespaceId, "component|" + identifier.ToLowerInvariant() + "|" + scope + "|" +
              runtimeIdentifier + "|Programs|" + identifier.ToLowerInvariant() + "-" +
              runtimeIdentifier.Substring(4) + "|" +
              relativePath.Replace('\\', '/').ToLowerInvariant() +
              (language == WixPackageLanguage.English ? "" : "|zh-CN"));

    internal static string StableId(string prefix, string name)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return prefix + BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty);
    }

    private static Guid Uuid5(Guid namespaceId, string name)
    {
        var ns = namespaceId.ToByteArray();
        SwapGuidByteOrder(ns);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[ns.Length + nameBytes.Length];
        Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
        Buffer.BlockCopy(nameBytes, 0, input, ns.Length, nameBytes.Length);
        using var sha = SHA1.Create();
        var digest = sha.ComputeHash(input);
        var result = new byte[16];
        Buffer.BlockCopy(digest, 0, result, 0, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(result);
        return new Guid(result);
    }

    private static void SwapGuidByteOrder(byte[] bytes)
    {
        Array.Reverse(bytes, 0, 4);
        Array.Reverse(bytes, 4, 2);
        Array.Reverse(bytes, 6, 2);
    }
}

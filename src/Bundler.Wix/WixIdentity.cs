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
        string target,
        WixInstallScope scope,
        string? upgradeCode = null,
        WixLanguageInfo? language = null,
        string? packageVersion = null)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("An MSI identifier is required.", nameof(identifier));
        }
        if (scope is not (WixInstallScope.CurrentUser or WixInstallScope.PerMachine))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        language ??= WixLanguageInfo.Resolve("en-US");
        if (target != "windows-i686" && target != "windows-x86_64" && target != "windows-arm64")
        {
            throw new NotSupportedException($"MSI target '{target}' is not supported.");
        }

        var versionToMap = packageVersion ?? version;
        var match = VersionPattern.Match(versionToMap ?? "");
        if (!match.Success ||
            !uint.TryParse(match.Groups[1].Value, out var major) || major > 255 ||
            !uint.TryParse(match.Groups[2].Value, out var minor) || minor > 255 ||
            !uint.TryParse(match.Groups[3].Value, out var build) || build > 65535)
        {
            throw new ArgumentException(
                "MSI requires a stable major.minor.patch version (major/minor 0-255, patch 0-65535); pre-release and build metadata are not supported.",
                packageVersion is null ? nameof(version) : nameof(packageVersion));
        }

        var normalizedIdentifier = identifier.ToLowerInvariant();
        var family = $"{normalizedIdentifier}|{scope}|{target}" + language.FamilyToken;
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
        var productLanguage = language.Lcid;
        var productCode = Uuid5(NamespaceId, $"product|{familyCode:D}|{productVersion}|{productLanguage}");
        return new WixIdentity(familyCode, productCode, productVersion);
    }

    internal static Guid ComponentCode(string identifier, string target, WixInstallScope scope, string relativePath,
        WixLanguageInfo? language = null)
    {
        var parsed = BundleTarget.Parse(target);
        var archToken = ArtifactNaming.ArchToken(parsed, PackageFormat.Msi)
            ?? throw new ArgumentException(
                $"Architecture '{parsed.Architecture}' is not supported for {PackageFormat.Msi}.");
        return Uuid5(NamespaceId, "component|" + identifier.ToLowerInvariant() + "|" + scope + "|" +
              target + "|Programs|" + identifier.ToLowerInvariant() + "-" +
              archToken + "|" +
              relativePath.Replace('\\', '/').ToLowerInvariant() +
              (language ?? WixLanguageInfo.Resolve("en-US")).FamilyToken);
    }

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

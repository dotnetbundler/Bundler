using System.Text.RegularExpressions;
using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

internal sealed record MacAppMetadata(
    string BundleName,
    string DisplayName,
    string ShortVersion,
    string BuildVersion,
    string? MinimumSystemVersion,
    string? Category,
    string? IconFileName)
{
    private static readonly Regex AppleVersionPattern = new(
        "^[0-9]+(\\.[0-9]+){0,2}$", RegexOptions.Compiled);
    private static readonly Regex MinimumSystemPattern = new(
        "^[0-9]+\\.[0-9]+(\\.[0-9]+)?$", RegexOptions.Compiled);
    private static readonly Regex CategoryPattern = new(
        "^public\\.app-category\\.[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static MacAppMetadata Resolve(
        BundleConfiguration bundle, MacAppBundleConfiguration settings)
    {
        var shortVersion = settings.ShortVersion ?? bundle.Version;
        RequireAppleVersion(shortVersion, "CFBundleShortVersionString (shortVersion)");
        var buildVersion = settings.BuildVersion ?? shortVersion;
        RequireAppleVersion(buildVersion, "CFBundleVersion (buildVersion)");

        string? minimumSystemVersion = null;
        if (!string.IsNullOrWhiteSpace(settings.MinimumSystemVersion))
        {
            minimumSystemVersion = settings.MinimumSystemVersion!.Trim();
            if (!MinimumSystemPattern.IsMatch(minimumSystemVersion))
            {
                throw new ArgumentException(
                    $"LSMinimumSystemVersion must look like '11.0' or '11.0.0'; got '{minimumSystemVersion}'.");
            }
        }

        string? category = null;
        if (!string.IsNullOrWhiteSpace(settings.Category))
        {
            category = settings.Category!.Trim();
            if (!CategoryPattern.IsMatch(category))
            {
                throw new ArgumentException(
                    $"LSApplicationCategoryType must look like 'public.app-category.utilities'; got '{category}'.");
            }
        }

        string? iconFileName = null;
        if (bundle.Icons.Count > 0)
        {
            var iconName = string.IsNullOrWhiteSpace(settings.IconName)
                ? "AppIcon"
                : settings.IconName!.Trim();
            if (Path.GetFileName(iconName) != iconName || iconName.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ||
                iconName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || iconName.Contains(':'))
            {
                throw new ArgumentException(
                    $"IconName must be a plain file name without extension; got '{settings.IconName}'.");
            }
            iconFileName = iconName + ".icns";
        }

        return new MacAppMetadata(
            string.IsNullOrWhiteSpace(settings.BundleName) ? bundle.ProductName : settings.BundleName!.Trim(),
            string.IsNullOrWhiteSpace(settings.BundleDisplayName)
                ? bundle.ProductName
                : settings.BundleDisplayName!.Trim(),
            shortVersion, buildVersion, minimumSystemVersion, category, iconFileName);
    }

    private static void RequireAppleVersion(string value, string displayName)
    {
        if (!AppleVersionPattern.IsMatch(value))
        {
            throw new ArgumentException(
                $"{displayName} must be one to three period-separated numbers for Apple (for example '1.2.3'); got '{value}'. " +
                "Configure an explicit value when the package version uses SemVer suffixes.");
        }
    }
}

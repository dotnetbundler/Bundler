using NsisPlugin;

namespace DotNet.Bundler.Nsis.Plugin;

public static class PluginActions
{
    [NsisAction]
    public static int SemverCompare(string candidate, string installed)
    {
        if (!SemanticVersion.TryParse(candidate, out var candidateVersion) ||
            !SemanticVersion.TryParse(installed, out var installedVersion))
        {
            // NSIS 将 2 视为无法比较，并采用保守的版本处理策略。
            return 2;
        }

        return Math.Sign(candidateVersion!.CompareTo(installedVersion));
    }

    [NsisAction]
    public static string FindMsiProduct(string productCodes, string upgradeCodes) =>
        MsiProducts.FindFirst(productCodes, upgradeCodes) ?? string.Empty;

    [NsisAction]
    public static string GetNewestMsiVersion(string productCodes, string upgradeCodes) =>
        MsiProducts.GetNewestVersion(productCodes, upgradeCodes) ?? string.Empty;

    [NsisAction]
    public static int RunAsUser(string executable, string arguments) =>
        UnelevatedProcess.Start(executable, arguments);
}

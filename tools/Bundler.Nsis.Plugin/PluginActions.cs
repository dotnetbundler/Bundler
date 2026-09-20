using NsisPlugin;
using System.Runtime.Versioning;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
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

    [NsisAction]
    public static int CreateShortcut(
        string shortcut,
        string ownedTargets,
        string target,
        string arguments,
        string workingDirectory,
        string icon,
        string appUserModelId) =>
        ShortcutManager.Create(shortcut, ownedTargets, target, arguments, workingDirectory, icon, appUserModelId);

    [NsisAction]
    public static int UpdateShortcutIfOwned(
        string shortcut,
        string ownedTargets,
        string target,
        string arguments,
        string workingDirectory,
        string icon,
        string appUserModelId) =>
        ShortcutManager.UpdateIfOwned(
            shortcut, ownedTargets, target, arguments, workingDirectory, icon, appUserModelId);

    [NsisAction]
    public static int MoveShortcutIfOwned(
        string source,
        string destination,
        string ownedTargets,
        string target,
        string arguments,
        string workingDirectory,
        string icon,
        string appUserModelId) =>
        ShortcutManager.MoveIfOwned(
            source, destination, ownedTargets, target, arguments, workingDirectory, icon, appUserModelId);

    [NsisAction]
    public static int DeleteShortcutIfOwned(string shortcut, string ownedTargets) =>
        ShortcutManager.DeleteIfOwned(shortcut, ownedTargets);
}

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
    public static int GetLockingProcessCount(string path) =>
        RestartManager.GetLockingProcessCount(path);

    [NsisAction]
    public static int ShutdownLockingProcesses(string path) =>
        RestartManager.ShutdownLockingProcesses(path);

    [NsisAction]
    public static int BeginInstallTransaction(string transactionDirectory, string installDirectory) =>
        InstallTransaction.Begin(transactionDirectory, installDirectory);

    [NsisAction]
    public static int BackupTransactionRegistryKey(
        string transactionDirectory,
        string name,
        string root,
        int viewBits,
        string subKey) =>
        InstallTransaction.BackupRegistryKey(transactionDirectory, name, root, viewBits, subKey);

    [NsisAction]
    public static int BackupTransactionRegistryValue(
        string transactionDirectory,
        string name,
        string root,
        int viewBits,
        string subKey,
        string valueName) =>
        InstallTransaction.BackupRegistryValue(transactionDirectory, name, root, viewBits, subKey, valueName);

    [NsisAction]
    public static int BackupTransactionFile(string transactionDirectory, string name, string path) =>
        InstallTransaction.BackupFile(transactionDirectory, name, path);

    [NsisAction]
    public static int ActivateInstallTransaction(string transactionDirectory, string root, int viewBits) =>
        InstallTransaction.Activate(transactionDirectory, root, viewBits);

    [NsisAction]
    public static int CommitInstallTransaction(string transactionDirectory, string root, int viewBits) =>
        InstallTransaction.Commit(transactionDirectory, root, viewBits);

    [NsisAction]
    public static int ValidateTransactionRegistryKeySnapshot(string transactionDirectory, string name, string root, int viewBits, string subKey) =>
        InstallTransaction.ValidateRegistryKeySnapshot(transactionDirectory, name, root, viewBits, subKey);

    [NsisAction]
    public static int ValidateTransactionRegistryValueSnapshot(string transactionDirectory, string name, string root, int viewBits, string subKey, string valueName) =>
        InstallTransaction.ValidateRegistryValueSnapshot(transactionDirectory, name, root, viewBits, subKey, valueName);

    [NsisAction]
    public static int ValidateTransactionFileSnapshot(string transactionDirectory, string name, string path) =>
        InstallTransaction.ValidateFileSnapshot(transactionDirectory, name, path);

    [NsisAction]
    public static int ValidateTransactionSnapshotSet(string transactionDirectory, int registryCount, int fileCount) =>
        InstallTransaction.ValidateSnapshotSet(transactionDirectory, registryCount, fileCount);

    [NsisAction]
    public static int ValidateTransactionSnapshotIntegrity(string transactionDirectory, string root, int viewBits) =>
        InstallTransaction.ValidateSnapshotIntegrity(transactionDirectory, root, viewBits);

    [NsisAction]
    public static int BeginInstallTransactionRecovery(string transactionDirectory, string installDirectory) =>
        InstallTransaction.BeginRecovery(transactionDirectory, installDirectory);

    [NsisAction]
    public static int BeginTransactionRegistryRestore(string transactionDirectory) =>
        InstallTransaction.BeginRegistryRestore(transactionDirectory);

    [NsisAction]
    public static int RestoreTransactionRegistryKey(string transactionDirectory, string name, string root, int viewBits, string subKey) =>
        InstallTransaction.RestoreRegistryKey(transactionDirectory, name, root, viewBits, subKey);

    [NsisAction]
    public static int RestoreTransactionRegistryValue(string transactionDirectory, string name, string root, int viewBits, string subKey, string valueName) =>
        InstallTransaction.RestoreRegistryValue(transactionDirectory, name, root, viewBits, subKey, valueName);

    [NsisAction]
    public static int RestoreTransactionFile(string transactionDirectory, string name, string path) =>
        InstallTransaction.RestoreFile(transactionDirectory, name, path);

    [NsisAction]
    public static int CompleteInstallTransactionRecovery(string transactionDirectory, string root, int viewBits) =>
        InstallTransaction.CompleteRecovery(transactionDirectory, root, viewBits);

    [NsisAction]
    public static int BeginUninstallTransaction(
        string transactionDirectory,
        string installDirectory,
        string uninstallerPath,
        int deleteAppData,
        string root,
        int viewBits,
        string uninstallSubKey) =>
        UninstallTransaction.Begin(transactionDirectory, installDirectory, uninstallerPath, deleteAppData, root, viewBits, uninstallSubKey);

    [NsisAction]
    public static string GetUninstallRecoveryHash(string path) =>
        UninstallTransaction.GetFileSha256(path);

    [NsisAction]
    public static int ValidateUninstallDeletionTrees(
        string installDirectory,
        string roamingDataDirectory,
        string localDataDirectory,
        int deleteAppData) =>
        UninstallTransaction.ValidateDeletionTrees(
            installDirectory,
            roamingDataDirectory,
            localDataDirectory,
            deleteAppData);

    [NsisAction]
    public static int ActivateUninstallTransaction(string transactionDirectory) =>
        UninstallTransaction.Activate(transactionDirectory);

    [NsisAction]
    public static int GetUninstallTransactionState(string transactionDirectory, string expectedInstallDirectory) =>
        UninstallTransaction.GetState(transactionDirectory, expectedInstallDirectory);

    [NsisAction]
    public static int GetUninstallTransactionDeleteAppData(string transactionDirectory) =>
        UninstallTransaction.GetDeleteAppData(transactionDirectory);

    [NsisAction]
    public static int MarkUninstallTransactionFinalizing(string transactionDirectory) =>
        UninstallTransaction.MarkFinalizing(transactionDirectory);

    [NsisAction]
    public static int CommitUninstallTransaction(string transactionDirectory) =>
        UninstallTransaction.Commit(transactionDirectory);

    [NsisAction]
    public static int RecoverUninstallTransaction(string transactionDirectory, string expectedInstallDirectory, string root, int viewBits, string uninstallSubKey) =>
        UninstallTransaction.Recover(transactionDirectory, expectedInstallDirectory, root, viewBits, uninstallSubKey);

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

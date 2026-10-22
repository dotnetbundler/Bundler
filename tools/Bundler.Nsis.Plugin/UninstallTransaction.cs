using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
internal static class UninstallTransaction
{
    private const string StateFileName = "state.txt";
    private const string DeleteAppDataFileName = "delete-app-data.txt";
    private const string RecoveryExecutableFileName = "recovery-uninstaller.exe";
    private const string ActiveFileName = "active";
    private const string FinalizingFileName = "finalizing";
    private const string CommittedSuffix = ".committed";
    private const string RecoveryHashValueName = "BundlerRecoverySha256";

    internal static string GetFileSha256(string path)
    {
        try
        {
            RejectExistingReparsePoints(path);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static int ValidateDeletionTrees(
        string installDirectory,
        string roamingDataDirectory,
        string localDataDirectory,
        int deleteAppData)
    {
        try
        {
            RejectReparsePointsInTree(Path.GetFullPath(installDirectory));
            if (deleteAppData != 0)
            {
                RejectReparsePointsInTree(Path.GetFullPath(roamingDataDirectory));
                RejectReparsePointsInTree(Path.GetFullPath(localDataDirectory));
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Begin(
        string transactionDirectory,
        string installDirectory,
        string uninstallerPath,
        int deleteAppData,
        string rootName,
        int viewBits,
        string uninstallSubKey)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            JournalAccess.EnsureProtectedMachineRoot(transaction);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (Directory.Exists(transaction))
            {
                throw new InvalidOperationException("An uninstall transaction is already active.");
            }

            var install = ValidateInstallDirectory(installDirectory, transaction);
            // 归属绑定：$INSTDIR 必须与注册项 InstallRoot（回退 InstallLocation）一致——
            // 否则被复制到任意目录的 Uninstall.exe 可以借卸载事务对目标树递归删除。
            var registered = ReadRegisteredInstallRoot(rootName, viewBits, uninstallSubKey);
            if (!install.Equals(registered, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The install directory does not match the registered install root.");
            }
            var uninstaller = Path.GetFullPath(uninstallerPath);
            var expectedUninstaller = Path.Combine(install, "Uninstall.exe");
            if (!uninstaller.Equals(expectedUninstaller, StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstaller))
            {
                throw new InvalidDataException("The recovery uninstaller must be the installed Uninstall.exe.");
            }
            RejectReparsePoint(uninstaller);
            var expectedHash = ReadRegisteredRecoveryHash(rootName, viewBits, uninstallSubKey);
            if (!GetFileSha256(uninstaller).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installed uninstaller does not match the registered recovery hash.");
            }

            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, StateFileName), install);
            File.WriteAllText(
                Path.Combine(transaction, DeleteAppDataFileName),
                deleteAppData == 0 ? "0" : "1",
                System.Text.Encoding.ASCII);
            File.Copy(uninstaller, Path.Combine(transaction, RecoveryExecutableFileName), overwrite: false);
            if (!GetFileSha256(Path.Combine(transaction, RecoveryExecutableFileName)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The copied recovery uninstaller does not match the registered recovery hash.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Activate(string transactionDirectory)
    {
        try
        {
            var transaction = ValidatePreparedTransaction(transactionDirectory);
            File.WriteAllText(Path.Combine(transaction, ActiveFileName), string.Empty);
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    // 归属兜底：标记件缺失时由插件按注册 InstallRoot（回退 InstallLocation）判定
    // 候选 $INSTDIR 归属——与 Begin 同一套 GetFullPath+OrdinalIgnoreCase 规范化比较。
    internal static int CheckDirectoryOwnership(
        string installDirectory,
        string rootName,
        int viewBits,
        string uninstallSubKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return 1;
            }
            var candidate = Path.GetFullPath(installDirectory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar);
            RejectExistingReparsePoints(candidate);
            var registered = ReadRegisteredInstallRoot(rootName, viewBits, uninstallSubKey);
            return candidate.Equals(registered, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int GetState(
        string transactionDirectory,
        string expectedInstallDirectory,
        string rootName,
        int viewBits,
        string uninstallSubKey)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            JournalAccess.ValidateProtectedMachineTree(transaction);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (!Directory.Exists(transaction))
            {
                return 0;
            }

            ValidateActiveTransaction(
                transaction,
                expectedInstallDirectory,
                allowMissingExpectedPathWhenFinalizing: true,
                rootName,
                viewBits,
                uninstallSubKey);
            return File.Exists(Path.Combine(transaction, FinalizingFileName)) ? 2 : 1;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int GetDeleteAppData(string transactionDirectory)
    {
        try
        {
            var transaction = ValidatePreparedTransaction(transactionDirectory);
            var value = File.ReadAllText(Path.Combine(transaction, DeleteAppDataFileName)).Trim();
            return value switch
            {
                "0" => 0,
                "1" => 1,
                _ => throw new InvalidDataException("The uninstall transaction contains an invalid application-data choice.")
            };
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int MarkFinalizing(string transactionDirectory)
    {
        try
        {
            var transaction = ValidatePreparedTransaction(transactionDirectory);
            if (!File.Exists(Path.Combine(transaction, ActiveFileName)))
            {
                throw new InvalidOperationException("The uninstall transaction is not active.");
            }
            File.WriteAllText(Path.Combine(transaction, FinalizingFileName), string.Empty);
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Commit(string transactionDirectory)
    {
        try
        {
            CommitCore(ValidateTransactionDirectory(transactionDirectory));
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Recover(
        string transactionDirectory,
        string expectedInstallDirectory,
        string rootName,
        int viewBits,
        string uninstallSubKey)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            JournalAccess.ValidateProtectedMachineTree(transaction);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (!Directory.Exists(transaction))
            {
                return 0;
            }

            var install = ValidateActiveTransaction(
                transaction,
                expectedInstallDirectory,
                allowMissingExpectedPathWhenFinalizing: true,
                rootName,
                viewBits,
                uninstallSubKey);
            if (File.Exists(Path.Combine(transaction, FinalizingFileName)))
            {
                // 持久状态已删除完毕；注册表锚点可能已移除，不再执行 journal 中的 EXE。
                CommitCore(transaction);
                return 0;
            }
            var recoveryExecutable = Path.Combine(transaction, RecoveryExecutableFileName);
            if (!File.Exists(recoveryExecutable))
            {
                throw new FileNotFoundException("The uninstall recovery executable is missing.", recoveryExecutable);
            }
            var expectedHash = ReadRegisteredRecoveryHash(rootName, viewBits, uninstallSubKey);
            if (!GetFileSha256(recoveryExecutable).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The uninstall recovery executable failed integrity validation.");
            }

            var startInfo = new ProcessStartInfo(recoveryExecutable)
            {
                UseShellExecute = false,
                // NSIS 的 `_?=` 必须保持为命令行中最后一个未加引号的原始尾参数；
                // ArgumentList 会为包含空格的整个参数自动加引号，导致卸载器忽略它。
                Arguments = $"/S /RESUME _?={install}"
            };
            // _?= 必须是 NSIS 卸载器的最后一个参数；收尾阶段不会再按此路径删除，
            // 但仍传入原目录以保持卸载器上下文一致。
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the uninstall recovery executable.");
            process.WaitForExit();
            if (process.ExitCode == 0 &&
                Directory.Exists(transaction) &&
                File.Exists(Path.Combine(transaction, FinalizingFileName)))
            {
                // 如果恢复卸载器已完成持久状态清理、但自身仍占用 journal 副本，
                // 当前安装器进程再执行一次幂等提交清理。
                CommitCore(transaction);
            }
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    private static RegistryKey OpenUninstallKey(string rootName, int viewBits, string uninstallSubKey)
    {
        var hive = rootName switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new InvalidDataException("The uninstall recovery registry root is invalid.")
        };
        var view = viewBits switch
        {
            32 => RegistryView.Registry32,
            64 => RegistryView.Registry64,
            _ => throw new InvalidDataException("The uninstall recovery registry view is invalid.")
        };
        var root = RegistryKey.OpenBaseKey(hive, view);
        var key = root.OpenSubKey(uninstallSubKey, writable: false);
        root.Dispose();
        if (key is null)
        {
            throw new InvalidDataException("The registered uninstall key is missing.");
        }
        return key;
    }

    private static string ReadRegisteredRecoveryHash(string rootName, int viewBits, string uninstallSubKey)
    {
        using var key = OpenUninstallKey(rootName, viewBits, uninstallSubKey);
        var hash = key.GetValue(RecoveryHashValueName) as string;
        if (hash is null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The registered uninstall recovery hash is missing or invalid.");
        }
        return hash;
    }

    private static string ReadRegisteredInstallRoot(string rootName, int viewBits, string uninstallSubKey)
    {
        using var key = OpenUninstallKey(rootName, viewBits, uninstallSubKey);
        var registered = (key.GetValue("InstallRoot") as string) ??
                         (key.GetValue("InstallLocation") as string);
        if (string.IsNullOrWhiteSpace(registered))
        {
            throw new InvalidDataException("The registered install root is missing.");
        }
        return Path.GetFullPath(registered).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string ValidateActiveTransaction(
        string transaction,
        string expectedInstallDirectory,
        bool allowMissingExpectedPathWhenFinalizing,
        string rootName,
        int viewBits,
        string uninstallSubKey)
    {
        var prepared = ValidatePreparedTransaction(transaction);
        if (!File.Exists(Path.Combine(prepared, ActiveFileName)))
        {
            throw new InvalidOperationException("The uninstall transaction is not active.");
        }

        var install = ValidateInstallDirectory(File.ReadAllText(Path.Combine(prepared, StateFileName)), prepared);
        var finalizing = File.Exists(Path.Combine(prepared, FinalizingFileName));
        if (string.IsNullOrWhiteSpace(expectedInstallDirectory))
        {
            if (!allowMissingExpectedPathWhenFinalizing || !finalizing)
            {
                throw new InvalidDataException("The installed product location is unavailable for uninstall recovery.");
            }
        }
        else
        {
            var expected = ValidateInstallDirectory(expectedInstallDirectory, prepared);
            if (!install.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The uninstall transaction does not match the registered install directory.");
            }
        }

        // 归属绑定：仍在删除窗口内的 journal 其安装目录必须与注册 InstallRoot 一致，
        // 否则复制/伪造的 journal 可以借恢复腿把删除目标重定向到任意目录。
        // finalizing 后注册锚点可能已移除，且持久状态删除已完成，不再要求绑定。
        if (!finalizing)
        {
            var registered = ReadRegisteredInstallRoot(rootName, viewBits, uninstallSubKey);
            if (!install.Equals(registered, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The uninstall transaction install directory does not match the registered install root.");
            }
        }
        return install;
    }

    private static string ValidatePreparedTransaction(string transactionDirectory)
    {
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!File.Exists(Path.Combine(transaction, StateFileName)) ||
            !File.Exists(Path.Combine(transaction, DeleteAppDataFileName)) ||
            !File.Exists(Path.Combine(transaction, RecoveryExecutableFileName)))
        {
            throw new InvalidDataException("The uninstall transaction is incomplete.");
        }
        return transaction;
    }

    private static void CommitCore(string transaction)
    {
        var committed = transaction + CommittedSuffix;
        CleanupCommitted(transaction);
        if (!Directory.Exists(transaction))
        {
            return;
        }
        Directory.Move(transaction, committed);
        try
        {
            SafeDeleteTree(committed);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupInactive(string transaction)
    {
        if (Directory.Exists(transaction) && !File.Exists(Path.Combine(transaction, ActiveFileName)))
        {
            SafeDeleteTree(transaction);
        }
    }

    private static void CleanupCommitted(string transaction)
    {
        var committed = transaction + CommittedSuffix;
        if (Directory.Exists(committed))
        {
            SafeDeleteTree(committed);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Uninstall transaction cannot safely use a reparse point: {path}");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var component in fullPath.Substring(root.Length)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
            {
                RejectReparsePoint(current);
            }
        }
    }

    private static void RejectReparsePointsInTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        RejectReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void SafeDeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry))
            {
                SafeDeleteTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
        Directory.Delete(path);
    }

    private static string ValidateTransactionDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) ||
            Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("The uninstall transaction directory must not be a drive root.", nameof(path));
        }
        RejectExistingReparsePoints(fullPath);
        return fullPath;
    }

    private static string ValidateInstallDirectory(string path, string transactionDirectory)
    {
        var fullPath = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) ||
            Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("The install directory must not be a drive root.", nameof(path));
        }
        if (transactionDirectory.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(transactionDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The uninstall transaction and install directories must not contain each other.", nameof(path));
        }
        RejectExistingReparsePoints(fullPath);
        return fullPath;
    }
}

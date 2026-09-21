using System.Diagnostics;
using System.Runtime.Versioning;

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

    internal static int Begin(
        string transactionDirectory,
        string installDirectory,
        string uninstallerPath,
        int deleteAppData)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (Directory.Exists(transaction))
            {
                throw new InvalidOperationException("An uninstall transaction is already active.");
            }

            var install = ValidateInstallDirectory(installDirectory, transaction);
            var uninstaller = Path.GetFullPath(uninstallerPath);
            var expectedUninstaller = Path.Combine(install, "Uninstall.exe");
            if (!uninstaller.Equals(expectedUninstaller, StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstaller))
            {
                throw new InvalidDataException("The recovery uninstaller must be the installed Uninstall.exe.");
            }

            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, StateFileName), install);
            File.WriteAllText(
                Path.Combine(transaction, DeleteAppDataFileName),
                deleteAppData == 0 ? "0" : "1",
                System.Text.Encoding.ASCII);
            File.Copy(uninstaller, Path.Combine(transaction, RecoveryExecutableFileName), overwrite: false);
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

    internal static int GetState(string transactionDirectory, string expectedInstallDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (!Directory.Exists(transaction))
            {
                return 0;
            }

            ValidateActiveTransaction(transaction, expectedInstallDirectory, allowMissingExpectedPathWhenFinalizing: true);
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

    internal static int Recover(string transactionDirectory, string expectedInstallDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            CleanupInactive(transaction);
            if (!Directory.Exists(transaction))
            {
                return 0;
            }

            var install = ValidateActiveTransaction(
                transaction,
                expectedInstallDirectory,
                allowMissingExpectedPathWhenFinalizing: true);
            var recoveryExecutable = Path.Combine(transaction, RecoveryExecutableFileName);
            if (!File.Exists(recoveryExecutable))
            {
                throw new FileNotFoundException("The uninstall recovery executable is missing.", recoveryExecutable);
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

    private static string ValidateActiveTransaction(
        string transaction,
        string expectedInstallDirectory,
        bool allowMissingExpectedPathWhenFinalizing)
    {
        var prepared = ValidatePreparedTransaction(transaction);
        if (!File.Exists(Path.Combine(prepared, ActiveFileName)))
        {
            throw new InvalidOperationException("The uninstall transaction is not active.");
        }

        var install = ValidateInstallDirectory(File.ReadAllText(Path.Combine(prepared, StateFileName)), prepared);
        if (string.IsNullOrWhiteSpace(expectedInstallDirectory))
        {
            if (!allowMissingExpectedPathWhenFinalizing || !File.Exists(Path.Combine(prepared, FinalizingFileName)))
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
            Directory.Delete(committed, recursive: true);
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
            Directory.Delete(transaction, recursive: true);
        }
    }

    private static void CleanupCommitted(string transaction)
    {
        var committed = transaction + CommittedSuffix;
        if (Directory.Exists(committed))
        {
            Directory.Delete(committed, recursive: true);
        }
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
        return fullPath;
    }
}

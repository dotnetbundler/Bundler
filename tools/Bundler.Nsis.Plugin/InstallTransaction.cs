using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
internal static class InstallTransaction
{
    private const string StateFileName = "state.txt";
    private const string ActiveFileName = "active";
    private const string OriginalPayloadFileName = "original-payload";
    private const string CommittedSuffix = ".committed";
    private const string RetainCommittedForTestFileName = ".dotnet-bundler-test-retain-committed";
    private const string FailNextSnapshotForTestFileName = ".dotnet-bundler-test-fail-next-snapshot";
    private const string FailNextActivationForTestFileName = ".dotnet-bundler-test-fail-next-activation";
    private const string FailNextPayloadRestoreForTestFileName = ".dotnet-bundler-test-fail-next-payload-restore";
    private const string FailNextRegistryRestoreForTestFileName = ".dotnet-bundler-test-fail-next-registry-restore";
    private const string FailNextJournalCleanupForTestFileName = ".dotnet-bundler-test-fail-next-journal-cleanup";
    private const string SealRegistryPrefix = @"Software\DotNetBundler\TransactionSeals\";
    internal const int RecoveryManifestMismatch = 6;

    internal static int Begin(string transactionDirectory, string installDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            JournalAccess.EnsureProtectedMachineRoot(transaction);
            CleanupCommitted(transaction);
            var install = ValidateInstallDirectory(installDirectory, transaction);
            if (Directory.Exists(transaction))
            {
                throw new InvalidOperationException("An existing install transaction must be recovered before a new transaction begins.");
            }
            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, StateFileName), install);

            if (Directory.Exists(install))
            {
                CopyDirectory(install, Path.Combine(transaction, "payload"));
                File.WriteAllText(Path.Combine(transaction, OriginalPayloadFileName), string.Empty);
            }

            Directory.CreateDirectory(Path.Combine(transaction, "registry"));
            Directory.CreateDirectory(Path.Combine(transaction, "files"));
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int BackupRegistryKey(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey)
    {
        try
        {
            FailOnceForTest(transactionDirectory, FailNextSnapshotForTestFileName);
            var path = SnapshotPath(transactionDirectory, "registry", name);
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((byte)1);
            writer.Write(rootName);
            writer.Write(viewBits);
            writer.Write(subKey);
            using var root = OpenRoot(rootName, viewBits, writable: false);
            using var key = root.OpenSubKey(subKey, writable: false);
            writer.Write(key is not null);
            if (key is not null)
            {
                WriteRegistryKey(writer, key);
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int BackupRegistryValue(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey,
        string valueName)
    {
        try
        {
            FailOnceForTest(transactionDirectory, FailNextSnapshotForTestFileName);
            var path = SnapshotPath(transactionDirectory, "registry", name);
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((byte)2);
            writer.Write(rootName);
            writer.Write(viewBits);
            writer.Write(subKey);
            writer.Write(valueName);
            using var root = OpenRoot(rootName, viewBits, writable: false);
            using var key = root.OpenSubKey(subKey, writable: false);
            var exists = key?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true;
            writer.Write(exists);
            if (exists)
            {
                WriteRegistryValue(writer, key!, valueName);
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int BackupFile(string transactionDirectory, string name, string path)
    {
        try
        {
            FailOnceForTest(transactionDirectory, FailNextSnapshotForTestFileName);
            var directory = SnapshotDirectory(transactionDirectory, "files", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "path.txt"), Path.GetFullPath(path));
            if (File.Exists(path))
            {
                RejectReparsePoint(path);
                File.Copy(path, Path.Combine(directory, "content"), overwrite: false);
                File.WriteAllText(Path.Combine(directory, "exists"), string.Empty);
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Activate(string transactionDirectory, string rootName, int viewBits)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            FailOnceForTest(transaction, FailNextActivationForTestFileName);
            if (!File.Exists(Path.Combine(transaction, StateFileName)))
            {
                throw new InvalidOperationException("The install transaction has not been prepared.");
            }
            WriteSeal(transaction, rootName, viewBits);
            File.WriteAllText(Path.Combine(transaction, ActiveFileName), string.Empty);
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int Commit(string transactionDirectory, string rootName, int viewBits)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            var committed = CommittedTransactionDirectory(transaction);
            CleanupCommitted(transaction);
            if (Directory.Exists(transaction))
            {
                // 仓库集成 Fixture 用此内部标记确定性模拟提交后的清理失败。
                // 它不属于公共协议，也不会改变目录重命名这一真实提交点。
                var retainCommittedForTest = File.Exists(Path.Combine(transaction, RetainCommittedForTestFileName));
                // 同卷目录重命名是提交点。提交后清理失败不能再回滚已完成的安装；
                // 保留 `.committed` 目录，由下一次安装启动安全重试清理。
                Directory.Move(transaction, committed);
                TryDeleteSeal(transaction, rootName, viewBits);
                if (!retainCommittedForTest)
                {
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
            }
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    internal static int ValidateRegistryKeySnapshot(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey) =>
        Execute(() => ValidateRegistrySnapshot(transactionDirectory, name, 1, rootName, viewBits, subKey, null));

    internal static int ValidateRegistryValueSnapshot(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey,
        string valueName) =>
        Execute(() => ValidateRegistrySnapshot(transactionDirectory, name, 2, rootName, viewBits, subKey, valueName));

    internal static int ValidateFileSnapshot(string transactionDirectory, string name, string destination) =>
        Execute(() => ValidateFileSnapshotCore(transactionDirectory, name, destination));

    internal static int ValidateSnapshotSet(string transactionDirectory, int registryCount, int fileCount) =>
        Execute(() =>
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            if (!IsActiveTransaction(transaction))
            {
                return;
            }
            JournalAccess.ValidateProtectedMachineTree(transaction);
            var actualRegistryCount = Directory.EnumerateFiles(Path.Combine(transaction, "registry"), "*.bin").Count();
            var actualFileCount = Directory.EnumerateDirectories(Path.Combine(transaction, "files")).Count();
            if (actualRegistryCount != registryCount || actualFileCount != fileCount)
            {
                throw new RecoveryManifestMismatchException();
            }
        });

    internal static int ValidateSnapshotIntegrity(string transactionDirectory, string rootName, int viewBits) =>
        Execute(() =>
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            if (!IsActiveTransaction(transaction))
            {
                return;
            }
            JournalAccess.ValidateProtectedMachineTree(transaction);
            using var root = OpenRoot(rootName, viewBits, writable: false);
            using var key = root.OpenSubKey(SealRegistryPath(transaction), writable: false);
            var expected = key?.GetValue("SHA256") as string;
            if (expected is null || !expected.Equals(HashSnapshotTree(transaction), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The active install transaction snapshot content failed integrity validation.");
            }
        });

    internal static int BeginRecovery(string transactionDirectory, string installDirectory)
    {
        return Execute(() =>
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            JournalAccess.ValidateProtectedMachineTree(transaction);
            if (!TryGetActiveTransaction(transaction, ValidateInstallDirectory(installDirectory, transaction), out var install))
            {
                return;
            }

            FailOnceForTest(transaction, FailNextPayloadRestoreForTestFileName);
            if (Directory.Exists(install))
            {
                SafeDeleteTree(install);
            }
            if (File.Exists(Path.Combine(transaction, OriginalPayloadFileName)))
            {
                CopyDirectory(Path.Combine(transaction, "payload"), install);
            }
        });
    }

    internal static int BeginRegistryRestore(string transactionDirectory) =>
        Execute(() =>
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            if (IsActiveTransaction(transaction))
            {
                FailOnceForTest(transaction, FailNextRegistryRestoreForTestFileName);
            }
        });

    internal static int RestoreRegistryKey(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey) =>
        Execute(() => RestoreRegistrySnapshot(transactionDirectory, name, 1, rootName, viewBits, subKey, null));

    internal static int RestoreRegistryValue(
        string transactionDirectory,
        string name,
        string rootName,
        int viewBits,
        string subKey,
        string valueName) =>
        Execute(() => RestoreRegistrySnapshot(transactionDirectory, name, 2, rootName, viewBits, subKey, valueName));

    internal static int RestoreFile(string transactionDirectory, string name, string destination) =>
        Execute(() => RestoreFileSnapshot(transactionDirectory, name, destination));

    internal static int CompleteRecovery(string transactionDirectory, string rootName, int viewBits) =>
        Execute(() =>
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            if (!IsActiveTransaction(transaction))
            {
                return;
            }
            FailOnceForTest(transaction, FailNextJournalCleanupForTestFileName);
            SafeDeleteTree(transaction);
            TryDeleteSeal(transaction, rootName, viewBits);
        });

    private static void WriteSeal(string transaction, string rootName, int viewBits)
    {
        var digest = HashSnapshotTree(transaction);
        using var root = OpenRoot(rootName, viewBits, writable: true);
        using var key = root.CreateSubKey(SealRegistryPath(transaction), writable: true)
            ?? throw new IOException("Cannot create the install transaction integrity seal.");
        key.SetValue("SHA256", digest, RegistryValueKind.String);
    }

    private static void DeleteSeal(string transaction, string rootName, int viewBits)
    {
        using var root = OpenRoot(rootName, viewBits, writable: true);
        root.DeleteSubKeyTree(SealRegistryPath(transaction), throwOnMissingSubKey: false);
    }

    private static void TryDeleteSeal(string transaction, string rootName, int viewBits)
    {
        // 提交点与恢复已完成后，清理锚点失败不能再把已完成操作报告为可回滚失败。
        try
        {
            DeleteSeal(transaction, rootName, viewBits);
        }
        catch (Exception)
        {
        }
    }

    private static string SealRegistryPath(string transaction) =>
        SealRegistryPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transaction.ToUpperInvariant())));

    private static string HashSnapshotTree(string transaction)
    {
        var entries = new List<(string Relative, string FullPath, bool Directory)>();
        var pending = new Stack<string>();
        pending.Push(transaction);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (current.Equals(transaction, StringComparison.OrdinalIgnoreCase) &&
                    (name == ActiveFileName || name.StartsWith(".dotnet-bundler-test-", StringComparison.Ordinal)))
                {
                    continue;
                }
                RejectReparsePoint(entry);
                var directory = Directory.Exists(entry);
                entries.Add((Path.GetRelativePath(transaction, entry), entry, directory));
                if (directory)
                {
                    pending.Push(entry);
                }
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes((entry.Directory ? "D:" : "F:") + entry.Relative.ToUpperInvariant() + "\0"));
            if (entry.Directory)
            {
                continue;
            }
            using var stream = File.OpenRead(entry.FullPath);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static int Execute(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (RecoveryManifestMismatchException)
        {
            return RecoveryManifestMismatch;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    private static bool TryGetActiveTransaction(
        string transaction,
        string expectedInstallDirectory,
        out string install)
    {
        install = expectedInstallDirectory;
        if (!Directory.Exists(transaction))
        {
            return false;
        }

        var active = Path.Combine(transaction, ActiveFileName);
        var state = Path.Combine(transaction, StateFileName);
        if (!File.Exists(active) || !File.Exists(state))
        {
            SafeDeleteTree(transaction);
            return false;
        }

        install = ValidateInstallDirectory(File.ReadAllText(state), transaction);
        if (!install.Equals(expectedInstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The install transaction does not match the selected install directory.");
        }
        return true;
    }

    private static bool IsActiveTransaction(string transaction) =>
        Directory.Exists(transaction) &&
        File.Exists(Path.Combine(transaction, ActiveFileName)) &&
        File.Exists(Path.Combine(transaction, StateFileName));

    private static void FailOnceForTest(string transactionDirectory, string markerName)
    {
        var marker = Path.Combine(ValidateTransactionDirectory(transactionDirectory), markerName);
        if (!File.Exists(marker))
        {
            return;
        }

        // 仅供仓库集成 Fixture 确定性验证可重试故障。
        // 先消费标记再失败，使下次安装启动能执行真实恢复。
        File.Delete(marker);
        throw new IOException($"Injected one-shot install transaction failure: {markerName}");
    }

    private static void CleanupCommitted(string transactionDirectory)
    {
        var committed = CommittedTransactionDirectory(transactionDirectory);
        if (Directory.Exists(committed))
        {
            SafeDeleteTree(committed);
        }
    }

    private static string CommittedTransactionDirectory(string transactionDirectory) =>
        ValidateTransactionDirectory(transactionDirectory) + CommittedSuffix;

    private static void ValidateRegistrySnapshot(
        string transactionDirectory,
        string name,
        byte expectedKind,
        string expectedRoot,
        int expectedViewBits,
        string expectedSubKey,
        string? expectedValueName)
    {
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!IsActiveTransaction(transaction))
        {
            return;
        }

        using var reader = OpenRegistrySnapshot(transaction, name);
        ValidateRegistryTarget(reader, expectedKind, expectedRoot, expectedViewBits, expectedSubKey, expectedValueName);
    }

    private static void RestoreRegistrySnapshot(
        string transactionDirectory,
        string name,
        byte expectedKind,
        string expectedRoot,
        int expectedViewBits,
        string expectedSubKey,
        string? expectedValueName)
    {
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!IsActiveTransaction(transaction))
        {
            return;
        }

        using var reader = OpenRegistrySnapshot(transaction, name);
        ValidateRegistryTarget(reader, expectedKind, expectedRoot, expectedViewBits, expectedSubKey, expectedValueName);
        using var root = OpenRoot(expectedRoot, expectedViewBits, writable: true);
        if (expectedKind == 1)
        {
            var exists = reader.ReadBoolean();
            root.DeleteSubKeyTree(expectedSubKey, throwOnMissingSubKey: false);
            if (exists)
            {
                using var key = root.CreateSubKey(expectedSubKey, writable: true)!;
                ReadRegistryKey(reader, key);
            }
        }
        else
        {
            var exists = reader.ReadBoolean();
            using var key = root.CreateSubKey(expectedSubKey, writable: true)!;
            if (exists)
            {
                ReadRegistryValue(reader, key, expectedValueName!);
            }
            else
            {
                key.DeleteValue(expectedValueName!, throwOnMissingValue: false);
            }
        }
    }

    private static BinaryReader OpenRegistrySnapshot(string transaction, string name)
    {
        var snapshot = SnapshotPath(transaction, "registry", name);
        if (!File.Exists(snapshot))
        {
            throw new RecoveryManifestMismatchException();
        }
        RejectReparsePoint(snapshot);
        return new BinaryReader(File.OpenRead(snapshot));
    }

    private static void ValidateRegistryTarget(
        BinaryReader reader,
        byte expectedKind,
        string expectedRoot,
        int expectedViewBits,
        string expectedSubKey,
        string? expectedValueName)
    {
        var kind = reader.ReadByte();
        var rootName = reader.ReadString();
        var viewBits = reader.ReadInt32();
        var subKey = reader.ReadString();
        var valueName = kind == 2 ? reader.ReadString() : null;
        if (kind != expectedKind ||
            !rootName.Equals(expectedRoot, StringComparison.Ordinal) ||
            viewBits != expectedViewBits ||
            !subKey.Equals(expectedSubKey, StringComparison.Ordinal) ||
            !string.Equals(valueName, expectedValueName, StringComparison.Ordinal))
        {
            throw new RecoveryManifestMismatchException();
        }
    }

    private static void ValidateFileSnapshotCore(string transactionDirectory, string name, string expectedDestination)
    {
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!IsActiveTransaction(transaction))
        {
            return;
        }

        var snapshot = SnapshotDirectory(transaction, "files", name);
        if (!Directory.Exists(snapshot))
        {
            throw new RecoveryManifestMismatchException();
        }
        RejectReparsePoint(snapshot);
        var journalDestination = Path.GetFullPath(File.ReadAllText(Path.Combine(snapshot, "path.txt")));
        var trustedDestination = Path.GetFullPath(expectedDestination);
        if (!journalDestination.Equals(trustedDestination, StringComparison.OrdinalIgnoreCase))
        {
            throw new RecoveryManifestMismatchException();
        }
    }

    private static void RestoreFileSnapshot(string transactionDirectory, string name, string expectedDestination)
    {
        ValidateFileSnapshotCore(transactionDirectory, name, expectedDestination);
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!IsActiveTransaction(transaction))
        {
            return;
        }

        var snapshot = SnapshotDirectory(transaction, "files", name);
        var destination = Path.GetFullPath(expectedDestination);
        RejectExistingReparsePoints(destination);
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }
        if (File.Exists(Path.Combine(snapshot, "exists")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(snapshot, "content"), destination, overwrite: false);
        }
        else
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    private static void WriteRegistryKey(BinaryWriter writer, RegistryKey key)
    {
        var valueNames = key.GetValueNames();
        writer.Write(valueNames.Length);
        foreach (var valueName in valueNames)
        {
            writer.Write(valueName);
            WriteRegistryValue(writer, key, valueName);
        }

        var subKeyNames = key.GetSubKeyNames();
        writer.Write(subKeyNames.Length);
        foreach (var subKeyName in subKeyNames)
        {
            writer.Write(subKeyName);
            using var subKey = key.OpenSubKey(subKeyName, writable: false)!;
            WriteRegistryKey(writer, subKey);
        }
    }

    private static void ReadRegistryKey(BinaryReader reader, RegistryKey key)
    {
        var valueCount = reader.ReadInt32();
        for (var index = 0; index < valueCount; index++)
        {
            var valueName = reader.ReadString();
            ReadRegistryValue(reader, key, valueName);
        }

        var subKeyCount = reader.ReadInt32();
        for (var index = 0; index < subKeyCount; index++)
        {
            var subKeyName = reader.ReadString();
            using var subKey = key.CreateSubKey(subKeyName, writable: true)!;
            ReadRegistryKey(reader, subKey);
        }
    }

    private static void WriteRegistryValue(BinaryWriter writer, RegistryKey key, string valueName)
    {
        var kind = key.GetValueKind(valueName);
        writer.Write((int)kind);
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new InvalidDataException($"Registry value disappeared while being captured: {valueName}");
        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                writer.Write((string)value);
                break;
            case RegistryValueKind.MultiString:
                var strings = (string[])value;
                writer.Write(strings.Length);
                foreach (var item in strings)
                {
                    writer.Write(item);
                }
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                var bytes = (byte[])value;
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            case RegistryValueKind.DWord:
                writer.Write((int)value);
                break;
            case RegistryValueKind.QWord:
                writer.Write((long)value);
                break;
            default:
                throw new InvalidDataException($"Unsupported registry value kind: {kind}");
        }
    }

    private static void ReadRegistryValue(BinaryReader reader, RegistryKey key, string valueName)
    {
        var kind = (RegistryValueKind)reader.ReadInt32();
        object value = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => reader.ReadString(),
            RegistryValueKind.MultiString => ReadStrings(reader),
            RegistryValueKind.Binary or RegistryValueKind.None => reader.ReadBytes(reader.ReadInt32()),
            RegistryValueKind.DWord => reader.ReadInt32(),
            RegistryValueKind.QWord => reader.ReadInt64(),
            _ => throw new InvalidDataException($"Unsupported registry value kind: {kind}")
        };
        key.SetValue(valueName, value, kind);
    }

    private static string[] ReadStrings(BinaryReader reader)
    {
        var values = new string[reader.ReadInt32()];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadString();
        }
        return values;
    }

    private static RegistryKey OpenRoot(string rootName, int viewBits, bool writable)
    {
        var hive = rootName switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new ArgumentException($"Unsupported registry root: {rootName}", nameof(rootName))
        };
        var view = viewBits switch
        {
            32 => RegistryView.Registry32,
            64 => RegistryView.Registry64,
            _ => throw new ArgumentOutOfRangeException(nameof(viewBits), viewBits, "Registry view must be 32 or 64.")
        };
        return RegistryKey.OpenBaseKey(hive, view);
    }

    private static void CopyDirectory(string source, string destination)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            RejectReparsePoint(directory);
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            RejectReparsePoint(file);
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
            File.SetAttributes(target, File.GetAttributes(file));
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(file));
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Install transaction cannot safely copy a reparse point: {path}");
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

    private static string SnapshotPath(string transactionDirectory, string category, string name) =>
        Path.Combine(SnapshotDirectory(transactionDirectory, category, name) + ".bin");

    private static string SnapshotDirectory(string transactionDirectory, string category, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Snapshot names may contain only ASCII letters, digits, and hyphens.", nameof(name));
        }
        return Path.Combine(ValidateTransactionDirectory(transactionDirectory), category, name);
    }

    private static string ValidateTransactionDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) || Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar).Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("The transaction directory must not be a drive root.", nameof(path));
        }
        RejectExistingReparsePoints(fullPath);
        return fullPath;
    }

    private static string ValidateInstallDirectory(string path, string transactionDirectory)
    {
        var fullPath = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) || Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar).Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("The install directory must not be a drive root.", nameof(path));
        }
        if (transactionDirectory.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(transactionDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The transaction and install directories must not contain each other.", nameof(path));
        }
        RejectExistingReparsePoints(fullPath);
        return fullPath;
    }

    private sealed class RecoveryManifestMismatchException : Exception
    {
        internal RecoveryManifestMismatchException()
            : base("The active transaction recovery manifest differs from this installer. Use the original installer with /S /RECOVERONLY and the same /D= directory before retrying.")
        {
        }
    }
}

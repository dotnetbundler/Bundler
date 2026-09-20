using Microsoft.Win32;
using System.Runtime.Versioning;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
internal static class InstallTransaction
{
    private const string StateFileName = "state.txt";
    private const string ActiveFileName = "active";
    private const string OriginalPayloadFileName = "original-payload";
    private const string CommittedSuffix = ".committed";
    private const string RetainCommittedForTestFileName = ".dotnet-bundler-test-retain-committed";

    internal static int Begin(string transactionDirectory, string installDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            var install = ValidateInstallDirectory(installDirectory, transaction);
            RecoverCore(transactionDirectory, install);
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
            var directory = SnapshotDirectory(transactionDirectory, "files", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "path.txt"), Path.GetFullPath(path));
            if (File.Exists(path))
            {
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

    internal static int Activate(string transactionDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            if (!File.Exists(Path.Combine(transaction, StateFileName)))
            {
                throw new InvalidOperationException("The install transaction has not been prepared.");
            }
            File.WriteAllText(Path.Combine(transaction, ActiveFileName), string.Empty);
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

    internal static int Rollback(string transactionDirectory, string installDirectory)
    {
        try
        {
            var transaction = ValidateTransactionDirectory(transactionDirectory);
            CleanupCommitted(transaction);
            RecoverCore(transaction, ValidateInstallDirectory(installDirectory, transaction));
            return 0;
        }
        catch (Exception exception)
        {
            return exception.HResult;
        }
    }

    private static void RecoverCore(string transactionDirectory, string expectedInstallDirectory)
    {
        var transaction = ValidateTransactionDirectory(transactionDirectory);
        if (!Directory.Exists(transaction))
        {
            return;
        }

        var active = Path.Combine(transaction, ActiveFileName);
        var state = Path.Combine(transaction, StateFileName);
        if (!File.Exists(active) || !File.Exists(state))
        {
            Directory.Delete(transaction, recursive: true);
            return;
        }

        var install = ValidateInstallDirectory(File.ReadAllText(state), transaction);
        if (!install.Equals(expectedInstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The install transaction does not match the selected install directory.");
        }
        if (Directory.Exists(install))
        {
            Directory.Delete(install, recursive: true);
        }
        if (File.Exists(Path.Combine(transaction, OriginalPayloadFileName)))
        {
            CopyDirectory(Path.Combine(transaction, "payload"), install);
        }

        RestoreRegistry(transaction);
        RestoreFiles(transaction);
        Directory.Delete(transaction, recursive: true);
    }

    private static void CleanupCommitted(string transactionDirectory)
    {
        var committed = CommittedTransactionDirectory(transactionDirectory);
        if (Directory.Exists(committed))
        {
            Directory.Delete(committed, recursive: true);
        }
    }

    private static string CommittedTransactionDirectory(string transactionDirectory) =>
        ValidateTransactionDirectory(transactionDirectory) + CommittedSuffix;

    private static void RestoreRegistry(string transaction)
    {
        var directory = Path.Combine(transaction, "registry");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var snapshot in Directory.EnumerateFiles(directory, "*.bin").OrderBy(path => path, StringComparer.Ordinal))
        {
            using var reader = new BinaryReader(File.OpenRead(snapshot));
            var kind = reader.ReadByte();
            var rootName = reader.ReadString();
            var viewBits = reader.ReadInt32();
            var subKey = reader.ReadString();
            using var root = OpenRoot(rootName, viewBits, writable: true);
            if (kind == 1)
            {
                var exists = reader.ReadBoolean();
                root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                if (exists)
                {
                    using var key = root.CreateSubKey(subKey, writable: true)!;
                    ReadRegistryKey(reader, key);
                }
            }
            else if (kind == 2)
            {
                var valueName = reader.ReadString();
                var exists = reader.ReadBoolean();
                using var key = root.CreateSubKey(subKey, writable: true)!;
                if (exists)
                {
                    ReadRegistryValue(reader, key, valueName);
                }
                else
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                }
            }
            else
            {
                throw new InvalidDataException($"Unknown registry snapshot kind: {kind}");
            }
        }
    }

    private static void RestoreFiles(string transaction)
    {
        var directory = Path.Combine(transaction, "files");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var snapshot in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var destination = Path.GetFullPath(File.ReadAllText(Path.Combine(snapshot, "path.txt")));
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
        return fullPath;
    }
}

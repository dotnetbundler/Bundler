using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
internal static class JournalAccess
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);

    internal static void EnsureProtectedMachineRoot(string transactionDirectory)
    {
        if (!TryGetMachinePaths(transactionDirectory, out var product, out var transactions))
        {
            return;
        }

        if (!Directory.Exists(product))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(Administrators);
            security.AddAccessRule(new FileSystemAccessRule(
                Administrators, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                SystemAccount, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(product).Create(security);
        }

        ValidateEntry(product);
        Directory.CreateDirectory(transactions);
        ValidateEntry(transactions);
    }

    internal static void ValidateProtectedMachineTree(string transactionDirectory)
    {
        if (!TryGetMachinePaths(transactionDirectory, out var product, out var transactions) ||
            !Directory.Exists(product))
        {
            return;
        }

        ValidateEntry(product);
        if (!Directory.Exists(transactions))
        {
            return;
        }
        ValidateEntry(transactions);
        if (!Directory.Exists(transactionDirectory))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(transactionDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ValidateEntry(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ValidateEntry(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool TryGetMachinePaths(string transactionDirectory, out string product, out string transactions)
    {
        product = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DotNetBundler");
        transactions = Path.Combine(product, "transactions");
        var fullPath = Path.GetFullPath(transactionDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(fullPath)?.Equals(transactions, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void ValidateEntry(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The per-machine journal contains a reparse point: {path}");
        }

        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException($"The per-machine journal has no trusted owner: {path}");
        if (!owner.Equals(Administrators) && !owner.Equals(SystemAccount))
        {
            throw new UnauthorizedAccessException($"The per-machine journal has an untrusted owner: {path}");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow &&
                !identity.Equals(Administrators) && !identity.Equals(SystemAccount))
            {
                throw new UnauthorizedAccessException($"The per-machine journal grants access outside Administrators and SYSTEM: {path}");
            }
        }
    }
}

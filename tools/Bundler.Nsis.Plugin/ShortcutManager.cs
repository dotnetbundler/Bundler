using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotNet.Bundler.Nsis.Plugin;

[SupportedOSPlatform("windows")]
internal static unsafe class ShortcutManager
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint StgmRead = 0x0;
    private const ushort VtLpwstr = 31;
    private const int ShowNormal = 1;
    private const int MaxPath = 32768;
    private const int Success = 1;
    private const int NotOwnedOrMissing = 0;

    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterface = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterface = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid PropertyStoreInterface = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    internal static int Create(string shortcut, string ownedTargets, string target, string arguments, string workingDirectory, string icon, string appUserModelId)
    {
        try
        {
            using var com = ComApartment.Enter();
            if (File.Exists(shortcut) && !IsOwned(shortcut, ownedTargets)) return NotOwnedOrMissing;
            Save(shortcut, target, arguments, workingDirectory, icon, appUserModelId);
            return Success;
        }
        catch { return -1; }
    }

    internal static int UpdateIfOwned(string shortcut, string ownedTargets, string target, string arguments, string workingDirectory, string icon, string appUserModelId)
    {
        try
        {
            using var com = ComApartment.Enter();
            if (!IsOwned(shortcut, ownedTargets)) return NotOwnedOrMissing;
            Save(shortcut, target, arguments, workingDirectory, icon, appUserModelId);
            return Success;
        }
        catch { return -1; }
    }

    internal static int MoveIfOwned(string source, string destination, string ownedTargets, string target, string arguments, string workingDirectory, string icon, string appUserModelId)
    {
        try
        {
            using var com = ComApartment.Enter();
            if (!IsOwned(source, ownedTargets)) return NotOwnedOrMissing;
            if (File.Exists(destination))
            {
                if (!IsOwned(destination, ownedTargets)) return NotOwnedOrMissing;
                File.Delete(source);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
            }
            Save(destination, target, arguments, workingDirectory, icon, appUserModelId);
            return Success;
        }
        catch { return -1; }
    }

    internal static int DeleteIfOwned(string shortcut, string ownedTargets)
    {
        try
        {
            using var com = ComApartment.Enter();
            if (!IsOwned(shortcut, ownedTargets)) return NotOwnedOrMissing;
            File.Delete(shortcut);
            return Success;
        }
        catch { return -1; }
    }

    private static bool IsOwned(string shortcut, string ownedTargets)
    {
        if (!File.Exists(shortcut)) return false;
        var target = ReadTarget(shortcut);
        return ownedTargets.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(candidate => PathsEqual(candidate, target));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadTarget(string shortcut)
    {
        var shellLink = CreateShellLink();
        try
        {
            var persistFile = QueryInterface(shellLink, PersistFileInterface);
            try
            {
                Load(persistFile, shortcut, StgmRead);
                var buffer = stackalloc char[MaxPath];
                ThrowIfFailed(GetPath(shellLink, buffer, MaxPath, 0x4));
                return new string(buffer);
            }
            finally { Release(persistFile); }
        }
        finally { Release(shellLink); }
    }

    private static void Save(string shortcut, string target, string arguments, string workingDirectory, string icon, string appUserModelId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var shellLink = CreateShellLink();
        try
        {
            SetString(shellLink, 20, target);
            SetString(shellLink, 11, arguments);
            SetString(shellLink, 9, workingDirectory);
            SetIconLocation(shellLink, icon, 0);
            SetShowCommand(shellLink, ShowNormal);

            var propertyStore = QueryInterface(shellLink, PropertyStoreInterface);
            try
            {
                var key = AppUserModelIdKey;
                var value = PropVariant.FromString(appUserModelId);
                try
                {
                    var vtable = *(nint**)propertyStore;
                    var setValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtable[6];
                    ThrowIfFailed(setValue(propertyStore, &key, &value));
                    var commit = (delegate* unmanaged[Stdcall]<nint, int>)vtable[7];
                    ThrowIfFailed(commit(propertyStore));
                }
                finally { value.Dispose(); }
            }
            finally { Release(propertyStore); }

            var persistFile = QueryInterface(shellLink, PersistFileInterface);
            try { SaveFile(persistFile, shortcut); }
            finally { Release(persistFile); }
        }
        finally { Release(shellLink); }
    }

    private static nint CreateShellLink()
    {
        var classId = ShellLinkClass;
        var interfaceId = ShellLinkInterface;
        ThrowIfFailed(CoCreateInstance(ref classId, 0, ClsctxInprocServer, ref interfaceId, out var shellLink));
        return shellLink;
    }

    private static nint QueryInterface(nint instance, Guid interfaceId)
    {
        var vtable = *(nint**)instance;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        nint result = 0;
        ThrowIfFailed(queryInterface(instance, &interfaceId, &result));
        return result;
    }

    private static uint Release(nint instance)
    {
        var vtable = *(nint**)instance;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        return release(instance);
    }

    private static int GetPath(nint shellLink, char* buffer, int count, uint flags)
    {
        var vtable = *(nint**)shellLink;
        var method = (delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)vtable[3];
        return method(shellLink, buffer, count, 0, flags);
    }

    private static void SetString(nint shellLink, int slot, string value)
    {
        fixed (char* pointer = value)
        {
            var vtable = *(nint**)shellLink;
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int>)vtable[slot];
            ThrowIfFailed(method(shellLink, pointer));
        }
    }

    private static void SetIconLocation(nint shellLink, string icon, int index)
    {
        fixed (char* pointer = icon)
        {
            var vtable = *(nint**)shellLink;
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)vtable[17];
            ThrowIfFailed(method(shellLink, pointer, index));
        }
    }

    private static void SetShowCommand(nint shellLink, int command)
    {
        var vtable = *(nint**)shellLink;
        var method = (delegate* unmanaged[Stdcall]<nint, int, int>)vtable[15];
        ThrowIfFailed(method(shellLink, command));
    }

    private static void Load(nint persistFile, string path, uint mode)
    {
        fixed (char* pointer = path)
        {
            var vtable = *(nint**)persistFile;
            var method = (delegate* unmanaged[Stdcall]<nint, char*, uint, int>)vtable[5];
            ThrowIfFailed(method(persistFile, pointer, mode));
        }
    }

    private static void SaveFile(nint persistFile, string path)
    {
        fixed (char* pointer = path)
        {
            var vtable = *(nint**)persistFile;
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)vtable[6];
            ThrowIfFailed(method(persistFile, pointer, 1));
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint instance);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private sealed class ComApartment : IDisposable
    {
        private const int RpcChangedMode = unchecked((int)0x80010106);
        private readonly bool ownsInitialization;
        private ComApartment(bool ownsInitialization) => this.ownsInitialization = ownsInitialization;
        internal static ComApartment Enter()
        {
            var result = CoInitializeEx(0, 0x2);
            if (result < 0 && result != RpcChangedMode) Marshal.ThrowExceptionForHR(result);
            return new ComApartment(result >= 0);
        }
        public void Dispose() { if (ownsInitialization) CoUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort valueType;
        [FieldOffset(8)] private nint pointer;
        public static PropVariant FromString(string value) => new() { valueType = VtLpwstr, pointer = Marshal.StringToCoTaskMemUni(value) };
        public void Dispose()
        {
            if (pointer == 0) return;
            Marshal.FreeCoTaskMem(pointer);
            pointer = 0;
        }
    }
}

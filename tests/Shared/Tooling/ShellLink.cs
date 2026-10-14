// .lnk 读写：IShellLinkW 逐字移植自原 tests/AssertLocalRestore.ps1 的内嵌 C#。
// WScript.Shell 走 ANSI 代码页无法读写 Unicode 目标路径，必须走 IShellLinkW；
// AppUserModelId 另经 Shell.Application ExtendedProperty 读取。
using System.Runtime.InteropServices;
using System.Text;

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out] StringBuilder file, int capacity, IntPtr findData, uint flags);
    void GetIDList(out IntPtr idList);
    void SetIDList(IntPtr idList);
    void GetDescription([Out] StringBuilder name, int capacity);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out] StringBuilder dir, int capacity);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out] StringBuilder args, int capacity);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out] StringBuilder iconPath, int capacity, out int iconIndex);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);
    void IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass { }

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed record ShellShortcutInfo(
    string Destination, string Arguments, string WorkingDirectory,
    string IconLocation, string? AppUserModelId);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ShellLink
{
    private const int BufferCapacity = 4096;

    public static ShellShortcutInfo Read(string path)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(BufferCapacity);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            if (target.Length == 0)
            {
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            }
            var arguments = new StringBuilder(BufferCapacity);
            link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(BufferCapacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var iconPath = new StringBuilder(BufferCapacity);
            link.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);
            return new ShellShortcutInfo(
                target.ToString(), arguments.ToString(), workingDirectory.ToString(),
                iconPath.Length == 0 ? "" : iconPath + "," + iconIndex,
                ReadAppUserModelId(path));
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public static void Write(string path, string targetPath, string workingDirectory = "")
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(targetPath);
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                link.SetWorkingDirectory(workingDirectory);
            }
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    // AppUserModelId 不在 IShellLinkW 上，走 Shell.Application 的 ExtendedProperty。
    private static string? ReadAppUserModelId(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            var shellType = Type.GetTypeFromProgID("Shell.Application")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var item = shell.NameSpace(folder).ParseName(name);
            return (string?)item?.ExtendedProperty("System.AppUserModel.ID");
        }
        catch
        {
            return null;
        }
    }
}

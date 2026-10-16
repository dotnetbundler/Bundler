// .app 测试桩与 quarantine xattr 读写：MacAppGate 门禁腿与 .app 更新链腿共用。
// macOS 专属面（xattr P/Invoke、主程序取真 Mach-O 才可被 codesign 签）——
// 宿主门禁（Assert.SkipUnless(TestPlatform.IsMacOS, …)）由调用方负责。
using System.Runtime.InteropServices;

internal static class MacAppStub
{
    internal const string QuarantineXattr = "com.apple.quarantine";

    // LaunchServices quarantine 的常见值形（flag;时间戳;来源;）——门禁不解析值，形如即可。
    private static readonly byte[] QuarantineValue = "0081;00000000;BundlerTests;"u8.ToArray();

    // 最小 .app：Info.plist（身份键全）+ Contents/MacOS/<name> + Resources/data.txt。
    // machOExecutable=true 时主程序落仓内真 Mach-O 引导件——codesign 拒签脚本主程序，
    // 签名面腿必须拿真二进制；未签面腿用文本桩即可（codesign -dv 非零即"未签"）。
    internal static string WriteBundle(
        string parent, string name, string bundleId, bool machOExecutable = false)
    {
        var app = Path.Combine(parent, name + ".app");
        var contents = Path.Combine(app, "Contents");
        var macos = Path.Combine(contents, "MacOS");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<plist version=\"1.0\"><dict>\n" +
            "<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>\n" +
            "<key>CFBundlePackageType</key><string>APPL</string>\n" +
            $"<key>CFBundleName</key><string>{name}</string>\n" +
            $"<key>CFBundleIdentifier</key><string>{bundleId}</string>\n" +
            $"<key>CFBundleExecutable</key><string>{name}</string>\n" +
            "<key>CFBundleShortVersionString</key><string>1.0.0</string>\n" +
            "<key>CFBundleVersion</key><string>1</string>\n" +
            "</dict></plist>\n");
        var executable = Path.Combine(macos, name);
        if (machOExecutable)
        {
            File.Copy(BootstrapperTool(TestPlatform.OsxTarget), executable);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        else
        {
            File.WriteAllText(executable, "#!/bin/sh\necho stub\n");
        }
        File.WriteAllText(Path.Combine(contents, "Resources", "data.txt"), "payload-data\n");
        return app;
    }

    // 下载经浏览器/通道而来的件真实带 quarantine——按真实形态整树打标（根+内层全节点）。
    internal static void SetQuarantineTree(string root)
    {
        SetQuarantine(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     root, "*", SearchOption.AllDirectories))
        {
            SetQuarantine(entry);
        }
    }

    internal static void SetQuarantine(string path)
    {
        var rc = setxattr(path, QuarantineXattr, QuarantineValue, QuarantineValue.Length, 0, 0);
        Assert.True(rc == 0,
            $"setxattr({QuarantineXattr}) failed rc={rc} errno={Marshal.GetLastWin32Error()}");
    }

    // getxattr 返回 -1（ENOATTR）即属性缺席。
    internal static bool HasQuarantine(string path)
    {
        var buffer = new byte[256];
        return getxattr(path, QuarantineXattr, buffer, buffer.Length, 0, 0).ToInt64() >= 0;
    }

    // 仓内引导件真件路径——macOS 签名腿拿它当 Mach-O 主程序，e2e 腿拿它当真引导件。
    internal static string BootstrapperTool(string target) => Path.Combine(
        RepositoryLayout.Root, "src", "Bundler.Updater.Bootstrap",
        "tools", target, "bundler-updater");

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr getxattr(
        string path, string name, byte[] value, int size, int position, int options);

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int setxattr(
        string path, string name, byte[] value, int size, int position, int options);
}

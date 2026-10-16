// .app 测试桩与 quarantine xattr 读写：MacAppGate 门禁腿与 .app 更新链腿共用。
// macOS 专属面（写标走 xattr CLI、读标走 getxattr P/Invoke、主程序取真 Mach-O
// 才可被 codesign 签）——宿主门禁（Assert.SkipUnless(TestPlatform.IsMacOS, …)）由调用方负责。
using System.Runtime.InteropServices;

internal static class MacAppStub
{
    internal const string QuarantineXattr = "com.apple.quarantine";

    // LaunchServices quarantine 的常见值形（flag;时间戳;来源;）——门禁不解析值，形如即可。
    private const string QuarantineValue = "0081;00000000;BundlerTests;";

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

    // 打标必须走 Apple 自带 xattr CLI：macOS 26 起 com.apple.quarantine 是受保护
    // xattr——第三方进程裸 setxattr(2) 返回 0 但属性被系统静默丢弃（随后 getxattr
    // 读回失败，macos-26 CI 实证）；/usr/bin/xattr -w 由系统授权写，与生产剥标
    // （MacAppGate 走 xattr -dr）同款 CLI 通道。
    internal static void SetQuarantine(string path)
    {
        ProcessRunner.AssertSuccess(ProcessRunner.Run("/usr/bin/xattr",
            ["-w", QuarantineXattr, QuarantineValue, path]),
            $"set {QuarantineXattr} on '{path}'");
    }

    // 存在性按返回码判：size=0 探测只回值长不落缓冲——值超缓冲的 ERANGE(-1)
    // 与"无属性"的 -1 同形，靠读缓冲非零猜会把大件误判缺席；空值属性返回 0 算存在。
    internal static bool HasQuarantine(string path) =>
        getxattr(path, QuarantineXattr, [], 0, 0, 0).ToInt64() >= 0;

    // 仓内引导件真件路径——macOS 签名腿拿它当 Mach-O 主程序，e2e 腿拿它当真引导件。
    internal static string BootstrapperTool(string target) => Path.Combine(
        RepositoryLayout.Root, "src", "Bundler.Updater.Bootstrap",
        "tools", target, "bundler-updater");

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr getxattr(
        string path, string name, byte[] value, nuint size, uint position, int options);
}

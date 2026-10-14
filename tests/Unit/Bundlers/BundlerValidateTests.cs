using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Wix;

namespace Bundler.UnitTests.Bundlers;

/// <summary>
/// 各格式 Bundler 适配器的 Validate 预检面——全在任何子进程/构建副作用之前抛，
/// 是统一契约：错格式 NotSupported、宿主不符 PNSE、配置错 Argument 族。
/// </summary>
public sealed class BundlerValidateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bundler-validate-" + Guid.NewGuid().ToString("N"));

    public BundlerValidateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        MacDmgBundleBackend.HostCheck = null;
        MacPkgBundleBackend.HostCheck = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private BundleConfiguration Bundle(
        PackageFormat format,
        string version = "1.2.3",
        string? mainExecutable = null,
        IReadOnlyList<string>? signingFiles = null,
        IReadOnlyList<string>? icons = null,
        string? licenseFile = null,
        string? payloadName = null)
    {
        var input = Path.Combine(_root, "in-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "app"), "x");
        if (payloadName is not null)
        {
            File.WriteAllText(Path.Combine(input, payloadName), "x");
        }
        var windows = format is PackageFormat.Nsis or PackageFormat.Msi;
        return new BundleConfiguration
        {
            ProductName = "Sample",
            Identifier = "com.example.sample",
            Version = version,
            Icons = icons ?? [],
            LicenseFile = licenseFile,
            Targets = [new BundleTargetConfiguration
            {
                Target = windows ? "windows-x86_64" : "linux-x86_64",
                InputDirectory = input,
                MainExecutable = mainExecutable,
                SigningFiles = signingFiles ?? [],
                Formats = [format],
            }],
        };
    }

    // ---- 通用格式门：每个后端只收自己格式 ----

    [Fact]
    public void FormatGate_RejectForeignFormats()
    {
        Assert.Throws<NotSupportedException>(() => new NsisBundler().Validate(Bundle(PackageFormat.Msi)));
        Assert.Throws<NotSupportedException>(() => new WixBundler().Validate(Bundle(PackageFormat.Deb)));
        Assert.Throws<NotSupportedException>(() => new DebBundler().Validate(Bundle(PackageFormat.Rpm)));
        Assert.Throws<NotSupportedException>(() => new AppImageBundler().Validate(Bundle(PackageFormat.Zip)));
        Assert.Throws<NotSupportedException>(() => new MacAppBundler().Validate(Bundle(PackageFormat.Pkg)));
        Assert.Throws<NotSupportedException>(() => new MacDmgBundler().Validate(Bundle(PackageFormat.App)));
        Assert.Throws<NotSupportedException>(() => new MacPkgBundler().Validate(Bundle(PackageFormat.Dmg)));
    }

    [Fact]
    public void NullBundle_RejectedEverywhere()
    {
        Assert.Throws<ArgumentNullException>(() => new NsisBundler().Validate(null!));
        Assert.Throws<ArgumentNullException>(() => new DebBundler().Validate(null!));
        Assert.Throws<ArgumentNullException>(() => new MacDmgBundler().Validate(null!));
    }

    // ---- NsisBundler.Validate ----

    [Fact]
    public void Nsis_BadVersion_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new NsisBundler().Validate(Bundle(PackageFormat.Nsis, "not-a-version")));
        // 超 0-65535 的 Windows 组件也被拒。
        Assert.Throws<ArgumentException>(() =>
            new NsisBundler().Validate(Bundle(PackageFormat.Nsis, "70000.0.0")));
    }

    [Fact]
    public void Nsis_SigningFiles_WithoutSigner_Rejected()
    {
        var file = Path.Combine(_root, "s.exe");
        File.WriteAllText(file, "x");
        Assert.Throws<ArgumentException>(() =>
            new NsisBundler().Validate(Bundle(PackageFormat.Nsis, signingFiles: [file])));
    }

    [Fact]
    public void Nsis_IconsWithoutIco_Rejected()
    {
        var png = Path.Combine(_root, "icon.png");
        File.WriteAllText(png, "x");
        Assert.Throws<ArgumentException>(() =>
            new NsisBundler().Validate(Bundle(PackageFormat.Nsis, icons: [png])));
    }

    [Fact]
    public void Nsis_PathologicalPayloadName_Rejected()
    {
        // NUL 在 Win32 是设备名——预检在打包前拒。
        // Windows 主机上 NUL 根本建不出来（IO 先抛）——仅在 POSIX 腿跑。
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Throws<ArgumentException>(() =>
            new NsisBundler().Validate(Bundle(PackageFormat.Nsis, payloadName: "NUL")));
    }

    // ---- WixBundler.Validate ----

    [Fact]
    public void Wix_NonWindowsHost_Rejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Throws<PlatformNotSupportedException>(() =>
            new WixBundler().Validate(Bundle(PackageFormat.Msi)));
    }

    [Fact]
    public void Wix_UtfCodepage_Rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 校验顺序：宿主门在先
        }
        Assert.Throws<ArgumentException>(() => new WixBundler(
            new WixBundleConfiguration { Codepage = 65001 }).Validate(Bundle(PackageFormat.Msi)));
    }

    // ---- Mac 宿主门（HostCheck 缝进/出两支） ----

    [Fact]
    public void Dmg_HostGate_RespectsProbe()
    {
        MacDmgBundleBackend.HostCheck = () => false;
        Assert.Throws<PlatformNotSupportedException>(() =>
            new MacDmgBundler().Validate(Bundle(PackageFormat.Dmg)));

        MacDmgBundleBackend.HostCheck = () => true;
        var ex = Record.Exception(() => new MacDmgBundler().Validate(Bundle(PackageFormat.Dmg)));
        // 门放行后走旋钮校验——合法 bundle 不应再有宿主错。
        Assert.Null(ex);
    }

    [Fact]
    public void Pkg_HostGate_RespectsProbe()
    {
        MacPkgBundleBackend.HostCheck = () => false;
        Assert.Throws<PlatformNotSupportedException>(() =>
            new MacPkgBundler().Validate(Bundle(PackageFormat.Pkg)));
    }

    // ---- MacAppBundler.Validate ----

    [Fact]
    public void MacApp_LicenseFile_Rejected()
    {
        var lic = Path.Combine(_root, "LICENSE.rtf");
        File.WriteAllText(lic, "x");
        Assert.Throws<NotSupportedException>(() =>
            new MacAppBundler().Validate(Bundle(PackageFormat.App, licenseFile: lic)));
    }

    [Fact]
    public void MacApp_SigningFiles_Rejected()
    {
        Assert.Throws<NotSupportedException>(() => new MacAppBundler().Validate(
            Bundle(PackageFormat.App, signingFiles: [Path.Combine(_root, "x")])));
    }

    [Fact]
    public void MacApp_MainExecutableMustBeTopLevel()
    {
        Assert.Throws<ArgumentException>(() => new MacAppBundler().Validate(
            Bundle(PackageFormat.App, mainExecutable: "sub/dir/app")));
    }

    [Fact]
    public void MacApp_IconFormats_Whitelist()
    {
        var bad = Path.Combine(_root, "icon.bmp");
        File.WriteAllText(bad, "x");
        Assert.Throws<ArgumentException>(() =>
            new MacAppBundler().Validate(Bundle(PackageFormat.App, icons: [bad])));

        var icns = Path.Combine(_root, "icon.icns");
        File.WriteAllText(icns, "x");
        var ex = Record.Exception(() =>
            new MacAppBundler().Validate(Bundle(PackageFormat.App, icons: [icns])));
        Assert.Null(ex);
    }

    [Fact]
    public void MacApp_FrameworkDirs_ExtensionGate()
    {
        var config = new MacAppBundleConfiguration
        {
            FrameworkDirectories = ["some/dir.bad"],
        };
        Assert.Throws<ArgumentException>(() =>
            new MacAppBundler(config).Validate(Bundle(PackageFormat.App)));
    }

    [Fact]
    public void MacApp_InfoPlistMutex()
    {
        var plist = Path.Combine(_root, "Info.plist");
        File.WriteAllText(plist, "<plist/>");
        var config = new MacAppBundleConfiguration
        {
            InfoPlistFile = plist,
            InfoPlistXml = "<plist/>",
        };
        Assert.Throws<ArgumentException>(() =>
            new MacAppBundler(config).Validate(Bundle(PackageFormat.App)));
    }
}

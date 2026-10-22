using DotNet.Bundler;
using DotNet.Bundler.MSBuild;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Bundler.UnitTests.MsBuild;

/// <summary>
/// MSBuild 任务属性 → 配置对象的映射契约（不经 msbuild 宿主，直接实例化断言）。
/// </summary>
public sealed class BundleDesktopApplicationTests
{
    private static BundleDesktopApplication Task() => new()
    {
        ProductName = "Sample",
        Identifier = "com.example.sample",
        Version = "1.2.3",
        BundlerTarget = "linux-x86_64",
        InputDirectory = "in",
        OutputDirectory = "out",
        MainExecutable = "sample",
        Formats = "deb"
    };

    [Fact]
    public void BuildBundleConfiguration_MapsSharedFields()
    {
        var task = Task();
        task.Publisher = "ACME";
        task.Description = "desc";
        task.Homepage = "https://x";
        task.Copyright = "(c)";
        task.LicenseFile = "lic.txt";
        task.OutputLayout = "byFormat";

        var bundle = task.BuildBundleConfiguration([PackageFormat.Deb]);
        Assert.Equal("Sample", bundle.ProductName);
        Assert.Equal("ACME", bundle.Publisher);
        Assert.Equal("desc", bundle.Description);
        Assert.Equal("https://x", bundle.Homepage);
        Assert.Equal("(c)", bundle.Copyright);
        Assert.Equal(Path.GetFullPath("lic.txt"), bundle.LicenseFile);
        Assert.Equal(Path.GetFullPath("out"), bundle.OutputDirectory);
        Assert.Equal(OutputLayout.ByFormat, bundle.OutputLayout);

        var target = Assert.Single(bundle.Targets);
        Assert.Equal("linux-x86_64", target.Target);
        Assert.Equal(Path.GetFullPath("in"), target.InputDirectory);
        Assert.Equal("sample", target.MainExecutable);
        Assert.Equal([PackageFormat.Deb], target.Formats);
    }

    [Fact]
    public void BuildBundleConfiguration_EmptyStrings_MapToNull()
    {
        var bundle = Task().BuildBundleConfiguration([PackageFormat.Deb]);
        Assert.Null(bundle.Publisher);
        Assert.Null(bundle.Description);
        Assert.Null(bundle.LicenseFile);
    }

    [Fact]
    public void BuildBundleConfiguration_Update_Mapped()
    {
        var task = Task();
        task.UpdateEnabled = true;
        task.UpdateFeedUrl = "https://feed/";
        task.UpdateChannel = "beta";
        task.UpdateSigningKeyFile = "key.json";
        task.UpdatePublicKey = "pub";
        task.UpdateNotes = "notes";

        var update = task.BuildBundleConfiguration([PackageFormat.Deb]).Update;
        Assert.NotNull(update);
        Assert.Equal("https://feed/", update!.FeedUrl);
        Assert.Equal("beta", update.Channel);
        Assert.Equal("pub", update.PublicKey);
        Assert.Equal("notes", update.Notes);
    }

    [Fact]
    public void BuildBundleConfiguration_ItemsExpand()
    {
        var task = Task();
        task.Resources = [new TaskItem("res.dat")];
        task.FileAssociations = [AssocItem(".xyz", "Xyz Doc")];
        task.UrlProtocols = [ProtoItem("myproto")];
        task.Icons = [new TaskItem("icon.png")];
        task.WindowsSigningFiles = [new TaskItem("a.exe"), new TaskItem("b.dll")];

        var bundle = task.BuildBundleConfiguration([PackageFormat.Deb]);
        var res = Assert.Single(bundle.Resources);
        Assert.Equal(Path.GetFullPath("res.dat"), res.Source);
        Assert.Equal("res.dat", res.Destination);

        var assoc = Assert.Single(bundle.FileAssociations);
        Assert.Equal([".xyz"], assoc.Extensions);
        Assert.Equal("Xyz Doc", assoc.Name);

        var proto = Assert.Single(bundle.UrlProtocols);
        Assert.Equal(["myproto"], proto.Schemes);

        Assert.Equal([Path.GetFullPath("icon.png")], bundle.Icons);
        Assert.Equal(["a.exe", "b.dll"], bundle.Targets[0].SigningFiles);
    }

    private static TaskItem AssocItem(string ext, string name)
    {
        var item = new TaskItem(ext);
        item.SetMetadata("Name", name);
        return item;
    }

    private static TaskItem ProtoItem(string scheme)
    {
        var item = new TaskItem(scheme);
        item.SetMetadata("Name", "Proto");
        return item;
    }

    [Fact]
    public void BuildFormatConfiguration_Deb_MapsKnobs()
    {
        var task = Task();
        task.DebPackageName = "pkg";
        task.DebVersion = "9.9";
        task.DebVendor = "v";
        task.DebDepends = "libc6 (>= 2.35);libssl3";
        task.DebSection = "utils";
        task.DebBinLink = "none";
        task.DebFiles = [new TaskItem("extra.txt")];

        var deb = Assert.IsType<DotNet.Bundler.Deb.DebBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.Deb));
        Assert.Equal("pkg", deb.PackageName);
        Assert.Equal("9.9", deb.Version);
        Assert.Equal("v", deb.Vendor);
        Assert.Equal(["libc6 (>= 2.35)", "libssl3"], deb.Depends);
        Assert.Equal("utils", deb.Section);
        Assert.Equal("", deb.BinLink); // "none" 关闭语义
        Assert.Single(deb.Files!);
    }

    [Fact]
    public void BuildFormatConfiguration_Rpm_MapsKnobs()
    {
        var task = Task();
        task.RpmPackageName = "pkg";
        task.RpmLicense = "MIT";
        task.RpmSigningKeyFile = "key.pem";
        task.RpmSigningKeyPassphraseEnvironmentVariable = "_UT_RPM_SIGNING_PASS";
        Environment.SetEnvironmentVariable("_UT_RPM_SIGNING_PASS", "pw");
        try
        {
            var rpm = Assert.IsType<DotNet.Bundler.Rpm.RpmBundleConfiguration>(
                task.BuildFormatConfiguration(PackageFormat.Rpm));
            Assert.Equal("pkg", rpm.PackageName);
            Assert.Equal("MIT", rpm.License);
            Assert.Equal(Path.GetFullPath("key.pem"), rpm.Signing.KeyFile);
            Assert.Equal("pw", rpm.Signing.Passphrase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_UT_RPM_SIGNING_PASS", null);
        }
    }

    [Fact]
    public void BuildFormatConfiguration_SecretEnvMissing_Throws()
    {
        var task = Task();
        task.RpmSigningKeyPassphraseEnvironmentVariable = "_UT_RPM_SIGNING_ABSENT";
        Environment.SetEnvironmentVariable("_UT_RPM_SIGNING_ABSENT", null);
        Assert.Throws<InvalidOperationException>(
            () => task.BuildFormatConfiguration(PackageFormat.Rpm));
    }

    [Fact]
    public void BuildBundleConfiguration_RelativePaths_ResolveAgainstProjectDirectory()
    {
        var project = Path.Combine(Path.GetTempPath(), "bundler-projdir-" + Guid.NewGuid().ToString("N"));
        var task = Task();
        task.ProjectDirectory = project;
        task.LicenseFile = "lic.txt";
        var bundle = task.BuildBundleConfiguration([PackageFormat.Deb]);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, "lic.txt")), bundle.LicenseFile);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, "in")), bundle.Targets[0].InputDirectory);
    }

    [Fact]
    public void BuildBundleConfiguration_Update_BootstrapperDirectoryMapped()
    {
        var task = Task();
        task.UpdateEnabled = true;
        task.UpdateBootstrapperDirectory = "tools/updater";
        var update = task.BuildBundleConfiguration([PackageFormat.Deb]).Update;
        Assert.Equal(Path.GetFullPath("tools/updater"), update!.BootstrapperDirectory);
    }

    [Fact]
    public void BuildFormatConfiguration_AlpineApk_ExtraPkgInfoMapped()
    {
        var task = Task();
        var item = new TaskItem("install_if");
        item.SetMetadata("Value", "busybox");
        task.AlpineApkExtraPkgInfo = [item];
        var apk = Assert.IsType<DotNet.Bundler.AlpineApk.AlpineApkBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.AlpineApk));
        Assert.Equal("busybox", apk.ExtraPkgInfo!["install_if"]);
    }

    [Fact]
    public void BuildFormatConfiguration_Nsis_MapsKnobs()
    {
        var task = Task();
        task.NsisInstallScope = "perMachine";
        task.NsisCompression = "zlib";
        task.NsisLanguages = "English;German";
        task.NsisShortcutDesktop = false;
        task.NsisDisplayLanguageSelector = true;

        var nsis = Assert.IsType<DotNet.Bundler.Nsis.NsisBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.Nsis));
        Assert.Equal(DotNet.Bundler.Nsis.NsisInstallScope.PerMachine, nsis.InstallScope);
        Assert.Equal(DotNet.Bundler.Nsis.NsisCompression.Zlib, nsis.Compression);
        Assert.Equal(["English", "German"], nsis.Languages);
        Assert.False(nsis.Shortcuts.Desktop);
        Assert.True(nsis.DisplayLanguageSelector);
    }

    [Fact]
    public void BuildFormatConfiguration_Nsis_BadScope_Throws()
    {
        var task = Task();
        task.NsisInstallScope = "bogus";
        Assert.Throws<ArgumentException>(
            () => task.BuildFormatConfiguration(PackageFormat.Nsis));
    }

    [Fact]
    public void BuildFormatConfiguration_Nsis_BadCompression_Throws()
    {
        var task = Task();
        task.NsisCompression = "7z";
        Assert.Throws<ArgumentException>(
            () => task.BuildFormatConfiguration(PackageFormat.Nsis));
    }

    [Fact]
    public void BuildFormatConfiguration_Nsis_EmptyLanguages_Throws()
    {
        var task = Task();
        task.NsisLanguages = ";;";
        Assert.Throws<ArgumentException>(
            () => task.BuildFormatConfiguration(PackageFormat.Nsis));
    }

    [Fact]
    public void BuildFormatConfiguration_Msi_BadScope_Throws()
    {
        var task = Task();
        task.WixInstallScope = "bogus";
        Assert.Throws<ArgumentException>(
            () => task.BuildFormatConfiguration(PackageFormat.Msi));
    }

    [Fact]
    public void BuildFormatConfiguration_Msi_LanguagesPrecedence()
    {
        var task = Task();
        task.WixLanguage = "en-US";
        task.WixLanguages = "de-DE;fr-FR";

        var wix = Assert.IsType<DotNet.Bundler.Wix.WixBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.Msi));
        // WixLanguages（复数）胜过 WixLanguage（单数）
        Assert.Equal(["de-DE", "fr-FR"], wix.Languages);
    }

    [Fact]
    public void BuildFormatConfiguration_Archive_MapsKnobs()
    {
        var task = Task();
        task.ArchivePackageName = "arc";
        task.ArchiveName = "custom";

        var archive = Assert.IsType<DotNet.Bundler.Archive.ArchiveBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.Zip));
        Assert.Equal("arc", archive.PackageName);
        Assert.Equal("custom", archive.ArchiveName);
    }

    [Fact]
    public void BuildFormatConfiguration_AppImage_MapsKnobs()
    {
        var task = Task();
        task.AppImagePackageName = "ai";
        task.AppImageBinLink = "none";
        task.AppImageSigningKeyFile = "k.pem";

        var ai = Assert.IsType<DotNet.Bundler.AppImage.AppImageBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.AppImage));
        Assert.Equal("ai", ai.PackageName);
        Assert.Equal("", ai.BinLink);
        Assert.Equal(Path.GetFullPath("k.pem"), ai.Signing.KeyFile);
    }

    [Fact]
    public void BuildFormatConfiguration_AlpineApk_MapsKnobs()
    {
        var task = Task();
        task.AlpineApkPackageName = "apk";
        task.AlpineApkRelease = "3";
        task.AlpineApkLicense = "MIT";

        var apk = Assert.IsType<DotNet.Bundler.AlpineApk.AlpineApkBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.AlpineApk));
        Assert.Equal("apk", apk.PackageName);
        Assert.Equal("3", apk.Release);
        Assert.Equal("MIT", apk.License);
    }

    [Fact]
    public void BuildFormatConfiguration_MacApp_MapsKnobs()
    {
        var task = Task();
        task.MacAppPackageName = "macpkg";
        task.MacAppDisplayName = "Mac App";
        task.MacAppMinimumSystemVersion = "13.0";
        task.MacAppCategory = "public.app-category.utilities";
        task.MacAppHardenedRuntime = true;

        var app = Assert.IsType<DotNet.Bundler.MacApp.MacAppBundleConfiguration>(
            task.BuildFormatConfiguration(PackageFormat.App));
        Assert.Equal("macpkg", app.PackageName);
        Assert.Equal("Mac App", app.BundleDisplayName);
        Assert.Equal("13.0", app.MinimumSystemVersion);
        Assert.Equal("public.app-category.utilities", app.Category);
        Assert.True(app.Signing.HardenedRuntime);
    }

    [Fact]
    public void ParseFormats_AllNames()
    {
        var task = Task();
        task.Formats = "nsis;msi;app;dmg;pkg;deb;rpm;appimage;zip;targz;alpineapk";
        var formats = task.ParseFormats();
        Assert.Equal(11, formats.Count);
        Assert.Contains(PackageFormat.TarGz, formats);
    }

    [Fact]
    public void ParseFormats_Unknown_Throws()
    {
        var task = Task();
        task.Formats = "deb;bogus";
        Assert.Throws<ArgumentException>(() => task.ParseFormats());
    }

    [Fact]
    public void ParseFormats_Empty_Throws()
    {
        var task = Task();
        task.Formats = ";;,";
        Assert.Throws<ArgumentException>(() => task.ParseFormats());
    }

    [Fact]
    public void ParseFormats_NumericString_Rejected()
    {
        // Enum.TryParse 放行 "3" 这类数值串——IsDefined 门禁必须拦住
        var task = Task();
        task.Formats = "3";
        Assert.Throws<ArgumentException>(() => task.ParseFormats());
    }

    [Fact]
    public void BuildBundleConfiguration_BadLayout_Throws()
    {
        var task = Task();
        task.OutputLayout = "bogus";
        Assert.Throws<ArgumentException>(
            () => task.BuildBundleConfiguration([PackageFormat.Deb]));
    }

    [Fact]
    public void Execute_ValidationFailure_ReturnsFalse()
    {
        var task = Task();
        task.ProductName = ""; // 触发校验失败
        task.BuildEngine = new FakeBuildEngine();
        Assert.False(task.Execute());
    }

    [Fact]
    public void Execute_MsiScopeError_ReturnsFalse()
    {
        var task = Task();
        task.WixInstallScope = "bogus";
        task.Formats = "msi";
        var engine = new FakeBuildEngine();
        task.BuildEngine = engine;
        Assert.False(task.Execute());
        Assert.True(engine.Errors.Count > 0);
    }

    /// <summary>最小 IBuildEngine：捕获消息供断言，不跑真构建。</summary>
    private sealed class FakeBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public List<string> Messages { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e.Message ?? "");
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames,
            System.Collections.IDictionary globalProperties,
            System.Collections.IDictionary targetOutputs) => true;
    }
}

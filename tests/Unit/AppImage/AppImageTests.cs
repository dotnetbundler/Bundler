using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using System.Text;


// 从旧 tests/Bundler.Tests/AppImageTests.cs 收割：AppDirBuilder 为纯托管实现。
// 去 spawn：SkipsNonRegularPayloadFiles（Linux 腿整体走真 mksquashfs）、
// BuildsRealAppImage、RepeatBuildIsByteIdentical、CrossBuildsAarch64、SignsAppImage
// 均执行真实 appimagetool/gpg——去向见 Integration/Manual 层对应文件。
// 判重跳过：RejectsNonAppImageFormats（已并入 BundlerValidateTests.FormatGate_RejectForeignFormats）。
namespace Bundler.UnitTests.AppImage;

public sealed class AppImageTests : TempBase
{

        [Fact]
    public void AssemblesAppDir()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        var icon = Path.Combine(input, "icon-256.png");
        File.WriteAllBytes(icon, PngBytes(256));
        var desktop = Path.Combine(input, "custom.desktop");
        File.WriteAllText(desktop, "[Desktop Entry]\nType=Application\nName=Custom\nExec=custom\nIcon=custom\n");
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(icon), PlanItem(input, work), new AppImageBundleConfiguration
                {
                    Categories = ["Utility", "Development"],
                    DesktopFile = desktop
                }, work, NullBundleLogger.Instance);
            var dir = result.AppDirPath;
            Assert.True(File.Exists(Path.Combine(dir, "usr", "lib", "example-app", "ExampleApp")),
                "payload binary must land under usr/lib/<pkg>");
            Assert.True(File.Exists(Path.Combine(dir, "usr", "bin", "example-app")),
                "usr/bin link must exist");
            Assert.True(File.Exists(Path.Combine(dir, "usr", "share", "applications", "example-app.desktop")),
                "staged .desktop must exist");
            var appRun = File.ReadAllText(Path.Combine(dir, "AppRun"));
            Assert.True(appRun.StartsWith("#!/bin/sh") && appRun.Contains("usr/bin/example-app"),
                "AppRun must exec the usr/bin link");
            Assert.True(File.Exists(Path.Combine(dir, "example-app.desktop")) &&
                   File.Exists(Path.Combine(dir, "example-app.png")) &&
                   File.Exists(Path.Combine(dir, ".DirIcon")),
                "root desktop/icon/.DirIcon entries must exist");
            Assert.True(result.FileArchitecture == "x86_64" && result.EnvironmentArchitecture == "x86_64",
                "linux-x86_64 must map to file/env arch x86_64");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    public void DefaultIconFallback()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work), new AppImageBundleConfiguration(), work, NullBundleLogger.Instance);
            var png = File.ReadAllBytes(Path.Combine(result.AppDirPath, "example-app.png"));
            Assert.True(png.Length > 4 && png[0] == 0x89 && png[1] == 'P',
                "default icon must be a real PNG");
            // AlwaysEmitIcon: generated .desktop carries Icon= even without bundle icons.
            var desktopText = Encoding.UTF8.GetString(
                File.ReadAllBytes(Path.Combine(result.AppDirPath,
                    "usr", "share", "applications", "example-app.desktop")));
            Assert.Contains("Icon=example-app", desktopText);
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    public void MapsNamingAndArch()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work, target: "linux-aarch64"),
                new AppImageBundleConfiguration
                {
                    PackageName = "My App",
                    Version = "2.0.0-beta.1"
                }, work, NullBundleLogger.Instance);
            Assert.Equal("my-app", result.PackageName);
            Assert.Equal("2.0.0-beta.1", result.Version);
            Assert.True(result.FileArchitecture == "aarch64" && result.EnvironmentArchitecture == "aarch64",
                "linux-aarch64 must map to aarch64");
            var normalized = AppImageIdentity.NormalizeArchitecture("amd64");
            Assert.Equal("x86_64", normalized);
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    public void RejectsInvalidSettings()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { InstallRoot = "../escape" }, work, NullBundleLogger.Instance),
                "escaping install root must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { Architecture = "ppc64" }, work, NullBundleLogger.Instance),
                "unknown arch must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { PackageName = "a" }, work, NullBundleLogger.Instance),
                "one-char package name must be rejected");
            AssertThrows<FileNotFoundException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { IconFile = input + "/missing.png" }, work, NullBundleLogger.Instance),
                "missing icon file must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { Categories = ["Bad", "Name!"] }, work, NullBundleLogger.Instance),
                "invalid categories must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    // installRoot/mainExecutable 插值进 AppRun 的 exec 双引号行——
    // shell 元字符（空白/引号/$/`）与 BinLink 同规拒绝。
    [Fact]
    public void RejectsShellMetacharactersInAppRunPaths()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            foreach (var bad in new[] { "usr/lib/$(id)", "usr/lib/a b", "usr/lib/a\"b", "usr/lib/a'b", "usr/lib/`id`" })
            {
                AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                    BundleWith(null), PlanItem(input, work),
                    new AppImageBundleConfiguration { InstallRoot = bad }, work, NullBundleLogger.Instance),
                    $"shell metachars in install root '{bad}' must be rejected");
            }
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work, mainExecutable: "bin/$(id)"),
                new AppImageBundleConfiguration(), work, NullBundleLogger.Instance),
                "shell metachars in the main executable must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    // Resources 与已落载荷撞名显式拒绝（含载荷文件与 update 注入件同规）。
    [Fact]
    public void RejectsResourceTargetCollidingWithPayload()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        var extra = Path.Combine(input, "extra.conf");
        File.WriteAllText(extra, "k=v");
        var resourceDir = Path.Combine(input, "resdir");
        Directory.CreateDirectory(resourceDir);
        File.WriteAllText(Path.Combine(resourceDir, "ExampleApp.dll"), "shadow");
        try
        {
            AssertThrows<InvalidOperationException>(() => AppDirBuilder.Build(
                BundleWith(null, resources:
                [
                    new BundleResourceConfiguration { Source = extra, Destination = "ExampleApp.dll" }
                ]),
                PlanItem(input, work), new AppImageBundleConfiguration(), work, NullBundleLogger.Instance),
                "a file resource overwriting a payload file must be rejected");
            AssertThrows<InvalidOperationException>(() => AppDirBuilder.Build(
                BundleWith(null, resources:
                [
                    new BundleResourceConfiguration { Source = resourceDir, Destination = "." }
                ]),
                PlanItem(input, work), new AppImageBundleConfiguration(), work, NullBundleLogger.Instance),
                "a directory resource colliding with a payload file must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    // BinLink 关闭 + 嵌套主程序时 .desktop Exec 指向完整安装路径而非 basename。
    [Fact]
    public void DesktopExecUsesFullMainPathWhenBinLinkDisabled()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        Directory.CreateDirectory(Path.Combine(input, "bin"));
        File.WriteAllText(Path.Combine(input, "bin", "ExampleApp"), "#!/bin/sh\necho ok\n");
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work, mainExecutable: "bin/ExampleApp"),
                new AppImageBundleConfiguration { BinLink = "none" }, work, NullBundleLogger.Instance);
            var desktop = File.ReadAllText(
                Path.Combine(result.AppDirPath, "example-app.desktop"));
            Assert.Contains("Exec=/usr/lib/example-app/bin/ExampleApp", desktop);
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    public void StagesArbitraryFiles()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        var extra = Path.Combine(input, "extra.conf");
        File.WriteAllText(extra, "k=v");
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work), new AppImageBundleConfiguration
                {
                    Files =
                    [
                        new AppImageFileEntry { Source = extra, Destination = "opt/extras/extra.conf" },
                        new AppImageFileEntry { Source = extra, Destination = "usr/share/example-app/extra-copy.conf" }
                    ]
                }, work, NullBundleLogger.Instance);
            Assert.Equal("k=v", File.ReadAllText(Path.Combine(result.AppDirPath, "opt", "extras", "extra.conf")));
            Assert.True(File.Exists(Path.Combine(result.AppDirPath, "usr", "share", "example-app", "extra-copy.conf")),
                "second destination must land too");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    public void RejectsInvalidFileMappings()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        var extra = Path.Combine(input, "extra.conf");
        File.WriteAllText(extra, "k=v");
        AppImageFileEntry Entry(string dest) => new() { Source = extra, Destination = dest };
        try
        {
            foreach (var bad in new[] { "/abs/x", "../escape", "a//b", "a/./b", "a/../b", "win\\sep", "" })
            {
                AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                    BundleWith(null), PlanItem(input, work),
                    new AppImageBundleConfiguration { Files = [Entry(bad)] }, work, NullBundleLogger.Instance),
                    $"bad destination '{bad}' must be rejected");
            }
            foreach (var collision in new[] { "AppRun", ".DirIcon", "example-app.desktop", "example-app.png" })
            {
                AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                    BundleWith(null), PlanItem(input, work),
                    new AppImageBundleConfiguration { Files = [Entry(collision)] }, work, NullBundleLogger.Instance),
                    $"generated-entry collision '{collision}' must be rejected");
            }
            // Payload file already under the AppDir.
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration
                {
                    Files = [Entry("usr/lib/example-app/ExampleApp.dll")]
                }, work, NullBundleLogger.Instance),
                "existing payload path must be rejected");
            AssertThrows<FileNotFoundException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration
                {
                    Files = [new AppImageFileEntry { Source = input + "/missing", Destination = "opt/x" }]
                }, work, NullBundleLogger.Instance),
                "missing source must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }


    // Real end-to-end runs of the bundled appimagetool (Linux host only).
                    [Fact]
    public async Task RejectsIncompleteSigning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            await AssertThrowsAsync<ArgumentException>(async () => await new AppImageBundler(
                new AppImageBundleConfiguration { Signing = new KeyFileSigningConfiguration { Passphrase = "x"  }})
                .BuildAsync(Configuration(input, output), TestContext.Current.CancellationToken),
                "passphrase without a key file must be rejected");
            await AssertThrowsAsync<ArgumentException>(async () => await new AppImageBundler(
                new AppImageBundleConfiguration { Signing = new KeyFileSigningConfiguration { KeyFile = input + "/missing.asc"  }})
                .BuildAsync(Configuration(input, output), TestContext.Current.CancellationToken),
                "a missing key file must be rejected");
        }
        finally
        {
            Cleanup(input, output);
        }
    }




    [Fact]
    public void MapsAppImageSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("AppImagePackageName=\"$(BundlerAppImagePackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageVersion=\"$(BundlerAppImageVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageArchitecture=\"$(BundlerAppImageArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageInstallRoot=\"$(BundlerAppImageInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageBinLink=\"$(BundlerAppImageBinLink)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageIconFile=\"$(BundlerAppImageIconFile)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageDesktopFile=\"$(BundlerAppImageDesktopFile)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageCategories=\"$(BundlerAppImageCategories)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageMetainfoFile=\"$(BundlerAppImageMetainfoFile)\"", StringComparison.Ordinal) &&
               targets.Contains("AppImageFiles=\"@(BundlerAppImageFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerAppImage* properties to the task.");
        Assert.True(props.Contains("<BundlerAppImagePackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerAppImageIconFile", StringComparison.Ordinal),
            "The BundlerAppImage* properties lack defaults in the .props file.");
        Assert.True(task.Contains("new AppImageBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.AppImage", StringComparison.Ordinal),
            "The MSBuild task does not construct the .appimage backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.AppImage.dll", msbuildProject);
    }

    [Fact]
    public void MapsTargetArchitectures()
    {
        Assert.Equal("x86_64", AppImageIdentity.EnvironmentArchitecture(BundleTarget.Parse("linux-x86_64")));
        Assert.Equal("aarch64", AppImageIdentity.EnvironmentArchitecture(BundleTarget.Parse("linux-aarch64")));
        // linux-x86/i686 无随包 runtime——收窄后与 NormalizeArchitecture 同口径拒绝。
        Assert.ThrowsAny<ArgumentException>(() =>
            AppImageIdentity.EnvironmentArchitecture(BundleTarget.Parse("linux-x86")));
    }

    [Fact]
    public void RejectsUnmappableTarget()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => AppImageIdentity.EnvironmentArchitecture(
                new BundleTarget("linux-ppc64le", TargetOperatingSystem.Linux, CpuArchitecture.Ppc64le)));
        Assert.Contains("Cannot map target", ex.Message);
    }

    static BundleConfiguration BundleWith(string? icon,
        IReadOnlyList<BundleResourceConfiguration>? resources = null)
    {
        return new BundleConfiguration
        {
            ProductName = "Example App",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Publisher = "Example",
            Description = "Fixture",
            Icons = icon is null ? [] : [icon],
            Resources = resources ?? [],
            Targets = []
        };
    }

    static BundlePlanItem PlanItem(string input, string work, string target = "linux-x86_64",
        string mainExecutable = "ExampleApp")
    {
        return new BundlePlanItem(
            BundleTarget.Parse(target),
            PackageFormat.AppImage,
            input,
            mainExecutable,
            work + "/out",
            Intermediate: true);
    }

    static BundleConfiguration Configuration(string input, string output,
        string target = "linux-x86_64", IReadOnlyList<PackageFormat>? formats = null)
    {
        var bundle = BundleWith(null);
        return new BundleConfiguration
        {
            ProductName = bundle.ProductName,
            Identifier = bundle.Identifier,
            Version = bundle.Version,
            Publisher = bundle.Publisher,
            Description = bundle.Description,
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.AppImage]
                }
            ]
        };
    }

    string CreateInputDirectory()
    {
        var input = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "#!/bin/sh\necho ok\n");
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        return input;
    }

    static byte[] PngBytes(int size)
    {
        // Minimal valid-enough PNG header + IHDR for the size probe.
        var ihdr = BitConverter.GetBytes(size);
        if (BitConverter.IsLittleEndian) Array.Reverse(ihdr);
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
        return header.Concat(ihdr).Concat(ihdr).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
    }

    static string Sha256(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var builder = new StringBuilder();
        foreach (var b in sha256.ComputeHash(bytes))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    static void AssertThrows<T>(Action action, string because) where T : Exception
    {
        var exception = Record.Exception(action);
        Assert.True(exception is T,
            $"{because}: expected {typeof(T).Name}, got {exception?.GetType().Name}: {exception?.Message}");
    }

    static async Task AssertThrowsAsync<T>(Func<Task> action, string because) where T : Exception
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.True(exception is T,
            $"{because}: expected {typeof(T).Name}, got {exception?.GetType().Name}: {exception?.Message}");
    }

    static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bundler.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
    }
}

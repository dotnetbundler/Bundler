using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using System.Text;

internal static class AppImageTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-appimage formats", () => RunSync(RejectsNonAppImageFormats));
            yield return ("Assembles a correct AppDir", () => RunSync(AssemblesAppDir));
            yield return ("Falls back to the bundled default icon", () => RunSync(DefaultIconFallback));
            yield return ("Maps appimage naming and arch", () => RunSync(MapsNamingAndArch));
            yield return ("Rejects invalid appimage settings", () => RunSync(RejectsInvalidSettings));
            yield return ("Rejects invalid file mappings", () => RunSync(RejectsInvalidFileMappings));
            yield return ("Stages arbitrary AppDir files", () => RunSync(StagesArbitraryFiles));
            yield return ("Builds a real .AppImage via bundled appimagetool", () => RunSync(BuildsRealAppImage));
            yield return ("Cross-builds aarch64 via the embedded runtime", () => RunSync(CrossBuildsAarch64));
            yield return ("Maps appimage settings through MSBuild", () => RunSync(MapsAppImageSettingsThroughMsBuild));
        }
    }

    static void RejectsNonAppImageFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var configuration = Configuration(input, "", formats: [PackageFormat.Deb]);
            AssertThrows<NotSupportedException>(
                () => new AppImageBundler().BuildAsync(configuration).GetAwaiter().GetResult(),
                "non-appimage formats must be rejected");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void AssemblesAppDir()
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
                    Categories = "Utility;Development",
                    DesktopFile = desktop
                }, work);
            var dir = result.AppDirPath;
            Assert(File.Exists(Path.Combine(dir, "usr", "lib", "example-app", "ExampleApp")),
                "payload binary must land under usr/lib/<pkg>");
            Assert(File.Exists(Path.Combine(dir, "usr", "bin", "example-app")),
                "usr/bin link must exist");
            Assert(File.Exists(Path.Combine(dir, "usr", "share", "applications", "example-app.desktop")),
                "staged .desktop must exist");
            var appRun = File.ReadAllText(Path.Combine(dir, "AppRun"));
            Assert(appRun.StartsWith("#!/bin/sh") && appRun.Contains("usr/bin/example-app"),
                "AppRun must exec the usr/bin link");
            Assert(File.Exists(Path.Combine(dir, "example-app.desktop")) &&
                   File.Exists(Path.Combine(dir, "example-app.png")) &&
                   File.Exists(Path.Combine(dir, ".DirIcon")),
                "root desktop/icon/.DirIcon entries must exist");
            Assert(result.FileArchitecture == "amd64" && result.EnvironmentArchitecture == "x86_64",
                "linux-x64 must map to file arch amd64 / env arch x86_64");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    static void DefaultIconFallback()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work), new AppImageBundleConfiguration(), work);
            var png = File.ReadAllBytes(Path.Combine(result.AppDirPath, "example-app.png"));
            Assert(png.Length > 4 && png[0] == 0x89 && png[1] == 'P',
                "default icon must be a real PNG");
            // AlwaysEmitIcon: generated .desktop carries Icon= even without bundle icons.
            var desktopText = Encoding.UTF8.GetString(
                File.ReadAllBytes(Path.Combine(result.AppDirPath,
                    "usr", "share", "applications", "example-app.desktop")));
            Assert(desktopText.Contains("Icon=example-app"),
                "generated .desktop must carry Icon= for the fallback icon");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    static void MapsNamingAndArch()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work, rid: "linux-arm64"),
                new AppImageBundleConfiguration
                {
                    PackageName = "My App",
                    Version = "2.0.0-beta.1"
                }, work);
            Assert(result.PackageName == "my-app", "package name must kebab-case");
            Assert(result.Version == "2.0.0-beta.1", "version passes through verbatim");
            Assert(result.FileArchitecture == "aarch64" && result.EnvironmentArchitecture == "aarch64",
                "linux-arm64 must map to aarch64");
            var normalized = AppImageIdentity.NormalizeArchitecture("amd64");
            Assert(normalized == "x86_64", "amd64 must normalize to x86_64");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    static void RejectsInvalidSettings()
    {
        var input = CreateInputDirectory();
        var work = input + ".work";
        try
        {
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { InstallRoot = "../escape" }, work),
                "escaping install root must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { Architecture = "ppc64" }, work),
                "unknown arch must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { PackageName = "a" }, work),
                "one-char package name must be rejected");
            AssertThrows<FileNotFoundException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { IconFile = input + "/missing.png" }, work),
                "missing icon file must be rejected");
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration { Categories = "Bad;Name!" }, work),
                "invalid categories must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    static void StagesArbitraryFiles()
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
                }, work);
            Assert(File.ReadAllText(Path.Combine(result.AppDirPath, "opt", "extras", "extra.conf")) == "k=v",
                "AppDir file must land at the relative destination");
            Assert(File.Exists(Path.Combine(result.AppDirPath, "usr", "share", "example-app", "extra-copy.conf")),
                "second destination must land too");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    static void RejectsInvalidFileMappings()
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
                    new AppImageBundleConfiguration { Files = [Entry(bad)] }, work),
                    $"bad destination '{bad}' must be rejected");
            }
            foreach (var collision in new[] { "AppRun", ".DirIcon", "example-app.desktop", "example-app.png" })
            {
                AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                    BundleWith(null), PlanItem(input, work),
                    new AppImageBundleConfiguration { Files = [Entry(collision)] }, work),
                    $"generated-entry collision '{collision}' must be rejected");
            }
            // Payload file already under the AppDir.
            AssertThrows<ArgumentException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration
                {
                    Files = [Entry("usr/lib/example-app/ExampleApp.dll")]
                }, work),
                "existing payload path must be rejected");
            AssertThrows<FileNotFoundException>(() => AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work),
                new AppImageBundleConfiguration
                {
                    Files = [new AppImageFileEntry { Source = input + "/missing", Destination = "opt/x" }]
                }, work),
                "missing source must be rejected");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    // Real end-to-end runs of the bundled appimagetool (Linux host only).
    static void BuildsRealAppImage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = new AppImageBundler().BuildAsync(
                Configuration(input, output)).GetAwaiter().GetResult().Single();
            Assert(Path.GetFileName(artifact.Path) == "example-app_1.0.0_amd64.AppImage",
                "file name must follow <pkg>_<version>_<arch>.AppImage");
            var magic = File.ReadAllBytes(artifact.Path).Take(4).ToArray();
            Assert(magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F',
                "an .AppImage is an ELF");
            Assert(File.Exists(artifact.Path + ".sha256"), "sha256 sidecar must exist");
            var sidecar = File.ReadAllText(artifact.Path + ".sha256");
            Assert(sidecar.Contains(Sha256(File.ReadAllBytes(artifact.Path))),
                "sidecar must carry the artifact's sha256");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void CrossBuildsAarch64()
    {
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64) return;
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = new AppImageBundler(
                new AppImageBundleConfiguration { Architecture = "aarch64" }).BuildAsync(
                Configuration(input, output)).GetAwaiter().GetResult().Single();
            Assert(Path.GetFileName(artifact.Path) == "example-app_1.0.0_aarch64.AppImage",
                "cross build must name the aarch64 file");
            // ELF e_machine at bytes 18-19: 0xB7 0x00 = EM_AARCH64.
            var header = File.ReadAllBytes(artifact.Path);
            Assert(header[18] == 0xB7 && header[19] == 0x00,
                "cross-built AppImage must carry the aarch64 runtime");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsAppImageSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("AppImagePackageName=\"$(BundlerAppImagePackageName)\"", StringComparison.Ordinal) &&
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
        Assert(props.Contains("<BundlerAppImagePackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerAppImageIconFile", StringComparison.Ordinal),
            "The BundlerAppImage* properties lack defaults in the .props file.");
        Assert(task.Contains("new AppImageBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.AppImage", StringComparison.Ordinal),
            "The MSBuild task does not construct the .appimage backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert(msbuildProject.Contains("DotNet.Bundler.AppImage.dll", StringComparison.Ordinal),
            "The MSBuild package must ship DotNet.Bundler.AppImage.dll.");
    }

    static BundleConfiguration BundleWith(string? icon)
    {
        return new BundleConfiguration
        {
            ProductName = "Example App",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Publisher = "Example",
            Description = "Fixture",
            Icons = icon is null ? [] : [icon],
            Targets = []
        };
    }

    static BundlePlanItem PlanItem(string input, string work, string rid = "linux-x64")
    {
        return new BundlePlanItem(
            new BundleTarget(rid, DesktopOperatingSystem.Linux,
                rid.EndsWith("arm64", StringComparison.Ordinal) ? CpuArchitecture.Arm64 : CpuArchitecture.X64),
            PackageFormat.AppImage,
            input,
            "ExampleApp",
            work + "/out",
            Intermediate: true);
    }

    static BundleConfiguration Configuration(string input, string output,
        string rid = "linux-x64", IReadOnlyList<PackageFormat>? formats = null)
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
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.AppImage]
                }
            ]
        };
    }

    static string CreateInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception other) when (other is not T)
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected {typeof(T).Name} ({because}), got {other.GetType().Name}: {other.Message}");
        }
        throw new InvalidOperationException($"Assertion failed: expected {typeof(T).Name} ({because}).");
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

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}

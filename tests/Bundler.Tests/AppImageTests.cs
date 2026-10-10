using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using System.Text;

public static class AppImageTests
{

    [Fact]
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

    [Fact]
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
            Assert.True(result.FileArchitecture == "amd64" && result.EnvironmentArchitecture == "x86_64",
                "linux-x64 must map to file arch amd64 / env arch x86_64");
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    static void DefaultIconFallback()
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
                }, work, NullBundleLogger.Instance);
            Assert.Equal("my-app", result.PackageName);
            Assert.Equal("2.0.0-beta.1", result.Version);
            Assert.True(result.FileArchitecture == "aarch64" && result.EnvironmentArchitecture == "aarch64",
                "linux-arm64 must map to aarch64");
            var normalized = AppImageIdentity.NormalizeArchitecture("amd64");
            Assert.Equal("x86_64", normalized);
        }
        finally
        {
            Cleanup(input, work);
        }
    }

    [Fact]
    static void RejectsInvalidSettings()
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
                new AppImageBundleConfiguration { Categories = "Bad;Name!" }, work, NullBundleLogger.Instance),
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
    static void RejectsShellMetacharactersInAppRunPaths()
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
    static void RejectsResourceTargetCollidingWithPayload()
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
                    new BundleResourceConfiguration { Source = extra, TargetPath = "ExampleApp.dll" }
                ]),
                PlanItem(input, work), new AppImageBundleConfiguration(), work, NullBundleLogger.Instance),
                "a file resource overwriting a payload file must be rejected");
            AssertThrows<InvalidOperationException>(() => AppDirBuilder.Build(
                BundleWith(null, resources:
                [
                    new BundleResourceConfiguration { Source = resourceDir, TargetPath = "." }
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
    static void DesktopExecUsesFullMainPathWhenBinLinkDisabled()
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

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than copied into the AppDir.
    [Fact]
    static void SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var work = input + ".work";
        var output = input + ".artifacts";
        try
        {
            CreateUnixSocket(Path.Combine(input, "agent.sock"));
            var result = AppDirBuilder.Build(
                BundleWith(null), PlanItem(input, work), new AppImageBundleConfiguration(), work, NullBundleLogger.Instance);
            Assert.True(File.Exists(Path.Combine(result.AppDirPath, "usr", "lib", "example-app", "ExampleApp")),
                "regular payload files still stage");
            Assert.False(File.Exists(Path.Combine(result.AppDirPath, "usr", "lib", "example-app", "agent.sock")), "a unix socket in the input must not reach the AppDir");
            if (OperatingSystem.IsLinux())
            {
                var artifact = new AppImageBundler().BuildAsync(
                    Configuration(input, output)).GetAwaiter().GetResult().Single();
                Assert.True(File.Exists(artifact.Path),
                    "the .AppImage build must succeed with a socket inside the input");
            }
        }
        finally
        {
            Cleanup(input, work, output);
        }
    }

    static void CreateUnixSocket(string path)
    {
        // sun_path is ~104 bytes on unix; bind a short name, then move the node into place
        var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
        using (var socket = new System.Net.Sockets.Socket(
                   System.Net.Sockets.AddressFamily.Unix,
                   System.Net.Sockets.SocketType.Stream,
                   System.Net.Sockets.ProtocolType.Unspecified))
        {
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
            // .NET unlinks the bound path on dispose; move the node while bound
            File.Move(bindPath, path);
        }
    }

    // Real end-to-end runs of the bundled appimagetool (Linux host only).
    [Fact]
    static void BuildsRealAppImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = new AppImageBundler().BuildAsync(
                Configuration(input, output)).GetAwaiter().GetResult().Single();
            Assert.Equal("example-app_1.0.0_amd64.AppImage", Path.GetFileName(artifact.Path));
            var magic = File.ReadAllBytes(artifact.Path).Take(4).ToArray();
            Assert.True(magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F',
                "an .AppImage is an ELF");
            Assert.True(File.Exists(artifact.Path + ".sha256"), "sha256 sidecar must exist");
            var sidecar = File.ReadAllText(artifact.Path + ".sha256");
            Assert.Contains(Sha256(File.ReadAllBytes(artifact.Path)), sidecar);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void CrossBuildsAarch64()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64, "requires an x64 Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = new AppImageBundler(
                new AppImageBundleConfiguration { Architecture = "aarch64" }).BuildAsync(
                Configuration(input, output)).GetAwaiter().GetResult().Single();
            Assert.Equal("example-app_1.0.0_aarch64.AppImage", Path.GetFileName(artifact.Path));
            // ELF e_machine at bytes 18-19: 0xB7 0x00 = EM_AARCH64.
            var header = File.ReadAllBytes(artifact.Path);
            Assert.True(header[18] == 0xB7 && header[19] == 0x00,
                "cross-built AppImage must carry the aarch64 runtime");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void SignsAppImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && HasTool("gpg"),
            "requires a Linux host with gpg on PATH");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        // The throwaway keyring must stay out of the packaged input: a live
        // gpg-agent drops unix sockets inside its home that cannot be copied.
        var gnupg = input + "-gnupg";
        Directory.CreateDirectory(gnupg);
        var keyFile = Path.Combine(gnupg, "key.asc");
        try
        {
            var batch = Path.Combine(gnupg, "keygen.txt");
            File.WriteAllText(batch,
                "Key-Type: RSA\nKey-Length: 2048\nName-Real: Bundler Test\n" +
                "Name-Email: bundler-test@example.com\nExpire-Date: 0\n" +
                "Passphrase: test-pass\n%commit\n");
            Run("chmod", null, ["700", gnupg]);
            Run("gpg", gnupg, ["--batch", "--gen-key", batch]);
            Run("gpg", gnupg, ["--batch", "--yes", "--pinentry-mode", "loopback",
                "--passphrase", "test-pass", "--export-secret-keys", "--armor",
                "bundler-test@example.com"], stdoutTo: keyFile);

            var artifact = new AppImageBundler(new AppImageBundleConfiguration
            {
                SigningKeyFile = keyFile,
                SigningKeyPassphrase = "test-pass"
            }).BuildAsync(Configuration(input, output)).GetAwaiter().GetResult().Single();

            // The runtime ELF template always carries both signature sections;
            // unsigned = zero-filled, signed = non-zero embedded data.
            var signedSection = ElfSection(File.ReadAllBytes(artifact.Path), ".sha256_sig");
            Assert.True(signedSection.Length > 0 && signedSection.Any(b => b != 0),
                "a signed .AppImage must carry a non-zero .sha256_sig section");
            var unsignedOut = output + "-unsigned";
            var unsigned = new AppImageBundler().BuildAsync(
                Configuration(input, unsignedOut)).GetAwaiter().GetResult().Single();
            var unsignedSection = ElfSection(File.ReadAllBytes(unsigned.Path), ".sha256_sig");
            Assert.True(unsignedSection.Length == 0 || unsignedSection.All(b => b == 0),
                "unsigned builds must leave .sha256_sig zeroed");
            Cleanup(unsignedOut);
        }
        finally
        {
            try { Run("gpgconf", gnupg, ["--kill", "gpg-agent"]); }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException
                    or System.ComponentModel.Win32Exception)
            {
                // 清场尽力而为：gpgconf 缺失/进程已退都不应遮蔽测试结果。
            }
            Cleanup(input, output, gnupg);
        }
    }

    [Fact]
    static void RejectsIncompleteSigning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            AssertThrows<ArgumentException>(() => new AppImageBundler(
                new AppImageBundleConfiguration { SigningKeyPassphrase = "x" })
                .BuildAsync(Configuration(input, output)).GetAwaiter().GetResult(),
                "passphrase without a key file must be rejected");
            AssertThrows<ArgumentException>(() => new AppImageBundler(
                new AppImageBundleConfiguration { SigningKeyFile = input + "/missing.asc" })
                .BuildAsync(Configuration(input, output)).GetAwaiter().GetResult(),
                "a missing key file must be rejected");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    private static byte[] ElfSection(byte[] elf, string name)
    {
        var shOff = (int)BitConverter.ToInt64(elf, 0x28);
        var shSize = (int)BitConverter.ToUInt16(elf, 0x3A);
        var shNum = (int)BitConverter.ToUInt16(elf, 0x3C);
        var shStrNdx = (int)BitConverter.ToUInt16(elf, 0x3E);
        var strOff = (int)BitConverter.ToInt64(elf, shOff + shStrNdx * shSize + 24);
        for (var i = 0; i < shNum; i++)
        {
            var sh = shOff + i * shSize;
            var nameOff = (int)(strOff + BitConverter.ToInt32(elf, sh));
            var end = Array.IndexOf(elf, (byte)0, nameOff);
            if (Encoding.ASCII.GetString(elf, nameOff, end - nameOff) == name)
            {
                var offset = (int)BitConverter.ToInt64(elf, sh + 24);
                var size = (int)BitConverter.ToInt64(elf, sh + 32);
                return elf[offset..(offset + size)];
            }
        }
        return Array.Empty<byte>();
    }

    static bool HasTool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, name))) return true;
        }
        return false;
    }

    private static void Run(string tool, string? gnupgHome, string[] arguments,
        string? stdoutTo = null)
    {
        var info = new System.Diagnostics.ProcessStartInfo(tool)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        info.Arguments = string.Join(" ",
            arguments.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
        if (gnupgHome is not null)
        {
            info.EnvironmentVariables["GNUPGHOME"] = gnupgHome;
        }
        using var process = System.Diagnostics.Process.Start(info)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail(tool + " did not exit in 120s");
        }
        Task.WaitAll(stdOut, stdErr);
        Assert.True(process.ExitCode == 0, tool + " failed: " + stdErr.Result);
        if (stdoutTo is not null)
        {
            File.WriteAllText(stdoutTo, stdOut.Result);
        }
    }

    [Fact]
    static void MapsAppImageSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
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
            RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.AppImage.dll", msbuildProject);
    }

    [Fact]
    static void MapsRuntimeIdentifierArchitectures()
    {
        Assert.Equal("amd64", AppImageIdentity.FileArchitecture("linux-x64"));
        Assert.Equal("aarch64", AppImageIdentity.FileArchitecture("linux-arm64"));
        Assert.Equal("i686", AppImageIdentity.FileArchitecture("linux-x86"));
        Assert.Equal("x86_64", AppImageIdentity.EnvironmentArchitecture("linux-x64"));
        Assert.Equal("aarch64", AppImageIdentity.EnvironmentArchitecture("linux-arm64"));
    }

    [Fact]
    static void RejectsUnmappableRuntimeIdentifier()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => AppImageIdentity.EnvironmentArchitecture("linux-ppc64le"));
        Assert.Contains("Cannot map runtime identifier", ex.Message);
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

    static BundlePlanItem PlanItem(string input, string work, string rid = "linux-x64",
        string mainExecutable = "ExampleApp")
    {
        return new BundlePlanItem(
            new BundleTarget(rid, DesktopOperatingSystem.Linux,
                rid.EndsWith("arm64", StringComparison.Ordinal) ? CpuArchitecture.Arm64 : CpuArchitecture.X64),
            PackageFormat.AppImage,
            input,
            mainExecutable,
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
        var exception = Record.Exception(action);
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

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);


}

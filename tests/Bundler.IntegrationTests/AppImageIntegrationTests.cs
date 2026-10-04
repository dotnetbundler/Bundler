// LINUX-APPIMAGE-1..3 + SIGN-2 集成腿的 C# 移植：对应原 tests/Linux.AppImage.Integration/Verify.sh。
// 覆盖点：nupkg（后端包内嵌 appimagetool/runtime/许可证）→ 命名/侧车/ELF 头
// → --appimage-extract AppDir 形状 → 真实 AppRun/extract-and-run → 覆盖变体 ×3
// → 失败变体 ×3 → aarch64 ELF + squashfs 直读 → 扇出 → docker 三镜像
// → API fixture → gpg 签名（objcopy 段抽取 + 段清零 sha256 + gpgv 验签）。
using System.Text.RegularExpressions;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AppImageFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; private set; } = null!;
    public string CacheDir { get; private set; } = null!;
    public string ExtractRoot { get; private set; } = null!;
    public string FixtureProject { get; private set; } = null!;
    public string FixtureDir { get; private set; } = null!;

    public string DefaultImage { get; private set; } = null!;
    public string OverridesImage { get; private set; } = null!;
    public string DesktopImage { get; private set; } = null!;
    public string FilesImage { get; private set; } = null!;
    public string Arm64Image { get; private set; } = null!;
    public string FanoutDir { get; private set; } = null!;
    public string SignedImage { get; private set; } = null!;
    public string SignDir { get; private set; } = null!;

    private readonly Lazy<bool> _init;

    public AppImageFixture() => _init = new Lazy<bool>(() => { Initialize(); return true; });

    public bool Ensure() => _init.Value;

    private void Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: AppImage integration test requires a Linux host.");
        foreach (var tool in new[] { "sha256sum", "unzip", "od", "objcopy", "readelf" })
        {
            ExternalTools.Require(tool);
        }
        Ws = IntegrationWorkspace.Create("linux-appimage-integration", "BundlerLinuxAppImageIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExtractRoot = Ws.Combine("extract");
        FixtureDir = Path.Combine(RepositoryLayout.TestsDirectory, "Linux.AppImage.Integration", "Fixture");
        FixtureProject = Path.Combine(FixtureDir, "BundlerAppImageIntegrationFixture.csproj");
        _ = RepositoryPackages.DirectoryPath;

        Publish("default");
        DefaultImage = RequireImage(Ws.Combine("default", "linux-x64", "appimage"),
            "bundler-appimage-fixture_1.0.0_amd64.AppImage");

        Publish("overrides",
            "-p:BundlerTestAppImagePackageName=Custom AppImage",
            "-p:BundlerTestAppImageVersion=9.9.9-rc.1",
            "-p:BundlerTestAppImageBinLink=custom-link",
            "-p:BundlerTestAppImageInstallRoot=opt/custom",
            $"-p:BundlerTestAppImageIconFile={FixtureDir}/Assets/icon48.png");
        OverridesImage = RequireImage(Ws.Combine("overrides", "linux-x64", "appimage"),
            "custom-appimage_9.9.9-rc.1_amd64.AppImage");

        Publish("desktop", $"-p:BundlerTestAppImageDesktopFile={FixtureDir}/Assets/custom.desktop");
        DesktopImage = RequireImage(Ws.Combine("desktop", "linux-x64", "appimage"));

        Publish("files", "-p:BundlerTestAppImageFiles=1");
        FilesImage = RequireImage(Ws.Combine("files", "linux-x64", "appimage"));

        Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine("arm64")}",
             "--packages", CacheDir, "-r", "linux-arm64"],
            "linux-arm64 publish failed", noRestore: false);
        Arm64Image = RequireImage(Ws.Combine("arm64", "linux-arm64", "appimage"), "*_aarch64.AppImage");

        Publish("fanout", "-p:BundlerTestFormats=deb%3Brpm%3Bappimage");
        FanoutDir = Ws.Combine("fanout", "linux-x64");

        SignDir = Ws.Combine("signing");
        Directory.CreateDirectory(Path.Combine(SignDir, "gnupg"));
        File.SetUnixFileMode(Path.Combine(SignDir, "gnupg"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(SignDir, "keygen.txt"), """
            Key-Type: RSA
            Key-Length: 2048
            Name-Real: Bundler AppImage Test
            Name-Email: bundler-appimage-test@example.com
            Expire-Date: 0
            Passphrase: bundler-sign-pass
            %commit
            """ + "\n");
        var env = new ProcessRunner.Options
        {
            Environment = new Dictionary<string, string?> { ["GNUPGHOME"] = Path.Combine(SignDir, "gnupg") },
            Timeout = TimeSpan.FromMinutes(2),
        };
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("gpg", ["--batch", "--gen-key", Path.Combine(SignDir, "keygen.txt")], env),
            "gpg key generation failed.");
        var secAsc = Path.Combine(SignDir, "sec.asc");
        var passFile = Path.Combine(SignDir, "sign.pass");
        File.WriteAllText(passFile, "bundler-sign-pass");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("/bin/sh",
            ["-c", $"gpg --batch --yes --pinentry-mode loopback --passphrase-file '{passFile}' --export-secret-keys --armor bundler-appimage-test@example.com > '{secAsc}'"], env),
            "secret key export failed.");
        var pubAsc = Path.Combine(SignDir, "pub.asc");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("/bin/sh",
            ["-c", $"gpg --batch --export --armor bundler-appimage-test@example.com > '{pubAsc}'"], env),
            "public key export failed.");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("gpg",
            ["--batch", "--no-default-keyring", "--keyring", Path.Combine(SignDir, "verify.gpg"),
             "--import", pubAsc], env),
            "verify keyring import failed.");
        Publish("signed",
            new Dictionary<string, string?> { ["BundlerTestAppImageSigningKeyPassphrase"] = "bundler-sign-pass" },
            $"-p:BundlerTestAppImageSigningKeyFile={secAsc}");
        SignedImage = RequireImage(Ws.Combine("signed", "linux-x64", "appimage"));
    }

    public void Publish(string name, params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"appimage fixture publish '{name}' failed", noRestore: false);

    // 带 env 的 publish——口令类值走环境变量（MSBuild 自动导入为同名属性），
    // 不进 argv：进程列表与失败日志都不回显。
    public void Publish(string name, Dictionary<string, string?> environment,
        params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"appimage fixture publish '{name}' failed", noRestore: false, environment);

    private static string RequireImage(string dir, string pattern = "*.AppImage")
    {
        var match = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern).FirstOrDefault()
            : null;
        Assert.NotNull(match);
        return match;
    }

    // --appimage-extract 到 extract_root/<sub>/squashfs-root，返回该目录。
    public string Extract(string appImage, string sub)
    {
        var dest = Path.Combine(ExtractRoot, sub);
        if (Directory.Exists(dest))
        {
            Directory.Delete(dest, recursive: true);
        }
        Directory.CreateDirectory(dest);
        var result = ProcessRunner.Run(appImage, ["--appimage-extract"],
            new ProcessRunner.Options
            {
                WorkingDirectory = dest,
                Timeout = TimeSpan.FromMinutes(3),
            });
        ProcessRunner.AssertSuccess(result, "--appimage-extract failed.");
        var root = Path.Combine(dest, "squashfs-root");
        Assert.True(Directory.Exists(root), "--appimage-extract produced no squashfs-root.");
        return root;
    }

    public void Dispose() => Ws?.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AppImageIntegrationTests : IClassFixture<AppImageFixture>
{
    private readonly AppImageFixture _f;

    public AppImageIntegrationTests(AppImageFixture fixture)
    {
        _f = fixture;
        _f.Ensure();
    }

    [Fact]
    public void RepositoryPackagesCarryAppImageBackendAndToolset()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.AppImage" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var msbuildEntries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(msbuildEntries, e => e.EndsWith("DotNet.Bundler.AppImage.dll"));
        var backendEntries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.AppImage.{version}.nupkg"));
        Assert.Contains(backendEntries, e => e.Contains("LICENSE-appimagetool"));
    }

    [Fact]
    public void ArtifactNamingSidecarAndElfHeader()
    {
        Assert.True(File.Exists(_f.DefaultImage + ".sha256"), "Missing sha256 sidecar.");
        var check = ProcessRunner.Run("sha256sum",
            ["-c", Path.GetFileName(_f.DefaultImage + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(_f.DefaultImage)! });
        ProcessRunner.AssertSuccess(check, "sha256 sidecar mismatch.");
        Assert.True(File.GetUnixFileMode(_f.DefaultImage).HasFlag(UnixFileMode.UserExecute),
            ".AppImage is not executable.");
        Assert.Equal("7f454c46", HexAt(_f.DefaultImage, 0, 4));
        Assert.Equal("3e00", HexAt(_f.DefaultImage, 18, 2));
    }

    [Fact]
    public void AppDirShape()
    {
        var root = _f.Extract(_f.DefaultImage, "default");
        AssertAppDirShape(root, "bundler-appimage-fixture", "BundlerAppImageIntegrationFixture");
    }

    [Fact]
    public void AppRunScriptContent()
    {
        var root = _f.Extract(_f.DefaultImage, "apprun");
        var lines = File.ReadAllLines(Path.Combine(root, "AppRun"));
        Assert.Equal("#!/bin/sh", lines[0]);
        Assert.Contains(lines, l => l.Contains("usr/bin/bundler-appimage-fixture"));
    }

    [Fact]
    public void GeneratedDesktopFileValidates()
    {
        ExternalTools.Require("desktop-file-validate");
        var root = _f.Extract(_f.DefaultImage, "desktop-validate");
        var desktop = Path.Combine(root, "usr/share/applications/bundler-appimage-fixture.desktop");
        ProcessRunner.AssertSuccess(ProcessRunner.Run("desktop-file-validate", [desktop]),
            "generated .desktop fails desktop-file-validate.");
    }

    [Fact]
    public void ExtractedAppRunAndWholeImageRun()
    {
        var root = _f.Extract(_f.DefaultImage, "run");
        var appRun = ProcessRunner.Run(Path.Combine(root, "AppRun"), ["hello", "world"],
            new ProcessRunner.Options { WorkingDirectory = root });
        ProcessRunner.AssertSuccess(appRun, "Extracted AppRun failed.");
        Assert.Equal("BundlerAppImageIntegrationFixture:hello,world", appRun.StdOut.Trim());

        var whole = ProcessRunner.Run(_f.DefaultImage, ["--appimage-extract-and-run", "hi"],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(3) });
        ProcessRunner.AssertSuccess(whole, "--appimage-extract-and-run failed.");
        Assert.Equal("BundlerAppImageIntegrationFixture:hi", whole.StdOut.Trim());
    }

    [Fact]
    public void OverrideVariant()
    {
        var root = _f.Extract(_f.OverridesImage, "overrides");
        Assert.True(File.GetUnixFileMode(
                Path.Combine(root, "opt/custom/BundlerAppImageIntegrationFixture"))
                .HasFlag(UnixFileMode.UserExecute),
            "custom install root payload missing.");
        var link = Path.Combine(root, "usr/bin/custom-link");
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains(File.ReadAllLines(Path.Combine(root, "AppRun")),
            l => l.Contains("usr/bin/custom-link"));
        Assert.True(File.Exists(Path.Combine(root, "custom-appimage.png")), "custom icon missing.");
        Assert.True(File.Exists(Path.Combine(root, ".DirIcon")), ".DirIcon missing.");
    }

    [Fact]
    public void DesktopOverrideVariant()
    {
        var root = _f.Extract(_f.DesktopImage, "desktop");
        var staged = Path.Combine(root, "usr/share/applications/bundler-appimage-fixture.desktop");
        Assert.Contains("Name=Bundler AppImage Fixture Custom", File.ReadAllText(staged));
        Assert.Equal("usr/share/applications/bundler-appimage-fixture.desktop",
            new FileInfo(Path.Combine(root, "bundler-appimage-fixture.desktop")).LinkTarget);
    }

    [Fact]
    public void FileMappingVariant()
    {
        var root = _f.Extract(_f.FilesImage, "files");
        var mapped = Path.Combine(root, "opt/extras/defaults.conf");
        Assert.True(File.Exists(mapped), "BundlerAppImageFile payload missing at AppDir-relative destination.");
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(_f.FixtureDir, "Assets", "defaults.conf")),
            File.ReadAllBytes(mapped));
    }

    [Fact]
    public void FailureVariants()
    {
        var badFile = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("bad-file")}",
             "-p:BundlerTestAppImageBadFile=1", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badFile.ExitCode);

        var badArch = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("bad-arch")}",
             "-p:BundlerTestAppImageArchitecture=ppc64", "--packages", _f.CacheDir]);
        Assert.NotEqual(0, badArch.ExitCode);
    }

    [Fact]
    public void Arm64StructureAndSquashfsPayload()
    {
        Assert.EndsWith("_aarch64.AppImage", _f.Arm64Image);
        Assert.Equal("7f454c46", HexAt(_f.Arm64Image, 0, 4));
        Assert.Equal("b700", HexAt(_f.Arm64Image, 18, 2));

        // aarch64 运行时本机不能执行：直读 squashfs（type2 = ELF + 'hsqs' 魔数偏移）
        ExternalTools.Require("unsquashfs");
        var data = File.ReadAllBytes(_f.Arm64Image);
        var magic = "hsqs"u8.ToArray();
        var offset = data.AsSpan().IndexOf(magic);
        Assert.True(offset >= 0, "Could not locate squashfs magic in aarch64 AppImage.");
        Directory.CreateDirectory(_f.ExtractRoot);
        var dest = Path.Combine(_f.ExtractRoot, "arm64-payload");
        if (Directory.Exists(dest))
        {
            Directory.Delete(dest, recursive: true);
        }
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("unsquashfs", ["-q", "-o", offset.ToString(), "-d", dest, _f.Arm64Image]),
            "unsquashfs on aarch64 AppImage failed.");
        var armBin = Path.Combine(dest, "usr/lib/bundler-appimage-fixture/BundlerAppImageIntegrationFixture");
        Assert.True(File.Exists(armBin), "aarch64 payload missing expected entry binary.");
        Assert.Equal("b700", HexAt(armBin, 18, 2));
    }

    [Fact]
    public void MultiFormatFanout()
    {
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_f.FanoutDir, "deb"), "*.deb"));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_f.FanoutDir, "rpm"), "*.rpm"));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_f.FanoutDir, "appimage"), "*.AppImage"));
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerExtractAndRunMatrix()
    {
        var ranAny = false;
        foreach (var image in new[] { "debian:stable", "ubuntu:latest", "fedora:latest" })
        {
            if (!DockerRunner.TryEnsureImage(image))
            {
                continue;
            }
            ranAny = true;
            var result = DockerRunner.RunScript(image,
                "cp /tmp/pkg.AppImage /tmp/run.AppImage && chmod +x /tmp/run.AppImage && cd /tmp && ./run.AppImage --appimage-extract-and-run docker-ok",
                [new DockerRunner.Mount(_f.DefaultImage, "/tmp/pkg.AppImage")]);
            ProcessRunner.AssertSuccess(result, $"docker {image} run failed.");
            Assert.Equal("BundlerAppImageIntegrationFixture:docker-ok", result.StdOut.Trim());
        }
        Assert.SkipWhen(!ranAny, "SKIP: no docker image available for the extract-and-run matrix.");
    }

    [Fact]
    public void ApiFixtureProducesAppImage()
    {
        Assert.SkipWhen(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                != System.Runtime.InteropServices.Architecture.X64,
            "SKIP: API fixture leg is x86_64-only.");
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "AppImageApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["APPIMAGE_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "AppImageApiTests failed");
        var produced = Directory.Exists(Path.Combine(output, "artifacts"))
            && Directory.EnumerateFiles(Path.Combine(output, "artifacts"), "*.AppImage",
                SearchOption.AllDirectories).Any();
        Assert.True(produced, "API fixture did not produce an .AppImage.");
    }

    [Fact]
    public void SignedImageCarriesVerifiableSignature()
    {
        var sigBin = Path.Combine(_f.SignDir, "sig.bin");
        ExtractSection(_f.SignedImage, ".sha256_sig", sigBin);
        Assert.True(new FileInfo(sigBin).Length > 0, ".sha256_sig section missing.");
        var sig = File.ReadAllBytes(sigBin);
        Assert.Contains(sig, b => b != 0);
        Assert.Contains("BEGIN PGP SIGNATURE",
            System.Text.Encoding.ASCII.GetString(sig));

        // 分离签名语义：两签名段清零后对整镜像 sha256（裸 hex，无换行）。
        var digestFile = Path.Combine(_f.SignDir, "digest.txt");
        File.WriteAllText(digestFile, ZeroedDigest(_f.SignedImage));
        var gpgv = ProcessRunner.Run("gpgv",
            ["--keyring", Path.Combine(_f.SignDir, "verify.gpg"), sigBin, digestFile]);
        ProcessRunner.AssertSuccess(gpgv, "gpgv invocation failed.");
        Assert.Contains("Good signature", gpgv.Output);

        var unsignedBin = Path.Combine(_f.SignDir, "usig.bin");
        ExtractSection(_f.DefaultImage, ".sha256_sig", unsignedBin);
        Assert.DoesNotContain(File.ReadAllBytes(unsignedBin), b => b != 0);
    }

    [Fact]
    public void HalfConfiguredSigningFailsPublish()
    {
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("half-sign")}",
             "--packages", _f.CacheDir],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?>
                {
                    ["BundlerTestAppImageSigningKeyPassphrase"] = "orphan-pass",
                },
            });
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void AppimagelintIsInformationalOnly()
    {
        // appimagelint 是纯信息腿（APPIMAGE-3 结论）：其发现多为载荷 ABI 属性
        // 与工具限制，没有可豁免的 tag 体系 —— 只跑不闸。
        var lintBin = new[] { "./appimagelint-x86_64.AppImage",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "bin/appimagelint-x86_64.AppImage"),
            "appimagelint" }
            .FirstOrDefault(c => File.Exists(c) || ExternalTools.Has(c));
        Assert.SkipWhen(lintBin is null, "SKIP: appimagelint not available (informational only).");
        var result = ProcessRunner.Run(lintBin!, [_f.DefaultImage],
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(5) });
        _ = result; // 输出不进断言
    }

    private static void AssertAppDirShape(string root, string pkg, string exe)
    {
        Assert.True(File.GetUnixFileMode(Path.Combine(root, "AppRun")).HasFlag(UnixFileMode.UserExecute),
            $"{pkg}: AppRun missing or not executable.");
        var rootDesktop = new FileInfo(Path.Combine(root, $"{pkg}.desktop"));
        Assert.NotNull(rootDesktop.LinkTarget);
        Assert.Equal($"usr/share/applications/{pkg}.desktop", rootDesktop.LinkTarget);
        Assert.True(File.Exists(Path.Combine(root, $"usr/share/applications/{pkg}.desktop")),
            $"{pkg}: usr/share .desktop missing.");
        Assert.True(File.Exists(Path.Combine(root, $"{pkg}.png")), $"{pkg}: root <pkg>.png missing.");
        Assert.True(File.Exists(Path.Combine(root, ".DirIcon")), $"{pkg}: .DirIcon missing.");
        Assert.True(File.GetUnixFileMode(Path.Combine(root, $"usr/lib/{pkg}/{exe}"))
                .HasFlag(UnixFileMode.UserExecute),
            $"{pkg}: payload executable missing under usr/lib.");
        Assert.NotNull(new FileInfo(Path.Combine(root, $"usr/bin/{pkg}")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(root, $"usr/share/icons/hicolor/48x48/apps/{pkg}.png")),
            $"{pkg}: hicolor icon missing.");
        Assert.True(File.Exists(Path.Combine(root, $"usr/share/metainfo/{pkg}.metainfo.xml")),
            $"{pkg}: metainfo missing.");
        Assert.True(File.Exists(Path.Combine(root, $"usr/lib/{pkg}/docs/readme.txt")),
            $"{pkg}: BundlerResource payload missing.");
    }

    private static string HexAt(string path, long offset, int length)
    {
        var bytes = new byte[length];
        using var stream = File.OpenRead(path);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = stream.Read(bytes);
        Assert.Equal(length, read);
        return Convert.ToHexStringLower(bytes);
    }

    private static void ExtractSection(string elfPath, string section, string destination)
    {
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("objcopy", ["-O", "binary", $"--only-section={section}", elfPath, destination]),
            $"objcopy could not extract {section}.");
    }

    private static string ZeroedDigest(string elfPath)
    {
        var elf = ProcessRunner.Run("readelf", ["-SW", elfPath]);
        ProcessRunner.AssertSuccess(elf, "readelf -SW failed.");
        var data = File.ReadAllBytes(elfPath);
        var found = 0;
        foreach (var line in elf.StdOut.Split('\n'))
        {
            var m = Regex.Match(line,
                @"\]\s+(\.sha256_sig|\.sig_key)\s+\w+\s+\w+\s+([0-9a-f]+)\s+([0-9a-f]+)");
            if (m.Success)
            {
                var off = Convert.ToInt32(m.Groups[2].Value, 16);
                var size = Convert.ToInt32(m.Groups[3].Value, 16);
                Array.Clear(data, off, size);
                found++;
            }
        }
        Assert.Equal(2, found);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
    }
}

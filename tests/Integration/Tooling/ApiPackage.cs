// 集成腿的"直调 API 产包"助手：等价原跨工程 dotnet test ApiTests 的 fixture 作用，
// 但进程内直调公开 bundler API——少一层子进程、不依赖另一测试工程。
// 载荷沿用旧 fixture 形态：Linux 系用 #!/bin/sh 脚本（docker 腿可直接 exec），
// macOS 系用 Mach-O 头 stub（只过结构校验，本机不执行）。
using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;

internal static class ApiPackage
{
    public static async Task<string> DebAsync(string root)
    {
        var artifacts = await new DebBundler().BuildAsync(ShellConfig(root,
            "linux-x86_64", PackageFormat.Deb, "com.dotnetbundler.debapifixture"));
        return Assert.Single(artifacts).Path;
    }

    public static async Task<string> ApkAsync(string root)
    {
        var artifacts = await new AlpineApkBundler().BuildAsync(ShellConfig(root,
            "linux-musl-x86_64", PackageFormat.AlpineApk, "com.dotnetbundler.alpineapkapifixture"));
        return Assert.Single(artifacts).Path;
    }

    public static async Task<IReadOnlyList<BundleArtifact>> AppImageAsync(string root)
    {
        return await new AppImageBundler().BuildAsync(ShellConfig(root,
            "linux-x86_64", PackageFormat.AppImage, "com.dotnetbundler.appimageapifixture"));
    }

    public static async Task<IReadOnlyList<BundleArtifact>> ArchiveAsync(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
        File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");
        var request = new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = "com.dotnetbundler.archiveapifixture",
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API integration fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64",
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [PackageFormat.Zip, PackageFormat.TarGz]
                }
            ]
        };
        return await new ArchiveBundler().BuildAsync(request);
    }

    public static async Task<IReadOnlyList<BundleArtifact>> MacAppAsync(string root)
    {
        var input = WriteMachOPayload(root);
        var request = new BundleConfiguration
        {
            ProductName = "Mac API Package Fixture",
            Identifier = "com.dotnetbundler.macapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = TestPlatform.OsxTarget,
                    InputDirectory = input,
                    MainExecutable = "ApiFixture",
                    Formats = [PackageFormat.App]
                }
            ],
            FileAssociations =
            [
                new BundleFileAssociationConfiguration
                {
                    Extensions = ["apifix"], Name = "Api Fixture Document",
                    MimeType = "application/x-apifix"
                }
            ],
            UrlProtocols =
            [
                new BundleUrlProtocolConfiguration { Schemes = ["apifix"], Name = "Api Fixture Link" }
            ]
        };
        var settings = new MacAppBundleConfiguration
        {
            ExceptionDomain = "bundler.invalid",
            DocumentTypes =
            [
                new MacAppDocumentTypeConfiguration
                {
                    Extensions = ["apifix"], Rank = MacAppHandlerRank.Owner,
                    ExportedTypeIdentifier = "com.dotnetbundler.apifix",
                    ExportedTypeConformsTo = ["public.data"]
                }
            ],
            InfoPlistXml = "<dict><key>ApiMergedKey</key><string>merged</string></dict>"
        };
        return await new MacAppBundler(settings).BuildAsync(request);
    }

    public static async Task<IReadOnlyList<BundleArtifact>> MacDmgAsync(string root)
    {
        var input = WriteMachOPayload(root);
        var request = MacConfig(root, input, PackageFormat.Dmg,
            "DMG API Package Fixture", "com.dotnetbundler.macdmgapifixture");
        return await new MacDmgBundler(
            dmgConfiguration: new MacDmgBundleConfiguration
            {
                VolumeName = "DMG API Fixture",
                Layout = null
            }).BuildAsync(request);
    }

    public static async Task<IReadOnlyList<BundleArtifact>> MacPkgAsync(string root)
    {
        var input = WriteMachOPayload(root);
        var request = MacConfig(root, input, PackageFormat.Pkg,
            "PKG API Package Fixture", "com.dotnetbundler.macpkgapifixture");
        return await new MacPkgBundler(
            pkgConfiguration: new MacPkgBundleConfiguration
            {
                PackageName = "com.dotnetbundler.macpkgapifixture.pkg"
            }).BuildAsync(request);
    }

    public static async Task<IReadOnlyList<BundleArtifact>> NsisAsync(string root, string toolCache)
    {
        Assert.SkipWhen(TestPlatform.IsMusl,
            "Bundled makensis is glibc-linked; musl hosts cannot run it.");
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.exe"),
            "package-api-fixture", TestContext.Current.CancellationToken);
        var request = new BundleConfiguration
        {
            ProductName = "NSIS API Package Fixture",
            Identifier = "com.dotnetbundler.nsisapifixture",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input,
                    MainExecutable = "ApiFixture.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };
        return await new NsisBundler(
            options: new NsisBundlerOptions { ToolCacheDirectory = toolCache })
            .BuildAsync(request, TestContext.Current.CancellationToken);
    }

    private static BundleConfiguration ShellConfig(string root, string target,
        PackageFormat format, string identifier)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
        File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");
        return new BundleConfiguration
        {
            ProductName = "Api Fixture",
            Identifier = identifier,
            Publisher = "DotNet.Bundler Tests",
            Version = "1.0.0",
            Description = "Direct-API integration fixture.",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = "FixtureApp",
                    Formats = [format]
                }
            ]
        };
    }

    private static BundleConfiguration MacConfig(string root, string input,
        PackageFormat format, string productName, string identifier)
        => new()
        {
            ProductName = productName,
            Identifier = identifier,
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = TestPlatform.OsxTarget,
                    InputDirectory = input,
                    MainExecutable = "ApiFixture",
                    Formats = [format]
                }
            ]
        };

    // .app/.dmg/.pkg 假载荷只需 Mach-O 魔数+cputype 过校验——cputype 随宿主架构取
    // （ARM64=0x0100000C、X86_64=0x01000007 小端）。
    private static string WriteMachOPayload(string root)
    {
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        var executable = new byte[64];
        executable[0] = 0xCF; executable[1] = 0xFA; executable[2] = 0xED; executable[3] = 0xFE;
        executable[4] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? (byte)0x0C : (byte)0x07;
        executable[5] = 0x00; executable[6] = 0x00; executable[7] = 0x01;
        File.WriteAllBytes(Path.Combine(input, "ApiFixture"), executable);
        File.WriteAllText(Path.Combine(input, "ApiFixture.dll"), "package-api-fixture");
        return input;
    }
}

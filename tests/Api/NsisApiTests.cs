using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Bundler;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Signing.Windows;

namespace Bundler.ApiTests;

/// <summary>
/// NSIS 公开 API 契约：内嵌 makensis 真编译产 setup.exe（内嵌工具在三宿主均有移植件）。
/// 真装真卸属 Integration localinstall 腿；本层只断言产物结构与契约。
/// 收割自旧 ProgramTests 的 spawn 腿：语言/压缩/自定义语言文件真实编译、
/// 真实证书签名与签名失败清理（外部签名命令腿在 <see cref="SigningApiTests"/>）。
/// 已拆入 Unit 的校验期拒绝断言（RejectsDuplicateAndUnsupportedNsisLanguages /
/// RejectsUnknownNsisCompression / RejectsInvalidCustomNsisLanguageFiles）不在此重复。
/// </summary>
public sealed class NsisApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesInstaller()
    {
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Nsis);
        var artifacts = await new NsisBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Nsis, artifact.Format);
        Assert.Equal("windows-x86_64", artifact.Target);
        // NSIS 生态命名 {name}-{ver}-{arch}-setup.exe
        Assert.Equal("Sample-1.2.3-x64-setup.exe", Path.GetFileName(artifact.Path));
        Assert.True(new FileInfo(artifact.Path).Length > 100_000);
        Assert.Equal([0x4D, 0x5A], BundleFixture.Magic(artifact.Path, 2)); // MZ
    }

    [Fact]
    public void BuildAsync_InvalidPayloadName_Fails()
    {
        // 病态名（NUL 设备名）只能在 POSIX 宿主造出来——Windows 文件系统根本不让你建。
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 文件系统无法创建病态名");
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Nsis);
        File.WriteAllText(Path.Combine(bundle.Targets[0].InputDirectory, "NUL"), "x");
        Assert.Throws<ArgumentException>(() => new NsisBundler().Validate(bundle));
    }

    [Fact]
    public async Task BuildsThroughStandaloneNsisApi()
    {
        // 内嵌 makensis 是 glibc 链接产物，musl 宿主上无法执行
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        var root = Temp("nsis-standalone").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "standalone-api-test",
            TestContext.Current.CancellationToken);

        var configuration = new BundleConfiguration
        {
            ProductName = "Standalone API App",
            Identifier = "com.example.standalone",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input + Path.DirectorySeparatorChar,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };
        var bundler = new NsisBundler(
            new NsisBundleConfiguration { Languages = ["English", "SimpChinese"] },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });

        var artifacts = await bundler.BuildAsync(configuration, TestContext.Current.CancellationToken);
        Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "The standalone NSIS API should materialize embedded resources and create an installer.");
    }

    [Fact]
    public async Task BuildsEveryBuiltInNsisLanguage()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        string[] expectedLanguages =
        [
            "Arabic", "Bulgarian", "Dutch", "English", "French", "German", "Italian", "Japanese",
            "Korean", "Norwegian", "Persian", "Portuguese", "PortugueseBR", "Russian", "SimpChinese",
            "Spanish", "SpanishInternational", "Swedish", "TradChinese", "Turkish", "Ukrainian", "Vietnamese"
        ];
        Assert.Equal(NsisBundler.SupportedLanguages, expectedLanguages);

        var root = Temp("nsis-languages").FullName;
        var input = Path.Combine(root, "发布内容");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "示例应用.exe"), "language-test",
            TestContext.Current.CancellationToken);

        var bundler = new NsisBundler(
            new NsisBundleConfiguration
            {
                Languages = expectedLanguages,
                DisplayLanguageSelector = true
            },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
        var artifacts = await bundler.BuildAsync(new BundleConfiguration
        {
            ProductName = "多语言 اختبار برنامه",
            Identifier = "com.example.languages",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "安装程序"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input,
                    MainExecutable = "示例应用.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        }, TestContext.Current.CancellationToken);
        Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "NSIS did not compile one installer containing every built-in language.");
    }

    // 完整自定义语言文件整份替换内置语言的真实编译腿；
    // 校验期拒绝腿（missing/duplicate/unknown/wrong-language）已拆入
    // Unit RejectsInvalidCustomNsisLanguageFiles。
    [Fact]
    public async Task ValidatesCustomNsisLanguageFiles()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        var root = Temp("nsis-custom-language").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "custom-language-test",
            TestContext.Current.CancellationToken);
        var english = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "languages", "English.nsh"),
            TestContext.Current.CancellationToken);

        var validCustomFile = Path.Combine(root, "complete.nsh");
        await File.WriteAllTextAsync(
            validCustomFile,
            english.Replace("Shortcut options", "Custom shortcut options", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        var validArtifacts = await new NsisBundler(
            new NsisBundleConfiguration
            {
                Languages = ["english"],
                CustomLanguageFiles = new Dictionary<string, string> { ["English"] = validCustomFile }
            },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") })
            .BuildAsync(LanguageTestConfiguration(input, Path.Combine(root, "artifacts", "valid")),
                TestContext.Current.CancellationToken);
        Assert.True(validArtifacts.Count == 1 && File.Exists(validArtifacts[0].Path),
            "A complete custom language file did not override and compile in place of the built-in file.");
    }

    static BundleConfiguration LanguageTestConfiguration(string input, string output) => new()
    {
        ProductName = "Custom Language App",
        Identifier = "com.example.custom-language",
        Version = "1.0.0",
        OutputDirectory = output,
        Targets =
        [
            new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            }
        ]
    };

    [Fact]
    public async Task BuildsEveryNsisCompressionMode()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        var root = Temp("nsis-compression").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "compression-test",
            TestContext.Current.CancellationToken);

        foreach (var compression in Enum.GetValues<NsisCompression>())
        {
            var bundler = new NsisBundler(
                new NsisBundleConfiguration { Compression = compression },
                new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
            var artifacts = await bundler.BuildAsync(new BundleConfiguration
            {
                ProductName = $"Compression {compression}",
                Identifier = $"com.example.compression.{compression.ToString().ToLowerInvariant()}",
                Version = "1.0.0",
                OutputDirectory = Path.Combine(root, "artifacts", compression.ToString()),
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            }, TestContext.Current.CancellationToken);
            Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
                $"NSIS failed to compile an installer with {compression} compression.");
        }
    }

    [Fact]
    public async Task SignsBothNsisInstallerArtifacts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");

        var root = Temp("nsis-signing").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test process path is available.");
        var mainExecutable = Path.Combine(input, "ExampleApp.exe");
        var sidecar = Path.Combine(input, "Sidecar.dll");
        File.Copy(executable, mainExecutable);
        File.Copy(executable, sidecar);
        var originalMainHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainExecutable)));
        var originalSidecarHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar)));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DotNet.Bundler NSIS test certificate",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") },
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddDays(1));
        const string password = "nsis-integration-password";
        var pfx = Path.Combine(root, "test-signing.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        var signer = new RecordingSigner(new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
        {
            PfxFile = pfx,
            PfxPassword = password
        }));
        var bundler = new NsisBundler(options: new NsisBundlerOptions
        {
            ToolCacheDirectory = Path.Combine(root, "shared-tools"),
            Signer = signer
        });
        var artifacts = await bundler.BuildAsync(new BundleConfiguration
        {
            ProductName = "Signed NSIS App",
            Identifier = "com.example.signed-nsis",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input + Path.DirectorySeparatorChar,
                    MainExecutable = "ExampleApp.exe",
                    SigningFiles = ["Sidecar.dll"],
                    Formats = [PackageFormat.Nsis]
                }
            ]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { BundleSigningArtifactKind.PayloadExecutable, BundleSigningArtifactKind.PayloadFile, BundleSigningArtifactKind.NativeComponent, BundleSigningArtifactKind.Uninstaller, BundleSigningArtifactKind.Installer }, signer.ArtifactKinds);
        Assert.True(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainExecutable))) == originalMainHash &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar))) == originalSidecarHash,
            "NSIS signing modified the caller's input directory instead of a staging copy.");
        Assert.True(signer.Paths[0].Contains("signed-payload", StringComparison.OrdinalIgnoreCase) &&
               signer.Paths[2].Contains("signed-plugins", StringComparison.OrdinalIgnoreCase),
            "Payload or Bundler native components were not signed from private staging directories.");
        var cachedPlugin = Directory.EnumerateFiles(
            Path.Combine(root, "shared-tools"),
            "DotNetBundlerNsis.dll",
            SearchOption.AllDirectories).Single();
        var repositoryPlugin = Path.Combine(RepoRoot(), "third_party", "nsis", "plugins", "x86-unicode", "DotNetBundlerNsis.dll");
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(cachedPlugin)), SHA256.HashData(File.ReadAllBytes(repositoryPlugin)));
        Assert.True(SignedFileCertificates.EmbeddedSignatureContains(artifacts[0].Path, certificate),
            "The final NSIS installer did not contain the expected signing certificate.");
    }

    [Fact]
    public async Task RemovesFailedSignedInstaller()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        var root = Temp("nsis-signing-failure").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "fixture",
            TestContext.Current.CancellationToken);
        var output = Path.Combine(root, "artifacts");

        var bundler = new NsisBundler(options: new NsisBundlerOptions
        {
            ToolCacheDirectory = Path.Combine(root, "tools"),
            Signer = new FailingInstallerSigner()
        });
        var finalSigningFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => bundler.BuildAsync(new BundleConfiguration
            {
                ProductName = "Failed Signing App",
                Identifier = "com.example.failed-signing",
                Version = "1.0.0",
                OutputDirectory = output,
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            }, TestContext.Current.CancellationToken));
        Assert.Equal("fixture signing failure", finalSigningFailure.Message);
        var installer = Path.Combine(output, "Failed Signing App-1.0.0-setup.exe");
        Assert.False(File.Exists(installer), "A signing failure left a final installer that could be mistaken for success.");
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

    private sealed class RecordingSigner(IBundleSigner inner) : IBundleSigner
    {
        public List<BundleSigningArtifactKind> ArtifactKinds { get; } = [];
        public List<string> Paths { get; } = [];

        public async Task SignAsync(
            BundleSigningRequest request,
            CancellationToken cancellationToken = default)
        {
            ArtifactKinds.Add(request.ArtifactKind);
            Paths.Add(request.Path);
            await inner.SignAsync(request, cancellationToken);
        }
    }

    private sealed class FailingInstallerSigner : IBundleSigner
    {
        public Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
        {
            if (request.ArtifactKind == BundleSigningArtifactKind.Installer)
            {
                throw new InvalidOperationException("fixture signing failure");
            }
            return Task.CompletedTask;
        }
    }
}

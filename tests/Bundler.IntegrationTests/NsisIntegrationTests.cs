// Windows.Nsis.Integration/Verify.ps1 的 C# 收编：~30 个安装器变体 + 真装/真卸/事务故障注入。
// 门控：Windows + BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1——真装会写真实用户配置与注册表。
// 进程级细节与原脚本逐项对齐：退出码契约 0/2/3/4/6/3010、直接卸载器用 _?= 拿真退出码、
// taskkill 中断进程树、junction/快照篡改经 HKCU+文件双 sentinel 验证。
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NsisFixture : IAsyncLifetime
{
    public IntegrationWorkspace Ws { get; } = IntegrationWorkspace.Create("windows-nsis-integration", "windows.nsis");
    public string PackageCache { get; }
    public string ApiOutput { get; }
    public string ApiTools { get; }
    public string? CertificateThumbprint { get; private set; }
    private X509Certificate2? _certificate;

    private static readonly string ScriptDir =
        Path.Combine(RepositoryLayout.FixturesDirectory, "Nsis");
    public string FixtureProject { get; } =
        Path.Combine(ScriptDir, "Fixture", "BundlerNsisIntegrationFixture.csproj");
    private static string Assets(string name) =>
        Path.Combine(ScriptDir, "Fixture", "Assets", name);

    private readonly Lazy<bool> _init;
    public NsisFixture()
    {
        PackageCache = Ws.Combine("packages");
        ApiOutput = Ws.Combine("standalone-api");
        ApiTools = Ws.Combine("shared-tools");
        _init = new Lazy<bool>(Initialize);
    }

    public bool Ensure() => _init.Value;

    private bool Initialize()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "NSIS 集成腿只覆盖 Windows。");
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: NSIS 工件为 win-x64 安装器，仅 x64 宿主验收；arm64 仿真语义另列特殊项。");
        Assert.SkipWhen(!File.Exists(FixtureProject), "NSIS integration fixture 缺失。");
        // 仓库包一次 pack（含 nodeReuse 文件锁前导）；NSIS 脚本的包装配检查放
        // RepositoryPackagesCarryNsisBackendAndAssets 一条无门禁事实。
        var packages = RepositoryPackages.DirectoryPath;
        _ = packages;

        Directory.CreateDirectory(PackageCache);
        ExtractNsisAssets();
        BuildLegacyMsis();
        ProcessRunner.AssertSuccess(Dotnet.Test(
            Path.Combine(RepositoryLayout.Root, "tests", "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
            "Release", "NsisApiTests", "NSIS API package fixture",
            new Dictionary<string, string?>
            {
                ["NSIS_API_FIXTURE_OUTPUT"] = ApiOutput,
                ["NSIS_API_FIXTURE_CACHE"] = ApiTools,
            }), "NSIS ApiTests");
        Assert.True(File.Exists(Path.Combine(ApiOutput, "artifacts", "win-x64", "nsis",
                "NSIS API Package Fixture-1.0.0-setup.exe")),
            "Standalone NSIS API package did not create its installer.");
        // 写 CurrentUser 证书库与真装同一信任级别——只在 localinstall 同意闸内创建。
        if (Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") == "1")
        {
            CreateTestCertificate();
        }
        BuildAllVariants();
        return true;
    }

    private string TestIcon { get; set; } = "";
    private string TestHeaderImage { get; set; } = "";
    private string TestSidebarImage { get; set; } = "";

    private void ExtractNsisAssets()
    {
        TestIcon = Ws.Combine("test-installer.ico");
        TestHeaderImage = Ws.Combine("test-header.bmp");
        TestSidebarImage = Ws.Combine("test-sidebar.bmp");
        var archivePath = Path.Combine(RepositoryLayout.Root,
            "third_party", "nsis", "nsis-toolset-3.12-r1.zip");
        // provenance 钉死的工具集哈希：取件前先核，防仓内资产被污染。
        Assert.Equal("41F15B7F7E3A0349185606EDE939C7B2E5B31FF76F0EB479143D6659EF1EDDBC",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))));
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var (entryName, destination) in new (string, string)[]
        {
            ("common/Contrib/Graphics/Icons/modern-install.ico", TestIcon),
            ("common/Contrib/Graphics/Header/nsis3-grey.bmp", TestHeaderImage),
            ("common/Contrib/Graphics/Wizard/nsis3-grey.bmp", TestSidebarImage),
        })
        {
            var entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using var input = entry!.Open();
            using var output = File.Create(destination);
            input.CopyTo(output);
        }
    }

    public string LegacyMsiPath { get; private set; } = "";
    public string LegacyMsiV2Path { get; private set; } = "";

    private void BuildLegacyMsis()
    {
        var legacy = Path.Combine(ScriptDir, "LegacyMsiFixture", "LegacyMsiFixture.wixproj");
        var legacyV2 = Path.Combine(ScriptDir, "LegacyMsiFixtureV2", "LegacyMsiFixtureV2.wixproj");
        Dotnet.Checked(["build", legacy, "-c", "Release"], "legacy MSI fixture build");
        Dotnet.Checked(["build", legacyV2, "-c", "Release"], "legacy MSI fixture V2 build");
        LegacyMsiPath = Path.Combine(Path.GetDirectoryName(legacy)!, "bin", "Release", "LegacyMsiFixture.msi");
        LegacyMsiV2Path = Path.Combine(Path.GetDirectoryName(legacyV2)!, "bin", "Release", "LegacyMsiFixtureV2.msi");
        Assert.True(File.Exists(LegacyMsiPath), "Legacy MSI fixture was not created.");
        Assert.True(File.Exists(LegacyMsiV2Path), "Legacy MSI fixture V2 was not created.");
    }

    private void CreateTestCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=DotNet.Bundler disposable integration certificate", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.3")], false));
        // CreateSelfSigned 返回的私钥是 ephemeral CNG 密钥——store.Add 只存证书不持久化密钥，
        // 签名时 HasPrivateKey=false。导出 PFX 再以 PersistKeySet 重导入让密钥落到用户密钥库。
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddDays(1));
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        _certificate = X509CertificateLoader.LoadPkcs12(pfx, password: null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        CryptographicOperations.ZeroMemory(pfx);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(_certificate);
        CertificateThumbprint = _certificate.Thumbprint;
    }

    public sealed record BundleVariant(
        string Name, string InstallScope = "currentUser", string ApplicationVersion = "1.0.0",
        bool AllowDowngrades = false, string LegacyMsiProductCodes = "",
        string LegacyMsiUpgradeCodes = "", bool LegacyMsiAutoDetect = false,
        string? Publisher = null, string? Formats = null,
        string? SigningCertificateThumbprint = null,
        bool ShortcutDesktop = true, bool ShortcutStartMenu = true,
        string? InstallerHooksFile = null,
        string ProductName = "Bundler Integration Fixture",
        string Identifier = "com.dotnetbundler.integrationfixture",
        string Languages = "English;SimpChinese", bool DisplayLanguageSelector = true,
        string Description = "Disposable Windows NSIS integration-test fixture.",
        string ShortcutArguments = "--shortcut-mode \"hello world\"",
        string ShortcutStartMenuFolder = "DotNet Bundler Integration");

    private void BuildBundle(BundleVariant v)
    {
        var hooks = v.InstallerHooksFile ?? Assets("installer-hooks.nsh");
        var args = new List<string>
        {
            "publish", FixtureProject, "-c", "Release", "--force",
            $"-p:Version={v.ApplicationVersion}",
            $"-p:BundlerIntegrationOutput={Ws.Combine(v.Name)}",
            $"-p:BundlerTestIconFile={TestIcon}",
            $"-p:BundlerTestHeaderFile={TestHeaderImage}",
            $"-p:BundlerTestSidebarFile={TestSidebarImage}",
            $"-p:BundlerNsisInstallScope={v.InstallScope}",
            $"-p:BundlerNsisAllowDowngrades={v.AllowDowngrades}",
            $"-p:BundlerNsisLegacyMsiProductCodes={v.LegacyMsiProductCodes}",
            $"-p:BundlerNsisLegacyMsiUpgradeCodes={v.LegacyMsiUpgradeCodes}",
            $"-p:BundlerNsisLegacyMsiAutoDetect={v.LegacyMsiAutoDetect}",
            $"-p:BundlerWindowsSigningCertificateThumbprint={v.SigningCertificateThumbprint ?? ""}",
            $"-p:BundlerIntegrationInstallerHooksFile={hooks}",
            $"-p:BundlerNsisShortcutDesktop={v.ShortcutDesktop}",
            $"-p:BundlerNsisShortcutStartMenu={v.ShortcutStartMenu}",
            $"-p:BundlerIntegrationProductName={v.ProductName}",
            $"-p:BundlerIntegrationIdentifier={v.Identifier}",
            $"-p:BundlerIntegrationLanguages={v.Languages.Replace(";", "%3B")}",
            $"-p:BundlerIntegrationDisplayLanguageSelector={v.DisplayLanguageSelector}",
            $"-p:BundlerIntegrationDescription={v.Description}",
            "-p:BundlerIntegrationShortcutArguments=" + v.ShortcutArguments
                .Replace("%", "%25").Replace(";", "%3B").Replace("\"", "%22"),
            $"-p:BundlerIntegrationShortcutStartMenuFolder={v.ShortcutStartMenuFolder}",
            $"-p:RestorePackagesPath={PackageCache}",
        };
        if (v.Publisher is not null)
        {
            args.Add($"-p:BundlerIntegrationPublisher={v.Publisher}");
        }
        if (v.Formats is not null)
        {
            args.Add($"-p:BundlerFormats={v.Formats}");
            if (v.Formats == "msi")
            {
                args.Add("-p:BundlerLicenseFile=");
            }
        }
        ProcessRunner.AssertSuccess(
            Dotnet.Run(args, new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) }),
            $"fixture publish failed for {v.Name}");
    }

    // ~30 个变体的定义（与脚本 Build-FixtureBundle 调用逐一对应）。
    public const string ProductNameConst = "Bundler Integration Fixture";
    public static string InstallerPath(string bundleDir, string productName, string version) =>
        Path.Combine(bundleDir, "win-x64", "nsis", $"{productName}-{version}-setup.exe");

    private void BuildAllVariants()
    {
        var variants = new List<BundleVariant>
        {
            new("bundle"), new("bundle-per-machine", InstallScope: "perMachine"),
            new("bundle-both", InstallScope: "both"),
            new("bundle-upgrade", ApplicationVersion: "1.1.0"),
            new("bundle-rollback-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-postinstall.nsh")),
            new("bundle-transaction-snapshot-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-transaction-snapshot.nsh")),
            new("bundle-transaction-activation-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-transaction-activation.nsh")),
            new("bundle-payload-restore-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-payload-restore.nsh")),
            new("bundle-registry-restore-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-registry-restore.nsh")),
            new("bundle-journal-cleanup-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-journal-cleanup.nsh")),
            new("bundle-shortcut-persistence-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-shortcut-persistence.nsh")),
            new("bundle-registry-persistence-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-registry-persistence.nsh")),
            new("bundle-commit-cleanup-failure", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("failing-commit-cleanup.nsh")),
            new("bundle-interrupted", ApplicationVersion: "1.2.0",
                InstallerHooksFile: Assets("aborting-postinstall.nsh")),
            new("bundle-different-manifest", ApplicationVersion: "1.3.0",
                ShortcutStartMenuFolder: "Different Manifest Fixture"),
            new("bundle-reboot-required",
                InstallerHooksFile: Assets("rebooting-postinstall.nsh")),
            new("bundle-allowed-downgrade", AllowDowngrades: true),
            new("bundle-legacy-msi-product-migration",
                LegacyMsiProductCodes: "{1D1A6B03-2BDA-4D18-B12C-574145D9CFA0}"),
            new("bundle-legacy-msi-upgrade-migration",
                LegacyMsiUpgradeCodes: "{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}"),
            new("bundle-legacy-msi-autodetect", LegacyMsiAutoDetect: true),
            new("bundle-legacy-msi-name-mismatch", LegacyMsiAutoDetect: true,
                ProductName: "Bundler Other Fixture",
                Identifier: "com.dotnetbundler.otherfixture"),
            new("bundle-legacy-msi-publisher-mismatch", LegacyMsiAutoDetect: true,
                Publisher: "Other Publisher",
                Identifier: "com.dotnetbundler.pubmismatch"),
            new("bundle-legacy-msi-downgrade-probe", ApplicationVersion: "0.8.5",
                LegacyMsiUpgradeCodes: "{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}"),
            new("bundle-msi-continuity", Formats: "msi"),
            new("bundle-signed",
                SigningCertificateThumbprint: CertificateThumbprint!),
            new("bundle-no-shortcut-defaults",
                ShortcutDesktop: false, ShortcutStartMenu: false),
            new("bundle-failing-uninstall-forward",
                InstallerHooksFile: Assets("failing-postuninstall-forward.nsh")),
            new("bundle-interrupted-uninstall-forward",
                InstallerHooksFile: Assets("interrupted-postuninstall-forward.nsh")),
            new("bundle-unicode",
                ProductName: "多言語テスト应用",
                Identifier: "com.dotnetbundler.localizationfixture",
                Languages: Environment.GetEnvironmentVariable("USERNAME") == "irrelevant"
                    ? "Korean" : FallbackLanguage(),
                DisplayLanguageSelector: false,
                Description: "Unicode 元数据と説明",
                ShortcutDesktop: false,
                ShortcutArguments: "--表示モード \"你好 世界\"",
                ShortcutStartMenuFolder: "多语言 开始菜单"),
        };
        foreach (var variant in variants)
        {
            BuildBundle(variant);
        }
    }

    private static string FallbackLanguage()
    {
        var ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
        return ui.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "Korean" : "Japanese";
    }

    public string BundleDir(string name) => Ws.Combine(name);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (_certificate is not null)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(_certificate);
            // PersistKeySet 落地的 CNG 密钥容器独立于证书存储，需显式删除。
            if (_certificate.GetECDsaPrivateKey() is ECDsaCng cng && cng.Key is not null)
            {
                cng.Key.Delete();
            }
        }
        Ws.Dispose();
        return ValueTask.CompletedTask;
    }
}

// ── 测试类：脚本主 try 块里的全部测试腿，逐条映射为独立 [Fact] ──

[System.Runtime.Versioning.SupportedOSPlatform("windows")]

public sealed class NsisIntegrationTests(NsisFixture fixture) : IClassFixture<NsisFixture>
{
    private readonly NsisFixture _f = fixture;

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string AppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Temp = Path.GetTempPath();
    private static readonly string Desktop =
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    private const string Identifier = "com.dotnetbundler.integrationfixture";
    private const string ProductName = "Bundler Integration Fixture";
    private const string UnicodeId = "com.dotnetbundler.localizationfixture";
    private const string UnicodeName = "多言語テスト应用";
    private const string LegacyMsiProductCode = "{1D1A6B03-2BDA-4D18-B12C-574145D9CFA0}";
    private const string LegacyMsiV2ProductCode = "{7B3E8C14-6A2D-4F5B-9C1E-8D4A5F6B7C8D}";

    private string InstallRoot => _f.Ws.Combine("安装 目录");
    private string InstallDir => Path.Combine(InstallRoot, ProductName);
    private string UnicodeInstallDir => Path.Combine(_f.Ws.Combine("多语言 安装目录"), "应用");
    private string Exe => Path.Combine(InstallDir, "BundlerIntegrationFixture.exe");
    private string Uninstaller => Path.Combine(InstallDir, "Uninstall.exe");
    private string RegistryPath => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Identifier}";
    private string UnicodeRegistryPath =>
        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UnicodeId}";
    private string FileExtensionKey => @"Software\Classes\.dbfixture";
    private string FileProgId => $"{Identifier}.File.dbfixture.1";
    private string FileProgIdKey => $@"Software\Classes\{FileProgId}";
    private string UrlSchemeKey => @"Software\Classes\bundlerfixture";
    private string UrlProgIdKey =>
        $@"Software\Classes\{Identifier}.Url.bundlerfixture.1";
    private string CapabilitiesKey => $@"Software\{Identifier}\Capabilities";
    private string RegisteredAppsKey => @"Software\RegisteredApplications";
    private string DeepLinkMarker => Path.Combine(Temp, "DotNetBundler-deep-link.txt");
    private string CommandLineMarker => Path.Combine(Temp, "DotNetBundler-command-line.txt");
    private string InterruptedHookMarker =>
        Path.Combine(Temp, "DotNetBundler-interrupted-postinstall.txt");
    private string FailingUninstallMarker =>
        Path.Combine(Temp, "DotNetBundler-failing-postuninstall-forward.once");
    private string InterruptedUninstallMarker =>
        Path.Combine(Temp, "DotNetBundler-interrupted-postuninstall-forward.once");
    private string RoamingData => Path.Combine(AppData, Identifier);
    private string LocalData => Path.Combine(LocalAppData, Identifier);
    private string TransactionDir =>
        Path.Combine(LocalAppData, "DotNetBundler", "transactions", Identifier);
    private string CommittedTransactionDir => TransactionDir + ".committed";
    private string UninstallTransactionDir => TransactionDir + ".uninstall";
    private string CommittedUninstallTransactionDir => UninstallTransactionDir + ".committed";
    private string DesktopShortcut => Path.Combine(Desktop, $"{ProductName}.lnk");
    private string StartMenuDir =>
        Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs",
            "DotNet Bundler Integration");
    private string StartMenuShortcut => Path.Combine(StartMenuDir, $"{ProductName}.lnk");
    private string LegacyStartMenuShortcut => Path.Combine(AppData, "Microsoft", "Windows",
        "Start Menu", "Programs", "Legacy Bundler Fixture", "Legacy Bundler Fixture.lnk");
    private string UnicodeStartMenuShortcut => Path.Combine(AppData, "Microsoft", "Windows",
        "Start Menu", "Programs", "多语言 开始菜单", $"{UnicodeName}.lnk");
    private string JournalTamperFile => _f.Ws.Combine("journal-tamper-sentinel.txt");
    private const string JournalTamperKey = @"Software\DotNetBundler\JournalTamperSentinel";
    private string BundlerDefinitionKey =>
        $@"Software\DotNetBundler\Products\{Identifier}\win-x64\Components";
    private string LegacyMsiInstallDir =>
        Path.Combine(LocalAppData, "Bundler Legacy MSI Fixture");
    private string LegacyMsiV2InstallDir =>
        Path.Combine(LocalAppData, "Bundler Legacy MSI Fixture V2");
    private string NsisContinuityDir =>
        Path.Combine(LocalAppData, "DotNetBundlerNsisContinuityProbe");
    private string NsisOutOfScopeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnetbundler-scope-probe");
    private string MsiContinuityDefaultDir =>
        Path.Combine(LocalAppData, "Programs", $"{Identifier}-x64");
    private string MsiPrecedenceDir =>
        Path.Combine(LocalAppData, "Programs", "bundler-precedence-probe");
    private string ReparseOutsideDir => _f.Ws.Combine("reparse-outside");

    private string Installer(string bundle, string version = "1.0.0", string? name = null)
        => Path.Combine(_f.BundleDir(bundle), "win-x64", "nsis",
            $"{name ?? ProductName}-{version}-setup.exe");

    // ── 公共小件 ──


    private static void RequireConsent() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") != "1",
            "真装会写真实用户配置与注册表；置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1 才跑。");

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string? RegGet(string key, string name)
        => Registry.CurrentUser.OpenSubKey(key)?.GetValue(name) as string;

    private static string RegGetRequired(string key, string name)
    {
        var value = RegGet(key, name);
        Assert.NotNull(value);
        return value!;
    }

    private static ProcessRunner.Result Run(string file, string argumentLine)
        => ProcessRunner.Run(file, argumentLine,
            new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(5) });

    private static void RunOk(string file, string argumentLine)
        => ProcessRunner.AssertExitCode(Run(file, argumentLine), 0, file);

    private static void Msiexec(string argumentLine, params int[] allowed)
        => MsiexecLogged(argumentLine, allowed);

    private static void MsiexecLogged(string argumentLine, params int[] allowed)
    {
        var result = MsiSupport.Msiexec(argumentLine);
        Assert.Contains(result.ExitCode, allowed);
    }

    private static void InstallLegacyMsi(string path)
    {
        Msiexec($"/i \"{path}\" /qn /norestart", 0, 3010);
    }

    private void MsiexecRemoveLegacy()
    {
        Msiexec($"/x {LegacyMsiProductCode} /qn /norestart", 0, 1605, 3010);
        Msiexec($"/x {LegacyMsiV2ProductCode} /qn /norestart", 0, 1605, 3010);
    }

    // NSIS 卸载器直启：复制到工作区 + _?= 让测试拿到真实退出码（launcher 不透传）。
    private ProcessRunner.Result DirectUninstall(string uninstaller, string installDir,
        string args)
    {
        var copy = _f.Ws.Combine($"direct-uninstaller-{Guid.NewGuid():N}.exe");
        File.Copy(uninstaller, copy);
        return Run(copy, $"{args} _?={installDir}");
    }

    private Process StartDirectUninstallerAsync(string uninstaller, string installDir, string args)
    {
        var copy = _f.Ws.Combine($"direct-uninstaller-{Guid.NewGuid():N}.exe");
        File.Copy(uninstaller, copy);
        return ProcessRunner.StartDetached(copy, $"{args} _?={installDir}");
    }

    // 标准清理（对应 Remove-TestState 的默认 identifier 段）。
    private void Cleanup()
    {
        foreach (var key in new[]
                 {
                     RegistryPath, JournalTamperKey, UnicodeRegistryPath,
                     $@"Software\{UnicodeId}", FileProgIdKey, UrlSchemeKey, UrlProgIdKey,
                     CapabilitiesKey, BundlerDefinitionKey,
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall\com.dotnetbundler.otherfixture",
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall\com.dotnetbundler.pubmismatch",
                 })
        {
            // 注册表句柄释放同样有滞后：刚退出的进程持有的键处于 pending-delete 时删会抛 IOException
            for (var i = 0; i < 10; i++)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(key, false); break; }
                catch (IOException) when (i < 9) { Thread.Sleep(200); }
            }
        }
        if (Registry.CurrentUser.OpenSubKey(FileExtensionKey) is { } ext)
        {
            using (ext)
            {
                ext.OpenSubKey("OpenWithProgids", true)?.DeleteValue(FileProgId, false);
                if (Registry.CurrentUser.OpenSubKey(FileExtensionKey) is { } check
                    && check.SubKeyCount == 0 && check.ValueCount == 0)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(FileExtensionKey, false);
                }
            }
        }
        Registry.CurrentUser.OpenSubKey(RegisteredAppsKey, true)?.DeleteValue(Identifier, false);
        foreach (var file in new[]
                 {
                     DeepLinkMarker, CommandLineMarker, InterruptedHookMarker,
                     FailingUninstallMarker, InterruptedUninstallMarker,
                     DesktopShortcut, StartMenuShortcut, LegacyStartMenuShortcut,
                     UnicodeStartMenuShortcut, JournalTamperFile,
                 }.Concat(new[] { "preinstall", "postinstall", "preuninstall", "postuninstall" }
                     .Select(h => Path.Combine(Temp, $"DotNetBundler-{h}.txt"))))
        {
            if (File.Exists(file))
            {
                // 钩子进程刚退出时对标记文件的占用有短暂滞后，删之前重试几次
                for (var i = 0; i < 10; i++)
                {
                    try { File.Delete(file); break; }
                    catch (IOException) when (i < 9) { Thread.Sleep(200); }
                    catch (UnauthorizedAccessException) when (i < 9) { Thread.Sleep(200); }
                }
            }
        }
        foreach (var dir in new[]
                 {
                     StartMenuDir,
                     Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs",
                         "Legacy Bundler Fixture"),
                     Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs",
                         "多语言 开始菜单"),
                     TransactionDir, CommittedTransactionDir, UninstallTransactionDir,
                     CommittedUninstallTransactionDir,
                     InstallRoot, UnicodeInstallDir, _f.Ws.Combine("多语言 安装目录"),
                     _f.Ws.Combine("same-name-external-process"), ReparseOutsideDir,
                     NsisContinuityDir, NsisOutOfScopeDir, MsiContinuityDefaultDir,
                     MsiPrecedenceDir, RoamingData, LocalData,
                     LegacyMsiInstallDir, LegacyMsiV2InstallDir,
                     Path.Combine(LocalAppData, "Programs", ProductName),
                 })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); }
                catch { /* 目录非空时由测试断言兜底 */ }
            }
        }
        // transactions 父目录跨产品共享——只在已空时修剪，不动其他产品的事务记录。
        try { Directory.Delete(Path.GetDirectoryName(TransactionDir)!); }
        catch { /* 非空即有其他产品记录，保留 */ }
        MsiexecRemoveLegacy();
    }

    private void InstallDefault()
    {
        RunOk(Installer("bundle"), $"/S /D={InstallDir}");
        WaitFor.Until(() => File.Exists(Exe), "Installed executable is missing.");
        WaitFor.Until(() => File.Exists(Uninstaller), "Uninstaller is missing.");
    }

    private void UninstallDefault(string args = "/S /DELETEAPPDATA")
    {
        RunOk(Uninstaller, args);
        WaitFor.Until(() => !Directory.Exists(InstallDir) || !File.Exists(Exe),
            "Uninstall cleanup did not finish.", 30);
    }

    // ── 事实 ──

    // 包装配段（无宿主门禁，跨平台可跑）：nupkg 条目契约。
    [Fact]
    public void RepositoryPackagesCarryNsisBackendAndAssets()
    {
        var packages = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        var msbuildEntries = Dotnet.NupkgEntries(
            Path.Combine(packages, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        foreach (var required in new[]
                 {
                     "buildTransitive/DotNet.Bundler.MSBuild.props",
                     "buildTransitive/DotNet.Bundler.MSBuild.targets",
                     "tasks/netstandard2.0/DotNet.Bundler.Abstractions.dll",
                     "tasks/netstandard2.0/DotNet.Bundler.Core.dll",
                     "tasks/netstandard2.0/DotNet.Bundler.Nsis.dll",
                     "tasks/netstandard2.0/DotNet.Bundler.Signing.Windows.dll",
                     "tasks/netstandard2.0/DotNet.Bundler.MSBuild.dll",
                     "licenses/nsis/COPYING",
                     "licenses/nsis-plugin/LICENSE",
                 })
        {
            Assert.Contains(msbuildEntries, e => e == required);
        }
        Assert.DoesNotContain(msbuildEntries, e => e.StartsWith("tools/net8.0/"));
        var nsisEntries = Dotnet.NupkgEntries(
            Path.Combine(packages, $"DotNet.Bundler.Nsis.{version}.nupkg"));
        Assert.Contains(nsisEntries, e => e == "lib/netstandard2.0/DotNet.Bundler.Nsis.dll");
        Assert.Contains(nsisEntries, e => e == "licenses/nsis/COPYING");
        Assert.Contains(nsisEntries, e => e == "licenses/nsis-plugin/LICENSE");
        var signingEntries = Dotnet.NupkgEntries(
            Path.Combine(packages, $"DotNet.Bundler.Signing.Windows.{version}.nupkg"));
        Assert.Contains(signingEntries,
            e => e == "lib/netstandard2.0/DotNet.Bundler.Signing.Windows.dll");
    }

    // 装配段：全部 ~29 个变体安装器必须产出。
    [Fact]
    public void AllInstallerVariantsProduced()
    {
        _f.Ensure();
        var expected = new (string Bundle, string File)[]
        {
            ("bundle", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-per-machine", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-both", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-upgrade", $"{ProductName}-1.1.0-setup.exe"),
            ("bundle-rollback-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-transaction-snapshot-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-transaction-activation-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-payload-restore-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-registry-restore-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-journal-cleanup-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-shortcut-persistence-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-registry-persistence-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-commit-cleanup-failure", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-interrupted", $"{ProductName}-1.2.0-setup.exe"),
            ("bundle-different-manifest", $"{ProductName}-1.3.0-setup.exe"),
            ("bundle-reboot-required", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-allowed-downgrade", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-legacy-msi-product-migration", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-legacy-msi-upgrade-migration", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-legacy-msi-autodetect", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-legacy-msi-name-mismatch", "Bundler Other Fixture-1.0.0-setup.exe"),
            ("bundle-legacy-msi-publisher-mismatch", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-legacy-msi-downgrade-probe", $"{ProductName}-0.8.5-setup.exe"),
            ("bundle-signed", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-no-shortcut-defaults", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-failing-uninstall-forward", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-interrupted-uninstall-forward", $"{ProductName}-1.0.0-setup.exe"),
            ("bundle-unicode", $"{UnicodeName}-1.0.0-setup.exe"),
        };
        foreach (var (bundle, file) in expected)
        {
            Assert.True(File.Exists(Path.Combine(_f.BundleDir(bundle), "win-x64", "nsis", file)),
                $"Installer variant was not created: {bundle}/{file}");
        }
        Assert.True(File.Exists(Path.Combine(_f.BundleDir("bundle-msi-continuity"),
            "win-x64", "msi", $"{ProductName}-1.0.0.msi")),
            "MSI continuity installer was not created.");
    }

    // Unicode 腿：非本机语言包+非 ASCII 全程保持（注册表/快捷方式/卸载）。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void UnicodeInstallRegistersMetadataAndCleansUp()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            RunOk(Installer("bundle-unicode", name: UnicodeName),
                $"/S /D={UnicodeInstallDir}");
            var unicodeExe = Path.Combine(UnicodeInstallDir, "BundlerIntegrationFixture.exe");
            var unicodeUninstaller = Path.Combine(UnicodeInstallDir, "Uninstall.exe");
            Assert.True(File.Exists(unicodeExe),
                "Fallback-language installer did not install when system UI language was not selected.");
            Assert.True(File.Exists(unicodeUninstaller));
            Assert.Equal(UnicodeName,
                RegGetRequired(UnicodeRegistryPath, "DisplayName"));
            Assert.Equal("Unicode 元数据と説明",
                RegGetRequired(UnicodeRegistryPath, "Comments"));
            Assert.True(File.Exists(UnicodeStartMenuShortcut),
                "Unicode Start Menu shortcut was not created.");
            var shortcut = ShellLink.Read(UnicodeStartMenuShortcut);
            Assert.Equal(unicodeExe, shortcut.Destination);
            Assert.Equal("--表示モード \"你好 世界\"", shortcut.Arguments);
            RunOk(unicodeUninstaller, "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(UnicodeInstallDir),
                "Unicode fixture cleanup did not finish.", 30);
            Assert.Null(Registry.CurrentUser.OpenSubKey(UnicodeRegistryPath));
            Assert.False(File.Exists(UnicodeStartMenuShortcut));
        }
        finally { Cleanup(); }
    }

    // 受管的签名证书提取：CreateFromSignedFile 直接读 PE 的 WIN_CERTIFICATE，
    // 不依赖 powershell 的 Microsoft.PowerShell.Security 模块（部分宿主该模块自动加载会拒）。
    private static string SignerThumbprint(string path)
    {
        System.Security.Cryptography.X509Certificates.X509Certificate signer;
        try
        {
#pragma warning disable SYSLIB0057 // 读 signed-PE 签名证书仅此 API，X509CertificateLoader 无对应物
            signer = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"File carries no Authenticode signer certificate: {path}", ex);
        }
        var thumbprint = signer.GetCertHashString();
        Assert.True(thumbprint.Length > 0,
            $"File carries no Authenticode signer certificate: {path}");
        return thumbprint;
    }

    // 签名链：安装器/载荷/卸载器逐层带测试证书的 Authenticode。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void SignedInstallerChain()
    {
        _f.Ensure();
        RequireConsent();
        var signed = Installer("bundle-signed");
        Assert.Equal(_f.CertificateThumbprint, SignerThumbprint(signed));
        try
        {
            RunOk(signed, $"/S /D={InstallDir}");
            Assert.Equal(_f.CertificateThumbprint, SignerThumbprint(Exe));
            Assert.Equal(_f.CertificateThumbprint, SignerThumbprint(Uninstaller));
            RunOk(Uninstaller, "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(InstallDir),
                "Signed installer cleanup did not finish.", 30);
        }
        finally { Cleanup(); }
    }

    // post-uninstall 失败：保留卸载 journal+恢复卸载器+注册表锚点，篡改恢复卸载器被拒，
    // 修复后下次安装先完成旧卸载再装新载荷。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void FailedUninstallKeepsJournalAndNextInstallRecovers()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            RunOk(Installer("bundle-failing-uninstall-forward"), $"/S /D={InstallDir}");
            var failing = DirectUninstall(Uninstaller, InstallDir, "/S /DELETEAPPDATA");
            Assert.Equal(2, failing.ExitCode);
            Assert.True(Directory.Exists(UninstallTransactionDir),
                "Failed uninstall did not preserve its active forward journal.");
            var recoveryUninstaller = Path.Combine(UninstallTransactionDir, "recovery-uninstaller.exe");
            Assert.True(File.Exists(recoveryUninstaller));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(RegistryPath));
            Assert.Equal(Sha256(recoveryUninstaller),
                RegGetRequired(RegistryPath, "BundlerRecoverySha256"));
            var original = File.ReadAllBytes(recoveryUninstaller);
            File.AppendAllText(recoveryUninstaller, "tampered");
            var tampered = Run(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.Equal(2, tampered.ExitCode);
            Assert.True(Directory.Exists(UninstallTransactionDir),
                "Rejected recovery uninstaller did not preserve the forward journal.");
            File.WriteAllBytes(recoveryUninstaller, original);
            RunOk(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.True(File.Exists(Exe),
                "Installer did not continue after completing a failed uninstall.");
            Assert.False(Directory.Exists(UninstallTransactionDir));
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // post-uninstall 中断：taskkill 杀掉整个卸载进程树，下次安装幂等完成旧卸载。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void InterruptedUninstallKeepsJournalAndNextInstallRecovers()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            RunOk(Installer("bundle-interrupted-uninstall-forward"), $"/S /D={InstallDir}");
            using var interrupted =
                StartDirectUninstallerAsync(Uninstaller, InstallDir, "/S /DELETEAPPDATA");
            WaitFor.Until(() => File.Exists(InterruptedUninstallMarker),
                "Interrupted-uninstall fixture did not reach its post-uninstall hook.");
            ProcessRunner.TryKillTree(interrupted);
            interrupted.WaitForExit();
            Assert.NotEqual(0, interrupted.ExitCode);
            Assert.True(Directory.Exists(UninstallTransactionDir),
                "Interrupted uninstall did not preserve its active forward journal.");
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(RegistryPath));
            RunOk(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.True(File.Exists(Exe),
                "Installer did not continue after completing an interrupted uninstall.");
            Assert.False(Directory.Exists(UninstallTransactionDir));
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // SetRebootFlag：事务提交后返回 3010，/R 也不得在重启前启动应用。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void RebootRequiredCommitsAndReturns3010WithoutLaunching()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            var reboot = Run(Installer("bundle-reboot-required"),
                $"/S /R /ARGS=--protocol-marker reboot-required /D={InstallDir}");
            Assert.Equal(3010, reboot.ExitCode);
            Assert.True(File.Exists(Exe),
                "Reboot-required install did not commit its payload.");
            Assert.Equal("1.0.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.False(Directory.Exists(TransactionDir),
                "Reboot-required install left an active transaction journal.");
            Assert.False(File.Exists(CommandLineMarker),
                "Reboot-required install launched the application before reboot.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // /ARGS 不带 /R 是调用错误：返回 3 且不写载荷。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void ArgsWithoutRunReturns3AndDoesNotInstall()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            var invalid = Run(Installer("bundle"), $"/S /ARGS orphaned /D={InstallDir}");
            Assert.Equal(3, invalid.ExitCode);
            Assert.False(File.Exists(Exe),
                "Invalid command-line arguments unexpectedly installed the application.");
        }
        finally { Cleanup(); }
    }

    // /P 静默进度 + /NS 不建快捷方式；包级 Shortcut*=false 默认同样不建。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void PassiveModeAndDisabledShortcutDefaults()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            RunOk(Installer("bundle"), $"/P /NS /D={InstallDir}");
            Assert.True(File.Exists(Exe),
                "Passive installation did not write the application payload.");
            Assert.False(File.Exists(DesktopShortcut), "/NS created a desktop shortcut.");
            Assert.False(File.Exists(StartMenuShortcut), "/NS created a Start Menu shortcut.");
            RunOk(Uninstaller, "/P /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(InstallDir), "Passive cleanup did not finish.", 30);

            RunOk(Installer("bundle-no-shortcut-defaults"), $"/S /D={InstallDir}");
            Assert.False(File.Exists(DesktopShortcut),
                "Disabled desktop shortcut default was ignored.");
            Assert.False(File.Exists(StartMenuShortcut),
                "Disabled Start Menu shortcut default was ignored.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // GUI 级验收：不带 /S /P 真弹向导，UIA 逐页驱动
    // 欢迎→许可(I Agree)→目录→快捷方式选项→INSTFILES→Finish，再驱动交互卸载器
    // 确认→AppData 选项→INSTFILES。断言产物、双快捷方式与 DisplayName。
    [Fact]
    [Trait("Requires", "localinstall")]
    [Trait("Requires", "interactive")]
    public void InteractiveWizardInstallsAndUninstalls()
    {
        _f.Ensure();
        RequireConsent();
        Assert.SkipWhen(!WindowsDesktop.IsInteractive(),
            "GUI 验收腿需要交互式桌面会话（UIA 可达顶层窗口）。");
        try
        {
            using var process = ProcessRunner.StartDetached(
                Installer("bundle"), $"/D={InstallDir}");
            var installDrive = WindowsDesktop.DriveWizard(
                () => WindowsDesktop.TopWindowsByProcess(process.Id),
                () => process.HasExited, TimeSpan.FromMinutes(5));
            Assert.True(installDrive.Finished,
                $"安装向导未走完（已点：{string.Join(" → ", installDrive.Actions)}）");
            Assert.Contains(installDrive.Actions, a => a.Contains("Agree"));
            Assert.True(installDrive.Actions.Count(a => a.Contains("Next")) >= 2,
                "向导页数不足：未见到 Directory/ShortcutOptions 的 Next。");
            WaitFor.Until(() => process.HasExited, "Installer did not exit after Finish.", 30);
            Assert.Equal(0, process.ExitCode);
            WaitFor.Until(() => File.Exists(Exe), "Interactive install did not write the payload.");
            WaitFor.Until(() => File.Exists(Uninstaller), "Uninstaller is missing.");
            Assert.True(File.Exists(DesktopShortcut) && File.Exists(StartMenuShortcut),
                "向导默认勾选项未产出双快捷方式。");
            Assert.Equal(ProductName, RegGetRequired(RegistryPath, "DisplayName"));

            using var uninstaller = StartDirectUninstallerAsync(
                Uninstaller, InstallDir, "");
            var removeDrive = WindowsDesktop.DriveWizard(
                () => WindowsDesktop.TopWindowsByProcess(uninstaller.Id),
                () => uninstaller.HasExited, TimeSpan.FromMinutes(3));
            Assert.True(removeDrive.Finished,
                $"卸载向导未走完（已点：{string.Join(" → ", removeDrive.Actions)}）");
            Assert.Contains(removeDrive.Actions, a => a.Contains("Uninstall"));
            Assert.Equal(0, uninstaller.ExitCode);
            WaitFor.Until(() => !File.Exists(Exe), "Interactive uninstall left the payload.", 30);
        }
        finally { Cleanup(); }
    }

    // Legacy MSI 迁移：ProductCode 与 UpgradeCode 两条精确标识路径。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void LegacyMsiProductCodeAndUpgradeCodeMigration()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallLegacyMsi(_f.LegacyMsiPath);
            Assert.True(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")));
            RunOk(Installer("bundle-legacy-msi-product-migration"), $"/S /D={InstallDir}");
            Assert.False(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "ProductCode migration did not remove the legacy MSI payload.");
            UninstallDefault();

            InstallLegacyMsi(_f.LegacyMsiPath);
            Assert.True(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")));
            RunOk(Installer("bundle-legacy-msi-upgrade-migration"), $"/S /D={InstallDir}");
            Assert.False(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "UpgradeCode migration did not remove the legacy MSI payload.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 自动检测三腿：匹配移除、名称不匹配保留、发布者不匹配保留。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void LegacyMsiAutoDetectRequiresNameAndPublisherMatch()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallLegacyMsi(_f.LegacyMsiPath);
            RunOk(Installer("bundle-legacy-msi-autodetect"), $"/S /D={InstallDir}");
            Assert.False(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "Auto-detect migration did not remove the legacy MSI payload.");
            UninstallDefault();

            InstallLegacyMsi(_f.LegacyMsiPath);
            RunOk(Installer("bundle-legacy-msi-name-mismatch", name: "Bundler Other Fixture"),
                $"/S /D={InstallDir}");
            Assert.True(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "Auto-detect removed an MSI whose product name did not match.");
            UninstallDefault();
            MsiexecRemoveLegacy();

            InstallLegacyMsi(_f.LegacyMsiPath);
            RunOk(Installer("bundle-legacy-msi-publisher-mismatch"), $"/S /D={InstallDir}");
            Assert.True(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "Auto-detect removed an MSI whose publisher did not match.");
            UninstallDefault();
            MsiexecRemoveLegacy();
        }
        finally { Cleanup(); }
    }

    // 同一 UpgradeCode 并存 0.9.0 与 0.8.0：0.8.5 探针被最高版本拦下（4），迁移移除两者。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void LegacyMsiMultiVersionUsesHighestVersionForDowngradeCheck()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallLegacyMsi(_f.LegacyMsiV2Path);
            InstallLegacyMsi(_f.LegacyMsiPath);
            Assert.True(File.Exists(Path.Combine(LegacyMsiV2InstallDir, "legacy-payload.txt")));
            var probe = Run(Installer("bundle-legacy-msi-downgrade-probe", "0.8.5"),
                $"/S /D={InstallDir}");
            Assert.Equal(4, probe.ExitCode);
            Assert.True(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "A blocked downgrade removed the legacy MSI.");
            RunOk(Installer("bundle-legacy-msi-upgrade-migration"), $"/S /D={InstallDir}");
            Assert.False(File.Exists(Path.Combine(LegacyMsiInstallDir, "legacy-payload.txt")),
                "Multi-version migration did not remove the 0.9.0 payload.");
            Assert.False(File.Exists(Path.Combine(LegacyMsiV2InstallDir, "legacy-payload.txt")),
                "Multi-version migration did not remove the 0.8.0 payload.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // NSIS→MSI 目录延续：MSI 读 NSIS InstallRoot；Bundler 自家 InstallDir 优先；
    // 范围外目录被忽略回落 MSI 默认。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void MsiContinuityReadsNsisInstallLocation()
    {
        _f.Ensure();
        RequireConsent();
        var continuityMsi = Path.Combine(_f.BundleDir("bundle-msi-continuity"),
            "win-x64", "msi", $"{ProductName}-1.0.0.msi");
        var productCode = MsiSupport.GetProperty(continuityMsi, "ProductCode");
        try
        {
            RunOk(Installer("bundle"), $"/S /D={NsisContinuityDir}");
            WaitFor.Until(() => File.Exists(Path.Combine(NsisContinuityDir, "Uninstall.exe")),
                "NSIS install for MSI continuity did not finish.");
            Msiexec($"/i \"{continuityMsi}\" /qn /norestart", 0, 3010);
            Assert.True(File.Exists(Path.Combine(NsisContinuityDir, "BundlerIntegrationFixture.exe")),
                "MSI did not continue into the previous NSIS install directory.");
            Msiexec($"/x {productCode} /qn /norestart", 0, 1605, 3010);
            RunOk(Path.Combine(NsisContinuityDir, "Uninstall.exe"), "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(NsisContinuityDir),
                "NSIS continuity directory cleanup did not finish.", 30);

            using (Registry.CurrentUser.CreateSubKey(BundlerDefinitionKey))
            { }
            Registry.CurrentUser.OpenSubKey(BundlerDefinitionKey, true)!
                .SetValue("InstallDir", MsiPrecedenceDir);
            Directory.CreateDirectory(MsiPrecedenceDir);
            RunOk(Installer("bundle"), $"/S /D={NsisContinuityDir}");
            WaitFor.Until(() => File.Exists(Path.Combine(NsisContinuityDir, "Uninstall.exe")),
                "NSIS reinstall for precedence test did not finish.");
            Msiexec($"/i \"{continuityMsi}\" /qn /norestart", 0, 3010);
            Assert.True(File.Exists(Path.Combine(MsiPrecedenceDir, "BundlerIntegrationFixture.exe")),
                "Bundler's own InstallDir registration did not take precedence over the NSIS InstallRoot.");
            Msiexec($"/x {productCode} /qn /norestart", 0, 1605, 3010);
            RunOk(Path.Combine(NsisContinuityDir, "Uninstall.exe"), "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(NsisContinuityDir),
                "Precedence test NSIS cleanup did not finish.", 30);
            Registry.CurrentUser.DeleteSubKeyTree(BundlerDefinitionKey, false);

            RunOk(Installer("bundle"), $"/S /D={NsisOutOfScopeDir}");
            WaitFor.Until(() => File.Exists(Path.Combine(NsisOutOfScopeDir, "Uninstall.exe")),
                "NSIS out-of-scope install did not finish.");
            Msiexec($"/i \"{continuityMsi}\" /qn /norestart", 0, 3010);
            Assert.True(File.Exists(Path.Combine(MsiContinuityDefaultDir,
                    "BundlerIntegrationFixture.exe")),
                "MSI used an out-of-scope NSIS directory instead of its own default.");
            Msiexec($"/x {productCode} /qn /norestart", 0, 1605, 3010);
            RunOk(Path.Combine(NsisOutOfScopeDir, "Uninstall.exe"), "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(NsisOutOfScopeDir),
                "Out-of-scope NSIS cleanup did not finish.", 30);
        }
        finally { Cleanup(); }
    }

    // 安装主断言腿：载荷/卸载器/外部资源/注册表/关联/能力/深链/快捷方式/钩子全覆盖。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void MainInstallLifecycleAssertions()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            Assert.True(File.Exists(Path.Combine(InstallDir, "docs", "license.txt")),
                "Configured external resource is missing.");
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(RegistryPath));
            Assert.Equal("Disposable Windows NSIS integration-test fixture.",
                RegGetRequired(RegistryPath, "Comments"));
            Assert.Equal("https://example.com/dotnet-bundler-fixture",
                RegGetRequired(RegistryPath, "URLInfoAbout"));
            Assert.Equal("", Registry.CurrentUser
                .OpenSubKey($@"{FileExtensionKey}\OpenWithProgids")?.GetValue(FileProgId));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(FileProgIdKey));
            Assert.Equal(ProductName, RegGetRequired(CapabilitiesKey, "ApplicationName"));
            Assert.Equal($"Software\\{Identifier}\\Capabilities",
                RegGetRequired(RegisteredAppsKey, Identifier));
            Assert.Equal($"\"{Exe}\" \"%1\"",
                Registry.CurrentUser.OpenSubKey($@"{UrlSchemeKey}\shell\open\command")
                    ?.GetValue(""));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(UrlProgIdKey));
            using (Process.Start(new ProcessStartInfo
                   { FileName = "bundlerfixture:integration-value", UseShellExecute = true }))
            { }
            WaitFor.Until(() => File.Exists(DeepLinkMarker),
                "Registered deep link did not launch the installed application.");
            Assert.Equal("bundlerfixture:integration-value",
                File.ReadAllText(DeepLinkMarker).Trim());
            Assert.True(File.Exists(DesktopShortcut), "Desktop shortcut is missing.");
            Assert.True(File.Exists(StartMenuShortcut), "Start Menu shortcut is missing.");
            var desktop = ShellLink.Read(DesktopShortcut);
            Assert.Equal(Exe, desktop.Destination);
            Assert.Equal("--shortcut-mode \"hello world\"", desktop.Arguments);
            Assert.Equal(Path.Combine(InstallDir, "docs"), desktop.WorkingDirectory);
            Assert.StartsWith(Exe, desktop.IconLocation, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("com.dotnetbundler.integrationfixture.desktop", desktop.AppUserModelId);
            var startMenu = ShellLink.Read(StartMenuShortcut);
            Assert.Equal(Exe, startMenu.Destination);
            Assert.Equal("com.dotnetbundler.integrationfixture.desktop", startMenu.AppUserModelId);
            Assert.True(File.Exists(Path.Combine(Temp, "DotNetBundler-preinstall.txt")),
                "Pre-install hook did not run.");
            Assert.True(File.Exists(Path.Combine(Temp, "DotNetBundler-postinstall.txt")),
                "Post-install hook did not run.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 重安装只关闭安装目录内的同名进程，不碰目录外同文件名进程。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void ReinstallClosesOwnedProcessOnly()
    {
        _f.Ensure();
        RequireConsent();
        Process? external = null;
        Process? owned = null;
        try
        {
            InstallDefault();
            var externalDir = _f.Ws.Combine("same-name-external-process");
            CopyDirectory(InstallDir, externalDir);
            external = ProcessRunner.StartDetached(
                Path.Combine(externalDir, "BundlerIntegrationFixture.exe"), "--wait");
            owned = ProcessRunner.StartDetached(Exe, "--wait");
            Thread.Sleep(500);
            Assert.False(owned.HasExited, "Fixture process did not remain running.");
            Assert.False(external.HasExited, "External fixture process did not remain running.");
            RunOk(Installer("bundle"), $"/S /D={InstallDir}");
            owned.Refresh();
            external.Refresh();
            Assert.True(owned.HasExited, "Reinstall did not close the running application.");
            Assert.False(external.HasExited,
                "Reinstall closed a same-name process outside the installation directory.");
            ProcessRunner.TryKillTree(external);
            external.WaitForExit();
            UninstallDefault();
        }
        finally
        {
            if (owned is { HasExited: false }) ProcessRunner.TryKillTree(owned);
            if (external is { HasExited: false }) ProcessRunner.TryKillTree(external);
            Cleanup();
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private void CreateJunction(string link, string target)
    {
        var mklink = ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"",
            new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(30) });
        ProcessRunner.AssertSuccess(mklink, "mklink /J");
    }

    // 安装快照与 journal 都拒绝重解析点；junction 失败不得跟随修改外部目录。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void JournalAndPayloadJunctionsAreRejected()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            Directory.CreateDirectory(ReparseOutsideDir);
            var sentinel = Path.Combine(ReparseOutsideDir, "sentinel.txt");
            File.WriteAllText(sentinel, "outside-owned");
            Directory.CreateDirectory(Path.GetDirectoryName(TransactionDir)!);
            CreateJunction(TransactionDir, ReparseOutsideDir);
            var journal = Run(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.Equal(2, journal.ExitCode);
            Assert.Equal("outside-owned", File.ReadAllText(sentinel).Trim());
            Directory.Delete(TransactionDir); // junction 本身，不递归外部

            var payloadJunction = Path.Combine(InstallDir, "linked-outside");
            CreateJunction(payloadJunction, ReparseOutsideDir);
            var payload = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal(2, payload.ExitCode);
            Assert.Equal("1.0.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.Equal("outside-owned", File.ReadAllText(sentinel).Trim());
            Assert.False(Directory.Exists(TransactionDir),
                "Reparse-point snapshot failure left a transaction journal.");
            Directory.Delete(payloadJunction);
            Directory.Delete(ReparseOutsideDir, true);
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // /UPDATE /R /ARGS：原位升级保留数据与删除快捷方式的选择、迁移旧快捷方式并刷新、
    // 成功后以桌面用户身份带参数启动应用。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void UpdatePreservesDataMigratesShortcutsAndForwardsArguments()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            var legacyExe = Path.Combine(InstallDir, "LegacyFixture.exe");
            File.Copy(Exe, legacyExe);
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyStartMenuShortcut)!);
            ShellLink.Write(LegacyStartMenuShortcut, legacyExe,
                Path.GetDirectoryName(legacyExe)!);
            File.Delete(StartMenuShortcut);
            File.Delete(DesktopShortcut);
            RunOk(Installer("bundle-upgrade", "1.1.0"),
                $"/UPDATE /R /ARGS=--protocol-marker \"hello world\" /D={InstallDir}");
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.True(File.Exists(preserved), "Upgrade removed runtime-created program data.");
            Assert.False(File.Exists(DesktopShortcut),
                "/UPDATE recreated a shortcut that the user had removed.");
            Assert.True(File.Exists(StartMenuShortcut),
                "/UPDATE did not migrate the legacy Start Menu shortcut.");
            Assert.False(File.Exists(LegacyStartMenuShortcut),
                "/UPDATE left the legacy shortcut behind.");
            var migrated = ShellLink.Read(StartMenuShortcut);
            Assert.Equal(Exe, migrated.Destination);
            Assert.Equal("--shortcut-mode \"hello world\"", migrated.Arguments);
            Assert.Equal("com.dotnetbundler.integrationfixture.desktop", migrated.AppUserModelId);
            WaitFor.Until(() => File.Exists(CommandLineMarker),
                "/R did not start the installed application.", 30);
            var forwarded = File.ReadAllLines(CommandLineMarker);
            Assert.Equal(2, forwarded.Length);
            Assert.Equal("--protocol-marker", forwarded[0]);
            Assert.Equal("hello world", forwarded[1]);
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 载荷文件被外部锁定时静默升级必须返回 2 + 保留 active journal + 不替换文件；
    // 释放锁后下一安装先恢复事务，随后按版本策略拦下旧安装器（4）。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void LockedPayloadFailsSafelyAndRecoveryContinuesToDowngradePolicy()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            var lockedPayload = Path.Combine(InstallDir, "docs", "license.txt");
            File.WriteAllText(lockedPayload, "locked-payload-sentinel");
            using (File.Open(lockedPayload, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var locked = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
                Assert.Equal(2, locked.ExitCode);
                Assert.True(Directory.Exists(TransactionDir),
                    "A failed locked-payload rollback did not preserve its active journal.");
                Assert.Equal("locked-payload-sentinel", File.ReadAllText(lockedPayload));
            }
            var recovery = Run(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.Equal(4, recovery.ExitCode);
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.Equal("locked-payload-sentinel", File.ReadAllText(lockedPayload));
            Assert.False(Directory.Exists(TransactionDir));
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 快捷方式/注册表持久化边界的注入失败：恢复相同的旧载荷、版本、快捷方式、journal。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void PersistenceFailuresRestorePriorState()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            // 与快照/激活失败腿同款前置：删掉桌面快捷方式，断言"用户已删"状态被回滚保留。
            File.Delete(DesktopShortcut);
            var hash = Sha256(Exe);
            var shortcut = ShellLink.Read(StartMenuShortcut);
            foreach (var bundle in new[] { "bundle-shortcut-persistence-failure",
                     "bundle-registry-persistence-failure" })
            {
                var failure = Run(Installer(bundle, "1.2.0"), $"/S /D={InstallDir}");
                Assert.Equal(2, failure.ExitCode);
                Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
                Assert.Equal(hash, Sha256(Exe));
                Assert.Equal(shortcut.Destination, ShellLink.Read(StartMenuShortcut).Destination);
                Assert.False(File.Exists(DesktopShortcut),
                    $"{bundle} recreated the removed desktop shortcut.");
                Assert.False(Directory.Exists(TransactionDir),
                    $"{bundle} left an active journal.");
            }
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 快照写入失败与激活失败都发生在修改旧状态之前：删除未激活 journal，不动旧态。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void TransactionSnapshotAndActivationFailuresLeavePriorState()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            File.Delete(DesktopShortcut);
            var hash = Sha256(Exe);
            foreach (var bundle in new[] { "bundle-transaction-snapshot-failure",
                     "bundle-transaction-activation-failure" })
            {
                var failure = Run(Installer(bundle, "1.2.0"), $"/S /D={InstallDir}");
                Assert.Equal(2, failure.ExitCode);
                Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
                Assert.Equal(hash, Sha256(Exe));
                Assert.True(File.Exists(preserved));
                Assert.True(File.Exists(StartMenuShortcut));
                Assert.False(File.Exists(DesktopShortcut));
                Assert.False(Directory.Exists(TransactionDir),
                    $"{bundle} left an inactive journal.");
            }
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 激活后三段恢复点失败：保留 active journal；下次启动重入完成恢复（回到 1.1.0）。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void RecoveryFailuresByPhaseKeepActiveJournalAndResume()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            var hash = Sha256(Exe);
            foreach (var (bundle, expectedVersion, payloadRestored) in new (string, string, bool)[]
            {
                ("bundle-payload-restore-failure", "1.2.0", false),
                ("bundle-registry-restore-failure", "1.2.0", true),
                ("bundle-journal-cleanup-failure", "1.1.0", true),
            })
            {
                var failure = Run(Installer(bundle, "1.2.0"), $"/S /D={InstallDir}");
                Assert.Equal(2, failure.ExitCode);
                Assert.True(Directory.Exists(TransactionDir),
                    $"{bundle} did not preserve its active journal.");
                Assert.Equal(expectedVersion, RegGetRequired(RegistryPath, "DisplayVersion"));
                if (payloadRestored)
                {
                    Assert.Equal(hash, Sha256(Exe));
                }
                else
                {
                    Assert.NotEqual(hash, Sha256(Exe));
                }
                RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
                Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
                Assert.Equal(hash, Sha256(Exe));
                Assert.True(File.Exists(preserved));
                Assert.False(Directory.Exists(TransactionDir));
            }
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 中断安装 + manifest 不匹配（6）+ RECOVERONLY + 第二次中断 + 四类篡改拒绝
    // （快照载荷/注册表内容、文件恢复目标、注册表恢复目标）+ 最终启动恢复。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void InterruptedInstallManifestMismatchAndTamperRejections()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            var hash = Sha256(Exe);

            using var interrupted = ProcessRunner.StartDetached(
                Installer("bundle-interrupted", "1.2.0"), $"/S /UPDATE /D={InstallDir}");
            WaitFor.Until(() => File.Exists(InterruptedHookMarker),
                "Interrupted-install fixture did not reach its post-install hook.", 120);
            ProcessRunner.TryKillTree(interrupted);
            interrupted.WaitForExit();
            Assert.NotEqual(0, interrupted.ExitCode);
            Assert.True(Directory.Exists(TransactionDir),
                "Interrupted install did not preserve its active journal.");
            var interruptedVersion = RegGetRequired(RegistryPath, "DisplayVersion");

            var mismatch = Run(Installer("bundle-different-manifest", "1.3.0"),
                $"/S /D={InstallDir}");
            Assert.Equal(6, mismatch.ExitCode);
            Assert.True(Directory.Exists(TransactionDir));
            Assert.Equal(interruptedVersion, RegGetRequired(RegistryPath, "DisplayVersion"));

            var recoverOnly = Run(Installer("bundle-interrupted", "1.2.0"),
                $"/S /RECOVERONLY /D={InstallDir}");
            Assert.Equal(0, recoverOnly.ExitCode);
            Assert.False(Directory.Exists(TransactionDir),
                "Recovery-only mode did not clean the original active journal.");
            Assert.Equal(hash, Sha256(Exe));
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));

            if (File.Exists(InterruptedHookMarker)) File.Delete(InterruptedHookMarker);
            using var interrupted2 = ProcessRunner.StartDetached(
                Installer("bundle-interrupted", "1.2.0"), $"/S /UPDATE /D={InstallDir}");
            WaitFor.Until(() => File.Exists(InterruptedHookMarker),
                "Second interrupted-install fixture did not reach its post-install hook.", 120);
            ProcessRunner.TryKillTree(interrupted2);
            interrupted2.WaitForExit();
            Assert.True(Directory.Exists(TransactionDir));

            var payloadSnapshot = Path.Combine(TransactionDir, "payload", "docs", "license.txt");
            Assert.True(File.Exists(payloadSnapshot),
                "Interrupted journal did not contain the expected payload snapshot.");
            var originalPayload = File.ReadAllBytes(payloadSnapshot);
            File.WriteAllText(payloadSnapshot, "modified-snapshot-content");
            var modifiedPayload = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal(2, modifiedPayload.ExitCode);
            Assert.True(Directory.Exists(TransactionDir));
            File.WriteAllBytes(payloadSnapshot, originalPayload);

            var registrySnapshot = Path.Combine(TransactionDir, "registry", "key-000.bin");
            Assert.True(File.Exists(registrySnapshot),
                "Interrupted journal did not contain a registry snapshot.");
            var originalRegistry = File.ReadAllBytes(registrySnapshot);
            File.AppendAllText(registrySnapshot, "modified-snapshot-content");
            var modifiedRegistry = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal(2, modifiedRegistry.ExitCode);
            Assert.True(Directory.Exists(TransactionDir));
            File.WriteAllBytes(registrySnapshot, originalRegistry);

            // journal 只存快照不授权恢复目标：篡改 files/path.txt 与 registry key 子键被拒（6）。
            File.WriteAllText(JournalTamperFile, "outside-file-owned");
            var fileSnapshot = Directory.EnumerateDirectories(
                Path.Combine(TransactionDir, "files")).FirstOrDefault();
            Assert.NotNull(fileSnapshot);
            var pathFile = Path.Combine(fileSnapshot, "path.txt");
            var originalPath = File.ReadAllText(pathFile);
            File.WriteAllText(pathFile, JournalTamperFile);
            var tamperedFile = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal(6, tamperedFile.ExitCode);
            Assert.Equal("outside-file-owned", File.ReadAllText(JournalTamperFile));
            Assert.True(Directory.Exists(TransactionDir));
            File.WriteAllText(pathFile, originalPath);

            using (var k = Registry.CurrentUser.CreateSubKey(JournalTamperKey))
            {
                k!.SetValue("Sentinel", "outside-registry-owned");
            }
            var regSnapshot = Directory.EnumerateFiles(
                Path.Combine(TransactionDir, "registry"), "key-*.bin").FirstOrDefault();
            Assert.NotNull(regSnapshot);
            var originalRegBytes = File.ReadAllBytes(regSnapshot);
            RewriteRegistrySnapshotSubKey(regSnapshot,
                @"Software\DotNetBundler\JournalTamperSentinel");
            var tamperedRegistry = Run(Installer("bundle-upgrade", "1.1.0"),
                $"/S /D={InstallDir}");
            Assert.Equal(6, tamperedRegistry.ExitCode);
            Assert.Equal("outside-registry-owned",
                RegGetRequired(JournalTamperKey, "Sentinel"));
            Assert.True(Directory.Exists(TransactionDir));
            File.WriteAllBytes(regSnapshot, originalRegBytes);
            Registry.CurrentUser.DeleteSubKeyTree(JournalTamperKey, false);

            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.Equal(hash, Sha256(Exe));
            Assert.True(File.Exists(preserved));
            Assert.False(Directory.Exists(TransactionDir));
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // registry key-*.bin 的快照子键字段原位改写（与脚本 Set-RegistrySnapshotSubKey 同布局）。
    private static void RewriteRegistrySnapshotSubKey(string path, string subKey)
    {
        var bytes = File.ReadAllBytes(path);
        using var input = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(input);
        var kind = reader.ReadByte();
        var root = reader.ReadString();
        var view = reader.ReadInt32();
        _ = reader.ReadString();
        var tail = reader.ReadBytes((int)(input.Length - input.Position));
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output))
        {
            writer.Write(kind);
            writer.Write(root);
            writer.Write(view);
            writer.Write(subKey);
            writer.Write(tail);
            writer.Flush();
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    // post-install 失败：恢复 1.1.0 全量可观察态并清理 journal。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void PostInstallFailureRollsBackCompletely()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            File.Delete(DesktopShortcut);
            var hash = Sha256(Exe);
            var shortcut = ShellLink.Read(StartMenuShortcut);
            var rollback = Run(Installer("bundle-rollback-failure", "1.2.0"),
                $"/S /D={InstallDir}");
            Assert.Equal(2, rollback.ExitCode);
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.Equal(hash, Sha256(Exe));
            Assert.True(File.Exists(preserved));
            Assert.Equal(shortcut.Destination, ShellLink.Read(StartMenuShortcut).Destination);
            Assert.False(Directory.Exists(TransactionDir),
                "Committed rollback journal was not cleaned up.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // commit 先原子改名再尽力清理：清理失败不判败、.committed 残留、下次启动只清理不回滚。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void CommitCleanupFailureKeepsCommittedState()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            var commit = Run(Installer("bundle-commit-cleanup-failure", "1.2.0"),
                $"/S /D={InstallDir}");
            Assert.Equal(0, commit.ExitCode);
            Assert.Equal("1.2.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.True(Directory.Exists(CommittedTransactionDir),
                "Commit cleanup failure did not preserve the committed journal for later cleanup.");
            Assert.False(Directory.Exists(TransactionDir));
            Assert.True(File.Exists(preserved));
            var recovery = Run(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            Assert.Equal(4, recovery.ExitCode);
            Assert.False(Directory.Exists(CommittedTransactionDir),
                "The next installer start did not clean the committed journal.");
            Assert.Equal("1.2.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 默认拦降级（4）→ 明确允许的降级走卸载-替换并保留运行时数据。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void DowngradeBlockedThenAllowedPreservesRuntimeData()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            RunOk(Installer("bundle-upgrade", "1.1.0"), $"/S /D={InstallDir}");
            var preserved = Path.Combine(InstallDir, "upgrade-preserved.db");
            File.WriteAllText(preserved, "preserve");
            var blocked = Run(Installer("bundle"), $"/S /D={InstallDir}");
            Assert.Equal(4, blocked.ExitCode);
            Assert.Equal("1.1.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.True(File.Exists(Exe), "Blocked downgrade removed the installed app.");
            RunOk(Installer("bundle-allowed-downgrade"), $"/S /D={InstallDir}");
            Assert.Equal("1.0.0", RegGetRequired(RegistryPath, "DisplayVersion"));
            Assert.True(File.Exists(preserved),
                "Allowed downgrade removed runtime-created program data.");
            UninstallDefault();
        }
        finally { Cleanup(); }
    }

    // 默认卸载：拒绝载荷 junction（2 且不跟随）、保留运行时数据与 appdata、清注册表与快捷方式。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void UninstallPreservesRuntimeDataAndRejectsPayloadJunction()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            var runtimeData = Path.Combine(InstallDir, "runtime-created.db");
            File.WriteAllText(runtimeData, "preserve");
            Directory.CreateDirectory(RoamingData);
            Directory.CreateDirectory(LocalData);
            File.WriteAllText(Path.Combine(RoamingData, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(LocalData, "cache.bin"), "cache");
            Directory.CreateDirectory(ReparseOutsideDir);
            var sentinel = Path.Combine(ReparseOutsideDir, "uninstall-sentinel.txt");
            File.WriteAllText(sentinel, "outside-owned");
            var payloadJunction = Path.Combine(InstallDir, "uninstall-linked-outside");
            CreateJunction(payloadJunction, ReparseOutsideDir);
            var unsafeUninstall = DirectUninstall(Uninstaller, InstallDir, "/S");
            Assert.Equal(2, unsafeUninstall.ExitCode);
            Assert.True(File.Exists(Exe), "Rejected uninstall modified the installed payload.");
            Assert.Equal("outside-owned", File.ReadAllText(sentinel).Trim());
            Assert.False(Directory.Exists(UninstallTransactionDir));
            Directory.Delete(payloadJunction);
            RunOk(Uninstaller, "/S");
            WaitFor.Until(() => !File.Exists(Exe), "Packaged executable survived uninstall.", 30);
            Assert.True(File.Exists(runtimeData),
                "Default uninstall should preserve runtime-created program data.");
            Assert.False(File.Exists(Path.Combine(InstallDir, "docs", "license.txt")));
            Assert.True(Directory.Exists(RoamingData),
                "Default uninstall removed roaming application data.");
            Assert.True(Directory.Exists(LocalData));
            Assert.Null(Registry.CurrentUser.OpenSubKey(RegistryPath));
            Assert.False(File.Exists(DesktopShortcut));
            Assert.False(File.Exists(StartMenuShortcut));
            Assert.Null(Registry.CurrentUser.OpenSubKey(FileProgIdKey));
            Assert.Null(Registry.CurrentUser
                .OpenSubKey($@"{FileExtensionKey}\OpenWithProgids")?.GetValue(FileProgId));
            Assert.Null(Registry.CurrentUser.OpenSubKey(UrlSchemeKey));
            Assert.Null(Registry.CurrentUser.OpenSubKey(UrlProgIdKey));
            Assert.Null(Registry.CurrentUser.OpenSubKey(CapabilitiesKey));
            Assert.True(File.Exists(Path.Combine(Temp, "DotNetBundler-preuninstall.txt")),
                "Pre-uninstall hook did not run.");
            Assert.True(File.Exists(Path.Combine(Temp, "DotNetBundler-postuninstall.txt")),
                "Post-uninstall hook did not run.");
        }
        finally { Cleanup(); }
    }

    // /DELETEAPPDATA：拒绝 appdata junction（2）、外属协议与同文件名快捷方式不删、
    // 目录/roaming/local 全清。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void DeleteAppDataRejectsJunctionAndPreservesForeignOwnership()
    {
        _f.Ensure();
        RequireConsent();
        try
        {
            InstallDefault();
            File.WriteAllText(Path.Combine(InstallDir, "runtime-created.db"), "delete");
            Directory.CreateDirectory(RoamingData);
            Directory.CreateDirectory(LocalData);
            File.WriteAllText(Path.Combine(RoamingData, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(LocalData, "cache.bin"), "cache");
            Directory.CreateDirectory(ReparseOutsideDir);
            var sentinel = Path.Combine(ReparseOutsideDir, "uninstall-sentinel.txt");
            File.WriteAllText(sentinel, "outside-owned");
            var appDataJunction = Path.Combine(RoamingData, "linked-outside");
            CreateJunction(appDataJunction, ReparseOutsideDir);
            var unsafeDelete = DirectUninstall(Uninstaller, InstallDir, "/S /DELETEAPPDATA");
            Assert.Equal(2, unsafeDelete.ExitCode);
            Assert.True(File.Exists(Exe), "Rejected DELETEAPPDATA modified the installation.");
            Assert.Equal("outside-owned", File.ReadAllText(sentinel).Trim());
            Assert.False(Directory.Exists(UninstallTransactionDir));
            Directory.Delete(appDataJunction);
            // 外属接管：协议所有者换成别的应用，两个同文件名快捷方式改指 notepad。
            using (var command = Registry.CurrentUser.OpenSubKey(
                       $@"{UrlSchemeKey}\shell\open\command", true))
            {
                Assert.NotNull(command);
                command!.SetValue("", "\"C:\\OtherApp\\Other.exe\" \"%1\"");
            }
            var foreignTarget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            ShellLink.Write(DesktopShortcut, foreignTarget,
                Path.GetDirectoryName(foreignTarget)!);
            Directory.CreateDirectory(StartMenuDir);
            ShellLink.Write(StartMenuShortcut, foreignTarget,
                Path.GetDirectoryName(foreignTarget)!);
            RunOk(Uninstaller, "/S /DELETEAPPDATA");
            WaitFor.Until(() => !Directory.Exists(InstallDir),
                "DELETEAPPDATA did not remove the complete program directory.", 30);
            Assert.False(Directory.Exists(RoamingData));
            Assert.False(Directory.Exists(LocalData));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(UrlSchemeKey));
            Assert.True(File.Exists(DesktopShortcut),
                "Uninstall removed a same-name desktop shortcut owned by another application.");
            Assert.True(File.Exists(StartMenuShortcut));
            Assert.Equal(foreignTarget, ShellLink.Read(DesktopShortcut).Destination,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(foreignTarget, ShellLink.Read(StartMenuShortcut).Destination,
                StringComparer.OrdinalIgnoreCase);
        }
        finally { Cleanup(); }
    }

}

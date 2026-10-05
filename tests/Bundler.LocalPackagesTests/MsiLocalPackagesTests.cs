// 本地包消费契约腿：fixture 经 Bundler.LocalPackages.props 从仓根 artifacts/packages 本地源
// 消费 DotNet.Bundler.* nupkg（不引用项目），验证打包产物在真实消费方构建中可用。
// 门控：Windows + BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1（对应原脚本的 -ConfirmLocalInstall
// 人工同意闸）——会在真实用户配置里装卸 MSI，未经同意一律 Skip。
// 对外仍走 msiexec/真实安装；MSI 数据库读取改走 WixToolset.Dtf（替代原 WindowsInstaller.Installer COM）。
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MsiLocalPackagesFixture : IAsyncLifetime
{
    public IntegrationWorkspace Ws { get; } = IntegrationWorkspace.Create("windows-msi-integration", "windows.msi");
    public string Packages { get; }
    public string Cache { get; }
    public string FixtureSource { get; }
    public string StandaloneSource { get; }
    public string Version { get; private set; } = "";

    private readonly Lazy<bool> _init;

    public MsiLocalPackagesFixture()
    {
        Packages = Ws.Combine("packages");
        Cache = Ws.Combine("nuget");
        var scriptDir = Path.Combine(RepositoryLayout.FixturesDirectory, "Msi");
        FixtureSource = Path.Combine(scriptDir, "Fixture");
        StandaloneSource = Path.Combine(scriptDir, "Standalone");
        _init = new Lazy<bool>(Initialize);
        _packOnly = new Lazy<bool>(InitializePackagesOnly);
    }

    private bool Initialize()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "MSI 集成腿只覆盖 Windows。");
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL") != "1",
            "真装会写真实用户配置与注册表；置 BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL=1 才跑（对应脚本 -ConfirmLocalInstall）。");
        Assert.SkipWhen(!File.Exists(Path.Combine(FixtureSource, "BundlerMsiSmoke.csproj")),
            "MSI smoke fixture 目录缺失。");
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Cache);
        Version = MsiSupport.PackPackages(Packages);
        return true;
    }

    public bool Ensure() => _init.Value;

    private readonly Lazy<bool> _packOnly;

    // 仅需 packages 的腿（如 PublicSample 表级契约，不真装）：免本地安装同意闸。
    public bool EnsurePackages() => _packOnly.Value;

    private bool InitializePackagesOnly()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "MSI 表级断言依赖 DTF 读取 msi，只在 Windows 跑。");
        Assert.SkipWhen(!File.Exists(Path.Combine(FixtureSource, "BundlerMsiSmoke.csproj")),
            "MSI smoke fixture 目录缺失。");
        // PublicSample 消费仓根 artifacts/packages；RepositoryPackages 已含
        // build-server shutdown + build + pack。
        Version = RepositoryLayout.PackageVersion;
        _ = RepositoryPackages.DirectoryPath;
        return true;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Ws.Dispose();
        return ValueTask.CompletedTask;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MsiLocalPackagesTests(MsiLocalPackagesFixture fixture) : IClassFixture<MsiLocalPackagesFixture>
{
    private readonly MsiLocalPackagesFixture _f = fixture;
    private const string MsiName = "Bundler MSI Smoke";

    private string NewIdentifier(string leg)
        => $"com.example.bundler.msi.{leg}.{Guid.NewGuid():N}";

    // 对应 dotnet publish --no-restore + Bundler 属性串（fixture 已还原）。
    private string Publish(string project, string output, string identifier,
        string version, string rid = "win-x64", params string[] extra)
    {
        var args = new List<string>
        {
            "publish", project, "-c", "Release", "--no-restore",
            $"-p:BundlerPackageSource={_f.Packages}", $"-p:RestorePackagesPath={_f.Cache}",
            $"-p:BundlerPackageVersion={_f.Version}", $"-p:RuntimeIdentifier={rid}",
            $"-p:BundlerOutputPath={output}", $"-p:BundlerIdentifier={identifier}",
            $"-p:BundlerVersion={version}",
        };
        args.AddRange(extra);
        ProcessRunner.AssertSuccess(
            Dotnet.Run(args, new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) }),
            $"MSI fixture publish failed: {project}");
        return Path.Combine(output, rid, "msi");
    }

    private void MsiexecLogged(string name, string root, string[] arguments, params int[] allowed)
    {
        var log = Path.Combine(root, name + ".log");
        var args = arguments.Concat(["/qn", "/norestart", "/L*v", $"\"{log}\""]).ToArray();
        var result = MsiSupport.Msiexec(string.Join(" ", args));
        Assert.True(allowed.Contains(result.ExitCode),
            $"msiexec {name} returned {result.ExitCode}, expected [{string.Join(", ", allowed)}]; log: {log}");
    }

    private static bool ProductInstalled(string productCode)
        => ProductInstallation.GetProducts(productCode, null, UserContexts.All).Any();

    private static void AssertInstalled(string productCode, bool expected)
        => Assert.Equal(expected, ProductInstalled(productCode));

    private void CleanupProducts(params string?[] codes)
    {
        foreach (var code in codes.Where(c => !string.IsNullOrEmpty(c)))
        {
            try
            {
                if (ProductInstalled(code!))
                {
                    MsiSupport.Msiexec($"/x {code} /qn /norestart");
                }
            }
            catch { /* 清场失败由后续断言如实报错 */ }
        }
    }

    // Verify.ps1：产 → 真装 → ProductState/名称/版本 → 未知用户文件 → 卸 → 文件保留。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void SmokeInstallUninstallPreservesUserData()
    {
        _f.Ensure();
        var id = NewIdentifier("smoke");
        var root = _f.Ws.Combine("smoke");
        var project = MsiSupport.CopyFixture(Path.Combine(root, "fixture"), _f.FixtureSource);
        MsiSupport.RestoreFixture(project, _f.Packages, _f.Cache, _f.Version);
        var msiDir = Publish(project, Path.Combine(root, "output"), id, "1.0.0");
        var msi = Path.Combine(msiDir, $"{MsiName}-1.0.0.msi");
        Assert.True(File.Exists(msi), $"MSI was not produced: {msi}");

        var productCode = MsiSupport.GetProperty(msi, "ProductCode");
        AssertInstalled(productCode, false);
        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        var unknown = Path.Combine(install, "user-created.txt");
        var createdUnknown = false;
        try
        {
            MsiexecLogged("install", root, ["/i", $"\"{msi}\""], 0);
            AssertInstalled(productCode, true);
            var info = new ProductInstallation(productCode);
            Assert.Equal("Bundler MSI Smoke", info.ProductName);
            Assert.Equal("1.0.0", info.ProductVersion?.ToString());
            File.WriteAllText(unknown, "preserve user data");
            createdUnknown = true;
            MsiexecLogged("uninstall", root, ["/x", productCode], 0);
            AssertInstalled(productCode, false);
            Assert.True(File.Exists(unknown), "Uninstall removed unknown user data.");
            Assert.False(File.Exists(Path.Combine(install, "BundlerMsiSmoke.exe")),
                "Uninstall left the managed executable.");
        }
        finally
        {
            CleanupProducts(productCode);
            if (createdUnknown && File.Exists(unknown)) File.Delete(unknown);
            if (Directory.Exists(install) && !Directory.EnumerateFileSystemEntries(install).Any())
                Directory.Delete(install);
        }
    }

    // VerifyLifecycle.ps1：major upgrade / same-version 变体 1638 / 降级 1603 / 快捷方式与注册全生命周期。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void MajorUpgradeVariantCollisionAndDowngradeRejected()
    {
        _f.Ensure();
        var id = NewIdentifier("lifecycle");
        var extension = $"bmsi{Guid.NewGuid():N}";
        var scheme = $"bmsi-{Guid.NewGuid():N}";
        var root = _f.Ws.Combine("lifecycle");
        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        var progId = $"{id}.win-x64.file.{extension}";
        var urlProgId = $"{id}.win-x64.url.{scheme}";
        var capabilities = $@"Software\DotNetBundler\Products\{id}\win-x64\Capabilities";
        var fileKey = $@"Software\Classes\." + extension;
        var schemeKey = $@"Software\Classes\{scheme}";
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", id, "Bundler MSI Smoke.lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"Bundler MSI Smoke ({id}).lnk");
        var unknown = Path.Combine(install, "user-created.txt");
        var extra = new[]
        {
            "-p:MsiLifecycleTest=true",
            $"-p:BundlerTestExtension={extension}",
            $"-p:BundlerTestScheme={scheme}",
            "-p:BundlerWixStartMenuShortcut=true",
            "-p:BundlerWixDesktopShortcut=true",
        };

        foreach (var probe in new[] { install, fileKey, schemeKey, startMenu, desktop })
        {
            var exists = probe.Contains('\\') && probe.StartsWith("Software", StringComparison.Ordinal)
                ? Registry.CurrentUser.OpenSubKey(probe) is not null
                : File.Exists(probe) || Directory.Exists(probe);
            Assert.False(exists, $"Test state already exists: {probe}");
        }

        using var fileKeyReg = Registry.CurrentUser.CreateSubKey(fileKey);
        fileKeyReg!.SetValue("", "Other.Test.Owner");
        using var schemeKeyReg = Registry.CurrentUser.CreateSubKey(schemeKey);
        schemeKeyReg!.SetValue("", "Other URL owner");
        var codes = new List<string>();
        var unknownCreated = false;
        try
        {
            var project1 = MsiSupport.CopyFixture(Path.Combine(root, "fixture-1.0.0"), _f.FixtureSource);
            MsiSupport.RestoreFixture(project1, _f.Packages, _f.Cache, _f.Version);
            var v1 = Path.Combine(Publish(project1, Path.Combine(root, "output-1.0.0"), id, "1.0.0",
                    "win-x64", extra), $"{MsiName}-1.0.0.msi");
            var project2 = MsiSupport.CopyFixture(Path.Combine(root, "fixture-1.1.0"), _f.FixtureSource);
            MsiSupport.RestoreFixture(project2, _f.Packages, _f.Cache, _f.Version);
            var v2 = Path.Combine(Publish(project2, Path.Combine(root, "output-1.1.0"), id, "1.1.0",
                    "win-x64", extra), $"{MsiName}-1.1.0.msi");
            var projectV = MsiSupport.CopyFixture(Path.Combine(root, "fixture-variant"), _f.FixtureSource);
            MsiSupport.RestoreFixture(projectV, _f.Packages, _f.Cache, _f.Version);
            var variant = Path.Combine(Publish(projectV, Path.Combine(root, "output-variant"), id,
                    "1.1.0", "win-x64", [.. extra, "-p:MsiVariantTest=true"]), $"{MsiName}-1.1.0.msi");
            foreach (var path in new[] { v1, v2, variant })
            {
                Assert.True(File.Exists(path), $"Missing MSI: {path}");
            }
            var code1 = MsiSupport.GetProperty(v1, "ProductCode");
            var code2 = MsiSupport.GetProperty(v2, "ProductCode");
            var variantCode = MsiSupport.GetProperty(variant, "ProductCode");
            Assert.NotEqual(code1, code2);
            Assert.Equal(code2, variantCode);
            codes.AddRange([code1, code2]);

            void AssertRegistrations()
            {
                var expectedCommand = $"\"{Path.Combine(install, "BundlerMsiSmoke.exe")}\" \"%1\"";
                Assert.Equal(expectedCommand,
                    Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell\open\command")?.GetValue(""));
                Assert.Equal(expectedCommand,
                    Registry.CurrentUser.OpenSubKey($@"Software\Classes\{urlProgId}\shell\open\command")?.GetValue(""));
                Assert.Equal("",
                    Registry.CurrentUser.OpenSubKey($@"{fileKey}\OpenWithProgids")?.GetValue(progId));
                Assert.Equal(progId,
                    Registry.CurrentUser.OpenSubKey($@"{capabilities}\FileAssociations")?.GetValue($".{extension}"));
                Assert.Equal(progId,
                    Registry.CurrentUser.OpenSubKey($@"{capabilities}\MIMEAssociations")
                        ?.GetValue("application/x-bundler-msi-lifecycle"));
                Assert.Equal(urlProgId,
                    Registry.CurrentUser.OpenSubKey($@"{capabilities}\UrlAssociations")?.GetValue(scheme));
                Assert.Equal($@"Software\DotNetBundler\Products\{id}\win-x64\Capabilities",
                    Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications")
                        ?.GetValue($"{id}.win-x64"));
                Assert.Equal("Other.Test.Owner",
                    Registry.CurrentUser.OpenSubKey(fileKey)?.GetValue(""));
                Assert.Equal("Other URL owner",
                    Registry.CurrentUser.OpenSubKey(schemeKey)?.GetValue(""));
            }

            void AssertShortcut(string path)
            {
                Assert.True(File.Exists(path), $"Shortcut is missing: {path}");
                Assert.Equal(Path.Combine(install, "BundlerMsiSmoke.exe"),
                    ShellLink.Read(path).TargetPath);
            }

            MsiexecLogged("install-v1", root, ["/i", $"\"{v1}\""], 0);
            AssertInstalled(code1, true);
            AssertShortcut(startMenu);
            AssertShortcut(desktop);
            AssertRegistrations();
            Assert.True(File.Exists(Path.Combine(install, "docs", "v1-only.txt")),
                "v1 payload is missing.");
            File.WriteAllText(unknown, "preserve user data");
            unknownCreated = true;

            MsiexecLogged("upgrade-v2", root, ["/i", $"\"{v2}\""], 0);
            AssertInstalled(code1, false);
            AssertInstalled(code2, true);
            Assert.False(File.Exists(Path.Combine(install, "docs", "v1-only.txt")),
                "Upgrade retained the old managed payload.");
            Assert.True(File.Exists(Path.Combine(install, "docs", "v2-only.txt")),
                "Upgrade lost the new managed payload.");
            Assert.True(File.Exists(unknown), "Upgrade removed unknown user data.");
            AssertShortcut(startMenu);
            AssertShortcut(desktop);
            AssertRegistrations();

            MsiexecLogged("same-version-variant", root, ["/i", $"\"{variant}\""], 1638);
            AssertInstalled(code2, true);
            Assert.False(File.Exists(Path.Combine(install, "docs", "variant-only.txt")),
                "Rejected variant installed its payload.");
            AssertRegistrations();

            MsiexecLogged("downgrade", root, ["/i", $"\"{v1}\""], 1603);
            Assert.Contains("A newer version of Bundler MSI Smoke is already installed",
                File.ReadAllText(Path.Combine(root, "downgrade.log")));
            AssertInstalled(code1, false);
            AssertInstalled(code2, true);
            AssertRegistrations();

            MsiexecLogged("uninstall-v2", root, ["/x", code2], 0);
            AssertInstalled(code2, false);
            Assert.False(File.Exists(startMenu), "Uninstall left Start Menu shortcut.");
            Assert.False(File.Exists(desktop), "Uninstall left desktop shortcut.");
            Assert.Null(Registry.CurrentUser.OpenSubKey(capabilities));
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}"));
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\{urlProgId}"));
            Assert.True(File.Exists(unknown), "Uninstall removed unknown user data.");
            Assert.False(File.Exists(Path.Combine(install, "BundlerMsiSmoke.exe")),
                "Uninstall left the managed executable.");
            Assert.False(File.Exists(Path.Combine(install, "docs", "v2-only.txt")),
                "Uninstall left managed v2 payload.");
        }
        finally
        {
            CleanupProducts([.. codes]);
            if (unknownCreated && File.Exists(unknown)) File.Delete(unknown);
            if (Directory.Exists(install) && !Directory.EnumerateFileSystemEntries(install).Any())
                Directory.Delete(install);
            Registry.CurrentUser.DeleteSubKeyTree(fileKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(schemeKey, false);
        }
    }

    // VerifyMaintenance.ps1：损坏 MSI 拒绝、注入失败回滚、passive 装卸、静默修复、zh-CN 独立身份。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void DamageInjectionRepairAndLocalizedIdentity()
    {
        _f.Ensure();
        var id = NewIdentifier("maintenance");
        var root = _f.Ws.Combine("maintenance");
        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        var chineseInstall = install + "-zh-cn";
        var executable = Path.Combine(install, "BundlerMsiSmoke.exe");
        var resource = Path.Combine(install, "docs", "marker.txt");
        var unknown = Path.Combine(install, "user-created.txt");

        var project = MsiSupport.CopyFixture(Path.Combine(root, "fixture"), _f.FixtureSource);
        MsiSupport.RestoreFixture(project, _f.Packages, _f.Cache, _f.Version);
        var msi = Path.Combine(Publish(project, Path.Combine(root, "output"), id, "1.0.0"),
            $"{MsiName}-1.0.0.msi");
        Assert.True(File.Exists(msi));
        var productCode = MsiSupport.GetProperty(msi, "ProductCode");
        AssertInstalled(productCode, false);
        var unknownCreated = false;
        string? chineseCode = null;
        try
        {
            // 损坏 MSI：1619/1620 且不留产品态。
            var damaged = Path.Combine(root, "damaged.msi");
            File.WriteAllText(damaged, "invalid MSI test fixture");
            MsiexecLogged("damaged", root, ["/i", $"\"{damaged}\""], 1619, 1620);
            AssertInstalled(productCode, false);
            Assert.False(Directory.Exists(install));

            // 注入 InstallFiles 之后必然失败的延迟自定义动作 → 1603 全量回滚。
            var faultMsi = Path.Combine(root, "injected-failure.msi");
            File.Copy(msi, faultMsi);
            using (var database = MsiSupport.OpenWritable(faultMsi))
            {
                using var seqView = database.OpenView(
                    "SELECT Sequence FROM InstallExecuteSequence WHERE Action = 'InstallFiles'");
                seqView.Execute();
                var seqRecord = seqView.Fetch();
                Assert.NotNull(seqRecord);
                var failureSequence = seqRecord!.GetInteger(1) + 1;
                using var tableView = database.OpenView(
                    "SELECT `Name` FROM `_Tables` WHERE `Name` = 'CustomAction'");
                tableView.Execute();
                var hasCustomAction = tableView.Fetch() is not null;
                if (!hasCustomAction)
                {
                    database.Execute(
                        "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(64), `Target` CHAR(255) LOCALIZABLE PRIMARY KEY `Action`)");
                }
                database.Execute(
                    "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES ('BundlerTestFail', 1058, 'TARGETDIR', '[SystemFolder]cmd.exe /c exit /b 17')");
                database.Execute(
                    $"INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES ('BundlerTestFail', 'NOT Installed', {failureSequence})");
                database.Commit();
            }
            MsiexecLogged("injected-failure", root, ["/i", $"\"{faultMsi}\""], 1603);
            var failLog = File.ReadAllText(Path.Combine(root, "injected-failure.log"));
            Assert.Contains("BundlerTestFail", failLog);
            Assert.Contains("Executing op: FileCopy", failLog);
            AssertInstalled(productCode, false);
            Assert.False(File.Exists(executable));
            Assert.False(File.Exists(resource));

            // passive 安装 + 静默修复 /fomus + passive 卸载，未知用户文件全程保留。
            MsiexecLogged("passive-install", root, ["/i", $"\"{msi}\""], 0);
            AssertInstalled(productCode, true);
            Assert.True(File.Exists(executable));
            Assert.True(File.Exists(resource));
            File.WriteAllText(unknown, "preserve user data");
            unknownCreated = true;
            File.Delete(resource);
            MsiexecLogged("quiet-repair", root, ["/fomus", $"\"{msi}\""], 0);
            Assert.True(File.Exists(resource), "Quiet repair did not restore the removed file.");
            Assert.True(File.Exists(unknown));
            AssertInstalled(productCode, true);

            var license = Path.Combine(root, "application-license.rtf");
            File.WriteAllText(license, "{\\rtf1\\ansi Test-only application license.}");
            var zhMsiDir = Publish(project, Path.Combine(root, "output-zh"), id, "1.0.0",
                extra: [$"-p:BundlerWixLanguage=zh-CN", $"-p:BundlerLicenseFile={license}"]);
            var chineseMsi = Path.Combine(zhMsiDir, $"{MsiName}-1.0.0-zh-cn.msi");
            Assert.True(File.Exists(chineseMsi));
            Assert.False(Directory.Exists(chineseInstall));
            chineseCode = MsiSupport.GetProperty(chineseMsi, "ProductCode");
            Assert.Equal("2052", MsiSupport.GetProperty(chineseMsi, "ProductLanguage"));
            Assert.NotEqual(productCode, chineseCode);
            MsiexecLogged("chinese-install", root, ["/i", $"\"{chineseMsi}\""], 0);
            AssertInstalled(chineseCode, true);
            Assert.True(File.Exists(Path.Combine(chineseInstall, "BundlerMsiSmoke.exe")));
            AssertInstalled(productCode, true);
            MsiexecLogged("chinese-uninstall", root, ["/x", chineseCode], 0);
            AssertInstalled(chineseCode, false);
            Assert.False(Directory.Exists(chineseInstall));
            AssertInstalled(productCode, true);

            MsiexecLogged("passive-uninstall", root, ["/x", productCode], 0);
            AssertInstalled(productCode, false);
            Assert.False(File.Exists(executable));
            Assert.False(File.Exists(resource));
            Assert.True(File.Exists(unknown), "Uninstall removed unknown user data.");
        }
        finally
        {
            CleanupProducts(productCode, chineseCode);
            if (unknownCreated && File.Exists(unknown)) File.Delete(unknown);
            foreach (var dir in new[] { install, chineseInstall })
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
    }

    // 仓外独立 API fixture（Standalone/Msi.Api.PackageFixture）的复制+还原+运行，
    // 对应各脚本里的 api-fixture 腿：它按 nupkg 契约消费，是全链路的关键验收。
    private string RunApiFixture(string root, params string[] appArgs)
    {
        var dir = Path.Combine(root, "api-fixture");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(_f.StandaloneSource, "Msi.Api.PackageFixture.csproj"),
            Path.Combine(dir, "Msi.Api.PackageFixture.csproj"));
        File.Copy(Path.Combine(_f.StandaloneSource, "Program.cs"),
            Path.Combine(dir, "Program.cs"));
        File.Copy(Path.Combine(RepositoryLayout.Root, "Bundler.LocalPackages.props"),
            Path.Combine(dir, "Bundler.LocalPackages.props"));
        var project = Path.Combine(dir, "Msi.Api.PackageFixture.csproj");
        var cache = Path.Combine(root, "nuget-cache");
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["restore", project, $"-p:BundlerPackageSource={_f.Packages}",
                $"-p:RestorePackagesPath={cache}", $"-p:BundlerPackageVersion={_f.Version}"],
                new ProcessRunner.Options()),
            "API fixture restore failed.");
        MsiSupport.AssertLocalBundlerRestore(project, _f.Version, _f.Packages, cache,
            ["DotNet.Bundler.Wix", "DotNet.Bundler.Core", "DotNet.Bundler.Abstractions"]);
        var runArgs = new List<string>
        {
            "run", "--project", project, "-c", "Release", "--no-restore",
            $"-p:RestorePackagesPath={cache}", $"-p:BundlerPackageSource={_f.Packages}",
            $"-p:BundlerPackageVersion={_f.Version}", "--",
        };
        runArgs.AddRange(appArgs);
        ProcessRunner.AssertSuccess(
            Dotnet.Run(runArgs, new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) }),
            "Standalone backend API fixture failed.");
        return Path.Combine(appArgs[0], "artifacts");
    }

    // VerifyWinMsi5.ps1：win-x86 四变体版本映射 + 允许/拒绝降级 + 同版碰撞 1638。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void X86VersionMappingAndDowngradePolicy()
    {
        _f.Ensure();
        var id = NewIdentifier("x86");
        var root = _f.Ws.Combine("x86");
        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x86");
        var unknown = Path.Combine(install, "user-created.txt");
        Assert.False(Directory.Exists(install), $"Test installation already exists: {install}");

        var cases = new (string Name, string AppVersion, string MsiVersion, bool Allow)[]
        {
            ("v1-reject", "1.0.0", "1.0.0", false),
            ("v2-mapped", "1.1.0-beta.1", "1.1.0", false),
            ("v1-allow", "1.0.0", "1.0.0", true),
            ("v2-collision", "1.1.0-beta.2", "1.1.0", false),
        };
        var msiPaths = new Dictionary<string, string>();
        try
        {
            foreach (var c in cases)
            {
                var project = MsiSupport.CopyFixture(Path.Combine(root, $"fixture-{c.Name}"),
                    _f.FixtureSource);
                MsiSupport.RestoreFixture(project, _f.Packages,
                    Path.Combine(root, "nuget"), _f.Version, "win-x86");
                var msiDir = Publish(project, Path.Combine(root, $"output-{c.Name}"), id,
                    c.AppVersion, "win-x86",
                    $"-p:BundlerWixMsiVersion={c.MsiVersion}",
                    $"-p:BundlerWixAllowDowngrades={c.Allow.ToString().ToLowerInvariant()}",
                    "-p:MsiLifecycleTest=true");
                var msi = Path.Combine(msiDir, $"{MsiName}-{c.MsiVersion}.msi");
                Assert.True(File.Exists(msi), $"Missing MSI: {msi}");
                msiPaths[c.Name] = msi;
            }
            var apiArtifacts = RunApiFixture(root,
                Path.Combine(root, "api-output"), Path.Combine(root, "api-tools"),
                "win-x86", "2.0.0-beta.1", "1.8.4", "false");
            var apiMsi = Path.Combine(apiArtifacts, "win-x86", "msi",
                "MSI API Package Fixture-1.8.4.msi");
            Assert.True(File.Exists(apiMsi), "Standalone API did not produce the mapped x86 MSI.");
            Assert.Equal("1.8.4", MsiSupport.GetProperty(apiMsi, "ProductVersion"));
            foreach (var c in cases)
            {
                Assert.Equal(c.MsiVersion,
                    MsiSupport.GetProperty(msiPaths[c.Name], "ProductVersion"));
            }
            var v1 = msiPaths["v1-reject"];
            var v2 = msiPaths["v2-mapped"];
            var v1Allow = msiPaths["v1-allow"];
            var v2Collision = msiPaths["v2-collision"];
            var code1 = MsiSupport.GetProperty(v1, "ProductCode");
            var code2 = MsiSupport.GetProperty(v2, "ProductCode");
            Assert.NotEqual(code1, code2);
            Assert.Equal(code1, MsiSupport.GetProperty(v1Allow, "ProductCode"));
            Assert.Equal(code2, MsiSupport.GetProperty(v2Collision, "ProductCode"));

            var codes = new[] { code1, code2 };
            var unknownCreated = false;
            try
            {
                MsiexecLogged("install-v1", root, ["/i", $"\"{v1}\""], 0);
                AssertInstalled(code1, true);
                Assert.True(File.Exists(Path.Combine(install, "docs", "v1-only.txt")),
                    "x86 v1 payload is missing.");
                // x86 包的组件注册必须落在 32 位注册表视图。
                using (var reg32 = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry32))
                using (var componentKey = reg32.OpenSubKey(
                    $@"Software\DotNetBundler\Products\{id}\win-x86\Components"))
                {
                    Assert.NotNull(componentKey);
                    Assert.NotNull(componentKey!.GetValue("DefinitionHash"));
                }
                File.WriteAllText(unknown, "preserve user data");
                unknownCreated = true;

                MsiexecLogged("upgrade-v2", root, ["/i", $"\"{v2}\""], 0);
                AssertInstalled(code1, false);
                AssertInstalled(code2, true);
                Assert.False(File.Exists(Path.Combine(install, "docs", "v1-only.txt")),
                    "Upgrade retained v1 payload.");
                Assert.True(File.Exists(Path.Combine(install, "docs", "v2-only.txt")),
                    "Upgrade lost mapped v2 payload.");
                Assert.True(File.Exists(unknown), "Upgrade removed unknown user data.");

                // 同 ProductCode 不同版本 = 1638 collision（此时 code2 在装态）。
                MsiexecLogged("mapped-version-collision", root, ["/i", $"\"{v2Collision}\""], 1638);
                AssertInstalled(code2, true);

                MsiexecLogged("downgrade-rejected", root, ["/i", $"\"{v1}\""], 1603);
                Assert.Contains("A newer version of Bundler MSI Smoke is already installed",
                    File.ReadAllText(Path.Combine(root, "downgrade-rejected.log")),
                    StringComparison.Ordinal);
                AssertInstalled(code2, true);

                MsiexecLogged("downgrade-allowed", root, ["/i", $"\"{v1Allow}\""], 0);
                AssertInstalled(code1, true);
                AssertInstalled(code2, false);
                Assert.True(File.Exists(Path.Combine(install, "docs", "v1-only.txt")) &&
                       !File.Exists(Path.Combine(install, "docs", "v2-only.txt")) &&
                       File.Exists(unknown),
                    "Downgrade did not replace managed files while preserving user data.");

                MsiexecLogged("uninstall-v1", root, ["/x", code1], 0);
                AssertInstalled(code1, false);
                Assert.True(File.Exists(unknown) &&
                       !File.Exists(Path.Combine(install, "BundlerMsiSmoke.exe")),
                    "Uninstall did not preserve only the unknown user file.");
            }
            finally
            {
                CleanupProducts(codes);
                if (unknownCreated && File.Exists(unknown)) File.Delete(unknown);
                if (Directory.Exists(install) && !Directory.EnumerateFileSystemEntries(install).Any())
                    Directory.Delete(install);
            }
        }
        finally { /* 输出目录由 workspace 托管 */ }
    }

    // VerifyWinMsi6.ps1：对话框/环境表/快捷方式/品牌图/属性契约 + INSTALLFOLDER 范围闸
    // + 自选目录安装 + PATH 追加与还原 + ARP 元数据 + 升级保目录 + 修复 + 干净卸载。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void FeatureSetScopePathAndCustomDirectory()
    {
        _f.Ensure();
        var id = NewIdentifier("features");
        var root = _f.Ws.Combine("features");
        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BundlerTests", $"msi6-{Guid.NewGuid():N}");
        var defaultInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        Assert.False(Directory.Exists(install) || Directory.Exists(defaultInstall),
            "Test installation already exists.");
        var baselinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        var unknown = Path.Combine(install, "user-created.txt");
        var unknownCreated = false;
        string? code1 = null, code2 = null;
        try
        {
            var msis = new List<string>();
            foreach (var version in new[] { "1.0.0", "1.1.0" })
            {
                var fixtureDir = Path.Combine(root, $"fixture-{version}");
                var project = MsiSupport.CopyFixture(fixtureDir, _f.FixtureSource);
                MsiSupport.RestoreFixture(project, _f.Packages,
                    Path.Combine(root, "nuget"), _f.Version, "win-x64");
                msis.Add(Path.Combine(
                    Publish(project, Path.Combine(root, $"output-{version}"), id, version, "win-x64",
                        "-p:MsiLifecycleTest=true", "-p:BundlerWixInstallDirectorySelection=true",
                        "-p:BundlerWixStartMenuShortcut=true", "-p:BundlerWixDesktopShortcut=true",
                        "-p:BundlerWixAddToPath=true", "-p:BundlerWixUninstallShortcut=true",
                        "-p:BundlerWixLaunchAfterInstall=true",
                        $"-p:BundlerWixBannerBitmap={Path.Combine(fixtureDir, "Assets", "banner.bmp")}",
                        $"-p:BundlerWixDialogBitmap={Path.Combine(fixtureDir, "Assets", "dialog.bmp")}"),
                    $"{MsiName}-{version}.msi"));
            }
            foreach (var msi in msis) Assert.True(File.Exists(msi), $"Missing MSI: {msi}");
            var v1 = msis[0];
            var v2 = msis[1];
            code1 = MsiSupport.GetProperty(v1, "ProductCode");
            code2 = MsiSupport.GetProperty(v2, "ProductCode");
            AssertInstalled(code1, false);

            var dialogs = MsiSupport.QueryColumn(v1, "SELECT `Dialog` FROM `Dialog`");
            foreach (var expected in new[] { "WelcomeDlg", "InstallDirDlg", "BrowseDlg", "InvalidDirDlg",
                         "VerifyReadyDlg", "ExitDialog", "MaintenanceWelcomeDlg", "MaintenanceTypeDlg" })
            {
                Assert.Contains(dialogs, d => d == expected);
            }
            Assert.DoesNotContain(dialogs, d => d == "LicenseAgreementDlg");
            var sequence = MsiSupport.QueryColumn(v1, "SELECT `Action` FROM `InstallExecuteSequence`");
            var conditions = MsiSupport.QueryColumn(v1, "SELECT `Condition` FROM `InstallExecuteSequence`");
            Assert.Contains(sequence, s => s == "BundlerInstallDirScope");
            Assert.Contains(conditions, c =>
                c != null && c.Contains("LocalAppDataFolder") && c.Contains("INSTALLFOLDER"));
            var envNames = MsiSupport.QueryColumn(v1, "SELECT `Name` FROM `Environment`");
            var envValues = MsiSupport.QueryColumn(v1, "SELECT `Value` FROM `Environment`");
            Assert.Contains(envNames, n => n == "=-PATH");
            Assert.Single(envValues, v => v == "[~];[INSTALLFOLDER]");
            var shortcutNames = MsiSupport.QueryColumn(v1, "SELECT `Name` FROM `Shortcut`");
            var shortcutArgs = MsiSupport.QueryColumn(v1, "SELECT `Arguments` FROM `Shortcut`");
            Assert.Equal(3, shortcutNames.Length);
            Assert.Single(shortcutArgs, a => a != null && a.Contains("ProductCode"));
            var binaries = MsiSupport.QueryColumn(v1, "SELECT `Name` FROM `Binary`");
            Assert.Contains(binaries, b => b == "WixUI_Bmp_Banner");
            Assert.Contains(binaries, b => b == "WixUI_Bmp_Dialog");
            Assert.Equal("INSTALLFOLDER", MsiSupport.GetProperty(v1, "WIXUI_INSTALLDIR"));
            Assert.Equal("1", MsiSupport.GetProperty(v1, "ARPNOMODIFY"));
            Assert.Equal("Bundler Tests", MsiSupport.GetProperty(v1, "ARPCONTACT"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            MsiexecLogged("reject-root", root, ["/i", $"\"{v1}\"", $"INSTALLFOLDER=\"{localAppData}\""], 1603);
            var rejectLog = Path.Combine(root, "reject-outside.log");
            var rejectResult = MsiSupport.Msiexec(
                $"/i \"{v1}\" /qn /norestart /L*v \"{rejectLog}\" INSTALLFOLDER=\"C:\\Program Files\\Common Files\\Forbidden\"");
            Assert.Equal(1603, rejectResult.ExitCode);
            Assert.Contains("installation folder must be a subfolder", File.ReadAllText(rejectLog));
            AssertInstalled(code1, false);

            MsiexecLogged("install-custom", root, ["/i", $"\"{v1}\"", $"INSTALLFOLDER=\"{install}\""], 0);
            AssertInstalled(code1, true);
            Assert.True(File.Exists(Path.Combine(install, "BundlerMsiSmoke.exe")));
            Assert.True(File.Exists(Path.Combine(install, "docs", "marker.txt")));
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            if (!string.IsNullOrEmpty(baselinePath))
            {
                Assert.True(userPath.StartsWith(baselinePath, StringComparison.OrdinalIgnoreCase),
                    "The MSI PATH feature overwrote unrelated user PATH entries.");
            }
            Assert.True(userPath.Contains(";" + install) || userPath == baselinePath + ";" + install,
                "The product directory was not appended to the user PATH.");
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", id);
            Assert.True(File.Exists(Path.Combine(startMenu, "Bundler MSI Smoke.lnk")));
            Assert.True(File.Exists(Path.Combine(startMenu, "Uninstall Bundler MSI Smoke.lnk")));
            var desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"Bundler MSI Smoke ({id}).lnk");
            Assert.True(File.Exists(desktopShortcut), "Desktop shortcut is missing.");
            RegistryKey? arp = null;
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                arp = hive.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{code1}");
                if (arp is not null) break;
            }
            Assert.NotNull(arp);
            var installLocation = arp!.GetValue("InstallLocation") as string;
            Assert.True(installLocation == install + "\\" || installLocation == install,
                "ARP InstallLocation is missing the install directory.");
            Assert.NotNull(arp.GetValue("Contact"));
            File.WriteAllText(unknown, "preserve user data");
            unknownCreated = true;

            MsiexecLogged("upgrade-v2", root, ["/i", $"\"{v2}\""], 0);
            AssertInstalled(code1, false);
            AssertInstalled(code2, true);
            Assert.True(File.Exists(Path.Combine(install, "docs", "v2-only.txt")),
                "Upgrade did not restore the previously chosen install directory.");
            Assert.True(File.Exists(unknown));
            Assert.False(Directory.Exists(defaultInstall));
            MsiexecLogged("repair-v2", root, ["/fomus", $"\"{v2}\""], 0);
            AssertInstalled(code2, true);
            MsiexecLogged("uninstall-v2", root, ["/x", code2], 0);
            AssertInstalled(code2, false);
            var afterPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            Assert.False(afterPath.Contains(install), "Uninstall left the product PATH entry behind.");
            if (!string.IsNullOrEmpty(baselinePath))
            {
                var baseEntries = baselinePath.Split(';').Where(e => e != "");
                var afterEntries = afterPath.Split(';').Where(e => e != "");
                Assert.Equal(string.Join(";", baseEntries), string.Join(";", afterEntries));
            }
            Assert.False(File.Exists(desktopShortcut), "Uninstall left the desktop shortcut.");
            Assert.False(Directory.Exists(startMenu), "Uninstall left the start menu folder.");
            Assert.False(File.Exists(Path.Combine(install, "BundlerMsiSmoke.exe")));
            Assert.True(File.Exists(unknown));
        }
        finally
        {
            CleanupProducts(code1, code2);
            if (unknownCreated && File.Exists(unknown)) File.Delete(unknown);
            foreach (var dir in new[] { install, defaultInstall })
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            var left = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"Bundler MSI Smoke ({id}).lnk");
            if (File.Exists(left)) File.Delete(left);
            var leftMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", id);
            if (Directory.Exists(leftMenu)) Directory.Delete(leftMenu, true);
            if (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) != baselinePath)
            {
                Environment.SetEnvironmentVariable("Path", baselinePath, EnvironmentVariableTarget.User);
            }
        }
    }

    // VerifyWinMsi7.ps1：一次 publish 产 en-US+ja-JP 两个 MSI，身份/目录/菜单相互隔离。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void PerLanguageMsisInstallCoexistAndUninstallIndependently()
    {
        _f.Ensure();
        var id = NewIdentifier("i18n");
        var root = _f.Ws.Combine("i18n");
        var installEn = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        var installJa = installEn + "-ja-jp";
        Assert.False(Directory.Exists(installEn) || Directory.Exists(installJa));
        string? codeEn = null, codeJa = null;
        try
        {
            var project = MsiSupport.CopyFixture(Path.Combine(root, "fixture"), _f.FixtureSource);
            MsiSupport.RestoreFixture(project, _f.Packages,
                Path.Combine(root, "nuget"), _f.Version, "win-x64");
            var msiDir = Publish(project, Path.Combine(root, "output"), id, "1.0.0", "win-x64",
                "-p:BundlerWixLanguages=en-US%3Bja-JP", "-p:BundlerWixStartMenuShortcut=true");
            var msiEn = Path.Combine(msiDir, $"{MsiName}-1.0.0.msi");
            var msiJa = Path.Combine(msiDir, $"{MsiName}-1.0.0-ja-jp.msi");
            Assert.True(File.Exists(msiEn) && File.Exists(msiJa), "Missing localized MSIs.");

            var apiArtifacts = RunApiFixture(root,
                Path.Combine(root, "api-output"), Path.Combine(root, "api-tools"),
                "win-x64", "1.0.0", "1.0.0", "false", "en-US;de-DE");
            Assert.True(File.Exists(Path.Combine(apiArtifacts, "win-x64", "msi",
                "MSI API Package Fixture-1.0.0.msi")));
            Assert.True(File.Exists(Path.Combine(apiArtifacts, "win-x64", "msi",
                "MSI API Package Fixture-1.0.0-de-de.msi")));

            codeEn = MsiSupport.GetProperty(msiEn, "ProductCode");
            codeJa = MsiSupport.GetProperty(msiJa, "ProductCode");
            Assert.NotEqual(codeEn, codeJa);
            Assert.NotEqual(MsiSupport.GetProperty(msiEn, "UpgradeCode"),
                MsiSupport.GetProperty(msiJa, "UpgradeCode"));
            Assert.Equal("1033", MsiSupport.GetProperty(msiEn, "ProductLanguage"));
            Assert.Equal("1041", MsiSupport.GetProperty(msiJa, "ProductLanguage"));

            MsiexecLogged("install-en", root, ["/i", $"\"{msiEn}\""], 0);
            MsiexecLogged("install-ja", root, ["/i", $"\"{msiJa}\""], 0);
            AssertInstalled(codeEn, true);
            AssertInstalled(codeJa, true);
            Assert.True(File.Exists(Path.Combine(installEn, "BundlerMsiSmoke.exe")));
            Assert.True(File.Exists(Path.Combine(installJa, "BundlerMsiSmoke.exe")));
            var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            var menuEn = Path.Combine(programsDir, id, "Bundler MSI Smoke.lnk");
            var menuJa = Path.Combine(programsDir, $"{id}-ja-jp", "Bundler MSI Smoke-ja-jp.lnk");
            Assert.True(File.Exists(menuEn));
            Assert.True(File.Exists(menuJa));

            MsiexecLogged("uninstall-ja", root, ["/x", codeJa], 0);
            AssertInstalled(codeJa, false);
            AssertInstalled(codeEn, true);
            MsiexecLogged("uninstall-en", root, ["/x", codeEn], 0);
            AssertInstalled(codeEn, false);
            Assert.False(Directory.Exists(installEn));
            Assert.False(Directory.Exists(installJa));
        }
        finally
        {
            CleanupProducts(codeEn, codeJa);
            foreach (var dir in new[] { installEn, installJa })
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            foreach (var menu in new[] { Path.Combine(programsDir, id), Path.Combine(programsDir, $"{id}-ja-jp") })
            {
                if (Directory.Exists(menu)) Directory.Delete(menu, true);
            }
        }
    }

    // VerifyWinMsi8.ps1：regular 扩展片段 + expert 自定义 .wxs 模板（Bundler.* 变量）两条模式。
    [Fact]
    [Trait("Requires", "localinstall")]
    public void ExtensionFragmentAndExpertTemplateInstallAndUninstall()
    {
        _f.Ensure();
        var id = NewIdentifier("ext");
        var root = _f.Ws.Combine("ext");
        var defaultInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", $"{id}-x64");
        var expertInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BundlerMsi8Api");
        const string registryKey = @"Software\BundlerTests\WinMsi8";
        Assert.False(Directory.Exists(defaultInstall) || Directory.Exists(expertInstall)
            || Registry.CurrentUser.OpenSubKey(registryKey) is not null);
        string? code = null, expertCode = null;
        try
        {
            var project = MsiSupport.CopyFixture(Path.Combine(root, "fixture"), _f.FixtureSource);
            MsiSupport.RestoreFixture(project, _f.Packages,
                Path.Combine(root, "nuget"), _f.Version, "win-x64");
            var msi = Path.Combine(
                Publish(project, Path.Combine(root, "output"), id, "1.0.0", "win-x64",
                    "-p:MsiExtensionTest=true", "-p:BundlerWixExtensionIdPrefix=Ext."),
                $"{MsiName}-1.0.0.msi");
            Assert.True(File.Exists(msi));
            code = MsiSupport.GetProperty(msi, "ProductCode");
            AssertInstalled(code, false);
            MsiexecLogged("install-regular", root, ["/i", $"\"{msi}\""], 0);
            Assert.True(File.Exists(Path.Combine(defaultInstall, "extension-marker.txt")),
                "The extension fragment file did not land in the install directory.");
            Assert.Equal("yes",
                Registry.CurrentUser.OpenSubKey(registryKey)?.GetValue("Installed") as string);
            MsiexecLogged("uninstall-regular", root, ["/x", code], 0);
            Assert.False(File.Exists(Path.Combine(defaultInstall, "extension-marker.txt")));
            Assert.Null(Registry.CurrentUser.OpenSubKey(registryKey));

            // expert 模式：调用方 .wxs 模板，消费 Bundler.* 变量。
            var apiArtifacts = Path.Combine(root, "api-output", "artifacts");
            var template = Path.Combine(root, "expert-template.wxs");
            var fixturePayload = Path.Combine(root, "api-output", "publish", "ApiFixture.exe")
                .Replace('\\', '/');
            File.WriteAllText(template,
                "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\">" +
                "<Product Id=\"$(var.Bundler.ProductCode)\" Name=\"$(var.Bundler.ProductName)\"" +
                " Language=\"$(var.Bundler.ProductLanguage)\" Version=\"$(var.Bundler.ProductVersion)\"" +
                " Manufacturer=\"$(var.Bundler.Manufacturer)\" UpgradeCode=\"$(var.Bundler.UpgradeCode)\"" +
                " Codepage=\"$(var.Bundler.Codepage)\">" +
                "<Package InstallerVersion=\"500\" Compressed=\"yes\" InstallScope=\"$(var.Bundler.InstallScope)\"/>" +
                "<Media Id=\"1\" Cabinet=\"app.cab\" EmbedCab=\"yes\"/>" +
                "<MajorUpgrade Schedule=\"afterInstallInitialize\" AllowSameVersionUpgrades=\"no\"" +
                " DowngradeErrorMessage=\"!(loc.BundlerDowngradeErrorMessage)\"/>" +
                "<Property Id=\"MSIINSTALLPERUSER\" Value=\"1\"/>" +
                "<Directory Id=\"TARGETDIR\" Name=\"SourceDir\"><Directory Id=\"LocalAppDataFolder\">" +
                "<Directory Id=\"INSTALLFOLDER\" Name=\"BundlerMsi8Api\"/></Directory></Directory>" +
                "<DirectoryRef Id=\"INSTALLFOLDER\">" +
                "<Component Id=\"Expert.App\" Guid=\"{7c4a5f2e-9b31-4d68-8a2f-6e1c5d9a0b7e}\">" +
                $"<File Source=\"{fixturePayload}\"/>" +
                "<RemoveFolder Id=\"Expert.RemoveFolder\" On=\"uninstall\"/>" +
                "<RegistryValue Root=\"HKCU\" Key=\"Software\\BundlerTests\\WinMsi8Expert\" Name=\"Mark\"" +
                " Type=\"string\" Value=\"expert\" KeyPath=\"yes\"/>" +
                "</Component></DirectoryRef>" +
                "<Feature Id=\"Complete\" Title=\"$(var.Bundler.ProductName)\" Level=\"1\">" +
                "<ComponentRef Id=\"Expert.App\"/></Feature>" +
                "</Product></Wix>");
            RunApiFixture(root, Path.Combine(root, "api-output"), Path.Combine(root, "api-tools"),
                "win-x64", "1.0.0", "1.0.0", "false", "en-US", template);
            var expertMsi = Path.Combine(apiArtifacts, "win-x64", "msi",
                "MSI API Package Fixture-1.0.0.msi");
            Assert.True(File.Exists(expertMsi), $"Missing expert MSI: {expertMsi}");
            expertCode = MsiSupport.GetProperty(expertMsi, "ProductCode");
            AssertInstalled(expertCode, false);
            MsiexecLogged("install-expert", root, ["/i", $"\"{expertMsi}\""], 0);
            Assert.True(File.Exists(Path.Combine(expertInstall, "ApiFixture.exe")),
                "The expert template payload did not land.");
            MsiexecLogged("uninstall-expert", root, ["/x", expertCode], 0);
            Assert.False(Directory.Exists(expertInstall),
                "The expert template install directory was left behind.");
        }
        finally
        {
            CleanupProducts(code, expertCode);
            foreach (var dir in new[] { defaultInstall, expertInstall })
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            Registry.CurrentUser.DeleteSubKeyTree(registryKey, false);
            var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", id);
            if (Directory.Exists(menu)) Directory.Delete(menu, true);
        }
    }

    // VerifyPublicSample.ps1：HelloBundlerApp 三变体（en user / zh user / en machine）的表级契约，不安装。
    [Fact]
    public void PublicSampleMsiTableContract()
    {
        _f.EnsurePackages();
        var root = _f.Ws.Combine("public-sample");
        var sample = Path.Combine(RepositoryLayout.Root, "samples", "HelloBundlerApp", "HelloBundlerApp.csproj");
        Assert.True(File.Exists(sample), $"Public sample is missing: {sample}");
        // 公共样例消费的是 artifacts/packages 中的已 pack 产物（脚本同样要求先 pack）。
        var source = Path.Combine(RepositoryLayout.Root, "artifacts", "packages");
        var version = _f.Version;
        foreach (var name in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.Wix" })
        {
            Assert.True(File.Exists(Path.Combine(source, $"{name}.{version}.nupkg")),
                $"Public sample needs the current local packages; run dotnet pack first: {name}");
        }
        var variants = new (string Name, string Language, string Scope, string ProductLanguage,
            string AllUsers, string FileName)[]
        {
            ("english-user", "en-US", "currentUser", "1033", "", "Hello Bundler App-1.0.0.msi"),
            ("chinese-user", "zh-CN", "currentUser", "2052", "", "Hello Bundler App-1.0.0-zh-cn.msi"),
            ("english-machine", "en-US", "perMachine", "1033", "1", "Hello Bundler App-1.0.0.msi"),
        };
        var productCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upgradeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = Path.Combine(root, "packages");
        foreach (var variant in variants)
        {
            var output = Path.Combine(root, variant.Name);
            var props = new List<string>
            {
                "-r", "win-x64",
                "-p:BundlerFormats=msi",
                $"-p:RestorePackagesPath={cache}",
                $"-p:HelloBundlerMsiLanguage={variant.Language}",
                $"-p:HelloBundlerMsiInstallScope={variant.Scope}",
                $"-p:BundlerOutputPath={output}",
            };
            ProcessRunner.AssertSuccess(
                Dotnet.Run(["restore", sample, .. props, "--force", "-v:minimal"],
                    new ProcessRunner.Options { WorkingDirectory = RepositoryLayout.Root }),
                $"Sample restore failed: {variant.Name}");
            ProcessRunner.AssertSuccess(
                Dotnet.Run(["publish", sample, "-c", "Release", "--no-restore", .. props,
                    "-v:minimal"],
                    new ProcessRunner.Options
                    { WorkingDirectory = RepositoryLayout.Root, Timeout = TimeSpan.FromMinutes(10) }),
                $"Sample publish failed: {variant.Name}");
            var msi = Path.Combine(output, "win-x64", "msi", variant.FileName);
            Assert.True(File.Exists(msi), $"Sample MSI is missing: {msi}");
            var properties = ReadProperties(msi);
            Assert.Equal("Hello Bundler App", properties["ProductName"]);
            Assert.Equal("1.0.0", properties["ProductVersion"]);
            Assert.Equal(variant.ProductLanguage, properties["ProductLanguage"]);
            Assert.Equal(variant.AllUsers, properties.TryGetValue("ALLUSERS", out var au) ? au : "");
            Assert.Equal("ProductIcon", properties["ARPPRODUCTICON"]);
            Assert.Equal("https://github.com/dotnetbundler", properties["ARPURLINFOABOUT"]);
            Assert.True(productCodes.Add(properties["ProductCode"])
                && upgradeCodes.Add(properties["UpgradeCode"]),
                "MSI language and scope variants must have separate identities.");
            var files = MsiSupport.QueryColumn(msi, "SELECT `FileName` FROM `File`");
            foreach (var expected in new[] { "demo.hellomsi", "Readme.txt", "open-link.cmd", "HelloBundlerApp.exe" })
            {
                Assert.Contains(files, f => f != null
                    && System.Text.RegularExpressions.Regex.IsMatch(f,
                        System.Text.RegularExpressions.Regex.Escape(expected) + "$"));
            }
            var dialogs = MsiSupport.QueryColumn(msi, "SELECT `Dialog` FROM `Dialog`");
            foreach (var expected in new[] { "WelcomeDlg", "LicenseAgreementDlg", "InstallDirDlg",
                         "VerifyReadyDlg", "ExitDialog", "MaintenanceWelcomeDlg", "MaintenanceTypeDlg" })
            {
                Assert.Contains(dialogs, d => d == expected);
            }
            Assert.DoesNotContain(dialogs, d => d == "WelcomeEulaDlg");
            Assert.Contains(MsiSupport.QueryColumn(msi, "SELECT `Name` FROM `Icon`"),
                i => i == "ProductIcon");
            Assert.Equal(3, MsiSupport.QueryColumn(msi, "SELECT `Name` FROM `Shortcut`").Length);
            var expectedPathName = variant.Scope == "perMachine" ? "=-*PATH" : "=-PATH";
            Assert.Contains(MsiSupport.QueryColumn(msi, "SELECT `Name` FROM `Environment`"),
                n => n == expectedPathName);
            var envValues = MsiSupport.QueryColumn(msi, "SELECT `Value` FROM `Environment`");
            Assert.Single(envValues, v => v != null && v.Contains("INSTALLFOLDER"));
            Assert.Single(envValues, v => v != null && v.StartsWith("[~];"));
            var binaries = MsiSupport.QueryColumn(msi, "SELECT `Name` FROM `Binary`");
            Assert.Contains(binaries, b => b == "WixUI_Bmp_Banner");
            Assert.Contains(binaries, b => b == "WixUI_Bmp_Dialog");
            Assert.Equal("INSTALLFOLDER", properties.GetValueOrDefault("WIXUI_INSTALLDIR"));
            Assert.Equal("1", properties.GetValueOrDefault("ARPNOMODIFY"));
            Assert.Contains("[ProductName]",
                properties.GetValueOrDefault("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT") ?? "");
            var registryRows = ReadRegistryRows(msi);
            Assert.Contains(registryRows,
                r => r.Contains(@"Classes\.hellomsi\OpenWithProgids"));
            Assert.Contains(registryRows,
                r => r.Contains(@"Capabilities\MIMEAssociations|application/x-hellomsi"));
            Assert.Contains(registryRows,
                r => r.Contains(@"Capabilities\UrlAssociations|hello-msi"));
        }
    }

    // GUI 级验收：msiexec /i 真弹向导——Welcome→LicenseAgreement（自动勾 I accept）→
    // InstallDir→VerifyReady(Install)→Exit(Finish，取消"启动应用"勾选）；
    // 再 /i 走维护流 MaintenanceWelcome→MaintenanceType(Remove)→VerifyReady→Exit。
    // per-user 范围无 UAC，UIA 可全通。
    [Fact]
    [Trait("Requires", "localinstall")]
    [Trait("Requires", "interactive")]
    public void InteractiveWizardInstallsAndRemoves()
    {
        _f.Ensure();
        Assert.SkipWhen(!WindowsDesktop.IsInteractive(),
            "GUI 验收腿需要交互式桌面会话（UIA 可达顶层窗口）。");
        var root = _f.Ws.Combine("interactive-sample");
        var sample = Path.Combine(RepositoryLayout.Root, "samples", "HelloBundlerApp",
            "HelloBundlerApp.csproj");
        var output = Path.Combine(root, "en-user");
        var props = new List<string>
        {
            "-r", "win-x64",
            "-p:BundlerFormats=msi",
            $"-p:RestorePackagesPath={Path.Combine(root, "packages")}",
            "-p:HelloBundlerMsiLanguage=en-US",
            "-p:HelloBundlerMsiInstallScope=currentUser",
            $"-p:BundlerOutputPath={output}",
        };
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["restore", sample, .. props, "--force", "-v:minimal"],
                new ProcessRunner.Options { WorkingDirectory = RepositoryLayout.Root }),
            "Sample restore failed (interactive leg)");
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["publish", sample, "-c", "Release", "--no-restore", .. props,
                "-v:minimal"],
                new ProcessRunner.Options
                { WorkingDirectory = RepositoryLayout.Root, Timeout = TimeSpan.FromMinutes(10) }),
            "Sample publish failed (interactive leg)");
        var msi = Path.Combine(output, "win-x64", "msi", "Hello Bundler App-1.0.0.msi");
        Assert.True(File.Exists(msi), $"Sample MSI is missing: {msi}");
        var productCode = MsiSupport.GetProperty(msi, "ProductCode");
        var installedExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Hello Bundler App", "HelloBundlerApp.exe");
        var log = _f.Ws.Combine("interactive-install.log");
        try
        {
            using var install = ProcessRunner.StartDetached("msiexec",
                $"/i \"{msi}\" /log \"{log}\"");
            var installDrive = WindowsDesktop.DriveWizard(
                () => WindowsDesktop.TopWindowByProcess(install.Id)
                      ?? WindowsDesktop.TopWindowByClass("MsiDialogCloseClass"),
                () => install.HasExited, TimeSpan.FromMinutes(5), autoCheck: true);
            Assert.True(installDrive.Finished,
                $"安装向导未走完（已点：{string.Join(" → ", installDrive.Actions)}）");
            Assert.Contains(installDrive.Actions, a => a.Contains("Install"));
            WaitFor.Until(() => install.HasExited,
                "msiexec did not exit after Finish.", 30);
            Assert.Equal(0, install.ExitCode);
            Assert.True(File.Exists(installedExe),
                $"Interactive install did not write {installedExe}");

            // 维护流 Remove：对已装产品再 /i 进维护模式。
            using var remove = ProcessRunner.StartDetached("msiexec", $"/i \"{msi}\"");
            var removeDrive = WindowsDesktop.DriveWizard(
                () => WindowsDesktop.TopWindowByProcess(remove.Id)
                      ?? WindowsDesktop.TopWindowByClass("MsiDialogCloseClass"),
                () => remove.HasExited, TimeSpan.FromMinutes(5), autoCheck: true);
            Assert.True(removeDrive.Finished,
                $"卸载向导未走完（已点：{string.Join(" → ", removeDrive.Actions)}）");
            Assert.Contains(removeDrive.Actions, a => a.Contains("Remove"));
            WaitFor.Until(() => remove.HasExited, "msiexec remove did not exit.", 30);
            Assert.Equal(0, remove.ExitCode);
            Assert.False(File.Exists(installedExe),
                "Interactive remove left the payload behind.");
        }
        finally
        {
            MsiSupport.Msiexec($"/x {productCode} /qn /norestart");
        }
    }

    private static Dictionary<string, string> ReadProperties(string msiPath)
    {
        using var database = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        using var view = database.OpenView("SELECT `Property`, `Value` FROM `Property`");
        view.Execute();
        var map = new Dictionary<string, string>();
        while (view.Fetch() is { } record)
        {
            map[record.GetString(1)] = record.GetString(2);
        }
        return map;
    }

    private static List<string> ReadRegistryRows(string msiPath)
    {
        using var database = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        using var view = database.OpenView("SELECT * FROM `Registry`");
        view.Execute();
        var rows = new List<string>();
        while (view.Fetch() is { } record)
        {
            rows.Add($"{record.GetString(3)}|{record.GetString(4)}|{record.GetString(5)}");
        }
        return rows;
    }
}

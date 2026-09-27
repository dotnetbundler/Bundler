using DotNet.Bundler;
using DotNet.Bundler.MacPkg;

internal static class MacPkgTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-PKG formats", () => RunSync(RejectsNonPkgFormats));
            yield return ("Rejects non-macOS hosts for PKG", () => RunSync(RejectsNonMacOsHost));
            yield return ("Rejects a relative install location", () => RunSync(RejectsRelativeInstallLocation));
            yield return ("Runs the pkgbuild chain on stubbed tools", RunsPkgbuildOnStubbedTools);
            yield return ("Maps identifier/version/install-location overrides", MapsOverrides);
            yield return ("Stages explicit payload items", StagesPayloadItems);
            yield return ("Rejects a missing payload source", () => RunSync(MissingPayloadRejected));
            yield return ("Rejects a build with no .app and no payload", () => RunSync(NoPayloadRejected));
            yield return ("Leaves no .pkg artifact when pkgbuild fails", PkgbuildFailureLeavesNoArtifact);
            yield return ("Maps .pkg settings through MSBuild", () => RunSync(MapsPkgSettingsThroughMsBuild));
            yield return ("Upgrades to a distribution package when configured", DistributionUpgrade);
            yield return ("Keeps the plain component package without distribution settings", NoDistributionByDefault);
            yield return ("Declares the current-user-home domain", CurrentUserHomeDomain);
            yield return ("Copies distribution page files into the resources directory", DistributionResources);
            yield return ("Rejects a missing welcome page file", () => RunSync(MissingWelcomeRejected));
        }
    }

    static void RejectsNonPkgFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var thrown = false;
            try
            {
                new MacPkgBundler()
                    .BuildAsync(PkgConfiguration(input, formats: [PackageFormat.App]))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("Pkg targets only");
            }
            Assert(thrown, "A non-PKG target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void RejectsNonMacOsHost()
    {
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => false;
        try
        {
            var thrown = false;
            try
            {
                new MacPkgBundler()
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("macOS host");
            }
            Assert(thrown, "PKG builds must be rejected off macOS hosts.");
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    static void RejectsRelativeInstallLocation()
    {
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            var thrown = false;
            try
            {
                new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        InstallLocation = "usr/local"
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult();
            }
            catch (ArgumentException exception)
            {
                thrown = exception.Message.Contains("absolute path");
            }
            Assert(thrown, "A relative install location must be rejected.");
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    static async Task RunsPkgbuildOnStubbedTools()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacPkgProcessRunner.Request>();
        var staged = new List<string>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            if (request.Executable == "pkgbuild")
            {
                var args = request.Arguments.ToList();
                var root = args[args.IndexOf("--root") + 1];
                staged.AddRange(Directory.GetFileSystemEntries(root).Select(Path.GetFileName)!);
                var pkgPath = request.Arguments[request.Arguments.Count - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
                File.WriteAllText(pkgPath, "pkg");
            }
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var artifacts = await new MacPkgBundler()
                .BuildAsync(PkgConfiguration(input, output));
            Assert(artifacts.Count == 2,
                $"A pkg request must produce the intermediate .app plus the .pkg, got {artifacts.Count}.");
            var pkg = artifacts.Single(artifact => artifact.Format == PackageFormat.Pkg);
            Assert(pkg.Path.EndsWith(Path.Combine("osx-arm64", "pkg", "ExampleApp.pkg"), StringComparison.Ordinal),
                $"Unexpected .pkg artifact path: {pkg.Path}");

            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var args = pkgbuild.Arguments.ToList();
            Assert(args.Contains("--install-location") && args.Contains("--identifier") &&
                   args.Contains("--version") && args.Contains("--ownership"),
                $"pkgbuild arguments must carry install-location/identifier/version/ownership: {string.Join(' ', args)}");
            Assert(args[args.IndexOf("--install-location") + 1] == "/Applications",
                "The default install location must be /Applications.");
            Assert(args[args.IndexOf("--identifier") + 1] == "com.example.app",
                "The default identifier must come from the bundle Identifier.");
            Assert(args[args.IndexOf("--version") + 1] == "1.0.0",
                "The default version must come from the bundle Version.");
            Assert(args[args.IndexOf("--ownership") + 1] == "recommended",
                "pkgbuild must run with --ownership recommended.");
            Assert(args[args.Count - 1] == pkg.Path,
                "The pkgbuild output path must be the last argument.");
            Assert(staged.Contains("ExampleApp.app"),
                $"The intermediate .app must be staged in the payload root, got: {string.Join(',', staged)}");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task MapsOverrides()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        var requests = new List<MacPkgProcessRunner.Request>();
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            if (request.Executable == "pkgbuild")
            {
                var pkgPath = request.Arguments[request.Arguments.Count - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
                File.WriteAllText(pkgPath, "pkg");
            }
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Identifier = "com.example.pkg.override",
                    Version = "2.5.1",
                    InstallLocation = "/usr/local"
                })
                .BuildAsync(PkgConfiguration(input, output));
            var args = requests.Single(request => request.Executable == "pkgbuild").Arguments.ToList();
            Assert(args[args.IndexOf("--identifier") + 1] == "com.example.pkg.override",
                "An explicit identifier must override the bundle Identifier.");
            Assert(args[args.IndexOf("--version") + 1] == "2.5.1",
                "An explicit version must override the bundle Version.");
            Assert(args[args.IndexOf("--install-location") + 1] == "/usr/local",
                "An explicit install location must reach --install-location.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task StagesPayloadItems()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(input, "helper.txt");
        File.WriteAllText(extra, "helper payload");
        var staged = new List<string>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            if (request.Executable == "pkgbuild")
            {
                var args = request.Arguments.ToList();
                var root = args[args.IndexOf("--root") + 1];
                staged.AddRange(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(root, path)));
                var pkgPath = request.Arguments[request.Arguments.Count - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
                File.WriteAllText(pkgPath, "pkg");
            }
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    PayloadItems =
                    [
                        new MacPkgPayloadItem { Source = extra, Destination = "support/helper.txt" }
                    ]
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert(staged.Any(path => path.Replace('\\', '/') == "support/helper.txt"),
                $"The payload item must be staged at its destination, got: {string.Join(',', staged)}");
            Assert(staged.Any(path => path.Contains("ExampleApp.app")),
                "The intermediate .app must still be staged next to explicit payload items.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static void MissingPayloadRejected()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (_, _) =>
            Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        try
        {
            var thrown = false;
            try
            {
                new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        PayloadItems =
                        [
                            new MacPkgPayloadItem { Source = Path.Combine(input, "missing.bin") }
                        ]
                    })
                    .BuildAsync(PkgConfiguration(input, output))
                    .GetAwaiter().GetResult();
            }
            catch (FileNotFoundException)
            {
                thrown = true;
            }
            Assert(thrown, "A missing payload source must fail the build.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // The bundler's planner always produces the .app intermediate first, so the
    // "no payload at all" branch is only reachable by invoking the backend directly.
    static void NoPayloadRejected()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            var thrown = false;
            try
            {
                var configuration = PkgConfiguration(input, output);
                var item = new BundlePlanItem(
                    new BundleTarget("osx-arm64", DesktopOperatingSystem.MacOS, CpuArchitecture.Arm64),
                    PackageFormat.Pkg,
                    input,
                    "ExampleApp",
                    Path.Combine(output, "osx-arm64", "pkg"),
                    Intermediate: false);
                new MacPkgBundleBackend(new MacPkgBundleConfiguration())
                    .BuildAsync(new BundleBuildContext(
                        configuration, item, Path.Combine(output, "work"), new SilentLogger()),
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (DirectoryNotFoundException)
            {
                thrown = true;
            }
            Assert(thrown, "A pkg build with no .app and no payload must fail.");
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    sealed class SilentLogger : IBundleLogger
    {
        public void Log(BundleLogLevel level, string message) { }
    }

    static async Task PkgbuildFailureLeavesNoArtifact()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
            Task.FromResult(new MacPkgProcessRunner.Result(
                request.Executable == "pkgbuild" ? 2 : 0, "", "pkgbuild failed"));
        try
        {
            var thrown = false;
            try
            {
                await new MacPkgBundler().BuildAsync(PkgConfiguration(input, output));
            }
            catch (InvalidOperationException exception)
            {
                thrown = exception.Message.Contains("pkgbuild");
            }
            Assert(thrown, "A pkgbuild failure must fail the build.");
            var pkgDirectory = Path.Combine(output, "osx-arm64", "pkg");
            Assert(!Directory.Exists(pkgDirectory) ||
                   !Directory.EnumerateFiles(pkgDirectory, "*.pkg").Any(),
                "A failed pkg build must not leave an output artifact.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static void MapsPkgSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("MacPkgIdentifier=\"$(BundlerMacPkgIdentifier)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgVersion=\"$(BundlerMacPkgVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgInstallLocation=\"$(BundlerMacPkgInstallLocation)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgPayloadItems=\"@(BundlerPkgPayload)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMacPkg* properties to the task.");
        Assert(props.Contains("<BundlerMacPkgInstallLocation Condition=\"'$(BundlerMacPkgInstallLocation)' == ''\">/Applications<", StringComparison.Ordinal),
            "The default BundlerMacPkgInstallLocation must be /Applications.");
        Assert(task.Contains("new MacPkgBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Pkg", StringComparison.Ordinal),
            "The MSBuild task does not construct the .pkg backend.");
    }

    static BundleConfiguration PkgConfiguration(
        string input,
        string output = "",
        string rid = "osx-arm64",
        IReadOnlyList<PackageFormat>? formats = null,
        string? license = null) => new()
        {
            ProductName = "ExampleApp",
            LicenseFile = license,
            Identifier = "com.example.app",
            Version = "1.0.0",
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.Pkg]
                }
            ]
        };

    // MAC-PKG-2: distribution upgrade — stub the toolchain, assert the chain
    // runs pkgbuild → productbuild with a generated distribution.xml.
    static async Task DistributionUpgrade()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacPkgProcessRunner.Request>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        // The pipeline deletes the work directory after each step, so the
        // distribution document must be read inside the productbuild call.
        var distribution = "";
        var productbuildArgs = new List<string>();
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            if (request.Executable == "productbuild")
            {
                productbuildArgs = request.Arguments.ToList();
                distribution = File.ReadAllText(
                    productbuildArgs[productbuildArgs.IndexOf("--distribution") + 1]);
            }
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var artifacts = await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Title = "Installer Title",
                    WelcomeFile = CreateTextFile(input, "welcome.html", "<b>hi</b>"),
                    ConclusionFile = CreateTextFile(input, "conclusion.rtf", "done"),
                })
                .BuildAsync(PkgConfiguration(input, output, license: CreateTextFile(input, "license.txt", "EULA")));
            var pkg = artifacts.Single(artifact => artifact.Format == PackageFormat.Pkg);
            var productbuild = requests.SingleOrDefault(request => request.Executable == "productbuild");
            Assert(productbuild != null,
                "Distribution settings must upgrade the output to a productbuild call.");
            var pargs = productbuildArgs;
            Assert(pargs[0] == "--distribution" && pargs.Contains("--package-path") &&
                   pargs[pargs.Count - 1] == pkg.Path,
                $"Unexpected productbuild arguments: {string.Join(' ', pargs)}");
            Assert(distribution.Contains("<installer-gui-script"),
                "distribution.xml must be an installer-gui-script document.");
            Assert(distribution.Contains("<title>Installer Title</title>"),
                "distribution.xml must carry the configured title.");
            Assert(distribution.Contains("<welcome file=\"welcome.html\" mime-type=\"text/html\"/>"),
                "distribution.xml must reference the welcome page.");
            Assert(distribution.Contains("<license file=\"license.txt\" mime-type=\"text/plain\"/>"),
                "distribution.xml must reference the license page (bundle LicenseFile).");
            Assert(distribution.Contains("<conclusion file=\"conclusion.rtf\" mime-type=\"text/richtext\"/>"),
                "distribution.xml must reference the conclusion page.");
            Assert(distribution.Contains("<pkg-ref id=\"com.example.app\" version=\"1.0.0\""),
                "distribution.xml must reference the component package.");
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var bargs = pkgbuild.Arguments.ToList();
            Assert(bargs[bargs.Count - 1].EndsWith("component.pkg", StringComparison.Ordinal),
                "Under a distribution package pkgbuild must emit an intermediate component.pkg.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task NoDistributionByDefault()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacPkgProcessRunner.Request>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler().BuildAsync(PkgConfiguration(input, output));
            Assert(requests.All(request => request.Executable != "productbuild"),
                "Without distribution settings the backend must not invoke productbuild.");
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            Assert(pkgbuild.Arguments[pkgbuild.Arguments.Count - 1]
                       .EndsWith(Path.Combine("pkg", "ExampleApp.pkg"), StringComparison.Ordinal),
                "Without distribution settings pkgbuild writes the final .pkg directly.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task CurrentUserHomeDomain()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacPkgProcessRunner.Request>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        var distribution = "";
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            if (request.Executable == "productbuild")
            {
                var pargs = request.Arguments.ToList();
                distribution = File.ReadAllText(
                    pargs[pargs.IndexOf("--distribution") + 1]);
            }
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Domain = MacPkgInstallDomain.CurrentUserHome
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert(requests.Any(request => request.Executable == "productbuild"),
                "A non-default domain must upgrade to a productbuild call.");
            Assert(distribution.Contains("enable_currentUserHome=\"true\"") &&
                   distribution.Contains("enable_localSystem=\"false\""),
                $"The current-user-home domain must be declared, got: {distribution}");
            Assert(distribution.Contains("<title>ExampleApp</title>"),
                "The title must default to the product name.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task DistributionResources()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var resources = new List<string>();
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            if (request.Executable == "productbuild")
            {
                var pargs = request.Arguments.ToList();
                var dir = pargs[pargs.IndexOf("--resources") + 1];
                resources.AddRange(Directory.GetFiles(dir).Select(Path.GetFileName)!);
            }
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    WelcomeFile = CreateTextFile(input, "welcome.txt", "hi"),
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert(resources.Contains("welcome.txt"),
                $"The welcome page must be copied into the resources dir, got: {string.Join(',', resources)}");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static void MissingWelcomeRejected()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var thrown = false;
            try
            {
                new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        WelcomeFile = Path.Combine(input, "absent.html")
                    })
                    .BuildAsync(PkgConfiguration(input, output))
                    .GetAwaiter().GetResult();
            }
            catch (FileNotFoundException)
            {
                thrown = true;
            }
            Assert(thrown, "A missing welcome page file must fail the build.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static string CreateTextFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    static string CreateInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "ExampleApp"), FakeMachO());
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        return input;
    }

    static byte[] FakeMachO(uint cpuType = 0x0100000C)
    {
        var bytes = new byte[64];
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
        bytes[4] = (byte)cpuType; bytes[5] = (byte)(cpuType >> 8);
        bytes[6] = (byte)(cpuType >> 16); bytes[7] = (byte)(cpuType >> 24);
        return bytes;
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

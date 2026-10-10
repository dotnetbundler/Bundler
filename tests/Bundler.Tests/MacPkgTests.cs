using DotNet.Bundler;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacPkg;

public static class MacPkgTests
{

    [Fact]
    static void RejectsNonPkgFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var wrongTarget = Assert.ThrowsAny<NotSupportedException>(
                () => new MacPkgBundler()
                    .BuildAsync(PkgConfiguration(input, formats: [PackageFormat.App]))
                    .GetAwaiter().GetResult());
            Assert.Contains("Pkg targets only", wrongTarget.Message);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsNonMacOsHost()
    {
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => false;
        try
        {
            var wrongHost = Assert.ThrowsAny<NotSupportedException>(
                () => new MacPkgBundler()
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
            Assert.Contains("macOS host", wrongHost.Message);
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
    static void HostGateIsPlatformNotSupported()
    {
        // 宿主门禁标记类型：入口层靠它区分“逐格式容错”与“配置错误聚合”。
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => false;
        try
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new MacPkgBundler().Validate(PkgConfiguration(input)));
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
    static void ValidateCoversEmbeddedAppKnobs()
    {
        // pkg 的构建管线内嵌 .app 阶段——其旋钮错也必须在预检趟报出。
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.Throws<ArgumentException>(
                () => new MacPkgBundler(
                    appConfiguration: new MacAppBundleConfiguration { ExceptionDomain = " " })
                    .Validate(PkgConfiguration(input)));
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsRelativeInstallLocation()
    {
        var input = CreateInputDirectory();
        var previous = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            var relativeLocation = Assert.ThrowsAny<ArgumentException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        InstallRoot = "usr/local"
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
            Assert.Contains("absolute path", relativeLocation.Message);
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
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
            Assert.Equal(2, artifacts.Count);
            var pkg = artifacts.Single(artifact => artifact.Format == PackageFormat.Pkg);
            Assert.EndsWith("ExampleApp-1.0.0-arm64.pkg", pkg.Path);

            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var args = pkgbuild.Arguments.ToList();
            Assert.True(args.Contains("--install-location") && args.Contains("--identifier") &&
                   args.Contains("--version") && args.Contains("--ownership"),
                $"pkgbuild arguments must carry install-location/identifier/version/ownership: {string.Join(' ', args)}");
            Assert.Equal("/Applications", args[args.IndexOf("--install-location") + 1]);
            Assert.Equal("com.example.app", args[args.IndexOf("--identifier") + 1]);
            Assert.Equal("1.0.0", args[args.IndexOf("--version") + 1]);
            Assert.Equal("recommended", args[args.IndexOf("--ownership") + 1]);
            Assert.Equal(pkg.Path, args[args.Count - 1]);
            Assert.Contains("ExampleApp.app", staged);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
                    PackageName = "com.example.pkg.override",
                    Version = "2.5.1",
                    InstallRoot = "/usr/local"
                })
                .BuildAsync(PkgConfiguration(input, output));
            var args = requests.Single(request => request.Executable == "pkgbuild").Arguments.ToList();
            Assert.Equal("com.example.pkg.override", args[args.IndexOf("--identifier") + 1]);
            Assert.Equal("2.5.1", args[args.IndexOf("--version") + 1]);
            Assert.Equal("/usr/local", args[args.IndexOf("--install-location") + 1]);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
                    Files =
                    [
                        new MacPkgFileEntry { Source = extra, Destination = "support/helper.txt" }
                    ]
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert.True(staged.Any(path => path.Replace('\\', '/') == "support/helper.txt"),
                $"The payload item must be staged at its destination, got: {string.Join(',', staged)}");
            Assert.Contains(staged, path => path.Contains("ExampleApp.app"));
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // A unix socket (or any other non-regular file) inside a payload directory
    // must be skipped rather than copied into the package root.
    [Fact]
    static async Task SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "helper.txt"), "helper");
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
            var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
            using (var socket = new System.Net.Sockets.Socket(
                       System.Net.Sockets.AddressFamily.Unix,
                       System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
                // .NET unlinks the bound path on dispose; move the node while bound
                File.Move(bindPath, Path.Combine(payload, "agent.sock"));
            }
            await new MacPkgBundler(new MacPkgBundleConfiguration
            {
                Files =
                [
                    new MacPkgFileEntry { Source = payload, Destination = "support" }
                ]
            }).BuildAsync(PkgConfiguration(input, output));
            Assert.True(staged.Any(path => path.Replace('\\', '/') == "support/helper.txt"),
                $"regular files still stage: {string.Join(',', staged)}");
            Assert.False(staged.Any(path => path.Contains("agent.sock")), "a unix socket must not be staged");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output, payload);
        }
    }

    [Fact]
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
            Assert.ThrowsAny<FileNotFoundException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        Files =
                        [
                            new MacPkgFileEntry { Source = Path.Combine(input, "missing.bin") }
                        ]
                    })
                    .BuildAsync(PkgConfiguration(input, output))
                    .GetAwaiter().GetResult());
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
    [Fact]
    static void NoPayloadRejected()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.ThrowsAny<DirectoryNotFoundException>(
                () =>
                {
                    var configuration = PkgConfiguration(input, output);
                    var item = new BundlePlanItem(
                        new BundleTarget("macos-arm64", DesktopOperatingSystem.MacOS, CpuArchitecture.Arm64),
                        PackageFormat.Pkg,
                        input,
                        "ExampleApp",
                        output,
                        Intermediate: false);
                    new MacPkgBundleBackend(new MacPkgBundleConfiguration())
                        .BuildAsync(new BundleBuildContext(
                            configuration, item, Path.Combine(output, "work"), new SilentLogger()),
                            CancellationToken.None)
                        .GetAwaiter().GetResult();
                });
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

    [Fact]
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
            var pkgbuildFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new MacPkgBundler().BuildAsync(PkgConfiguration(input, output)));
            Assert.Contains("pkgbuild", pkgbuildFailure.Message);
            var pkgDirectory = output;
            Assert.True(!Directory.Exists(pkgDirectory) ||
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

    [Fact]
    static void MapsPkgSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("MacPkgPackageName=\"$(BundlerMacPkgPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgVersion=\"$(BundlerMacPkgVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgInstallRoot=\"$(BundlerMacPkgInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("MacPkgFiles=\"@(BundlerMacPkgFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMacPkg* properties to the task.");
        Assert.Contains("<BundlerMacPkgInstallRoot Condition=\"'$(BundlerMacPkgInstallRoot)' == ''\">/Applications<", props);
        Assert.True(task.Contains("new MacPkgBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Pkg", StringComparison.Ordinal),
            "The MSBuild task does not construct the .pkg backend.");
    }

    // R2 N1: target=macos-universal 分发包的 hostArchitectures 必须是逗号分隔（Apple 约定）。
    [Fact]
    static async Task HostArchitecturesCommaSeparated()
    {
        var input = CreateInputDirectory();
        // macos-universal 通用载荷要求 fat Mach-O（x86_64+arm64）。
        File.WriteAllBytes(Path.Combine(input, "ExampleApp"), FakeFatMachO(0x01000007, 0x0100000C));
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var distribution = "";
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            if (request.Executable == "productbuild")
            {
                var args = request.Arguments.ToList();
                distribution = File.ReadAllText(args[args.IndexOf("--distribution") + 1]);
            }
            var pkgPath = request.Arguments[request.Arguments.Count - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            File.WriteAllText(pkgPath, "pkg");
            return Task.FromResult(new MacPkgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration { Title = "T" })
                .BuildAsync(PkgConfiguration(input, output, target: "macos-universal"));
            Assert.Contains("hostArchitectures=\"x86_64,arm64\"", distribution);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // R2 N5: 空字符串页文件/标题按未配置处理——不炸 GetFullPath("")，也不误升分发包。
    [Fact]
    static async Task EmptyPageFilesStayComponentPkg()
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
            var artifacts = await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Title = "", WelcomeFile = "", ConclusionFile = ""
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert.True(File.Exists(artifacts.Single(a => a.Format == PackageFormat.Pkg).Path));
            Assert.DoesNotContain(requests, r => r.Executable == "productbuild");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // R2 N5: 空 WelcomeFile 混真实 ConclusionFile 时——分发包照出、welcome 元素缺席、title 回退产品名。
    [Fact]
    static async Task EmptyPageFileSkippedInDistribution()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var distribution = "";
        var previousHost = MacPkgBundleBackend.HostCheck;
        var previous = MacPkgProcessRunner.Handler;
        MacPkgBundleBackend.HostCheck = () => true;
        MacPkgProcessRunner.Handler = (request, _) =>
        {
            if (request.Executable == "productbuild")
            {
                var args = request.Arguments.ToList();
                distribution = File.ReadAllText(args[args.IndexOf("--distribution") + 1]);
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
                    Title = "", WelcomeFile = "",
                    ConclusionFile = CreateTextFile(input, "conclusion.rtf", "done")
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert.Contains("<title>ExampleApp</title>", distribution);
            Assert.DoesNotContain("<welcome", distribution);
            Assert.Contains("<conclusion file=\"conclusion.rtf\" mime-type=\"text/richtext\"/>", distribution);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // R2 残留尾巴：纯空白 payload.Destination 同样拒绝。
    [Fact]
    static async Task RejectsWhitespacePayloadDestination()
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
            var error = await Assert.ThrowsAsync<ArgumentException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Files =
                    [
                        new MacPkgFileEntry
                        {
                            Source = Path.Combine(input, "ExampleApp.dll"),
                            Destination = "   "
                        }
                    ]
                }).BuildAsync(PkgConfiguration(input, output)));
            Assert.Contains("destination", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // R2: CopyTree 遇目录符号链接环显式报错，不再递归失控。
    [Fact]
    static void CopyTreeRejectsSymlinkLoop()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires symlink support");
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var destination = root + ".out";
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateSymbolicLink(Path.Combine(root, "sub", "loop"), root);
            // macOS 内核 MAXSYMLINKS=32 先于托管跳数上限报 ELOOP（IOException），
            // Linux 走托管判定 InvalidDataException（非 IOException 子类）——两种都接受。
            var error = Record.Exception(
                () => MacPkgBundleBackend.CopyTree(root, destination, NullBundleLogger.Instance));
            Assert.True(error is InvalidDataException or IOException,
                $"环链必须被拒：期望 InvalidDataException/IOException，实际 {error?.GetType().Name}: {error?.Message}");
        }
        finally
        {
            Cleanup(root, destination);
        }
    }

    static BundleConfiguration PkgConfiguration(
        string input,
        string output = "",
        string target = "macos-arm64",
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
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.Pkg]
                }
            ]
        };

    // MAC-PKG-2: distribution upgrade — stub the toolchain, assert the chain
    // runs pkgbuild → productbuild with a generated distribution.xml.
    [Fact]
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
            Assert.NotNull(productbuild);
            var pargs = productbuildArgs;
            Assert.True(pargs[0] == "--distribution" && pargs.Contains("--package-path") &&
                   pargs[pargs.Count - 1] == pkg.Path,
                $"Unexpected productbuild arguments: {string.Join(' ', pargs)}");
            Assert.Contains("<installer-gui-script", distribution);
            Assert.Contains("<title>Installer Title</title>", distribution);
            Assert.Contains("<welcome file=\"welcome.html\" mime-type=\"text/html\"/>", distribution);
            Assert.Contains("<license file=\"license.txt\" mime-type=\"text/plain\"/>", distribution);
            Assert.Contains("<conclusion file=\"conclusion.rtf\" mime-type=\"text/richtext\"/>", distribution);
            Assert.Contains("<pkg-ref id=\"com.example.app\" version=\"1.0.0\"", distribution);
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var bargs = pkgbuild.Arguments.ToList();
            Assert.EndsWith("component.pkg", bargs[bargs.Count - 1]);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.True(requests.All(request => request.Executable != "productbuild"),
                "Without distribution settings the backend must not invoke productbuild.");
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            Assert.EndsWith("ExampleApp-1.0.0-arm64.pkg", pkgbuild.Arguments[pkgbuild.Arguments.Count - 1]);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
                    InstallScope = MacPkgInstallScope.CurrentUserHome
                })
                .BuildAsync(PkgConfiguration(input, output));
            Assert.Contains(requests, request => request.Executable == "productbuild");
            Assert.True(distribution.Contains("enable_currentUserHome=\"true\"") &&
                   distribution.Contains("enable_localSystem=\"false\""),
                $"The current-user-home domain must be declared, got: {distribution}");
            Assert.Contains("<title>ExampleApp</title>", distribution);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Contains("welcome.txt", resources);
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.ThrowsAny<FileNotFoundException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        WelcomeFile = Path.Combine(input, "absent.html")
                    })
                    .BuildAsync(PkgConfiguration(input, output))
                    .GetAwaiter().GetResult());
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    // MAC-PKG-3: signing / notarization / scripts knob.

    [Fact]
    static async Task SignsComponentPackage()
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
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Signing = new MacPkgSigningConfiguration
                    {
                        Identity = "Developer ID Installer: Example"
                    }
                })
                .BuildAsync(PkgConfiguration(input, output));
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var args = pkgbuild.Arguments.ToList();
            var signIndex = args.IndexOf("--sign");
            Assert.True(signIndex > 0 && args[signIndex + 1] == "Developer ID Installer: Example",
                $"pkgbuild must sign the component package, got: {string.Join(' ', args)}");
            Assert.Contains("--timestamp", args);
            Assert.DoesNotContain("--keychain", args);
            Assert.True(requests.All(request => request.Executable != "productsign"),
                "A component package is signed inside pkgbuild; productsign must not run.");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task SignsDistributionPackage()
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
            if (request.Executable == "productsign")
            {
                // productsign reads <unsigned.pkg> and writes <output.pkg>: emulate the rename.
                File.WriteAllText(request.Arguments[request.Arguments.Count - 1], "pkg");
            }
            else
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
                    Title = "T",
                    Signing = new MacPkgSigningConfiguration
                    {
                        Identity = "Developer ID Installer: Example"
                    }
                })
                .BuildAsync(PkgConfiguration(input, output));
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            Assert.DoesNotContain("--sign", pkgbuild.Arguments);
            var productsign = requests.Single(request => request.Executable == "productsign");
            var args = productsign.Arguments.ToList();
            Assert.True(args[0] == "--sign" && args[1] == "Developer ID Installer: Example" &&
                   args[args.Count - 2].EndsWith("unsigned.pkg", StringComparison.Ordinal) &&
                   args[args.Count - 1].EndsWith(".pkg", StringComparison.Ordinal),
                $"Unexpected productsign arguments: {string.Join(' ', args)}");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsAdHocIdentity()
    {
        var input = CreateInputDirectory();
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.ThrowsAny<ArgumentException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        Signing = new MacPkgSigningConfiguration { Identity = "-" }
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsExclusiveSigning()
    {
        var input = CreateInputDirectory();
        var certificate = CreateTextFile(input, "cert.p12", "p12");
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.ThrowsAny<ArgumentException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        Signing = new MacPkgSigningConfiguration
                        {
                            Identity = "Developer ID Installer: Example",
                            TemporaryCertificateFile = certificate
                        }
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsNotarizeWithoutSigning()
    {
        var input = CreateInputDirectory();
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.ThrowsAny<ArgumentException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        Signing = new MacPkgSigningConfiguration { Notarize = true }
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input);
        }
    }

    [Fact]
    static async Task PassesScriptsToPkgbuild()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(input, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "postinstall"), "#!/bin/sh\nexit 0\n");
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
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    ScriptsDirectory = scripts
                })
                .BuildAsync(PkgConfiguration(input, output));
            var pkgbuild = requests.Single(request => request.Executable == "pkgbuild");
            var args = pkgbuild.Arguments.ToList();
            var index = args.IndexOf("--scripts");
            Assert.True(index > 0 && args[index + 1] == Path.GetFullPath(scripts),
                $"--scripts must forward the configured directory, got: {string.Join(' ', args)}");
        }
        finally
        {
            MacPkgProcessRunner.Handler = previous;
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MissingScriptsRejected()
    {
        var input = CreateInputDirectory();
        var previousHost = MacPkgBundleBackend.HostCheck;
        MacPkgBundleBackend.HostCheck = () => true;
        try
        {
            Assert.ThrowsAny<DirectoryNotFoundException>(
                () => new MacPkgBundler(new MacPkgBundleConfiguration
                    {
                        ScriptsDirectory = Path.Combine(input, "absent-scripts")
                    })
                    .BuildAsync(PkgConfiguration(input))
                    .GetAwaiter().GetResult());
        }
        finally
        {
            MacPkgBundleBackend.HostCheck = previousHost;
            Cleanup(input);
        }
    }

    [Fact]
    static async Task NotarizesPackage()
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
            var last = request.Arguments[request.Arguments.Count - 1];
            if (last.EndsWith(".pkg", StringComparison.Ordinal) &&
                request.Executable is "pkgbuild" or "productbuild" or "productsign")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(last)!);
                File.WriteAllText(last, "pkg");
            }
            var stdout = request.Executable == "xcrun" &&
                         request.Arguments.Contains("notarytool")
                ? "{\"id\":\"123e4567-e89b-12d3-a456-426614174000\"}"
                : "";
            return Task.FromResult(new MacPkgProcessRunner.Result(0, stdout, ""));
        };
        try
        {
            await new MacPkgBundler(new MacPkgBundleConfiguration
                {
                    Signing = new MacPkgSigningConfiguration
                    {
                        Identity = "Developer ID Installer: Example",
                        Notarize = true,
                        KeychainProfile = "test-profile"
                    }
                })
                .BuildAsync(PkgConfiguration(input, output));
            var submit = requests.Single(request =>
                request.Executable == "xcrun" && request.Arguments.Contains("submit"));
            var args = submit.Arguments.ToList();
            Assert.True(args.Contains("--keychain-profile") && args.Contains("test-profile") &&
                   args.Contains("--wait") && args.Any(a => a.EndsWith(".pkg", StringComparison.Ordinal)),
                $"The .pkg itself must be submitted, got: {string.Join(' ', args)}");
            Assert.True(requests.Any(request =>
                    request.Executable == "xcrun" && request.Arguments.Contains("stapler")),
                "stapler must attach the ticket to the .pkg.");
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

    static byte[] FakeFatMachO(params uint[] cpuTypes)
    {
        var bytes = new byte[8 + 20 * cpuTypes.Length];
        bytes[0] = 0xCA; bytes[1] = 0xFE; bytes[2] = 0xBA; bytes[3] = 0xBE;
        bytes[7] = (byte)cpuTypes.Length;
        for (var index = 0; index < cpuTypes.Length; index++)
        {
            var offset = 8 + index * 20;
            bytes[offset] = (byte)(cpuTypes[index] >> 24);
            bytes[offset + 1] = (byte)(cpuTypes[index] >> 16);
            bytes[offset + 2] = (byte)(cpuTypes[index] >> 8);
            bytes[offset + 3] = (byte)cpuTypes[index];
        }
        return bytes;
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


}

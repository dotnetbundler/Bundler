using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacDmg;
using System.Runtime.InteropServices;

internal static class MacDmgTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-DMG formats", () => RunSync(RejectsNonDmgFormats));
            yield return ("Rejects non-macOS hosts for DMG", () => RunSync(RejectsNonMacOsHost));
            yield return ("Rejects an empty DMG volume name", () => RunSync(RejectsEmptyVolumeName));
            yield return ("Runs the full hdiutil chain on stubbed tools", RunsHdiutilChainOnStubbedTools);
            yield return ("Maps the compression enum to hdiutil formats", MapsCompressionFormats);
            yield return ("Retries a busy detach with backoff", RetriesBusyDetach);
            yield return ("Fails cleanly after detach retries are exhausted", DetachExhaustionCleansUp);
            yield return ("Leaves no .dmg artifact when convert fails", ConvertFailureLeavesNoArtifact);
            yield return ("Runs the Finder layout with upstream defaults", RunsFinderLayoutDefaults);
            yield return ("Degrades the Finder layout without a GUI session", DegradesLayoutWithoutGui);
            yield return ("SkipWindowLayout skips osascript", SkipWindowLayoutSkipsOsascript);
            yield return ("Stages background and volume icon", StagesBrandingFiles);
            yield return ("Rejects a missing background image", MissingBackgroundRejected);
            yield return ("Maps .dmg settings through MSBuild", () => RunSync(MapsDmgSettingsThroughMsBuild));
        }
    }

    static void RejectsNonDmgFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var thrown = false;
            try
            {
                new MacDmgBundler()
                    .BuildAsync(DmgConfiguration(input, formats: [PackageFormat.App]))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("Dmg targets only");
            }
            Assert(thrown, "A non-DMG target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void RejectsNonMacOsHost()
    {
        var input = CreateInputDirectory();
        var previous = MacDmgBundleBackend.HostCheck;
        MacDmgBundleBackend.HostCheck = () => false;
        try
        {
            var thrown = false;
            try
            {
                new MacDmgBundler()
                    .BuildAsync(DmgConfiguration(input))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("macOS host");
            }
            Assert(thrown, "DMG builds must be rejected off macOS hosts.");
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    static void RejectsEmptyVolumeName()
    {
        var input = CreateInputDirectory();
        var previous = MacDmgBundleBackend.HostCheck;
        MacDmgBundleBackend.HostCheck = () => true;
        try
        {
            var thrown = false;
            try
            {
                new MacDmgBundler(new MacDmgBundleConfiguration { VolumeName = "  " })
                    .BuildAsync(DmgConfiguration(input))
                    .GetAwaiter().GetResult();
            }
            catch (ArgumentException exception)
            {
                thrown = exception.Message.Contains("volume name");
            }
            Assert(thrown, "An empty DMG volume name must be rejected.");
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    static async Task RunsHdiutilChainOnStubbedTools()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacDmgProcessRunner.Request>();
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            // The convert step's -o artifact must exist for the pipeline artifact check.
            if (request.Arguments.Contains("convert"))
            {
                var index = request.Arguments.ToList().IndexOf("-o");
                var path = request.Arguments[index + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "dmg");
            }
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var artifacts = await new MacDmgBundler()
                .BuildAsync(DmgConfiguration(input, output));
            Assert(artifacts.Count == 2,
                $"A dmg request must produce the intermediate .app plus the .dmg, got {artifacts.Count}.");
            var dmg = artifacts.Single(artifact => artifact.Format == PackageFormat.Dmg);
            Assert(dmg.Path.EndsWith(Path.Combine("osx-arm64", "dmg", "ExampleApp.dmg"), StringComparison.Ordinal),
                $"Unexpected .dmg artifact path: {dmg.Path}");

            var joined = string.Join("\n", requests.Select(request =>
                request.Executable + " " + string.Join(' ', request.Arguments)));
            var order = requests.Select(request => request.Executable + " " +
                string.Join(' ', request.Arguments.Take(1))).ToList();

            Assert(order[0] == "ln -s" &&
                   requests[0].Arguments.Last().EndsWith("/Applications", StringComparison.Ordinal),
                $"The /Applications drop link must be staged first, got: {order[0]}");
            Assert(order.Any(step => step == "hdiutil create") &&
                   order.Any(step => step == "hdiutil attach") &&
                   order.Any(step => step == "hdiutil detach") &&
                   order.Any(step => step == "hdiutil convert"),
                $"Missing hdiutil steps in:\n{joined}");
            var createArgs = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("create"));
            Assert(createArgs.Arguments.Contains("UDRW") && createArgs.Arguments.Contains("-srcfolder"),
                "The master image must be a UDRW read-write image from -srcfolder.");
            var convertArgs = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("convert"));
            Assert(convertArgs.Arguments.Contains("ULMO"),
                "The default compression must map to hdiutil -format ULMO.");
            var convertArgList = convertArgs.Arguments.ToList();
            var outputFlag = convertArgList.IndexOf("-o");
            Assert(outputFlag >= 0 && convertArgList[outputFlag + 1] == dmg.Path,
                "The convert output must be written via -o <artifact path>.");
            Assert(requests.Any(request => request.Executable == "SetFile"),
                "SetFile should hide the .app extension inside the mounted volume.");
            Assert(order.IndexOf("hdiutil attach") < order.IndexOf("hdiutil detach") &&
                   order.IndexOf("hdiutil detach") < order.LastIndexOf("hdiutil convert"),
                "attach → detach → convert ordering is required.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task MapsCompressionFormats()
    {
        foreach (var (compression, expected) in new[]
                 {
                     (MacDmgCompression.Udzo, "UDZO"),
                     (MacDmgCompression.Ulmo, "ULMO"),
                     (MacDmgCompression.Udbz, "UDBZ")
                 })
        {
            var input = CreateInputDirectory();
            var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
            var previousHost = MacDmgBundleBackend.HostCheck;
            var previous = MacDmgProcessRunner.Handler;
            MacDmgBundleBackend.HostCheck = () => true;
            var requests = new List<MacDmgProcessRunner.Request>();
            MacDmgProcessRunner.Handler = (request, _) =>
            {
                requests.Add(request);
                if (request.Arguments.Contains("convert"))
                {
                    var index = request.Arguments.ToList().IndexOf("-o");
                    var path = request.Arguments[index + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "dmg");
                }
                return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
            };
            try
            {
                await new MacDmgBundler(new MacDmgBundleConfiguration
                    {
                        Compression = compression,
                        VolumeName = "Custom Volume"
                    })
                    .BuildAsync(DmgConfiguration(input, output));
                var convertArgs = requests.First(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("convert"));
                Assert(convertArgs.Arguments.Contains(expected),
                    $"Compression {compression} must map to -format {expected}.");
                var createArgs = requests.First(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("create"));
                Assert(createArgs.Arguments.Contains("Custom Volume"),
                    "A configured volume name must reach hdiutil create -volname.");
            }
            finally
            {
                MacDmgProcessRunner.Handler = previous;
                MacDmgBundleBackend.HostCheck = previousHost;
                Cleanup(input, output);
            }
        }
    }

    static async Task RetriesBusyDetach()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        var detachAttempts = 0;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            if (request.Arguments.Contains("detach") && !request.Arguments.Contains("-force"))
            {
                detachAttempts++;
                return Task.FromResult(new MacDmgProcessRunner.Result(
                    detachAttempts < 3 ? 1 : 0, "", detachAttempts < 3 ? "Resource busy" : ""));
            }
            if (request.Arguments.Contains("convert"))
            {
                var index = request.Arguments.ToList().IndexOf("-o");
                var path = request.Arguments[index + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "dmg");
            }
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler().BuildAsync(DmgConfiguration(input, output));
            Assert(detachAttempts == 3,
                $"A busy detach must retry with backoff until it succeeds; got {detachAttempts} attempts.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task DetachExhaustionCleansUp()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        var forcedDetach = false;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            if (request.Arguments.Contains("detach") && request.Arguments.Contains("-force"))
            {
                forcedDetach = true;
            }
            return Task.FromResult(new MacDmgProcessRunner.Result(
                request.Arguments.Contains("detach") && !request.Arguments.Contains("-force") ? 1 : 0,
                "", "Resource busy"));
        };
        try
        {
            var thrown = false;
            try
            {
                await new MacDmgBundler().BuildAsync(DmgConfiguration(input, output));
            }
            catch (InvalidOperationException exception)
            {
                thrown = exception.Message.Contains("detach");
            }
            Assert(thrown, "Detach retries must give up after the backoff budget.");
            Assert(forcedDetach, "A leftover mounted volume must be force-detached on failure.");
            Assert(!Directory.EnumerateFiles(
                    Path.Combine(output, "osx-arm64", "dmg"), "*", SearchOption.AllDirectories).Any(),
                "A failed DMG build must not leave an output artifact.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task ConvertFailureLeavesNoArtifact()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
            Task.FromResult(new MacDmgProcessRunner.Result(
                request.Arguments.Contains("convert") ? 2 : 0, "", "convert failed"));
        try
        {
            var thrown = false;
            try
            {
                await new MacDmgBundler().BuildAsync(DmgConfiguration(input, output));
            }
            catch (InvalidOperationException)
            {
                thrown = true;
            }
            Assert(thrown, "A failed hdiutil convert must surface as an error.");
            var dmgDirectory = Path.Combine(output, "osx-arm64", "dmg");
            Assert(!Directory.Exists(dmgDirectory) ||
                   !Directory.EnumerateFiles(dmgDirectory, "*.dmg").Any(),
                "A failed convert must not leave a .dmg artifact.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task RunsFinderLayoutDefaults()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        var requests = new List<MacDmgProcessRunner.Request>();
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            CreateMountPoint(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler().BuildAsync(DmgConfiguration(input, output));
            var osascript = requests.Single(request => request.Executable == "osascript");
            var script = string.Join("\n", osascript.Arguments);
            Assert(script.Contains("{200, 120, 860, 520}"),
                $"Default window bounds must be 200,120 + 660x400, got:\n{script}");
            Assert(script.Contains("{180, 170}") && script.Contains("{480, 170}"),
                $"Default icon positions must be app=180,170 / Applications=480,170, got:\n{script}");
            Assert(script.Contains("set icon size of theViewOptions to 128"),
                $"Default icon size must be 128, got:\n{script}");
            Assert(script.Contains("tell disk \"ExampleApp\""),
                "The layout script must target the configured volume name.");
            Assert(requests.Any(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("resize")),
                "The read-write image must gain headroom before the layout pass.");
            Assert(!script.Contains("background picture"),
                "No background picture statement is expected without a background file.");
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert(!attach.Arguments.Contains("-nobrowse"),
                "Finder needs visibility: -nobrowse must be absent while the layout pass runs.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task DegradesLayoutWithoutGui()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            CreateMountPoint(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(
                request.Executable == "osascript" ? 1 : 0, "", "no GUI session"));
        };
        var warnings = new List<string>();
        try
        {
            var artifacts = await new MacDmgBundler(
                    dmgConfiguration: new MacDmgBundleConfiguration(),
                    appConfiguration: null,
                    options: new MacDmgBundlerOptions
                    {
                        Logger = new ListLogger(warnings)
                    })
                .BuildAsync(DmgConfiguration(input, output));
            Assert(artifacts.Count == 2, "A headless host must still produce the .dmg.");
            Assert(warnings.Any(message => message.Contains("GUI session")),
                $"A missing GUI session must degrade to a warning, got: {string.Join(" | ", warnings)}");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task SkipWindowLayoutSkipsOsascript()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        var requests = new List<MacDmgProcessRunner.Request>();
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            CreateMountPoint(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler(new MacDmgBundleConfiguration { SkipWindowLayout = true })
                .BuildAsync(DmgConfiguration(input, output));
            Assert(!requests.Any(request => request.Executable == "osascript"),
                "SkipWindowLayout must not invoke osascript.");
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert(attach.Arguments.Contains("-nobrowse"),
                "SkipWindowLayout should keep the -nobrowse attach flag.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task StagesBrandingFiles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var background = Path.Combine(input, "bg.png");
        var volumeIcon = Path.Combine(input, "volume.icns");
        File.WriteAllText(background, "png");
        File.WriteAllText(volumeIcon, "icns");
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        var stagedBackground = false;
        var stagedVolumeIcon = false;
        var volumeIconFlagged = false;
        var mountDirectory = "";
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            CreateMountPoint(request);
            WriteConvertArtifact(request);
            var mountIndex = request.Arguments.ToList().IndexOf("-mountpoint");
            if (mountIndex >= 0)
            {
                mountDirectory = request.Arguments[mountIndex + 1];
            }
            if (request.Executable == "osascript")
            {
                stagedBackground = File.Exists(
                    Path.Combine(mountDirectory, ".background", "bg.png"));
                stagedVolumeIcon = File.Exists(
                    Path.Combine(mountDirectory, ".VolumeIcon.icns"));
            }
            if (request.Executable == "SetFile" && request.Arguments.Contains("C"))
            {
                volumeIconFlagged = true;
            }
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler(new MacDmgBundleConfiguration
                {
                    BackgroundFile = background,
                    VolumeIconFile = volumeIcon
                })
                .BuildAsync(DmgConfiguration(input, output));
            Assert(stagedBackground, "The background image must land in .background/ on the volume.");
            Assert(stagedVolumeIcon, "The volume icon must land as .VolumeIcon.icns on the volume.");
            Assert(volumeIconFlagged, "SetFile -a C must mark the volume for the custom icon.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static async Task MissingBackgroundRejected()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            CreateMountPoint(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var thrown = false;
            try
            {
                await new MacDmgBundler(new MacDmgBundleConfiguration
                    {
                        BackgroundFile = Path.Combine(input, "missing.png")
                    })
                    .BuildAsync(DmgConfiguration(input, output));
            }
            catch (FileNotFoundException)
            {
                thrown = true;
            }
            Assert(thrown, "A missing background image must fail the build.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    static void CreateMountPoint(MacDmgProcessRunner.Request request)
    {
        var index = request.Arguments.ToList().IndexOf("-mountpoint");
        if (index >= 0)
        {
            Directory.CreateDirectory(request.Arguments[index + 1]);
        }
    }

    static void WriteConvertArtifact(MacDmgProcessRunner.Request request)
    {
        if (request.Executable == "hdiutil" && request.Arguments.Contains("convert"))
        {
            var index = request.Arguments.ToList().IndexOf("-o");
            var path = request.Arguments[index + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "dmg");
        }
    }

    sealed class ListLogger(List<string> warnings) : IBundleLogger
    {
        public void Log(BundleLogLevel level, string message)
        {
            if (level == BundleLogLevel.Warning)
            {
                warnings.Add(message);
            }
        }
    }

    static void MapsDmgSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("MacDmgCompression=\"$(BundlerMacDmgCompression)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDmgVolumeName=\"$(BundlerMacDmgVolumeName)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMacDmg* properties to the task.");
        Assert(props.Contains("<BundlerMacDmgCompression Condition=\"'$(BundlerMacDmgCompression)' == ''\">Ulmo<", StringComparison.Ordinal),
            "The default BundlerMacDmgCompression must be Ulmo.");
        Assert(task.Contains("new MacDmgBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Dmg", StringComparison.Ordinal),
            "The MSBuild task does not construct the .dmg backend.");
    }

    static BundleConfiguration DmgConfiguration(
        string input,
        string output = "",
        string rid = "osx-arm64",
        IReadOnlyList<PackageFormat>? formats = null) => new()
        {
            ProductName = "ExampleApp",
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
                    Formats = formats ?? [PackageFormat.Dmg]
                }
            ]
        };

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

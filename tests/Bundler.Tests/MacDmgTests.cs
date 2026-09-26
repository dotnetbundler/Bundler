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

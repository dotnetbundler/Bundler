using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacDmg;
using System.Runtime.InteropServices;

public static class MacDmgTests
{

    [Fact]
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
            Assert.True(thrown, "A non-DMG target must be rejected with NotSupportedException.");
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
            Assert.True(thrown, "DMG builds must be rejected off macOS hosts.");
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
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
            Assert.True(thrown, "An empty DMG volume name must be rejected.");
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
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
            WriteDsStore(request);
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
            Assert.True(artifacts.Count == 2,
                $"A dmg request must produce the intermediate .app plus the .dmg, got {artifacts.Count}.");
            var dmg = artifacts.Single(artifact => artifact.Format == PackageFormat.Dmg);
            Assert.True(dmg.Path.EndsWith(Path.Combine("osx-arm64", "dmg", "ExampleApp.dmg"), StringComparison.Ordinal),
                $"Unexpected .dmg artifact path: {dmg.Path}");

            var joined = string.Join("\n", requests.Select(request =>
                request.Executable + " " + string.Join(' ', request.Arguments)));
            var order = requests.Select(request => request.Executable + " " +
                string.Join(' ', request.Arguments.Take(1))).ToList();

            Assert.True(order[0] == "ln -s" &&
                   requests[0].Arguments.Last().Replace('\\', '/').EndsWith("/Applications", StringComparison.Ordinal),
                $"The /Applications drop link must be staged first, got: {order[0]}");
            Assert.True(order.Any(step => step == "hdiutil create") &&
                   order.Any(step => step == "hdiutil attach") &&
                   order.Any(step => step == "hdiutil detach") &&
                   order.Any(step => step == "hdiutil convert"),
                $"Missing hdiutil steps in:\n{joined}");
            var createArgs = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("create"));
            Assert.True(createArgs.Arguments.Contains("UDRW") && createArgs.Arguments.Contains("-srcfolder"),
                "The master image must be a UDRW read-write image from -srcfolder.");
            var convertArgs = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("convert"));
            Assert.True(convertArgs.Arguments.Contains("ULMO"),
                "The default compression must map to hdiutil -format ULMO.");
            var convertArgList = convertArgs.Arguments.ToList();
            var outputFlag = convertArgList.IndexOf("-o");
            Assert.True(outputFlag >= 0 && convertArgList[outputFlag + 1] == dmg.Path,
                "The convert output must be written via -o <artifact path>.");
            Assert.True(requests.Any(request => request.Executable == "SetFile"),
                "SetFile should hide the .app extension inside the mounted volume.");
            Assert.True(order.IndexOf("hdiutil attach") < order.IndexOf("hdiutil detach") &&
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

    [Fact]
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
                WriteDsStore(request);
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
                Assert.True(convertArgs.Arguments.Contains(expected),
                    $"Compression {compression} must map to -format {expected}.");
                var createArgs = requests.First(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("create"));
                Assert.True(createArgs.Arguments.Contains("Custom Volume"),
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

    [Fact]
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
            WriteDsStore(request);
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
            Assert.True(detachAttempts == 3,
                $"A busy detach must retry with backoff until it succeeds; got {detachAttempts} attempts.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            WriteDsStore(request);
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
            Assert.True(thrown, "Detach retries must give up after the backoff budget.");
            Assert.True(forcedDetach, "A leftover mounted volume must be force-detached on failure.");
            Assert.True(!Directory.EnumerateFiles(
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

    [Fact]
    static async Task ConvertFailureLeavesNoArtifact()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            WriteDsStore(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(
                request.Arguments.Contains("convert") ? 2 : 0, "", "convert failed"));
        };
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
            Assert.True(thrown, "A failed hdiutil convert must surface as an error.");
            var dmgDirectory = Path.Combine(output, "osx-arm64", "dmg");
            Assert.True(!Directory.Exists(dmgDirectory) ||
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

    [Fact]
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
            WriteDsStore(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler().BuildAsync(DmgConfiguration(input, output));
            var osascript = requests.Single(request => request.Executable == "osascript");
            var script = string.Join("\n", osascript.Arguments);
            Assert.True(script.Contains("{200, 120, 860, 520}"),
                $"Default window bounds must be 200,120 + 660x400, got:\n{script}");
            Assert.True(script.Contains("{180, 170}") && script.Contains("{480, 170}"),
                $"Default icon positions must be app=180,170 / Applications=480,170, got:\n{script}");
            Assert.True(script.Contains("set icon size of theViewOptions to 128"),
                $"Default icon size must be 128, got:\n{script}");
            Assert.True(script.Contains("set theDisk to disk (name of (POSIX file") &&
                   script.Contains("dmg-mount") && script.Contains("tell theDisk"),
                $"The layout script must resolve the disk via the mount point, got:\n{script}");
            Assert.True(!script.Contains("tell disk \"ExampleApp\""),
                "Finder keys a custom-mountpoint disk by mount name, not volume name.");
            Assert.True(requests.Any(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("resize")),
                "The read-write image must gain headroom before the layout pass.");
            Assert.True(!script.Contains("background picture"),
                "No background picture statement is expected without a background file.");
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert.True(!attach.Arguments.Contains("-nobrowse"),
                "Finder needs visibility: -nobrowse must be absent while the layout pass runs.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            WriteDsStore(request);
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
            Assert.True(artifacts.Count == 2, "A headless host must still produce the .dmg.");
            Assert.True(warnings.Any(message => message.Contains("GUI session")),
                $"A missing GUI session must degrade to a warning, got: {string.Join(" | ", warnings)}");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            WriteDsStore(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler(new MacDmgBundleConfiguration { SkipWindowLayout = true })
                .BuildAsync(DmgConfiguration(input, output));
            Assert.True(!requests.Any(request => request.Executable == "osascript"),
                "SkipWindowLayout must not invoke osascript.");
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert.True(attach.Arguments.Contains("-nobrowse"),
                "SkipWindowLayout should keep the -nobrowse attach flag.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
        var stagedVolumeIconEarly = false;
        var volumeIconStagedLate = false;
        var volumeIconFlaggedLate = false;
        var osascriptSeen = false;
        var mountDirectory = "";
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            CreateMountPoint(request);
            WriteDsStore(request);
            WriteConvertArtifact(request);
            var mountIndex = request.Arguments.ToList().IndexOf("-mountpoint");
            if (mountIndex >= 0)
            {
                mountDirectory = request.Arguments[mountIndex + 1];
            }
            if (request.Executable == "osascript")
            {
                osascriptSeen = true;
                stagedBackground = File.Exists(
                    Path.Combine(mountDirectory, ".background", "bg.png"));
                stagedVolumeIconEarly = File.Exists(
                    Path.Combine(mountDirectory, ".VolumeIcon.icns"));
            }
            if (request.Executable == "SetFile" && request.Arguments.Contains("C"))
            {
                volumeIconFlaggedLate = osascriptSeen;
                volumeIconStagedLate = File.Exists(
                    Path.Combine(mountDirectory, ".VolumeIcon.icns"));
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
            Assert.True(stagedBackground, "The background image must land in .background/ on the volume.");
            Assert.True(!stagedVolumeIconEarly,
                "Finder strips a pre-staged .VolumeIcon.icns during the window pass; it must be copied after osascript.");
            Assert.True(volumeIconStagedLate,
                "The volume icon must land as .VolumeIcon.icns after the layout pass.");
            Assert.True(volumeIconFlaggedLate,
                "SetFile -a C must mark the volume after the layout pass ran.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
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
            WriteDsStore(request);
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
            Assert.True(thrown, "A missing background image must fail the build.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task InjectsLicenseThenSigns()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var license = Path.Combine(input, "license.txt");
        File.WriteAllText(license, "Test license.");
        var requests = new List<MacDmgProcessRunner.Request>();
        var slaPlist = "";
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            CreateMountPoint(request);
            WriteDsStore(request);
            WriteConvertArtifact(request);
            if (request.Arguments.Contains("udifrez"))
            {
                // The work directory is deleted after the build; capture the SLA
                // plist while the udifrez call is in flight.
                var args = request.Arguments.ToList();
                slaPlist = File.ReadAllText(args[args.IndexOf("-xml") + 1]);
            }
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacDmgBundler(new MacDmgBundleConfiguration
                {
                    SkipWindowLayout = true,
                    Signing = new MacDmgSigningConfiguration { Identity = "-" }
                })
                .BuildAsync(DmgConfiguration(input, output, licenseFile: license));
            var verbs = requests
                .Where(request => request.Executable == "hdiutil" || request.Executable == "codesign")
                .Select(request =>
                    request.Executable == "codesign"
                        ? "codesign " + (request.Arguments.Contains("--sign") ? "sign" : "verify")
                        : request.Arguments[0])
                .ToList();
            var convert = verbs.IndexOf("convert");
            var udifrez = verbs.IndexOf("udifrez");
            var sign = verbs.IndexOf("codesign sign");
            var verify = verbs.IndexOf("codesign verify");
            Assert.True(convert >= 0 && udifrez > convert && sign > udifrez && verify > sign,
                $"Expected convert → udifrez → codesign --sign → --verify order, got: {string.Join(",", verbs)}");
            var signCall = requests.First(request =>
                request.Executable == "codesign" && request.Arguments.Contains("--sign"));
            Assert.True(signCall.Arguments.Contains("--timestamp=none"),
                "Ad-hoc signatures must skip the timestamp server.");
            var frezCall = requests.First(request => request.Arguments.Contains("udifrez"));
            Assert.True(frezCall.Arguments.Contains("-xml") && frezCall.Arguments.Contains("-image"),
                "udifrez must take the generated SLA plist via -xml and the image via -image.");
            Assert.True(slaPlist.Contains("<key>TEXT</key>") && slaPlist.Contains("<key>STR#</key>") &&
                   slaPlist.Contains("<key>LPic</key>"),
                "The SLA plist must carry LPic/STR#/TEXT resources.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task RejectsConflictingSigning()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var certificate = Path.Combine(input, "cert.p12");
        File.WriteAllText(certificate, "cert");
        var previousHost = MacDmgBundleBackend.HostCheck;
        MacDmgBundleBackend.HostCheck = () => true;
        try
        {
            var thrown = false;
            try
            {
                await new MacDmgBundler(new MacDmgBundleConfiguration
                    {
                        Signing = new MacDmgSigningConfiguration
                        {
                            Identity = "-",
                            TemporaryCertificatePath = certificate
                        }
                    })
                    .BuildAsync(DmgConfiguration(input, output));
            }
            catch (ArgumentException exception)
            {
                thrown = exception.Message.Contains("mutually exclusive");
            }
            Assert.True(thrown, "Identity and TemporaryCertificatePath must be mutually exclusive.");
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task MissingLicenseRejected()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previousHost = MacDmgBundleBackend.HostCheck;
        var previous = MacDmgProcessRunner.Handler;
        MacDmgBundleBackend.HostCheck = () => true;
        MacDmgProcessRunner.Handler = (request, _) =>
        {
            CreateMountPoint(request);
            WriteDsStore(request);
            WriteConvertArtifact(request);
            return Task.FromResult(new MacDmgProcessRunner.Result(0, "", ""));
        };
        try
        {
            var thrown = false;
            try
            {
                await new MacDmgBundler(new MacDmgBundleConfiguration { SkipWindowLayout = true })
                    .BuildAsync(DmgConfiguration(
                        input, output, licenseFile: Path.Combine(input, "missing.txt")));
            }
            catch (BundleValidationException)
            {
                thrown = true;
            }
            Assert.True(thrown, "A missing license file must fail the build.");
        }
        finally
        {
            MacDmgProcessRunner.Handler = previous;
            MacDmgBundleBackend.HostCheck = previousHost;
            Cleanup(input, output);
        }
    }

    [Fact]
    static void BuildsSlaPlist()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var warnings = new List<string>();
            var logger = new ListLogger(warnings);
            var txt = Path.Combine(directory, "sla.txt");
            File.WriteAllText(txt, "Plain license text.");
            var plist = MacDmgLicenseResources.BuildPlist(txt, logger);
            Assert.True(plist.Contains("<key>LPic</key>") && plist.Contains("<key>STR#</key>") &&
                   plist.Contains("<key>TEXT</key>"),
                "The SLA plist must carry LPic/STR#/TEXT resources.");
            var rtf = Path.Combine(directory, "sla.rtf");
            File.WriteAllText(rtf, "{\\rtf1 ansi hello}");
            var rtfPlist = MacDmgLicenseResources.BuildPlist(rtf, logger);
            Assert.True(rtfPlist.Contains("<key>RTF </key>"),
                ".rtf licenses must land in the 'RTF ' resource, not TEXT.");
            var unicode = Path.Combine(directory, "sla-unicode.txt");
            File.WriteAllText(unicode, "License 中文 text.");
            MacDmgLicenseResources.BuildPlist(unicode, logger);
            Assert.True(warnings.Any(message => message.Contains("non-ASCII")),
                "Non-ASCII .txt licenses must warn about the TEXT-resource charset limit.");
        }
        finally
        {
            Cleanup(directory);
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

    static void WriteDsStore(MacDmgProcessRunner.Request request)
    {
        if (request.Executable == "osascript")
        {
            // Finder flushes .DS_Store to the volume asynchronously after the layout pass;
            // the stub writes it up front so the post-layout wait returns immediately.
            var mount = Path.Combine(request.WorkingDirectory, "dmg-mount");
            Directory.CreateDirectory(mount);
            File.WriteAllText(Path.Combine(mount, ".DS_Store"), "store");
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

    [Fact]
    static void MapsDmgSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("MacDmgCompression=\"$(BundlerMacDmgCompression)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDmgVolumeName=\"$(BundlerMacDmgVolumeName)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMacDmg* properties to the task.");
        Assert.True(props.Contains("<BundlerMacDmgCompression Condition=\"'$(BundlerMacDmgCompression)' == ''\">Ulmo<", StringComparison.Ordinal),
            "The default BundlerMacDmgCompression must be Ulmo.");
        Assert.True(task.Contains("new MacDmgBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Dmg", StringComparison.Ordinal),
            "The MSBuild task does not construct the .dmg backend.");
        Assert.True(targets.Contains("MacDmgSignIdentity=\"$(BundlerMacDmgSignIdentity)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDmgSignCertificatePath=\"$(BundlerMacDmgSignCertificatePath)\"", StringComparison.Ordinal) &&
               targets.Contains("LicenseFile=\"$(BundlerLicenseFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the .dmg signing/license properties to the task.");
    }

    static BundleConfiguration DmgConfiguration(
        string input,
        string output = "",
        string rid = "osx-arm64",
        IReadOnlyList<PackageFormat>? formats = null,
        string? licenseFile = null) => new()
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            LicenseFile = licenseFile,
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


}

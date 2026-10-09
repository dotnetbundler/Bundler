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
            var wrongTarget = Assert.ThrowsAny<NotSupportedException>(
                () => new MacDmgBundler()
                    .BuildAsync(DmgConfiguration(input, formats: [PackageFormat.App]))
                    .GetAwaiter().GetResult());
            Assert.Contains("Dmg targets only", wrongTarget.Message);
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
            var wrongHost = Assert.ThrowsAny<NotSupportedException>(
                () => new MacDmgBundler()
                    .BuildAsync(DmgConfiguration(input))
                    .GetAwaiter().GetResult());
            Assert.Contains("macOS host", wrongHost.Message);
        }
        finally
        {
            MacDmgBundleBackend.HostCheck = previous;
            Cleanup(input);
        }
    }

    [Fact]
    static void HostGateIsPlatformNotSupported()
    {
        // 宿主门禁标记类型：入口层靠它区分“逐格式容错”与“配置错误聚合”。
        var input = CreateInputDirectory();
        var previous = MacDmgBundleBackend.HostCheck;
        MacDmgBundleBackend.HostCheck = () => false;
        try
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new MacDmgBundler().Validate(DmgConfiguration(input)));
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
            var emptyVolumeName = Assert.ThrowsAny<ArgumentException>(
                () => new MacDmgBundler(new MacDmgBundleConfiguration { VolumeName = "  " })
                    .BuildAsync(DmgConfiguration(input))
                    .GetAwaiter().GetResult());
            Assert.Contains("volume name", emptyVolumeName.Message);
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
            Assert.Equal(2, artifacts.Count);
            var dmg = artifacts.Single(artifact => artifact.Format == PackageFormat.Dmg);
            Assert.EndsWith(Path.Combine("osx-arm64", "dmg", "ExampleApp.dmg"), dmg.Path);

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
            Assert.Contains("ULMO", convertArgs.Arguments);
            var convertArgList = convertArgs.Arguments.ToList();
            var outputFlag = convertArgList.IndexOf("-o");
            Assert.True(outputFlag >= 0 && convertArgList[outputFlag + 1] == dmg.Path,
                "The convert output must be written via -o <artifact path>.");
            // 隐藏扩展位的 SetFile -a E 已移除——它把 com.apple.FinderInfo 写到 .app 根，
            // 拖放安装后 codesign --verify --deep --strict 判 detritus 拒绝（D2 回归断言）。
            Assert.DoesNotContain(requests,
                request => request.Executable == "SetFile" && request.Arguments.Contains("E"));
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
                Assert.Contains("Custom Volume", createArgs.Arguments);
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
            Assert.Equal(3, detachAttempts);
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
            var detachFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new MacDmgBundler().BuildAsync(DmgConfiguration(input, output)));
            Assert.Contains("detach", detachFailure.Message);
            Assert.True(forcedDetach, "A leftover mounted volume must be force-detached on failure.");
            Assert.Empty(Directory.EnumerateFiles( Path.Combine(output, "osx-arm64", "dmg"), "*", SearchOption.AllDirectories));
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
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new MacDmgBundler().BuildAsync(DmgConfiguration(input, output)));
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
            Assert.Contains("{200, 120, 860, 520}", script);
            Assert.True(script.Contains("{180, 170}") && script.Contains("{480, 170}"),
                $"Default icon positions must be app=180,170 / Applications=480,170, got:\n{script}");
            Assert.Contains("set icon size of theViewOptions to 128", script);
            Assert.True(script.Contains("set theDisk to disk (name of (POSIX file") &&
                   script.Contains("dmg-mount") && script.Contains("tell theDisk"),
                $"The layout script must resolve the disk via the mount point, got:\n{script}");
            Assert.DoesNotContain("tell disk \"ExampleApp\"", script);
            Assert.True(requests.Any(request =>
                    request.Executable == "hdiutil" && request.Arguments.Contains("resize")),
                "The read-write image must gain headroom before the layout pass.");
            Assert.DoesNotContain("background picture", script);
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert.DoesNotContain("-nobrowse", attach.Arguments);
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
            Assert.Equal(2, artifacts.Count);
            Assert.Contains(warnings, message => message.Contains("GUI session"));
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
            Assert.False(requests.Any(request => request.Executable == "osascript"), "SkipWindowLayout must not invoke osascript.");
            var attach = requests.First(request =>
                request.Executable == "hdiutil" && request.Arguments.Contains("attach"));
            Assert.Contains("-nobrowse", attach.Arguments);
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
            Assert.False(stagedVolumeIconEarly, "Finder strips a pre-staged .VolumeIcon.icns during the window pass; it must be copied after osascript.");
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
            await Assert.ThrowsAnyAsync<FileNotFoundException>(
                () => new MacDmgBundler(new MacDmgBundleConfiguration
                    {
                        BackgroundFile = Path.Combine(input, "missing.png")
                    })
                    .BuildAsync(DmgConfiguration(input, output)));
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
            Assert.Contains("--timestamp=none", signCall.Arguments);
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
            var exclusive = await Assert.ThrowsAnyAsync<ArgumentException>(
                () => new MacDmgBundler(new MacDmgBundleConfiguration
                    {
                        Signing = new MacDmgSigningConfiguration
                        {
                            Identity = "-",
                            TemporaryCertificatePath = certificate
                        }
                    })
                    .BuildAsync(DmgConfiguration(input, output)));
            Assert.Contains("mutually exclusive", exclusive.Message);
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
            await Assert.ThrowsAnyAsync<BundleValidationException>(
                () => new MacDmgBundler(new MacDmgBundleConfiguration { SkipWindowLayout = true })
                    .BuildAsync(DmgConfiguration(
                        input, output, licenseFile: Path.Combine(input, "missing.txt"))));
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
            Assert.Contains("<key>RTF </key>", rtfPlist);
            var unicode = Path.Combine(directory, "sla-unicode.txt");
            File.WriteAllText(unicode, "License 中文 text.");
            MacDmgLicenseResources.BuildPlist(unicode, logger);
            Assert.Contains(warnings, message => message.Contains("non-ASCII"));
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
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("MacDmgCompression=\"$(BundlerMacDmgCompression)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDmgVolumeName=\"$(BundlerMacDmgVolumeName)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMacDmg* properties to the task.");
        Assert.Contains("<BundlerMacDmgCompression Condition=\"'$(BundlerMacDmgCompression)' == ''\">Ulmo<", props);
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

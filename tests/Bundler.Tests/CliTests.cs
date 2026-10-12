using DotNet.Bundler;
using DotNet.Bundler.Cli;

public static class CliTests
{



    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliProgram.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    static string CreateInputDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "cli-fixture");
        File.WriteAllText(exe, "#!/bin/sh\necho ok\n");
        return root;
    }

    static string[] BaseArgs(string command, string input, string output = "/tmp/bundler-cli-tests-out") =>
    [
        command, "--input-dir", input, "--target", "linux-x86_64", "--formats", "zip",
        "--product-name", "CliFixture", "--identifier", "dev.example.cli",
        "--package-version", "1.0.0", "--main-executable", "cli-fixture",
        "--output-dir", output
    ];

    [Fact]
    static void PrintsUsage()
    {
        var (code, _, err) = Run();
        Assert.Equal(2, code);
        Assert.Contains("Usage:", err);
    }

    [Fact]
    static void RejectsUnknownCommand()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("Unknown command", err);
    }

    [Fact]
    static void RejectsUnknownFormat()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--target", "linux-x86_64", "--formats", "bogus",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0");
            Assert.Equal(2, code);
            Assert.Contains("Unknown format", err);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void ValidateOk()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, stdout, _) = Run(BaseArgs("validate", input));
            Assert.Equal(0, code);
            Assert.Contains("valid", stdout);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void ValidateBadInput()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bundler-cli-missing-" + Guid.NewGuid().ToString("N"));
        var (code, _, err) = Run(BaseArgs("validate", missing));
        Assert.Equal(2, code);
        Assert.Contains("inputDirectory", err);
    }

    [Fact]
    static void PlanJson()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var args = BaseArgs("plan", input, output)
                .Select(a => a == "zip" ? "zip,targz" : a).Concat(["--json"]).ToArray();
            var (code, stdout, _) = Run(args);
            Assert.Equal(0, code);
            Assert.Contains("\"format\":\"zip\"", stdout);
            Assert.Contains("\"format\":\"targz\"", stdout);
            var expectedOutputDir = output.Replace("\\", "\\\\");
            Assert.Contains($"\"outputDirectory\":\"{expectedOutputDir}\"", stdout);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void PlanAll()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var args = BaseArgs("plan", input, output);
            var rewritten = args.Select(a => a == "zip" ? "all" : a).ToArray();
            var (code, stdout, err) = Run(rewritten);
            Assert.True(code == 0, $"plan --formats all must exit 0, got {code}: {err}");
            Assert.Contains("deb", stdout);
            Assert.Contains("appimage", stdout);
            Assert.DoesNotContain("nsis", stdout);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void BundleProducesZip()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (code, stdout, _) = Run(BaseArgs("bundle", input, output).Concat(["--quiet"]).ToArray());
            Assert.Equal(0, code);
            var zip = Path.Combine(output, "clifixture-1.0.0-linux-x86_64.zip");
            Assert.True(File.Exists(zip), $"zip artifact must exist at {zip}");
            Assert.True(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            Assert.Contains(zip, stdout);
        }
        finally
        {
            Directory.Delete(input, true);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    static void BundlePerFormatIndependentFailure()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "gated set differs per host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-perfmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 宿主门禁格式失败只 WARN+计入失败汇总——可产格式照常产出，末位 rc=1。
            // 组合须是“rid 合法但宿主不可产”（rid 不匹配属配置错，统一预检 rc=2）。
            // 产物名用生态词不是 target 字面：windows-x86_64→windows-x64，macos-universal→macos-universal。
            var (gatedRid, gated, zipWord) = TestPlatform.IsMacOS
                ? ("windows-x86_64", "msi", "windows-x64")
                : ("macos-universal", "dmg", "macos-universal");
            var args = BaseArgs("bundle", input, output)
                .Select(arg => arg == "linux-x86_64" ? gatedRid : arg)
                .Select(arg => arg == "zip" ? $"{gated},zip" : arg)
                .Concat(["--quiet"]).ToArray();
            var (code, stdout, err) = Run(args);
            Assert.Equal(1, code);
            Assert.Contains("1 format(s) failed", err);
            Assert.Contains(gated, err);
            var zip = Path.Combine(output, $"clifixture-1.0.0-{zipWord}.zip");
            Assert.True(File.Exists(zip), $"producible format artifact must still land at {zip}");
            Assert.Contains(zip, stdout);
        }
        finally
        {
            Directory.Delete(input, true);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    static void BundleSigningHostGateDoesNotBlockOtherFormats()
    {
        // 签名要求 macOS 宿主——属宿主门禁不是配置错：同请求的 zip 照常产出。
        Assert.SkipWhen(TestPlatform.IsMacOS, "仅非 macOS 宿主上签名走宿主门禁。");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-signhost-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new
        {
            productName = "CfgApp",
            identifier = "dev.example.cfg",
            version = "2.0.0",
            outputDirectory = output,
            targets = new[]
            {
                new
                {
                    target = "macos-universal", inputDirectory = input,
                    mainExecutable = "cli-fixture", formats = new[] { "zip", "app" }
                }
            },
            app = new { signing = new { identity = "-" } }
        }));
        try
        {
            var (code, _, err) = Run("bundle", "--config", config, "--quiet");
            Assert.Equal(1, code);
            Assert.Contains("1 format(s) failed", err);
            Assert.Contains("app", err);
            var zip = Path.Combine(output, "cfgapp-2.0.0-macos-universal.zip");
            Assert.True(File.Exists(zip),
                $"producible format artifact must still land at {zip}");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    static void BundlePreValidationRejectsBeforeAnyArtifact()
    {
        // 后置格式旋钮错：统一预检聚合拒绝（rc=2），排在前面可产的 zip 不得先落盘。
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-prev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--target", "macos-universal", "--formats", "zip,app",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "sub/dir/cli-fixture",
                "--output-dir", output, "--quiet");
            Assert.Equal(2, code);
            Assert.Contains("directly inside the input directory", err);
            Assert.False(Directory.Exists(output),
                "pre-fanout validation must fail before any format builds; artifacts landed at " + output);
        }
        finally
        {
            Directory.Delete(input, true);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    static void BundlePreValidationAggregatesAllFormatErrors()
    {
        // 不同 target 上两种格式各自的旋钮错要一次报完——不是撞见第一个就停。
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-prevagg-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new
        {
            productName = "CfgApp",
            identifier = "dev.example.cfg",
            version = "2.0.0",
            outputDirectory = output,
            targets = new[]
            {
                new
                {
                    target = "macos-universal", inputDirectory = input,
                    mainExecutable = "sub/dir/cli-fixture", formats = new[] { "app" }
                },
                new
                {
                    target = "windows-x86_64", inputDirectory = input,
                    mainExecutable = "cli-fixture", formats = new[] { "nsis" }
                }
            },
            nsis = new { languages = new[] { "English", "english" } }
        }));
        try
        {
            var (code, _, err) = Run("bundle", "--config", config, "--quiet");
            Assert.Equal(2, code);
            Assert.Contains("directly inside the input directory", err);
            Assert.Contains("selected more than once", err);
            Assert.False(Directory.Exists(output),
                "pre-fanout validation must fail before any format builds; artifacts landed at " + output);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    static void ValidateReportsFormatKnobErrors()
    {
        // bundler validate 也跑各格式旋钮——不是只验共享配置。
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "validate", "--input-dir", input, "--target", "macos-universal", "--formats", "app",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "sub/dir/cli-fixture");
            Assert.Equal(2, code);
            Assert.Contains("directly inside the input directory", err);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void BundleMatrixViolation()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--target", "linux-x86_64", "--formats", "nsis",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture");
            Assert.Equal(2, code);
            Assert.Contains("not supported", err);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void MergesRepeatedInputDirs()
    {
        var x64 = Path.Combine(Path.GetTempPath(), "bundler-cli-merge-x64-" + Guid.NewGuid().ToString("N"));
        var arm64 = Path.Combine(Path.GetTempPath(), "bundler-cli-merge-arm64-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-merge-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(x64);
            Directory.CreateDirectory(arm64);
            WriteThinMachO(Path.Combine(x64, "cli-fixture"), 0x01000007);
            WriteThinMachO(Path.Combine(arm64, "cli-fixture"), 0x0100000C);
            File.WriteAllText(Path.Combine(x64, "shared.txt"), "portable payload\n");
            File.WriteAllText(Path.Combine(arm64, "shared.txt"), "portable payload\n");
            var (code, _, err) = Run(
                "bundle", "--input-dir", x64, "--input-dir", arm64,
                "--target", "macos-universal", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 0, $"merged bundle must exit 0, got {code}: {err}");
            var zip = Path.Combine(output, "clifixture-1.0.0-macos-universal.zip");
            Assert.True(File.Exists(zip), $"merged archive missing at {zip}");
            using var archive = new System.IO.Compression.ZipArchive(
                File.OpenRead(zip), System.IO.Compression.ZipArchiveMode.Read);
            var exeEntry = archive.GetEntry("clifixture-1.0.0-macos-universal/cli-fixture");
            Assert.NotNull(exeEntry);
            var extracted = Path.GetTempFileName();
            using (var stream = exeEntry!.Open())
            try
            {
                using (var file = File.Create(extracted))
                {
                    stream.CopyTo(file);
                }
                var cpus = DotNet.Bundler.MacApp.MachO.ReadSliceInfos(extracted)
                    .Select(s => s.CpuType).OrderBy(c => c).ToArray();
                Assert.True(cpus.Length == 2 && cpus[0] == 0x01000007 && cpus[1] == 0x0100000C,
                    "merged executable must be a fat Mach-O with x86_64 + arm64 slices, got "
                    + string.Join(",", cpus.Select(c => c.ToString("X8"))));
            }
            finally
            {
                File.Delete(extracted);
            }
            Assert.NotNull(archive.GetEntry("clifixture-1.0.0-macos-universal/shared.txt"));
        }
        finally
        {
            Directory.Delete(x64, true);
            Directory.Delete(arm64, true);
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    static void RepeatedInputDirsHonorReferenceDir()
    {
        // --reference-dir must reach MacUniversalPayloadMerger (regression: the option used to
        // be rejected as an unknown configuration key before it could feed the merge).
        var x64 = Path.Combine(Path.GetTempPath(), "bundler-cli-ref-x64-" + Guid.NewGuid().ToString("N"));
        var arm64 = Path.Combine(Path.GetTempPath(), "bundler-cli-ref-arm64-" + Guid.NewGuid().ToString("N"));
        var neutral = Path.Combine(Path.GetTempPath(), "bundler-cli-ref-neutral-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-ref-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in new[] { x64, arm64, neutral })
            {
                Directory.CreateDirectory(dir);
            }
            WriteThinMachO(Path.Combine(x64, "cli-fixture"), 0x01000007);
            WriteThinMachO(Path.Combine(arm64, "cli-fixture"), 0x0100000C);
            File.WriteAllText(Path.Combine(x64, "managed.txt"), "x64 payload\n");
            File.WriteAllText(Path.Combine(arm64, "managed.txt"), "arm64 payload\n");
            File.WriteAllText(Path.Combine(neutral, "managed.txt"), "neutral payload\n");
            var (code, _, err) = Run(
                "bundle", "--input-dir", x64, "--input-dir", arm64,
                "--reference-dir", neutral,
                "--target", "macos-universal", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 0, $"reference-dir merge must exit 0, got {code}: {err}");
            var zip = Path.Combine(output, "clifixture-1.0.0-macos-universal.zip");
            using var archive = new System.IO.Compression.ZipArchive(
                File.OpenRead(zip), System.IO.Compression.ZipArchiveMode.Read);
            var entry = archive.GetEntry("clifixture-1.0.0-macos-universal/managed.txt");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open());
            Assert.Equal("neutral payload\n", reader.ReadToEnd());
        }
        finally
        {
            foreach (var dir in new[] { x64, arm64, neutral, output })
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    static void MissingReferenceDirIsAUsageError()
    {
        // --reference-dir pointing nowhere must fail fast as a usage error (RC=2) rather
        // than silently merging without the neutral publish (regression: review finding —
        // missing dir used to be ignored and reference-only files vanished from the payload).
        var x64 = Path.Combine(Path.GetTempPath(), "bundler-cli-refmiss-x64-" + Guid.NewGuid().ToString("N"));
        var arm64 = Path.Combine(Path.GetTempPath(), "bundler-cli-refmiss-arm64-" + Guid.NewGuid().ToString("N"));
        var missing = Path.Combine(Path.GetTempPath(), "bundler-cli-refmiss-none-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-refmiss-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(x64);
            Directory.CreateDirectory(arm64);
            WriteThinMachO(Path.Combine(x64, "cli-fixture"), 0x01000007);
            WriteThinMachO(Path.Combine(arm64, "cli-fixture"), 0x0100000C);
            var (code, _, err) = Run(
                "bundle", "--input-dir", x64, "--input-dir", arm64,
                "--reference-dir", missing,
                "--target", "macos-universal", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 2, $"missing reference-dir must exit 2, got {code}: {err}");
            Assert.Contains("--reference-dir", err);
        }
        finally
        {
            foreach (var dir in new[] { x64, arm64, missing, output })
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    static void WriteThinMachO(string path, uint cpuType)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0xFEEDFACFu);           // MH_MAGIC_64 little-endian
        writer.Write(cpuType);               // cputype
        writer.Write(3u);                    // cpusubtype
        writer.Write(2u);                    // filetype MH_EXECUTE
        writer.Write(0u); writer.Write(0u);  // ncmds + sizeofcmds
        writer.Write(0u);                    // flags
        writer.Write(0u);                    // reserved
    }

    static string WriteConfig(string input, string output)
    {
        var document = new
        {
            productName = "CfgApp",
            identifier = "dev.example.cfg",
            version = "2.0.0",
            outputDirectory = output,
            targets = new[]
            {
                new
                {
                    target = "linux-x86_64",
                    inputDirectory = input,
                    mainExecutable = "cli-fixture",
                    formats = new[] { "zip" }
                }
            },
            archive = new { archiveName = "from-config" },
            deb = new { section = "utils" }
        };
        return System.Text.Json.JsonSerializer.Serialize(document);
    }

    [Fact]
    static void ConfigFileBundle()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-cfg-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, output));
        try
        {
            var (code, stdout, err) = Run("bundle", "--config", config, "--quiet");
            Assert.True(code == 0, $"config-driven bundle must exit 0, got {code}: {err}");
            var zip = Path.Combine(output, "from-config.zip");
            Assert.True(File.Exists(zip), $"config archiveName must land at {zip}");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output)) { Directory.Delete(output, true); }
        }
    }

    [Fact]
    static void ConfigCliOverride()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-ovr-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, output));
        try
        {
            var (code, stdout, _) = Run(
                "plan", "--config", config, "--formats", "targz", "--json");
            Assert.Equal(0, code);
            Assert.True(stdout.Contains("\"format\":\"targz\"") && !stdout.Contains("\"zip\""),
                "cli --formats must override the config file");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void RejectsUnknownConfigKey()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x").Replace(
            "\"archive\"", "\"achive\""));
        try
        {
            var (code, _, err) = Run("plan", "--config", config);
            Assert.Equal(2, code);
            Assert.Contains("achive", err);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void DottedKnobOverride()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-dot-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, output));
        try
        {
            var (code, stdout, err) = Run(
                "bundle", "--config", config, "--archive.archive-name=dotted-override", "--quiet");
            Assert.True(code == 0, $"dotted-knob bundle must exit 0, got {code}: {err}");
            Assert.True(File.Exists(Path.Combine(output, "dotted-override.zip")),
                "--archive.archive-name must override the config file");
            var (badCode, _, badErr) = Run("plan", "--config", config, "--deb.bogus=1");
            Assert.Equal(2, badCode);
            Assert.Contains("deb.bogus", badErr);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output)) { Directory.Delete(output, true); }
        }
    }

    [Fact]
    static void ConfigSectionMemberDefaults()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, """
            {
              "productName": "CfgDefaults",
              "identifier": "dev.example.cfg",
              "version": "1.0.0",
              "targets": [
                {
                  "target": "linux-x86_64",
                  "inputDirectory": "<input>",
                  "mainExecutable": "cli-fixture",
                  "formats": [ "zip" ]
                }
              ],
              "app": { "signing": { "identity": "-" } },
              "pkg": { "signing": { "identity": "Developer ID Installer: Example" } },
              "archive": { "files": [ { "destination": "bin/cli-fixture" } ] }
            }
            """.Replace("<input>", input.Replace("\\", "\\\\")));
        try
        {
            var resolved = CliConfig.Resolve(CliArguments.Parse(["bundle", "--config", config]));

            var app = resolved.App;
            Assert.NotNull(app);
            Assert.True(app!.Files is { Count: 0 } &&
                app.DocumentTypes is { Count: 0 } && app.UrlTypes is { Count: 0 } &&
                app.FrameworkDirectories is { Count: 0 },
                "absent app collection keys must keep their empty initializers");
            Assert.True(app.Signing is { Identity: "-", NotaryWait: true },
                "app.signing must merge onto defaults: identity set, NotaryWait stays true");
            Assert.True(resolved.Pkg?.Signing.NotaryWait == true,
                "pkg.signing must merge onto defaults: NotaryWait stays true");
            var file = resolved.Archive?.Files?.SingleOrDefault();
            Assert.True(file is { Source: "", Destination: "bin/cli-fixture" },
                "files[] elements must merge onto defaults: absent source stays empty");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void IconsOverrideResolvesEachEntry()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x"));
        try
        {
            var resolved = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config, "--icons=icon-a.png,icon-b.png"]));

            Assert.Equal(
                new[] { "icon-a.png", "icon-b.png" }
                    .Select(name => Path.GetFullPath(name)).ToArray(),
                resolved.Bundle.Icons);

            // 首项为绝对路径时整串 rooted——相对项仍须按工作目录逐项解析。
            var anchored = Path.Combine(Path.GetTempPath(), "anchor.png");
            var mixed = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config, $"--icons={anchored},icon-c.png"]));
            Assert.Equal(
                new[] { anchored, Path.GetFullPath("icon-c.png") },
                mixed.Bundle.Icons);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void OutputLayoutSurvivesConfigAndCliOverride()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x")
            .Replace("\"productName\"", "\"outputLayout\": \"byFormat\", \"productName\""));
        try
        {
            var resolved = CliConfig.Resolve(CliArguments.Parse(["bundle", "--config", config]));
            Assert.Equal(OutputLayout.ByFormat, resolved.Bundle.OutputLayout);

            var cliOverride = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config, "--output-layout=flat"]));
            Assert.Equal(OutputLayout.Flat, cliOverride.Bundle.OutputLayout);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void JsonPathKnobOverridesResolveEachLeaf()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x"));
        try
        {
            // JSON 字典值带逗号：须按 JSON 解析逐叶判 rooted，逗号不得拆串。
            var resolved = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config,
                 "--nsis.custom-language-files={\"en-US\":\"rel/en.nsh\",\"de-DE\":\"rel/de.nsh\"}"]));
            var langs = resolved.Nsis?.CustomLanguageFiles;
            Assert.Equal(2, langs?.Count);
            Assert.Equal(Path.GetFullPath("rel/en.nsh"), langs?["en-US"]);
            Assert.Equal(Path.GetFullPath("rel/de.nsh"), langs?["de-DE"]);

            // JSON 数组同语义逐元素解析。
            var array = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config, "--icons=[\"a.png\",\"b.png\"]"]));
            Assert.Equal(
                new[] { "a.png", "b.png" }.Select(Path.GetFullPath).ToArray(),
                array.Bundle.Icons);

            // 文件名恰为数字/布尔字面量（"00123"、"true"）：仍按路径解析不得
            // 被 ParseValue 收成非字符串类型，前导零保留。
            var numeric = CliConfig.Resolve(CliArguments.Parse(
                ["bundle", "--config", config, "--icons=00123", "--license-file=true"]));
            Assert.Equal([Path.GetFullPath("00123")], numeric.Bundle.Icons);
            Assert.Equal(Path.GetFullPath("true"), numeric.Bundle.LicenseFile);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void RejectsUnknownNestedSectionKeys()
    {
        // 嵌套对象键同样过门禁：旧名 apiIssuer 与笔误都必须报 unknown key 而非
        // 静默丢弃（mac 验证抓到的下钻缺口）。
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x").Replace(
            "\"deb\":{\"section\":\"utils\"}",
            "\"deb\":{\"section\":\"utils\",\"files\":[{\"soruce\":\"a\",\"destination\":\"b\"}]}"));
        try
        {
            var (code, _, err) = Run("plan", "--config", config);
            Assert.Equal(2, code);
            Assert.Contains("deb.files[0].soruce", err);

            File.WriteAllText(config, WriteConfig(input, "/tmp/x").Replace(
                "\"archive\"", "\"app\":{\"signing\":{\"apiIssuer\":\"old\"}},\"archive\""));
            var (oldKeyCode, _, oldKeyErr) = Run("plan", "--config", config);
            Assert.Equal(2, oldKeyCode);
            Assert.Contains("app.signing.apiIssuer", oldKeyErr);
            Assert.Contains("apiKeyIssuer", oldKeyErr);
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void PayloadRelativeKnobsStayUnresolved()
    {
        // PathKnobs 由配置类型 JSON 元数据按 *Files/*Directories 复数后缀导出；
        // 载荷相对键（shortcuts.icon、configLocations）无宿主路径后缀，不得按
        // 配置文件目录解析——钉住 shortcuts.icon 误收 PathKnobs 的回归。
        var input = CreateInputDirectory();
        var configDir = Path.Combine(Path.GetTempPath(), "bundler-cli-pathknob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        var config = Path.Combine(configDir, "bundler.json");
        File.WriteAllText(config, """
            {
                "productName": "CfgApp",
                "identifier": "dev.example.cfg",
                "version": "2.0.0",
                "outputDirectory": "/tmp/x",
                "targets": [{ "target": "linux-x86_64", "inputDirectory": "<input>",
                              "mainExecutable": "cli-fixture", "formats": ["zip"] }],
                "nsis": { "shortcuts": { "icon": "app.ico" } },
                "app": { "frameworkDirectories": ["rel-fw"] },
                "deb": { "configLocations": ["/etc/cfgapp.conf"] }
            }
            """.Replace("<input>", input.Replace("\\", "\\\\")));
        try
        {
            var resolved = CliConfig.Resolve(CliArguments.Parse(["bundle", "--config", config]));

            Assert.Equal("app.ico", resolved.Nsis?.Shortcuts.Icon);
            Assert.Equal(["/etc/cfgapp.conf"], resolved.Deb?.ConfigLocations);
            // 生成表仍须收宿主路径目录：相对项按配置文件目录解析。
            Assert.Equal([Path.GetFullPath(Path.Combine(configDir, "rel-fw"))],
                resolved.App?.FrameworkDirectories);
        }
        finally
        {
            Directory.Delete(input, true);
            Directory.Delete(configDir, true);
        }
    }

    [Fact]
    static void PrintsVersion()
    {
        var (code, stdout, _) = Run("--version");
        Assert.Equal(0, code);
        Assert.True(stdout.Trim().Length > 0, "version output must be non-empty");
    }

    [Fact]
    static void BundleWithOnlyInputDirUsesInferredDefaults()
    {
        // 快速路契约：只给 --input-dir 必须产出包，推断值逐条进 stderr 明示。
        Assert.SkipUnless(OperatingSystem.IsLinux(), "host-derived defaults are asserted on linux");
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-infer-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "coolapp");
        var output = Path.Combine(root, "out");
        try
        {
            Directory.CreateDirectory(input);
            var exe = Path.Combine(input, "coolapp");
            File.WriteAllText(exe, "#!/bin/sh\necho ok\n");
            if (OperatingSystem.IsLinux()) // CA1416：分析器不认 SkipUnless
            {
                File.SetUnixFileMode(exe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var (code, _, err) = Run("bundle", "--input-dir", input, "--output-dir", output);
            Assert.Equal(0, code); // err 上下文由后续 Contains 断言给出
            Assert.Contains("target=linux-x86_64", err);
            Assert.Contains("formats=deb,zip", err);
            Assert.Contains("productName=coolapp", err);
            Assert.Contains("identifier=dev.bundler.coolapp", err);
            Assert.Contains("version=0.1.0", err);
            Assert.Contains("mainExecutable=coolapp", err);
            Assert.True(Directory.EnumerateFiles(output, "*.deb").Any(),
                "expected a .deb in output: " + string.Join(",", Directory.EnumerateFiles(output)));
            Assert.True(Directory.EnumerateFiles(output, "*.zip").Any(),
                "expected a .zip in output: " + string.Join(",", Directory.EnumerateFiles(output)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    static void StrictDisablesInference()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run("bundle", "--input-dir", input, "--strict");
            Assert.Equal(2, code);
            Assert.Contains("strict", err);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void InitWritesTemplateAndRefusesOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundler-cli-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            var (code, stdout, _) = Run("init");
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(dir, "bundler.json")), $"init must write bundler.json: {stdout}");
            var (code2, _, err2) = Run("init");
            Assert.Equal(2, code2);
            Assert.Contains("already exists", err2);

            // 模板开箱即可用：不写 targets，init 后直接 bundle --input-dir 即可跑通
            var input = Path.Combine(dir, "publish");
            Directory.CreateDirectory(input);
            var exe = Path.Combine(input, "myapp");
            File.WriteAllText(exe, "x");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(exe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var (code3, _, err3) = Run("bundle", "--input-dir", input, "--formats", "zip");
            Assert.Equal(0, code3);
            Assert.DoesNotContain("At least one", err3);

            // 取消注释 targets 示例（含首个逗号行）后 JSON 必须仍合法——
            // 报错应落在配置校验（目录不存在）而非 JSON 解析（review BUG）。
            var uncommented = File.ReadAllText(Path.Combine(dir, "bundler.json"))
                .Replace("// , \"targets\"", ", \"targets\"")
                .Replace("//   {", "  {")
                .Replace("//     \"target\"", "    \"target\"")
                .Replace("//     \"inputDirectory\"", "    \"inputDirectory\"")
                .Replace("//     \"formats\"", "    \"formats\"")
                .Replace("//   }", "  }")
                .Replace("// ]", "]");
            File.WriteAllText(Path.Combine(dir, "bundler.json"), uncommented);
            var (code4, _, err4) = Run("validate");
            Assert.DoesNotContain("JSON", err4);
            Assert.DoesNotContain("parse", err4);
            // 钉死在目录缺失校验：publish/linux-x64 不存在 → 存在性校验错（review flag）
            Assert.Equal(2, code4);
            Assert.Contains("does not exist", err4);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    static void BundlerJsonInWorkingDirectoryIsAutoDiscovered()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-discover-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "appdir");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "DiscApp"), "x");
        File.WriteAllText(Path.Combine(root, "bundler.json"), $$"""
            { "productName": "DiscApp", "identifier": "dev.example.disc", "version": "1.0.0",
              "targets": [{ "target": "linux-x86_64", "inputDirectory": "{{input.Replace("\\", "\\\\")}}",
                           "formats": ["zip"] }] }
            """);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);
            var (code, _, err) = Run("validate");
            Assert.Equal(0, code);
            Assert.Contains("using configuration file", err);
            // --no-config 跳过自动发现——此时连输入目录都没有，报错而非静默通过
            var (code2, _, err2) = Run("validate", "--no-config");
            Assert.NotEqual(0, code2);
            Assert.DoesNotContain("using configuration file", err2);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    static void PackageVersionIsAnAliasForVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-alias-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "appdir");
        var configDir = Path.Combine(root, "cfg");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(input, "AliasApp"), "x");
        try
        {
            var ok = Path.Combine(configDir, "ok.json");
            File.WriteAllText(ok, $$"""
                { "productName": "AliasApp", "identifier": "dev.example.alias",
                  "packageVersion": "2.3.4",
                  "targets": [{ "target": "linux-x86_64", "inputDirectory": "{{input.Replace("\\", "\\\\")}}",
                               "formats": ["zip"] }] }
                """);
            var resolved = CliConfig.Resolve(CliArguments.Parse(["validate", "--config", ok]));
            Assert.Equal("2.3.4", resolved.Bundle.Version);

            var conflict = Path.Combine(configDir, "conflict.json");
            File.WriteAllText(conflict, $$"""
                { "productName": "AliasApp", "identifier": "dev.example.alias",
                  "version": "1.0.0", "packageVersion": "2.3.4",
                  "targets": [{ "target": "linux-x86_64", "inputDirectory": "{{input.Replace("\\", "\\\\")}}",
                               "formats": ["zip"] }] }
                """);
            var (code, _, err) = Run("validate", "--config", conflict);
            Assert.Equal(2, code);
            Assert.Contains("aliases", err);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    static void MultiTargetInferenceLabelsOnlyFirstAsUniversal()
    {
        // ≥2 个 --input-dir 走 universal 合并且只进 targets[0]；
        // 其余目标仍按宿主推导——断言两条推断标签各归其位（review flag）。
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-multitarget-" + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "osx-x64");
        var dirB = Path.Combine(root, "osx-arm64");
        var dirC = Path.Combine(root, "linux-x64");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(dirC);
        var config = Path.Combine(root, "bundler.json");
        File.WriteAllText(config, $$"""
            { "productName": "MultiApp", "identifier": "dev.example.multi", "version": "1.0.0",
              "targets": [
                { "inputDirectory": "{{dirA.Replace("\\", "\\\\")}}", "formats": ["zip"] },
                { "inputDirectory": "{{dirC.Replace("\\", "\\\\")}}", "formats": ["deb"] }
              ] }
            """);
        try
        {
            var resolved = CliConfig.Resolve(CliArguments.Parse(
                ["plan", "--config", config, "--input-dir", dirA, "--input-dir", dirB]));
            Assert.Equal("macos-universal", resolved.Bundle.Targets[0].Target.ToString());
            Assert.NotEqual("macos-universal", resolved.Bundle.Targets[1].Target.ToString());
            Assert.True(BundleTarget.TryParse(resolved.Bundle.Targets[1].Target.ToString(), out _),
                "second target must get a real host-derived target, not the universal marker");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    static async Task MsBuildDefaultsInferFormatsIdentifierAndVersion()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("linux host only (asserts deb derivation)");
        }
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var taskDll = Path.Combine(repo, "src/Bundler.MSBuild/bin/Release/netstandard2.0/DotNet.Bundler.MSBuild.dll");
        if (!File.Exists(taskDll))
        {
            Assert.Skip("Bundler.MSBuild not built: " + taskDll);
        }
        var root = Path.Combine(Path.GetTempPath(), "bundler-msbuild-defaults-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "TestApp"), "x");
        var project = Path.Combine(root, "proj.csproj");
        File.WriteAllText(project, $$"""
            <Project>
              <PropertyGroup>
                <AssemblyName>TestApp</AssemblyName>
                <TargetName>TestApp</TargetName>
                <_BundlerTaskAssembly>{{taskDll}}</_BundlerTaskAssembly>
              </PropertyGroup>
              <Import Project="{{Path.Combine(repo, "src/Bundler.MSBuild/buildTransitive/DotNet.Bundler.MSBuild.props")}}" />
              <Import Project="{{Path.Combine(repo, "src/Bundler.MSBuild/buildTransitive/DotNet.Bundler.MSBuild.targets")}}" />
            </Project>
            """);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
                $"msbuild \"{project}\" -t:BundlerBundle -p:BundlerInputDir=\"{input}\" " +
                $"-p:BundlerTarget=linux-x86_64 -p:BundlerOutputPath=\"{output}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var log = await proc.StandardOutput.ReadToEndAsync() + proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("dev.bundler.testapp", log); // identifier 合成警告
            Assert.Contains("placeholder '0.1.0'", log); // Version 空缺兜底警告
            Assert.Contains(": deb", log); // formats 按 linux 目标推导
            Assert.True(Directory.EnumerateFiles(output, "*.deb").Any(),
                "expected a .deb in output: " + string.Join(",", Directory.EnumerateFiles(output)));

            // SDK 项目免 BundlerTarget：NETCoreSdkPortableRuntimeIdentifier → 宿主推导
            var sdkProject = Path.Combine(root, "sdk.csproj");
            File.WriteAllText(sdkProject, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>TestApp</AssemblyName>
                    <TargetName>TestApp</TargetName>
                    <_BundlerTaskAssembly>{{taskDll}}</_BundlerTaskAssembly>
                  </PropertyGroup>
                  <Import Project="{{Path.Combine(repo, "src/Bundler.MSBuild/buildTransitive/DotNet.Bundler.MSBuild.props")}}" />
                  <Import Project="{{Path.Combine(repo, "src/Bundler.MSBuild/buildTransitive/DotNet.Bundler.MSBuild.targets")}}" />
                </Project>
                """);
            var output2 = Path.Combine(root, "out2");
            var psi2 = new System.Diagnostics.ProcessStartInfo("dotnet",
                $"msbuild \"{sdkProject}\" -t:BundlerBundle -p:BundlerInputDir=\"{input}\" " +
                $"-p:BundlerOutputPath=\"{output2}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc2 = System.Diagnostics.Process.Start(psi2)!;
            var log2 = await proc2.StandardOutput.ReadToEndAsync() + proc2.StandardError.ReadToEndAsync();
            await proc2.WaitForExitAsync();
            Assert.Equal(0, proc2.ExitCode);
            // normal 级推断消息在默认 minimal 详细度不输出——看 high 级 Bundling 行的推导目标
            Assert.Contains("for linux-x86_64:", log2);
            Assert.True(Directory.EnumerateFiles(output2, "*.deb").Any(),
                "expected a .deb in output2: " + string.Join(",", Directory.EnumerateFiles(output2)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    static void FormatsAcceptSemicolonSeparators()
    {
        // bundler.json/MSBuild 列表惯例是 ';'——CLI 同一字段必须同样收。
        var input = CreateInputDirectory();
        try
        {
            var (code, stdout, _) = Run("plan", "--input-dir", input, "--formats", "zip;tar.gz",
                "--main-executable", "cli-fixture");
            Assert.Equal(0, code);
            Assert.Contains(" zip ", stdout);
            Assert.Contains("targz", stdout);
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    [Fact]
    static void UpdateKeygenRefusesOverwriteAndLabelsOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundler-keygen-" + Guid.NewGuid().ToString("N"));
        var keyPath = Path.Combine(root, "update.key");
        try
        {
            var (code, stdout, _) = Run("update-keygen", "--key-file", keyPath);
            Assert.Equal(0, code);
            // 两行必须有标签——人看输出才分得清路径与公钥。
            Assert.Contains("key file:", stdout);
            Assert.Contains("public key:", stdout);
            // 覆盖私钥=签名链孤儿——先拦 rc=2 而非写半截 IO 错。
            var (code2, _, err2) = Run("update-keygen", "--key-file", keyPath);
            Assert.Equal(2, code2);
            Assert.Contains("already exists", err2);
            if (OperatingSystem.IsLinux())
            {
                // 新父目录必须 700——宽松 umask 下私钥目录不许对本地他用户可读。
                var mode = File.GetUnixFileMode(Path.GetDirectoryName(keyPath)!);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    static void InitAndKeygenRejectBundleOptions()
    {
        // 不读配置的命令吃 bundle 旋钮静默无效是误导——rc=2 明示。
        var (code, _, err) = Run("init", "--formats", "zip");
        Assert.Equal(2, code);
        Assert.Contains("no effect", err);
        var (code2, _, err2) = Run("update-keygen", "--key-file", "k", "--identifier", "dev.x");
        Assert.Equal(2, code2);
        Assert.Contains("no effect", err2);
        // 配置相关旗标同拒——--json/--quiet/--verbose 通用旗不在此列。
        var (code3, _, err3) = Run("init", "--strict");
        Assert.Equal(2, code3);
        Assert.Contains("no effect", err3);
        var (code4, _, err4) = Run("update-keygen", "--key-file", "k", "--no-config");
        Assert.Equal(2, code4);
        Assert.Contains("no effect", err4);
    }

    [Fact]
    static void ShortHelpFlagIsAccepted()
    {
        var (code, stdout, _) = Run("-h");
        Assert.Equal(0, code);
        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    static void IconsAcceptSemicolonSeparatedLists()
    {
        // 路径列表同样收 MSBuild 式 ';'——--icons=a.png;b.png 不当单路径。
        var resolved = CliConfig.Resolve(CliArguments.Parse(
            ["plan", "--input-dir", "/tmp/x", "--formats", "zip",
             "--icons", "/tmp/a.png;/tmp/b.png"]));
        Assert.Equal(2, resolved.Bundle.Icons.Count);
    }

    [Fact]
    static void ScalarPathKnobKeepsSemicolonLiteral()
    {
        // 标量路径含 ';' 是合法单值——--output-dir "a;b" 不许劈成数组（review BUG）。
        var resolved = CliConfig.Resolve(CliArguments.Parse(
            ["plan", "--input-dir", "/tmp/x", "--formats", "zip",
             "--output-dir", "/tmp/a;b"]));
        Assert.Equal("/tmp/a;b", resolved.Bundle.OutputDirectory);
    }

    [Fact]
    static void NoExecutableCandidatesEmitsFallbackNote()
    {
        // 目录内零可执行候选时推断层先 note 预警——不等到 validator 才爆炸。
        var root = Path.Combine(Path.GetTempPath(), "bundler-cli-noexec-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "plaindir");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "data.txt"), "x");
        try
        {
            var (code, _, err) = Run("plan", "--input-dir", input, "--formats", "zip");
            Assert.Equal(2, code);
            Assert.Contains("no executable candidates", err);
            Assert.Contains("mainExecutable", err);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

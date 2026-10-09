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
        command, "--input-dir", input, "--rid", "linux-x64", "--formats", "zip",
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
                "bundle", "--input-dir", input, "--rid", "linux-x64", "--formats", "bogus",
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
            var expectedOutputDir = Path.Combine(output, "linux-x64", "zip").Replace("\\", "\\\\");
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
            var zip = Path.Combine(output, "linux-x64", "zip", "clifixture-1.0.0-linux-x64.zip");
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
            var (gatedRid, gated) = TestPlatform.IsMacOS ? ("win-x64", "msi") : ("osx", "dmg");
            var args = BaseArgs("bundle", input, output)
                .Select(arg => arg == "linux-x64" ? gatedRid : arg)
                .Select(arg => arg == "zip" ? $"{gated},zip" : arg)
                .Concat(["--quiet"]).ToArray();
            var (code, stdout, err) = Run(args);
            Assert.Equal(1, code);
            Assert.Contains("1 format(s) failed", err);
            Assert.Contains(gated, err);
            var zip = Path.Combine(output, gatedRid, "zip", $"clifixture-1.0.0-{gatedRid}.zip");
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
                    runtimeIdentifier = "osx", inputDirectory = input,
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
            var zip = Path.Combine(output, "osx", "zip", "cfgapp-2.0.0-osx.zip");
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
                "bundle", "--input-dir", input, "--rid", "osx", "--formats", "zip,app",
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
                    runtimeIdentifier = "osx", inputDirectory = input,
                    mainExecutable = "sub/dir/cli-fixture", formats = new[] { "app" }
                },
                new
                {
                    runtimeIdentifier = "win-x64", inputDirectory = input,
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
                "validate", "--input-dir", input, "--rid", "osx", "--formats", "app",
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
                "bundle", "--input-dir", input, "--rid", "linux-x64", "--formats", "nsis",
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
                "--rid", "osx", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 0, $"merged bundle must exit 0, got {code}: {err}");
            var zip = Path.Combine(output, "osx", "zip", "clifixture-1.0.0-osx.zip");
            Assert.True(File.Exists(zip), $"merged archive missing at {zip}");
            using var archive = new System.IO.Compression.ZipArchive(
                File.OpenRead(zip), System.IO.Compression.ZipArchiveMode.Read);
            var exeEntry = archive.GetEntry("clifixture-1.0.0-osx/cli-fixture");
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
            Assert.NotNull(archive.GetEntry("clifixture-1.0.0-osx/shared.txt"));
        }
        finally
        {
            Directory.Delete(x64, true);
            Directory.Delete(arm64, true);
            if (Directory.Exists(output)) Directory.Delete(output, true);
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
                    runtimeIdentifier = "linux-x64",
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
            var zip = Path.Combine(output, "linux-x64", "zip", "from-config.zip");
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
            Assert.True(File.Exists(Path.Combine(output, "linux-x64", "zip", "dotted-override.zip")),
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
                  "runtimeIdentifier": "linux-x64",
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
            Assert.True(app!.Contents is { Count: 0 } &&
                app.DocumentTypes is { Count: 0 } && app.UrlTypes is { Count: 0 } &&
                app.Frameworks is { Count: 0 },
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
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    [Fact]
    static void PrintsVersion()
    {
        var (code, stdout, _) = Run("--version");
        Assert.Equal(0, code);
        Assert.True(stdout.Trim().Length > 0, "version output must be non-empty");
    }
}

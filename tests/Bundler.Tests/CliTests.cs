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
        Assert.True(code == 2, $"no command must exit 2, got {code}");
        Assert.True(err.Contains("Usage:"), "usage text expected on stderr");
    }

    [Fact]
    static void RejectsUnknownCommand()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.True(code == 2, $"unknown command must exit 2, got {code}");
        Assert.True(err.Contains("Unknown command"), "error must name the command");
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
            Assert.True(code == 2, $"unknown format must exit 2, got {code}");
            Assert.True(err.Contains("Unknown format"), "error must name the format");
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
            Assert.True(code == 0, $"valid configuration must exit 0, got {code}");
            Assert.True(stdout.Contains("valid"), "success message expected");
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
        Assert.True(code == 2, $"missing input must exit 2, got {code}");
        Assert.True(err.Contains("inputDirectory"), "issue path must be reported");
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
            Assert.True(code == 0, $"plan must exit 0, got {code}");
            Assert.True(stdout.Contains("\"format\":\"zip\""), "plan json must include zip");
            Assert.True(stdout.Contains("\"format\":\"targz\""), "plan json must include targz");
            var expectedOutputDir = Path.Combine(output, "linux-x64", "zip").Replace("\\", "\\\\");
            Assert.True(stdout.Contains($"\"outputDirectory\":\"{expectedOutputDir}\""),
                "plan json must include the per-format output directory");
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
            var args = BaseArgs("plan", input, output)
                .Where(a => a != "zip" || true).ToArray();
            var rewritten = args.Select(a => a == "zip" ? "all" : a).ToArray();
            var (code, stdout, err) = Run(rewritten);
            Assert.True(code == 0, $"plan --formats all must exit 0, got {code}: {err}");
            Assert.True(stdout.Contains("deb"), "all must include deb on linux-x64");
            Assert.True(stdout.Contains("appimage"), "all must include appimage on linux-x64");
            Assert.True(!stdout.Contains("nsis"), "all must exclude windows formats on linux-x64");
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
            Assert.True(code == 0, $"bundle must exit 0, got {code}");
            var zip = Path.Combine(output, "linux-x64", "zip", "clifixture-1.0.0-linux-x64.zip");
            Assert.True(File.Exists(zip), $"zip artifact must exist at {zip}");
            Assert.True(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            Assert.True(stdout.Contains(zip), "stdout must list the artifact path");
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
    static void BundleMatrixViolation()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--rid", "linux-x64", "--formats", "nsis",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture");
            Assert.True(code == 2, $"nsis on linux-x64 must exit 2, got {code}");
            Assert.True(err.Contains("not supported"), "matrix violation must be reported");
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
            Assert.True(exeEntry is not null, "merged archive must contain cli-fixture");
            var extracted = Path.GetTempFileName();
            using (var stream = exeEntry!.Open())
            using (var file = File.Create(extracted))
            {
                stream.CopyTo(file);
            }
            var cpus = DotNet.Bundler.MacApp.MachO.ReadSliceInfos(extracted)
                .Select(s => s.CpuType).OrderBy(c => c).ToArray();
            File.Delete(extracted);
            Assert.True(cpus.Length == 2 && cpus[0] == 0x01000007 && cpus[1] == 0x0100000C,
                "merged executable must be a fat Mach-O with x86_64 + arm64 slices, got "
                + string.Join(",", cpus.Select(c => c.ToString("X8"))));
            Assert.True(archive.GetEntry("clifixture-1.0.0-osx/shared.txt") is not null,
                "portable files must survive the merge");
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
            Assert.True(code == 0, $"plan must exit 0, got {code}");
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
            Assert.True(code == 2, $"unknown config key must exit 2, got {code}");
            Assert.True(err.Contains("achive"), "error must name the unknown key");
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
            Assert.True(badCode == 2, $"unknown dotted knob must exit 2, got {badCode}");
            Assert.True(badErr.Contains("deb.bogus"), "error must name the unknown knob");
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
            Assert.True(app is not null, "the app section must deserialize");
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
    static void PrintsVersion()
    {
        var (code, stdout, _) = Run("--version");
        Assert.True(code == 0, $"--version must exit 0, got {code}");
        Assert.True(stdout.Trim().Length > 0, "version output must be non-empty");
    }
}

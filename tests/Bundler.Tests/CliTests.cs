using DotNet.Bundler.Cli;

internal static class CliTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Prints usage and exits 2 without command", () => RunSync(PrintsUsage));
            yield return ("Rejects an unknown command", () => RunSync(RejectsUnknownCommand));
            yield return ("Rejects an unknown format", () => RunSync(RejectsUnknownFormat));
            yield return ("Validate reports a valid configuration", () => RunSync(ValidateOk));
            yield return ("Validate exits 2 on missing input directory", () => RunSync(ValidateBadInput));
            yield return ("Plan emits machine-readable json", () => RunSync(PlanJson));
            yield return ("Plan expands 'all' to rid-supported formats", () => RunSync(PlanAll));
            yield return ("Bundle produces a real zip artifact", () => RunSync(BundleProducesZip));
            yield return ("Bundle exits 2 on matrix violation", () => RunSync(BundleMatrixViolation));
            yield return ("--version prints the package version", () => RunSync(PrintsVersion));
            yield return ("Config file drives bundle end-to-end", () => RunSync(ConfigFileBundle));
            yield return ("CLI options override config file values", () => RunSync(ConfigCliOverride));
            yield return ("Unknown config keys are rejected", () => RunSync(RejectsUnknownConfigKey));
            yield return ("Dotted format knobs merge into config", () => RunSync(DottedKnobOverride));
            yield return ("Config sections keep member defaults", () => RunSync(ConfigSectionMemberDefaults));
        }
    }

    static Task RunSync(Action test)
    {
        test();
        return Task.CompletedTask;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

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

    static void PrintsUsage()
    {
        var (code, _, err) = Run();
        Assert(code == 2, $"no command must exit 2, got {code}");
        Assert(err.Contains("Usage:"), "usage text expected on stderr");
    }

    static void RejectsUnknownCommand()
    {
        var (code, _, err) = Run("frobnicate");
        Assert(code == 2, $"unknown command must exit 2, got {code}");
        Assert(err.Contains("Unknown command"), "error must name the command");
    }

    static void RejectsUnknownFormat()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--rid", "linux-x64", "--formats", "bogus",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0");
            Assert(code == 2, $"unknown format must exit 2, got {code}");
            Assert(err.Contains("Unknown format"), "error must name the format");
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    static void ValidateOk()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, stdout, _) = Run(BaseArgs("validate", input));
            Assert(code == 0, $"valid configuration must exit 0, got {code}");
            Assert(stdout.Contains("valid"), "success message expected");
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    static void ValidateBadInput()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bundler-cli-missing-" + Guid.NewGuid().ToString("N"));
        var (code, _, err) = Run(BaseArgs("validate", missing));
        Assert(code == 2, $"missing input must exit 2, got {code}");
        Assert(err.Contains("inputDirectory"), "issue path must be reported");
    }

    static void PlanJson()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var args = BaseArgs("plan", input, output)
                .Select(a => a == "zip" ? "zip,targz" : a).Concat(["--json"]).ToArray();
            var (code, stdout, _) = Run(args);
            Assert(code == 0, $"plan must exit 0, got {code}");
            Assert(stdout.Contains("\"format\":\"zip\""), "plan json must include zip");
            Assert(stdout.Contains("\"format\":\"targz\""), "plan json must include targz");
            var expectedOutputDir = Path.Combine(output, "linux-x64", "zip").Replace("\\", "\\\\");
            Assert(stdout.Contains($"\"outputDirectory\":\"{expectedOutputDir}\""),
                "plan json must include the per-format output directory");
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

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
            Assert(code == 0, $"plan --formats all must exit 0, got {code}: {err}");
            Assert(stdout.Contains("deb"), "all must include deb on linux-x64");
            Assert(stdout.Contains("appimage"), "all must include appimage on linux-x64");
            Assert(!stdout.Contains("nsis"), "all must exclude windows formats on linux-x64");
        }
        finally
        {
            Directory.Delete(input, true);
        }
    }

    static void BundleProducesZip()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (code, stdout, _) = Run(BaseArgs("bundle", input, output).Concat(["--quiet"]).ToArray());
            Assert(code == 0, $"bundle must exit 0, got {code}");
            var zip = Path.Combine(output, "linux-x64", "zip", "clifixture-1.0.0-linux-x64.zip");
            Assert(File.Exists(zip), $"zip artifact must exist at {zip}");
            Assert(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            Assert(stdout.Contains(zip), "stdout must list the artifact path");
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

    static void BundleMatrixViolation()
    {
        var input = CreateInputDirectory();
        try
        {
            var (code, _, err) = Run(
                "bundle", "--input-dir", input, "--rid", "linux-x64", "--formats", "nsis",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture");
            Assert(code == 2, $"nsis on linux-x64 must exit 2, got {code}");
            Assert(err.Contains("not supported"), "matrix violation must be reported");
        }
        finally
        {
            Directory.Delete(input, true);
        }
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

    static void ConfigFileBundle()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "bundler-cli-cfg-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, output));
        try
        {
            var (code, stdout, err) = Run("bundle", "--config", config, "--quiet");
            Assert(code == 0, $"config-driven bundle must exit 0, got {code}: {err}");
            var zip = Path.Combine(output, "linux-x64", "zip", "from-config.zip");
            Assert(File.Exists(zip), $"config archiveName must land at {zip}");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output)) { Directory.Delete(output, true); }
        }
    }

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
            Assert(code == 0, $"plan must exit 0, got {code}");
            Assert(stdout.Contains("\"format\":\"targz\"") && !stdout.Contains("\"zip\""),
                "cli --formats must override the config file");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    static void RejectsUnknownConfigKey()
    {
        var input = CreateInputDirectory();
        var config = Path.Combine(Path.GetTempPath(), $"bundler-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, WriteConfig(input, "/tmp/x").Replace(
            "\"archive\"", "\"achive\""));
        try
        {
            var (code, _, err) = Run("plan", "--config", config);
            Assert(code == 2, $"unknown config key must exit 2, got {code}");
            Assert(err.Contains("achive"), "error must name the unknown key");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

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
            Assert(code == 0, $"dotted-knob bundle must exit 0, got {code}: {err}");
            Assert(File.Exists(Path.Combine(output, "linux-x64", "zip", "dotted-override.zip")),
                "--archive.archive-name must override the config file");
            var (badCode, _, badErr) = Run("plan", "--config", config, "--deb.bogus=1");
            Assert(badCode == 2, $"unknown dotted knob must exit 2, got {badCode}");
            Assert(badErr.Contains("deb.bogus"), "error must name the unknown knob");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
            if (Directory.Exists(output)) { Directory.Delete(output, true); }
        }
    }

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
            Assert(app is not null, "the app section must deserialize");
            Assert(app!.Contents is { Count: 0 } &&
                app.DocumentTypes is { Count: 0 } && app.UrlTypes is { Count: 0 } &&
                app.Frameworks is { Count: 0 },
                "absent app collection keys must keep their empty initializers");
            Assert(app.Signing is { Identity: "-", NotaryWait: true },
                "app.signing must merge onto defaults: identity set, NotaryWait stays true");
            Assert(resolved.Pkg?.Signing.NotaryWait == true,
                "pkg.signing must merge onto defaults: NotaryWait stays true");
            var file = resolved.Archive?.Files?.SingleOrDefault();
            Assert(file is { Source: "", Destination: "bin/cli-fixture" },
                "files[] elements must merge onto defaults: absent source stays empty");
        }
        finally
        {
            Directory.Delete(input, true);
            File.Delete(config);
        }
    }

    static void PrintsVersion()
    {
        var (code, stdout, _) = Run("--version");
        Assert(code == 0, $"--version must exit 0, got {code}");
        Assert(stdout.Trim().Length > 0, "version output must be non-empty");
    }
}

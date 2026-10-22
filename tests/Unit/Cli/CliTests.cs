using DotNet.Bundler;
using DotNet.Bundler.Cli;


// 从旧 tests/Bundler.Tests/CliTests.cs 收割：CLI 走 CliProgram.Run 进程内入口。
// 去 spawn：MsBuildDefaultsInferFormatsIdentifierAndVersion 直接 Process.Start("dotnet")
// 驱动真实 MSBuild——去向见 Integration/Manual 层对应文件。
// 逐方法判重已跳过（已并入 Cli/CliProgramTests.cs 等）：
//   PrintsUsage、RejectsUnknownCommand、ValidateOk、ValidateBadInput、PlanJson、PlanAll、
//   ConfigCliOverride、RejectsUnknownConfigKey、PrintsVersion、InitWritesTemplateAndRefusesOverwrite、
//   PackageVersionIsAnAliasForVersion、ShortHelpFlagIsAccepted、StrictDisablesInference。
namespace Bundler.UnitTests.Cli;

public sealed class CliTests : TempBase
{
    // 宿主推导目标词：与 CliConfig.HostTarget 同一合同（linux 腿断言用，独立复算防循环遮蔽）。
    static string LinuxHostTarget =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "linux-aarch64" : "linux-x86_64";

    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliProgram.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    string CreateInputDirectory()
    {
        var root = Temp("bundler-cli-tests").FullName;
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
    public void RejectsUnknownFormat()
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
    public void BundleProducesZip()
    {
        var input = CreateInputDirectory();
        var output = Temp("bundler-cli-bundle-").FullName;
        try
        {
            var (code, stdout, _) = Run(BaseArgs("bundle", input, output).Concat(["--quiet"]).ToArray());
            Assert.Equal(0, code);
            var zip = Path.Combine(output, "clifixture-1.0.0-linux-x86_64.zip");
            Assert.True(File.Exists(zip), $"zip artifact must exist at {zip}");
            Assert.True(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            Assert.Contains(zip, stdout);
            // .bundler-work 脚手架连壳层一并回收，不留空目录噪音
            Assert.False(Directory.Exists(Path.Combine(output, ".bundler-work")),
                ".bundler-work scaffold must be fully reclaimed");
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
    public void BundlePerFormatIndependentFailure()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "gated set differs per host");
        var input = CreateInputDirectory();
        var output = Temp("bundler-cli-perfmt-").FullName;
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
    public void BundleSigningHostGateDoesNotBlockOtherFormats()
    {
        // 签名要求 macOS 宿主——属宿主门禁不是配置错：同请求的 zip 照常产出。
        Assert.SkipWhen(TestPlatform.IsMacOS, "仅非 macOS 宿主上签名走宿主门禁。");
        var input = CreateInputDirectory();
        var output = Temp("bundler-cli-signhost-").FullName;
        var config = TempPath("bundler-cli-config.json");
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
    public void BundlePreValidationRejectsBeforeAnyArtifact()
    {
        // 后置格式旋钮错：统一预检聚合拒绝（rc=2），排在前面可产的 zip 不得先落盘。
        var input = CreateInputDirectory();
        var output = TempPath("bundler-cli-prev-out");
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
    public void BundlePreValidationAggregatesAllFormatErrors()
    {
        // 不同 target 上两种格式各自的旋钮错要一次报完——不是撞见第一个就停。
        var input = CreateInputDirectory();
        var output = TempPath("bundler-cli-prevagg-out");
        var config = TempPath("bundler-cli-config.json");
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
    public void ValidateReportsFormatKnobErrors()
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
    public void BundleMatrixViolation()
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
    public void MergesRepeatedInputDirs()
    {
        var x64 = Temp("bundler-cli-merge-x64-").FullName;
        var arm64 = Temp("bundler-cli-merge-arm64-").FullName;
        var output = Temp("bundler-cli-merge-out-").FullName;
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
            var extracted = TempPath("tmp");
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
    public void RepeatedInputDirsHonorReferenceDir()
    {
        // --reference-dir must reach MacUniversalPayloadMerger (regression: the option used to
        // be rejected as an unknown configuration key before it could feed the merge).
        var x64 = Temp("bundler-cli-ref-x64-").FullName;
        var arm64 = Temp("bundler-cli-ref-arm64-").FullName;
        var neutral = Temp("bundler-cli-ref-neutral-").FullName;
        var output = Temp("bundler-cli-ref-out-").FullName;
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
    public void ReferenceDirWithSingleInputDirIsAUsageError()
    {
        // --reference-dir 只服务多输入合并——单输入给了必须拒（rc=2），
        // 不许静默吞旋钮。
        var dir = Temp("bundler-cli-ref1-").FullName;
        var neutral = Temp("bundler-cli-ref1-n-").FullName;
        var output = Temp("bundler-cli-ref1-out-").FullName;
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(neutral);
            var (code, _, err) = Run(
                "bundle", "--input-dir", dir,
                "--reference-dir", neutral,
                "--target", "macos-universal", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 2, $"single-input --reference-dir must exit 2, got {code}: {err}");
            Assert.Contains("--reference-dir", err);
        }
        finally
        {
            foreach (var d in new[] { dir, neutral, output })
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
        }
    }

    [Fact]
    public void MissingInputDirInMergeIsAUsageError()
    {
        // 合并路缺件输入目录与单输入校验同口径——rc=2 用法错，不是 rc=1 IO 失败。
        var dir = Temp("bundler-cli-miss-in-").FullName;
        var missing = TempPath("bundler-cli-miss-none");
        var output = Temp("bundler-cli-miss-out-").FullName;
        try
        {
            Directory.CreateDirectory(dir);
            var (code, _, err) = Run(
                "bundle", "--input-dir", dir, "--input-dir", missing,
                "--target", "macos-universal", "--formats", "zip",
                "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                "--package-version", "1.0.0", "--main-executable", "cli-fixture",
                "--output-dir", output);
            Assert.True(code == 2, $"missing merge input must exit 2, got {code}: {err}");
            Assert.Contains("--input-dir", err);
        }
        finally
        {
            foreach (var d in new[] { dir, missing, output })
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
        }
    }

    [Fact]
    public void MissingReferenceDirIsAUsageError()
    {
        // --reference-dir pointing nowhere must fail fast as a usage error (RC=2) rather
        // than silently merging without the neutral publish (regression: review finding —
        // missing dir used to be ignored and reference-only files vanished from the payload).
        var x64 = Temp("bundler-cli-refmiss-x64-").FullName;
        var arm64 = Temp("bundler-cli-refmiss-arm64-").FullName;
        var missing = TempPath("bundler-cli-refmiss-none");
        var output = Temp("bundler-cli-refmiss-out-").FullName;
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
    public void ConfigFileBundle()
    {
        var input = CreateInputDirectory();
        var output = Temp("bundler-cli-cfg-").FullName;
        var config = TempPath("bundler-cli-config.json");
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
    public void DottedKnobOverride()
    {
        var input = CreateInputDirectory();
        var output = Temp("bundler-cli-dot-").FullName;
        var config = TempPath("bundler-cli-config.json");
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
    public void ConfigSectionMemberDefaults()
    {
        var input = CreateInputDirectory();
        var config = TempPath("bundler-cli-config.json");
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
    public void IconsOverrideResolvesEachEntry()
    {
        var input = CreateInputDirectory();
        var config = TempPath("bundler-cli-config.json");
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
            var anchored = TempPath("anchor.png");
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
    public void OutputLayoutSurvivesConfigAndCliOverride()
    {
        var input = CreateInputDirectory();
        var config = TempPath("bundler-cli-config.json");
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
    public void JsonPathKnobOverridesResolveEachLeaf()
    {
        var input = CreateInputDirectory();
        var config = TempPath("bundler-cli-config.json");
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
    public void RejectsUnknownNestedSectionKeys()
    {
        // 嵌套对象键同样过门禁：旧名 apiIssuer 与笔误都必须报 unknown key 而非
        // 静默丢弃（mac 验证抓到的下钻缺口）。
        var input = CreateInputDirectory();
        var config = TempPath("bundler-cli-config.json");
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
    public void PayloadRelativeKnobsStayUnresolved()
    {
        // PathKnobs 由配置类型 JSON 元数据按 *Files/*Directories 复数后缀导出；
        // 载荷相对键（shortcuts.icon、configLocations）无宿主路径后缀，不得按
        // 配置文件目录解析——钉住 shortcuts.icon 误收 PathKnobs 的回归。
        var input = CreateInputDirectory();
        var configDir = Temp("bundler-cli-pathknob-").FullName;
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
    public void BundleWithOnlyInputDirUsesInferredDefaults()
    {
        // 快速路契约：只给 --input-dir 必须产出包，推断值逐条进 stderr 明示。
        Assert.SkipUnless(OperatingSystem.IsLinux(), "host-derived defaults are asserted on linux");
        var root = Temp("bundler-cli-infer-").FullName;
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
            Assert.Contains($"target={LinuxHostTarget}", err);
            // 缺省集契约（BundleDefaults.DefaultFormatsForTarget）：glibc linux→单件 deb，
            // " (default for" 尾锚把 "deb,zip" 之类的多件集挡在断言外。
            Assert.Contains("formats=deb (default for", err);
            Assert.Contains("productName=coolapp", err);
            Assert.Contains("identifier=dev.bundler.coolapp", err);
            Assert.Contains("version=0.1.0", err);
            Assert.Contains("mainExecutable=coolapp", err);
            Assert.True(Directory.EnumerateFiles(output, "*.deb").Any(),
                "expected a .deb in output: " + string.Join(",", Directory.EnumerateFiles(output)));
            Assert.False(Directory.EnumerateFiles(output, "*.zip").Any(),
                "linux 缺省集只产 deb——出现 zip 说明缺省集回退到了多件形态");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

            [Fact]
    public void BundlerJsonInWorkingDirectoryIsAutoDiscovered()
    {
        var root = Temp("bundler-cli-discover-").FullName;
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
    public void MultiTargetInferenceLabelsOnlyFirstAsUniversal()
    {
        // ≥2 个 --input-dir 走 universal 合并且只进 targets[0]；
        // 其余目标仍按宿主推导——断言两条推断标签各归其位（review flag）。
        var root = Temp("bundler-cli-multitarget-").FullName;
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
    public void FormatsAcceptSemicolonSeparators()
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
    public void UpdateKeygenRefusesOverwriteAndLabelsOutput()
    {
        var root = Temp("bundler-keygen-").FullName;
        // 700 断言必须落在 keygen 自建的父目录上——Temp() 已建的目录是默认
        // umask 权限，断言它会误判实现；用不存在的子目录逼 keygen 亲手建。
        var keyDir = Path.Combine(root, "keys");
        var keyPath = Path.Combine(keyDir, "update.key");
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
                var mode = File.GetUnixFileMode(keyDir);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InitAndKeygenRejectBundleOptions()
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
    public void IconsAcceptSemicolonSeparatedLists()
    {
        // 路径列表同样收 MSBuild 式 ';'——--icons=a.png;b.png 不当单路径。
        var resolved = CliConfig.Resolve(CliArguments.Parse(
            ["plan", "--input-dir", "/tmp/x", "--formats", "zip",
             "--icons", "/tmp/a.png;/tmp/b.png"]));
        Assert.Equal(2, resolved.Bundle.Icons.Count);
    }

    [Fact]
    public void ScalarPathKnobKeepsSemicolonLiteral()
    {
        // 标量路径含 ';' 是合法单值——--output-dir "a;b" 不许劈成数组（review BUG）。
        var resolved = CliConfig.Resolve(CliArguments.Parse(
            ["plan", "--input-dir", "/tmp/x", "--formats", "zip",
             "--output-dir", "/tmp/a;b"]));
        // rooted 路径经 Path.GetFullPath 归一——Windows 下 '/'-根落到当前盘根，
        // 两侧同调抵消宿主差异，断言只钉";"不劈列。
        Assert.Equal(Path.GetFullPath("/tmp/a;b"), resolved.Bundle.OutputDirectory);
    }

    [Fact]
    public void NoExecutableCandidatesEmitsFallbackNote()
    {
        // 目录内零可执行候选时推断层先 note 预警——不等到 validator 才爆炸。
        var root = Temp("bundler-cli-noexec-").FullName;
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

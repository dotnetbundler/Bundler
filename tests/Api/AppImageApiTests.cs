using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.AppImage;

namespace Bundler.ApiTests;

/// <summary>
/// AppImage 公开 API 契约：内嵌 appimagetool 真产 .AppImage——Linux 腿独占
/// （runtime 二进件依目标架构内嵌物化）。
/// 收割自旧 AppImageTests 的 spawn 段（SkipsNonRegularPayloadFiles/BuildsRealAppImage/
/// RepeatBuildIsByteIdentical/CrossBuildsAarch64/SignsAppImage）；AppDirBuilder 直调腿
/// 属 internal 表面，留 Unit 层。
/// </summary>
public sealed class AppImageApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesAppImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "appimagetool/runtime 仅 Linux 宿主可执行");
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.AppImage);
        var artifacts = await new AppImageBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.AppImage, artifact.Format);
        Assert.Equal("sample-1.2.3-x86_64.AppImage", Path.GetFileName(artifact.Path));
        // AppImage = ELF 头（runtime）+ squashfs 负载
        Assert.Equal([0x7F, 0x45, 0x4C, 0x46], BundleFixture.Magic(artifact.Path, 4));
    }

    // input 里混入 unix socket 等非普通文件必须被跳过——公开 API 可观测面是
    // 带套接字的 input 仍能产 .AppImage（AppDir 内不落地属 internal 断言，留 Unit）。
    [Fact]
    public async Task SkipsNonRegularPayloadFiles()
    {
        Assert.SkipUnless(TestPlatform.IsLinux, "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            CreateUnixSocket(Path.Combine(input, "agent.sock"));
            var artifact = (await new AppImageBundler().BuildAsync(
                Configuration(input, output), TestContext.Current.CancellationToken)).Single();
            Assert.True(File.Exists(artifact.Path),
                "the .AppImage build must succeed with a socket inside the input");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    // Real end-to-end runs of the bundled appimagetool (Linux host only).
    [Fact]
    public async Task BuildsRealAppImage()
    {
        Assert.SkipUnless(TestPlatform.IsLinux, "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = (await new AppImageBundler().BuildAsync(
                Configuration(input, output), TestContext.Current.CancellationToken)).Single();
            Assert.Equal("example-app-1.0.0-x86_64.AppImage", Path.GetFileName(artifact.Path));
            var magic = File.ReadAllBytes(artifact.Path).Take(4).ToArray();
            Assert.True(magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F',
                "an .AppImage is an ELF");
            Assert.True(File.Exists(artifact.Path + ".sha256"), "sha256 sidecar must exist");
            var sidecar = File.ReadAllText(artifact.Path + ".sha256");
            Assert.Contains(Sha256(File.ReadAllBytes(artifact.Path)), sidecar);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task RepeatBuildIsByteIdentical()
    {
        Assert.SkipUnless(TestPlatform.IsLinux, "requires a Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var first = (await new AppImageBundler().BuildAsync(
                Configuration(input, output), TestContext.Current.CancellationToken)).Single();
            var firstBytes = File.ReadAllBytes(first.Path);
            var second = (await new AppImageBundler().BuildAsync(
                Configuration(input, output), TestContext.Current.CancellationToken)).Single();
            var secondBytes = File.ReadAllBytes(second.Path);
            Assert.Equal(firstBytes, secondBytes);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task CrossBuildsAarch64()
    {
        Assert.SkipUnless(TestPlatform.IsLinux && TestPlatform.IsX64,
            "requires an x64 Linux host");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        try
        {
            var artifact = (await new AppImageBundler(
                new AppImageBundleConfiguration { Architecture = "aarch64" }).BuildAsync(
                Configuration(input, output), TestContext.Current.CancellationToken)).Single();
            Assert.Equal("example-app-1.0.0-aarch64.AppImage", Path.GetFileName(artifact.Path));
            // ELF e_machine at bytes 18-19: 0xB7 0x00 = EM_AARCH64.
            var header = File.ReadAllBytes(artifact.Path);
            Assert.True(header[18] == 0xB7 && header[19] == 0x00,
                "cross-built AppImage must carry the aarch64 runtime");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task SignsAppImage()
    {
        Assert.SkipUnless(TestPlatform.IsLinux && HasTool("gpg"),
            "requires a Linux host with gpg on PATH");
        var input = CreateInputDirectory();
        var output = input + ".artifacts";
        // The throwaway keyring must stay out of the packaged input: a live
        // gpg-agent drops unix sockets inside its home that cannot be copied.
        var gnupg = input + "-gnupg";
        Directory.CreateDirectory(gnupg);
        var keyFile = Path.Combine(gnupg, "key.asc");
        try
        {
            var batch = Path.Combine(gnupg, "keygen.txt");
            File.WriteAllText(batch,
                "Key-Type: RSA\nKey-Length: 2048\nName-Real: Bundler Test\n" +
                "Name-Email: bundler-test@example.com\nExpire-Date: 0\n" +
                "Passphrase: test-pass\n%commit\n");
            Run("chmod", null, ["700", gnupg]);
            Run("gpg", gnupg, ["--batch", "--gen-key", batch]);
            Run("gpg", gnupg, ["--batch", "--yes", "--pinentry-mode", "loopback",
                "--passphrase", "test-pass", "--export-secret-keys", "--armor",
                "bundler-test@example.com"], stdoutTo: keyFile);

            var artifact = (await new AppImageBundler(new AppImageBundleConfiguration
            {
                Signing = new KeyFileSigningConfiguration { KeyFile = keyFile, Passphrase = "test-pass" }
            }).BuildAsync(Configuration(input, output), TestContext.Current.CancellationToken)).Single();

            // The runtime ELF template always carries both signature sections;
            // unsigned = zero-filled, signed = non-zero embedded data.
            var signedSection = ElfSection(File.ReadAllBytes(artifact.Path), ".sha256_sig");
            Assert.True(signedSection.Length > 0 && signedSection.Any(b => b != 0),
                "a signed .AppImage must carry a non-zero .sha256_sig section");
            var unsignedOut = output + "-unsigned";
            var unsigned = (await new AppImageBundler().BuildAsync(
                Configuration(input, unsignedOut), TestContext.Current.CancellationToken)).Single();
            var unsignedSection = ElfSection(File.ReadAllBytes(unsigned.Path), ".sha256_sig");
            Assert.True(unsignedSection.Length == 0 || unsignedSection.All(b => b == 0),
                "unsigned builds must leave .sha256_sig zeroed");
            Cleanup(unsignedOut);
        }
        finally
        {
            try { Run("gpgconf", gnupg, ["--kill", "gpg-agent"]); }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException
                    or System.ComponentModel.Win32Exception)
            {
                // 清场尽力而为：gpgconf 缺失/进程已退都不应遮蔽测试结果。
            }
            Cleanup(input, output, gnupg);
        }
    }

    static void CreateUnixSocket(string path)
    {
        // sun_path is ~104 bytes on unix; bind a short name, then move the node into place
        var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
        using (var socket = new System.Net.Sockets.Socket(
                   System.Net.Sockets.AddressFamily.Unix,
                   System.Net.Sockets.SocketType.Stream,
                   System.Net.Sockets.ProtocolType.Unspecified))
        {
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
            // .NET unlinks the bound path on dispose; move the node while bound
            File.Move(bindPath, path);
        }
    }

    static byte[] ElfSection(byte[] elf, string name)
    {
        var shOff = (int)BitConverter.ToInt64(elf, 0x28);
        var shSize = (int)BitConverter.ToUInt16(elf, 0x3A);
        var shNum = (int)BitConverter.ToUInt16(elf, 0x3C);
        var shStrNdx = (int)BitConverter.ToUInt16(elf, 0x3E);
        var strOff = (int)BitConverter.ToInt64(elf, shOff + shStrNdx * shSize + 24);
        for (var i = 0; i < shNum; i++)
        {
            var sh = shOff + i * shSize;
            var nameOff = (int)(strOff + BitConverter.ToInt32(elf, sh));
            var end = Array.IndexOf(elf, (byte)0, nameOff);
            if (Encoding.ASCII.GetString(elf, nameOff, end - nameOff) == name)
            {
                var offset = (int)BitConverter.ToInt64(elf, sh + 24);
                var size = (int)BitConverter.ToInt64(elf, sh + 32);
                return elf[offset..(offset + size)];
            }
        }
        return Array.Empty<byte>();
    }

    static bool HasTool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
            {
                return true;
            }
        }
        return false;
    }

    static void Run(string tool, string? gnupgHome, string[] arguments,
        string? stdoutTo = null)
    {
        var info = new DotNet.Bundler.Core.ToolProcessStartInfo
        {
            FileName = tool,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.Arguments.AddRange(arguments);
        if (gnupgHome is not null)
        {
            info.Environment["GNUPGHOME"] = gnupgHome;
        }
        using var process = DotNet.Bundler.Core.ToolProcess.Start(info)
            ?? throw new InvalidOperationException(tool + " spawn did not complete in 60s");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail(tool + " did not exit in 120s");
        }
        Task.WaitAll(stdOut, stdErr);
        Assert.True(process.ExitCode == 0, tool + " failed: " + stdErr.Result);
        if (stdoutTo is not null)
        {
            File.WriteAllText(stdoutTo, stdOut.Result);
        }
    }

    static BundleConfiguration Configuration(string input, string output,
        string target = "linux-x86_64", IReadOnlyList<PackageFormat>? formats = null) => new()
        {
            ProductName = "Example App",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Publisher = "Example",
            Description = "Fixture",
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.AppImage]
                }
            ]
        };

    string CreateInputDirectory()
    {
        var input = Temp("input").FullName;
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "#!/bin/sh\necho ok\n");
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        return input;
    }

    static string Sha256(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var builder = new StringBuilder();
        foreach (var b in sha256.ComputeHash(bytes))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
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
}

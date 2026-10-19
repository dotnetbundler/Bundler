using DotNet.Bundler;
using DotNet.Bundler.Signing.Windows;

namespace Bundler.UnitTests.Signing;

public sealed class WindowsAuthenticodeSignerTests
{
    [Fact]
    public void Ctor_RequiresExactlyOneCertSource()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions()));
        Assert.Throws<ArgumentException>(() =>
            new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
            {
                PfxFile = "a.pfx",
                CertificateThumbprint = "AB12"
            }));
    }

    [Fact]
    public void Ctor_PfxOrThumbprint_Accepted()
    {
        Assert.NotNull(new WindowsAuthenticodeSigner(
            new WindowsAuthenticodeSigningOptions { PfxFile = "a.pfx" }));
        Assert.NotNull(new WindowsAuthenticodeSigner(
            new WindowsAuthenticodeSigningOptions { CertificateThumbprint = "AB12" }));
    }

    [Fact]
    public async Task SignAsync_NullRequest_Throws()
    {
        var signer = new WindowsAuthenticodeSigner(
            new WindowsAuthenticodeSigningOptions { PfxFile = "a.pfx" });
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => signer.SignAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignAsync_NonWindows_Pnse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "PNSE 支路只在非 Windows 触发");
        var signer = new WindowsAuthenticodeSigner(
            new WindowsAuthenticodeSigningOptions { PfxFile = "a.pfx" });
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            signer.SignAsync(new BundleSigningRequest(
                "x.exe", BundleSigningArtifactKind.Installer, "App", "windows-x86_64"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignAsync_MissingFile_Throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "文件存在性检查在宿主门之后");
        var signer = new WindowsAuthenticodeSigner(
            new WindowsAuthenticodeSigningOptions { PfxFile = "a.pfx" });
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            signer.SignAsync(new BundleSigningRequest(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"),
                BundleSigningArtifactKind.Installer, "App", "windows-x86_64"),
                TestContext.Current.CancellationToken));
    }
}

public sealed class WindowsExternalCommandSignerTests
{
    private static readonly BundleSigningRequest Request = new(
        "C:\\out dir\\setup.exe", BundleSigningArtifactKind.Installer, "App", "windows-x86_64");

    [Fact]
    public void Ctor_EmptyCommand_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions()));
    }

    [Fact]
    public void Ctor_NoPathPlaceholder_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
            {
                Command = "signtool",
                Arguments = ["sign", "/f", "cert.pfx"]
            }));
    }

    [Fact]
    public void Ctor_PathPlaceholders_Accepted()
    {
        Assert.NotNull(new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = "signtool", Arguments = ["sign", "{path}"]
        }));
        Assert.NotNull(new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = "signtool", Arguments = ["sign", "%1"]
        }));
    }

    [Fact]
    public void Placeholder_Pct10_NotAPathPlaceholder()
    {
        // %1 只在 token 边界匹配：%10/%100 不得误伤
        Assert.False(WindowsExternalCommandSigner.ContainsPathPlaceholder("%10"));
        Assert.False(WindowsExternalCommandSigner.ContainsPathPlaceholder("%100"));
        Assert.True(WindowsExternalCommandSigner.ContainsPathPlaceholder("%1"));
        Assert.True(WindowsExternalCommandSigner.ContainsPathPlaceholder("sign %1 /x"));
    }

    [Fact]
    public void Expand_AllPlaceholders()
    {
        var expanded = WindowsExternalCommandSigner.Expand(
            "sign {path} {artifactKind} {target} {productName} %1",
            Request, "C:\\resolved\\setup.exe");
        Assert.Equal(
            "sign C:\\resolved\\setup.exe Installer windows-x86_64 App C:\\resolved\\setup.exe",
            expanded);
    }

    [Fact]
    public void Expand_LongerPercentTokens_Untouched()
    {
        Assert.Equal("%10",
            WindowsExternalCommandSigner.Expand("%10", Request, "p"));
    }

    [Fact]
    public void Quote_NoSpecialChars_PassThrough()
    {
        Assert.Equal("abc.exe", WindowsExternalCommandSigner.Quote("abc.exe"));
    }

    [Fact]
    public void Quote_Spaces_Wrapped()
    {
        Assert.Equal("\"a b\"", WindowsExternalCommandSigner.Quote("a b"));
    }

    [Fact]
    public void Quote_InnerQuote_Escaped()
    {
        Assert.Equal("\"a\\\"b\"", WindowsExternalCommandSigner.Quote("a\"b"));
    }

    [Fact]
    public void Quote_NoWhitespace_PassThrough()
    {
        // 无空格/引号不包引号——尾部反斜杠原样
        Assert.Equal("dir\\", WindowsExternalCommandSigner.Quote("dir\\"));
    }

    [Fact]
    public void Quote_SpacedPathTrailingSlash_SlashesDoubled()
    {
        // argv 解析规则：包引号时收尾反斜杠须翻倍防吞引号
        Assert.Equal("\"dir \\\\\"", WindowsExternalCommandSigner.Quote("dir \\"));
    }

    [Fact]
    public async Task SignAsync_MissingFile_Throws()
    {
        var signer = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = "anything", Arguments = ["{path}"]
        });
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            signer.SignAsync(new BundleSigningRequest(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"),
                BundleSigningArtifactKind.Installer, "App", "windows-x86_64"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignAsync_HungCommand_BoundedTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe 仅 Windows 宿主");
        var dir = Path.Combine(Path.GetTempPath(), "bundler-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "hang.cmd");
        File.WriteAllText(script, "@ping -n 60 127.0.0.1 >nul\r\n");
        try
        {
            // Timeout 走 options 显式旋钮而非进程级环境变量——并发测试互不污染。
            var signer = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
            {
                Command = "cmd", Arguments = ["/c", "{path}"],
                Timeout = TimeSpan.FromSeconds(1)
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                signer.SignAsync(new BundleSigningRequest(
                    script, BundleSigningArtifactKind.Installer, "App", "windows-x86_64"),
                    CancellationToken.None));
            sw.Stop();
            Assert.Contains("did not exit", ex.Message, StringComparison.Ordinal);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                $"hung signer must be bounded, took {sw.Elapsed}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

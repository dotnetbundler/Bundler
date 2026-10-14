using DotNet.Bundler.Cli;

namespace Bundler.UnitTests.Cli;

/// <summary>CliProgram.Run 进程内驱动：退出码表与 stdout/stderr 契约。</summary>
public sealed class CliProgramTests : TempBase
{


    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliProgram.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Run_Version_Zero()
    {
        var (code, stdout, _) = Run("--version");
        Assert.Equal(0, code);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }

    [Fact]
    public void Run_Help_Zero_UsageOnStdout()
    {
        var (code, stdout, stderr) = Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("Usage:", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Run_NoArgs_Two_UsageOnStderr()
    {
        var (code, _, stderr) = Run();
        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Fact]
    public void Run_UnknownCommand_Two()
    {
        var (code, _, stderr) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("Unknown command", stderr);
    }

    [Fact]
    public void Run_UnknownOption_Two()
    {
        var (code, _, stderr) = Run("bundle", "--bogus");
        Assert.Equal(2, code);
        Assert.Contains("Unknown option", stderr);
    }

    [Fact]
    public void Run_UpdateKeygen_WritesKey_PublicOnStdout()
    {
        var keyPath = Path.Combine(Root, "keys", "k.pem");
        var (code, stdout, _) = Run("update-keygen", "--key-file", keyPath);
        Assert.Equal(0, code);
        Assert.True(File.Exists(keyPath));
        Assert.Contains("public key:", stdout);
        var keyJson = File.ReadAllText(keyPath);
        Assert.Contains("ec-p256", keyJson);
        Assert.Contains("\"d\"", keyJson);

        // 二跑同路径拒覆盖 rc=2
        var (code2, _, stderr2) = Run("update-keygen", "--key-file", keyPath);
        Assert.Equal(2, code2);
        Assert.Contains("already exists", stderr2);
    }

    [Fact]
    public void Run_Init_WritesTemplate_RefusesOverwrite()
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Root);
            var (code, stdout, _) = Run("init");
            Assert.Equal(0, code);
            var config = Path.Combine(Root, "bundler.json");
            Assert.True(File.Exists(config));
            Assert.Contains("productName", File.ReadAllText(config));

            var (code2, _, stderr2) = Run("init");
            Assert.Equal(2, code2);
            Assert.Contains("already exists", stderr2);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Run_Init_WithBundleOptions_Rejected()
    {
        var (code, _, stderr) = Run("init", "--product-name", "X");
        Assert.Equal(2, code);
        Assert.Contains("has no effect", stderr);
    }

    [Fact]
    public void Run_Validate_ValidConfig_Zero()
    {
        var input = Path.Combine(Root, "in");
        var output = Path.Combine(Root, "out");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "sample"), "#!/bin/sh\n");

        // --main-executable 显式给：缺省名 <productName> 的存在性检查在
        // Windows 大小写不敏感 FS 上会误配 sample——断言不该依赖宿主 FS 语义。
        var (code, stdout, _) = Run("validate", "--no-config",
            "--target", "linux-x86_64", "--formats", "deb",
            "--input-dir", input, "--output-dir", output,
            "--product-name", "Sample", "--identifier", "com.example.sample",
            "--package-version", "1.0.0", "--main-executable", "sample");
        Assert.Equal(0, code);
        Assert.Contains("valid", stdout);
    }

    [Fact]
    public void Run_Validate_InvalidConfig_Two()
    {
        var (code, _, stderr) = Run("validate", "--no-config",
            "--target", "linux-x86_64", "--formats", "deb",
            "--input-dir", Root, "--output-dir", Path.Combine(Root, "out"),
            "--product-name", "", "--identifier", "com.example.sample",
            "--package-version", "1.0.0");
        Assert.Equal(2, code);
        Assert.Contains("productName", stderr);
    }

    [Fact]
    public void Run_Plan_ListsItems()
    {
        var input = Path.Combine(Root, "in");
        var output = Path.Combine(Root, "out");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "sample"), "#!/bin/sh\n");

        var (code, stdout, _) = Run("plan", "--no-config",
            "--target", "linux-x86_64", "--formats", "deb",
            "--input-dir", input, "--output-dir", output,
            "--product-name", "Sample", "--identifier", "com.example.sample",
            "--package-version", "1.0.0", "--main-executable", "sample");
        Assert.Equal(0, code);
        Assert.Contains("linux-x86_64", stdout);
        Assert.Contains("deb", stdout);
    }

    [Fact]
    public void Run_Plan_Json_FormatsStructured()
    {
        var input = Path.Combine(Root, "in");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "sample"), "x");

        var (code, stdout, stderr) = Run("plan", "--no-config", "--json",
            "--target", "linux-x86_64", "--formats", "deb,zip",
            "--input-dir", input, "--output-dir", Path.Combine(Root, "out"),
            "--product-name", "Sample", "--identifier", "com.example.sample",
            "--package-version", "1.0.0", "--main-executable", "sample");
        Assert.True(code == 0, $"rc={code} stderr: {stderr}");
        Assert.Contains("\"items\"", stdout);
        Assert.Contains("\"deb\"", stdout);
        Assert.Contains("\"zip\"", stdout);
    }
}

// WiX 内部缝腿：WixProcessRunner/WixToolsetResolver 是 internal（IVT 到本程序集），
// Api 面够不着——挂起工具进程的取消语义与真实 candle.exe 的 -wx 警告升级腿留在这里。
// 原 WixTests 的进程行为两腿；挂起桩是 POSIX sh 脚本，编译腿要真 Windows 宿主。
using DotNet.Bundler.Wix;

public sealed class WixToolingFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; } = IntegrationWorkspace.Create(
        "wix-tooling-integration", "BundlerWixToolingIntegration");

    public void Dispose() => Ws.Dispose();
}

public sealed class WixToolingTests : IClassFixture<WixToolingFixture>
{
    private readonly IntegrationWorkspace _ws;

    public WixToolingTests(WixToolingFixture fixture) => _ws = fixture.Ws;

    [Fact]
    public async Task CancelsHangingWixToolProcess()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "the hanging stub is a POSIX shell script");
        var root = _ws.Combine("hang-cancel");
        Directory.CreateDirectory(root);
        var stub = Path.Combine(root, "hang.sh");
        await File.WriteAllTextAsync(stub, "#!/bin/sh\nexec sleep 60\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WixProcessRunner.RunAsync(stub, [], root, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RejectsWixCompilerWarnings()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = _ws.Combine("compiler-warnings");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        var toolset = await WixToolsetResolver.ResolveAsync(cache, null, CancellationToken.None);
        var source = Path.Combine(root, "warning.wxs");
        var output = Path.Combine(root, "warning.wixobj");
        File.WriteAllText(source,
            "<?xml version=\"1.0\"?>\n<?warning freeze sentinel warning?>\n" +
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment /></Wix>");
        await ExpectAsync<InvalidOperationException>(() => WixProcessRunner.RunAsync(
            toolset.CandlePath, ["-nologo", "-out", output, source],
            root, CancellationToken.None), "freeze sentinel warning");
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception) when (exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} containing '{message}'.");
    }
}

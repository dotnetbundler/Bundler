using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class ToolRunnerTests
{
    [Fact]
    public async Task RunAsync_ViaHandler_SuccessResult()
    {
        ToolRunner.Handler = (request, _) =>
            Task.FromResult(new ToolRunner.Result(0, "ok", ""));
        try
        {
            await ToolRunner.RunAsync("tool", ["--flag"], "/work", TestContext.Current.CancellationToken);
        }
        finally
        {
            ToolRunner.Handler = null;
        }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsWithOutput()
    {
        ToolRunner.Handler = (request, _) =>
            Task.FromResult(new ToolRunner.Result(3, "stdout-text", "stderr-text"));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ToolRunner.RunAsync("tool", [], "/work", TestContext.Current.CancellationToken));
            Assert.Contains("code 3", exception.Message);
            Assert.Contains("stdout-text", exception.Message);
            Assert.Contains("stderr-text", exception.Message);
        }
        finally
        {
            ToolRunner.Handler = null;
        }
    }

    [Fact]
    public async Task TryRunAsync_HandlerCannotYieldNull_RealStartOnly()
    {
        // Handler 缝签名是 Task<Result>（非可空）——"启动失败返 null" 只发生在真实 spawn 路径。
        ToolRunner.Handler = (_, _) => Task.FromResult(new ToolRunner.Result(0, "", ""));
        try
        {
            var result = await ToolRunner.TryRunAsync(
                "tool", [], "/work", TestContext.Current.CancellationToken);
            Assert.NotNull(result);
        }
        finally
        {
            ToolRunner.Handler = null;
        }
    }

    [Fact]
    public async Task TryRunAsync_HandlerReceivesRequest()
    {
        ToolRunner.Request? captured = null;
        ToolRunner.Handler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(new ToolRunner.Result(0, "", ""));
        };
        try
        {
            await ToolRunner.TryRunAsync(
                "mytool", ["a", "b"], "/wd", TestContext.Current.CancellationToken,
                new Dictionary<string, string> { ["K"] = "V" });

            Assert.NotNull(captured);
            Assert.Equal("mytool", captured!.Executable);
            Assert.Equal(["a", "b"], captured.Arguments);
            Assert.Equal("/wd", captured.WorkingDirectory);
            Assert.Equal("V", captured.Environment!["K"]);
        }
        finally
        {
            ToolRunner.Handler = null;
        }
    }

    [Fact]
    public async Task TryRunAsync_Handler_FlowsIntoAsyncTasks()
    {
        // AsyncLocal 随 ExecutionContext 流入 Task.Run——缝在异步上下文内一致可见，
        // 这正是它能替代全局静态、又允许测试隔离的语义。
        ToolRunner.Handler = (_, _) => Task.FromResult(new ToolRunner.Result(7, "", ""));
        try
        {
            var probe = await Task.Run(() =>
                ToolRunner.TryRunAsync("t", [], "/w", TestContext.Current.CancellationToken));
            Assert.NotNull(probe);
            Assert.Equal(7, probe.ExitCode);
        }
        finally
        {
            ToolRunner.Handler = null;
        }
    }

    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("trailing\\", "\"trailing\\\\\"")]
    [InlineData("back\\slash", "\"back\\slash\"")]
    public void QuoteArgument_WindowsRules(string input, string expected)
    {
        Assert.Equal(expected, ToolRunner.QuoteArgument(input));
    }
}

using DotNet.Bundler;
using DotNet.Bundler.Cli;

namespace Bundler.UnitTests.Cli;

public sealed class CliBundleLoggerTests
{
    private static string Render(bool quiet, bool verbose, Action<CliBundleLogger> act)
    {
        var writer = new StringWriter();
        act(new CliBundleLogger(writer, quiet, verbose));
        return writer.ToString();
    }

    [Fact]
    public void Quiet_ErrorsOnly()
    {
        var output = Render(quiet: true, verbose: false, log =>
        {
            log.Log(BundleLogLevel.Information, "info");
            log.Log(BundleLogLevel.Warning, "warn");
            log.Log(BundleLogLevel.Error, "err");
            log.Log(BundleLogLevel.Trace, "trace");
        });
        Assert.Contains("err", output);
        Assert.DoesNotContain("info", output);
        Assert.DoesNotContain("warn", output);
        Assert.DoesNotContain("trace", output);
    }

    [Fact]
    public void Normal_TraceSuppressed()
    {
        var output = Render(quiet: false, verbose: false, log =>
        {
            log.Log(BundleLogLevel.Information, "info");
            log.Log(BundleLogLevel.Trace, "trace");
        });
        Assert.Contains("info", output);
        Assert.DoesNotContain("trace", output);
    }

    [Fact]
    public void Verbose_AllLevels()
    {
        var output = Render(quiet: false, verbose: true, log =>
            log.Log(BundleLogLevel.Trace, "trace"));
        Assert.Contains("[bundler:trace] trace", output);
    }

    [Fact]
    public void LevelLabel_Lowercase()
    {
        var output = Render(quiet: false, verbose: false, log =>
            log.Log(BundleLogLevel.Warning, "w"));
        Assert.Contains("[bundler:warning]", output);
    }
}

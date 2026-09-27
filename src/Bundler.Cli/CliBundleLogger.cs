using DotNet.Bundler;

namespace DotNet.Bundler.Cli;

/// <summary>Three-level stderr logger: quiet (errors only), normal (default), verbose (includes Trace).</summary>
internal sealed class CliBundleLogger : IBundleLogger
{
    private readonly TextWriter _output;
    private readonly bool _quiet;
    private readonly bool _verbose;

    public CliBundleLogger(TextWriter output, bool quiet, bool verbose)
    {
        _output = output;
        _quiet = quiet;
        _verbose = verbose;
    }

    public void Log(BundleLogLevel level, string message)
    {
        if (_quiet && level is not BundleLogLevel.Error)
        {
            return;
        }
        if (!_verbose && level == BundleLogLevel.Trace)
        {
            return;
        }
        _output.WriteLine($"[bundler:{level.ToString().ToLowerInvariant()}] {message}");
    }
}

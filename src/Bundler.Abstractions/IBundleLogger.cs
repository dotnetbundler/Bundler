namespace DotNet.Bundler;

public interface IBundleLogger
{
    void Log(BundleLogLevel level, string message);
}

public enum BundleLogLevel
{
    Trace,
    Information,
    Warning,
    Error
}

public sealed class NullBundleLogger : IBundleLogger
{
    public static NullBundleLogger Instance { get; } = new();

    private NullBundleLogger()
    {
    }

    public void Log(BundleLogLevel level, string message)
    {
    }
}

using DotNet.Bundler;

namespace DotNet.Bundler.Deb;

public sealed class DebBundlerOptions
{
    public IBundleLogger? Logger { get; init; }
}

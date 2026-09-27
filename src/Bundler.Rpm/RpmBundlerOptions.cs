using DotNet.Bundler;

namespace DotNet.Bundler.Rpm;

public sealed class RpmBundlerOptions
{
    public IBundleLogger? Logger { get; init; }
}

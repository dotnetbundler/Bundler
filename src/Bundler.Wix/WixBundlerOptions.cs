namespace DotNet.Bundler.Wix;

using DotNet.Bundler;
using DotNet.Bundler.Core;

public sealed class WixBundlerOptions
{
    public string? ToolCacheDirectory { get; init; }
    public string? ToolsetArchivePath { get; init; }
    public IBundleLogger? Logger { get; init; }
    public IBundleSigner? Signer { get; init; }

    internal string ResolveToolCacheDirectory() =>
        ToolCache.ResolveCacheDirectory(ToolCacheDirectory);
}

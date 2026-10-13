namespace DotNet.Bundler.Nsis;

using DotNet.Bundler;
using DotNet.Bundler.Core;

public sealed class NsisBundlerOptions
{
    public string? ToolCacheDirectory { get; init; }
    public string? ToolsetArchivePath { get; init; }
    public string? CompilerPath { get; init; }
    public string? DataDirectory { get; init; }
    public string? TemplatePath { get; init; }
    public string? LanguageDirectory { get; init; }
    public IBundleSigner? Signer { get; init; }
    public IBundleLogger? Logger { get; init; }

    internal string ResolveToolCacheDirectory() =>
        ToolCache.ResolveCacheDirectory(ToolCacheDirectory);
}

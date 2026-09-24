namespace DotNet.Bundler.Wix;

using DotNet.Bundler;

public sealed class WixBundlerOptions
{
    public string? ToolCacheDirectory { get; init; }
    public string? ToolsetArchivePath { get; init; }
    public IBundleLogger? Logger { get; init; }

    internal string ResolveToolCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ToolCacheDirectory))
        {
            return Path.GetFullPath(ToolCacheDirectory!);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }
        return Path.Combine(localData, "DotNetBundler", "tools");
    }
}

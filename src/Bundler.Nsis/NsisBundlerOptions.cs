namespace DotNet.Bundler.Nsis;

using DotNet.Bundler;

public sealed class NsisBundlerOptions
{
    public string? ToolCacheDirectory { get; init; }
    public string? ToolArchivePath { get; init; }
    public string? CompilerPath { get; init; }
    public string? TemplatePath { get; init; }
    public string? LanguageDirectory { get; init; }
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

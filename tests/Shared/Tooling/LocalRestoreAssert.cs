// Assert-LocalBundlerRestore 的 C# 移植：仓外 fixture 还原后核验
// 本地包源、隔离缓存、逐包来源元数据与 Bundler 版本一致性。
using System.Text.Json;

internal static class LocalRestoreAssert
{
    public static void Verify(string project, string packageVersion, string source,
        string cache, IReadOnlyList<string> requiredPackages)
    {
        var assetsPath = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Restore assets are missing: {assetsPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = doc.RootElement;
        var expectedSource = Normalize(source);
        var expectedCache = Normalize(cache);

        var sources = root.GetProperty("project").GetProperty("restore").GetProperty("sources");
        var localSources = sources.EnumerateObject()
            .Select(p => p.Name)
            .Where(s => !s.Contains("://"))
            .Select(Normalize)
            .ToArray();
        Assert.Contains(expectedSource, localSources);

        var folders = root.GetProperty("packageFolders").EnumerateObject()
            .Select(p => Normalize(p.Name))
            .ToArray();
        Assert.Contains(expectedCache, folders);

        var libraries = root.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name)
            .ToArray();
        foreach (var name in requiredPackages)
        {
            Assert.Contains($"{name}/{packageVersion}", libraries);
            var packageDir = Path.Combine(expectedCache, name.ToLowerInvariant(), packageVersion);
            var packagePath = Path.Combine(packageDir, $"{name.ToLowerInvariant()}.{packageVersion}.nupkg");
            Assert.True(File.Exists(packagePath), $"Restored package is missing from the isolated cache: {packagePath}");
            var metadataPath = Path.Combine(packageDir, ".nupkg.metadata");
            Assert.True(File.Exists(metadataPath), $"Restored package is missing provenance metadata: {metadataPath}");
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var origin = metadata.RootElement.GetProperty("source").GetString() ?? "";
            Xunit.Assert.True(origin.Length > 0 && !origin.Contains("://") && Normalize(origin) == expectedSource,
                $"Restored package did not come from the local source: {name}/{packageVersion} (source: {origin})");
        }
        var wrongVersions = libraries
            .Where(l => l.StartsWith("DotNet.Bundler/", StringComparison.Ordinal) ||
                        l.StartsWith("DotNet.Bundler.", StringComparison.Ordinal))
            .Where(l => !l.EndsWith($"/{packageVersion}", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(wrongVersions);
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

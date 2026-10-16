using System.Text;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class ToolCacheTests : TempBase
{
    private static string Sha(byte[] content) =>
        Hashes.Sha256Hex(content);

    private static ToolFileSet Set(params (string path, byte[] content)[] files) =>
        new("tool", "1.0",
            ToolCache.ComputeContentId(files.Select(f => new ToolFileSpec(f.path, Sha(f.content))).ToArray()),
            files.Select(f => new ToolFileSpec(f.path, Sha(f.content), f.content.Length)).ToArray());

    private static Func<ToolFileSpec, CancellationToken, Task<Stream>> Opener(
        IReadOnlyDictionary<string, byte[]> content) =>
        (spec, _) => Task.FromResult<Stream>(new MemoryStream(content[spec.RelativePath]));

    private static Dictionary<string, byte[]> Map(params (string path, byte[] content)[] files) =>
        files.ToDictionary(f => f.path, f => f.content);

    [Fact]
    public void ResolveCacheDirectory_ExplicitWins()
    {
        Assert.Equal(Path.GetFullPath("/custom/cache"),
            ToolCache.ResolveCacheDirectory("/custom/cache"));
    }

    [Fact]
    public void ResolveCacheDirectory_Default_UnderLocalAppData()
    {
        var resolved = ToolCache.ResolveCacheDirectory(null);
        Assert.EndsWith(Path.Combine("DotNetBundler", "tools"), resolved);
    }

    [Fact]
    public void ComputeContentId_Stable_AndOrderIndependent()
    {
        var a = new ToolFileSpec("b/x", new string('a', 64));
        var b = new ToolFileSpec("a/y", new string('b', 64));
        var forward = ToolCache.ComputeContentId([a, b]);
        var reverse = ToolCache.ComputeContentId([b, a]);
        Assert.Equal(forward, reverse);
        Assert.Equal(64, forward.Length);
    }

    [Fact]
    public void ResolveToolPath_RejectsEscape()
    {
        var dir = TempPath("tooldir");
        Assert.Throws<ArgumentException>(() => ToolCache.ResolveToolPath(dir, "../evil"));
        Assert.Throws<ArgumentException>(() => ToolCache.ResolveToolPath(dir, ""));
        Assert.Throws<ArgumentException>(() => ToolCache.ResolveToolPath(dir, Path.IsPathRooted("C:/x") ? "C:/x" : "/x"));
    }

    [Fact]
    public async Task ResolveAsync_MaterializesFiles()
    {
        var cache = Temp("bundler-cache").FullName;
        var set = Set(("bin/tool", "binary"u8.ToArray()), ("etc/cfg", "cfg"u8.ToArray()));
        var map = Map(("bin/tool", "binary"u8.ToArray()), ("etc/cfg", "cfg"u8.ToArray()));

        var resolved = await ToolCache.ResolveAsync(cache, set, Opener(map),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, "bin", "tool")));
        Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, ToolCache.ManifestFileName)));
        Assert.Equal("binary", File.ReadAllText(Path.Combine(resolved.DirectoryPath, "bin", "tool")));
    }

    [Fact]
    public async Task ResolveAsync_CacheHit_SkipsOpen()
    {
        var cache = Temp("bundler-cache").FullName;
        var set = Set(("a", "x"u8.ToArray()));
        var map = Map(("a", "x"u8.ToArray()));

        await ToolCache.ResolveAsync(cache, set, Opener(map), TestContext.Current.CancellationToken);

        var opened = false;
        var resolved = await ToolCache.ResolveAsync(cache, set,
            (spec, _) => { opened = true; return Task.FromResult<Stream>(new MemoryStream()); },
            TestContext.Current.CancellationToken);

        Assert.False(opened);
        Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, "a")));
    }

    [Fact]
    public async Task ResolveAsync_TamperedFile_Reinstalls()
    {
        var cache = Temp("bundler-cache").FullName;
        var set = Set(("a", "original"u8.ToArray()));
        var map = Map(("a", "original"u8.ToArray()));

        var first = await ToolCache.ResolveAsync(cache, set, Opener(map),
            TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(first.DirectoryPath, "a"), "tampered");

        var reopened = false;
        var second = await ToolCache.ResolveAsync(cache, set,
            (spec, _) => { reopened = true; return Task.FromResult<Stream>(new MemoryStream(map[spec.RelativePath])); },
            TestContext.Current.CancellationToken);

        Assert.True(reopened);
        Assert.Equal("original", File.ReadAllText(Path.Combine(second.DirectoryPath, "a")));
    }

    [Fact]
    public async Task ResolveAsync_WrongContent_ThrowsAndNotPersisted()
    {
        var cache = Temp("bundler-cache").FullName;
        var set = Set(("a", "expected"u8.ToArray()));
        var bad = Map(("a", "WRONG"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ToolCache.ResolveAsync(cache, set, Opener(bad), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(cache));
    }

    [Fact]
    public async Task ResolveAsync_InvalidSegments_Rejected()
    {
        var cache = Temp("bundler-cache").FullName;
        var files = new[] { new ToolFileSpec("a", new string('a', 64)) };
        var badName = new ToolFileSet("..", "1.0", ToolCache.ComputeContentId(files), files);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ToolCache.ResolveAsync(cache, badName, Opener(Map()), TestContext.Current.CancellationToken));

        var badSha = new ToolFileSet("ok", "1.0", "nothex", files);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ToolCache.ResolveAsync(cache, badSha, Opener(Map()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_DuplicatePaths_Rejected()
    {
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var files = new[]
        {
            new ToolFileSpec("a", Sha(content), content.Length),
            new ToolFileSpec("a", Sha(content), content.Length)
        };
        var set = new ToolFileSet("tool", "1.0", ToolCache.ComputeContentId(files), files);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ToolCache.ResolveAsync(cache, set, Opener(Map(("a", content))),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_ReservedManifestName_Rejected()
    {
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var files = new[] { new ToolFileSpec(ToolCache.ManifestFileName, Sha(content), content.Length) };
        var set = new ToolFileSet("tool", "1.0", ToolCache.ComputeContentId(files), files);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ToolCache.ResolveAsync(cache, set, Opener(Map()), TestContext.Current.CancellationToken));
    }
}

using System.Diagnostics;
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

    private static string LockPath(string cache, ToolFileSet set) =>
        Path.Combine(cache,
            $"{set.Name}-{set.Version}-{set.ContentId.Substring(0, 12).ToLowerInvariant()}.lock");

    [Fact]
    public async Task ResolveAsync_LockHeld_WaitsForRelease()
    {
        // 排他锁真挡物化：持锁期间 ResolveAsync 不能完成，放锁后才落位。
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var set = Set(("a", content));
        var map = Map(("a", content));
        using var held = new FileStream(
            LockPath(cache, set), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var pending = ToolCache.ResolveAsync(
            cache, set, Opener(map), TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        held.Dispose();
        var resolved = await pending.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("x", File.ReadAllText(Path.Combine(resolved.DirectoryPath, "a")));
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentResolves_SingleConsistentEntry()
    {
        var cache = Temp("bundler-cache").FullName;
        var a = "payload"u8.ToArray();
        var c = "data"u8.ToArray();
        var set = Set(("a", a), ("b/c", c));
        var map = Map(("a", a), ("b/c", c));
        // 慢 opener 拉长临界区——并发者真的在锁上排队而不是错身。
        Func<ToolFileSpec, CancellationToken, Task<Stream>> slowOpener = async (spec, ct) =>
        {
            await Task.Delay(20, ct);
            return new MemoryStream(map[spec.RelativePath]);
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => ToolCache.ResolveAsync(
                cache, set, slowOpener, TestContext.Current.CancellationToken))));

        var directory = results[0].DirectoryPath;
        Assert.All(results, r => Assert.Equal(directory, r.DirectoryPath));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(directory, "a")));
        Assert.True(File.Exists(Path.Combine(directory, ToolCache.ManifestFileName)));
        // 暂存原子落位：并发收敛后只剩最终目录，不留 .{name}-* 暂存残骸。
        Assert.Single(Directory.GetDirectories(cache));
    }

    [Fact]
    public async Task ResolveAsync_SweepsStaleStaging_OnlyMatchingSha12()
    {
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var set = Set(("a", content));
        var map = Map(("a", content));
        var sha12 = set.ContentId.Substring(0, 12).ToLowerInvariant();

        // 同 sha12 的崩溃暂存：本工具集的孤儿，清扫。
        var stale = Path.Combine(cache, $".tool-1.0-{sha12}-deadbeef");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk"), "j");
        // 异 sha12 前缀的暂存：可能是另一 (name,version) 并行物化的在跑目录，不许动。
        var inflight = Path.Combine(cache, ".tool-1.0-ffffffffffff-inflight");
        Directory.CreateDirectory(inflight);
        File.WriteAllText(Path.Combine(inflight, "junk"), "j");

        var resolved = await ToolCache.ResolveAsync(
            cache, set, Opener(map), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(inflight));
        Assert.Equal("x", File.ReadAllText(Path.Combine(resolved.DirectoryPath, "a")));
    }

    [Fact]
    public async Task ResolveAsync_CacheRootReparsePoint_Rejected()
    {
        var real = Temp("bundler-real").FullName;
        var link = TempPath("bundler-cache-link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (IOException) { Assert.Skip("symlink creation not permitted"); }
        catch (UnauthorizedAccessException) { Assert.Skip("symlink creation not permitted"); }

        var content = "x"u8.ToArray();
        var set = Set(("a", content));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ToolCache.ResolveAsync(link, set, Opener(Map(("a", content))),
                TestContext.Current.CancellationToken));
        Assert.Contains("reparse", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_LockFileReparsePoint_Rejected()
    {
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var set = Set(("a", content));
        var target = TempPath("lock-target");
        File.WriteAllText(target, "x");
        try
        {
            File.CreateSymbolicLink(LockPath(cache, set), target);
        }
        catch (IOException) { Assert.Skip("symlink creation not permitted"); }
        catch (UnauthorizedAccessException) { Assert.Skip("symlink creation not permitted"); }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ToolCache.ResolveAsync(cache, set, Opener(Map(("a", content))),
                TestContext.Current.CancellationToken));
        Assert.Contains("reparse", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_CachedTreeWithInjectedLink_Reinstalled()
    {
        var cache = Temp("bundler-cache").FullName;
        var content = "x"u8.ToArray();
        var set = Set(("a", content));
        var map = Map(("a", content));
        var first = await ToolCache.ResolveAsync(
            cache, set, Opener(map), TestContext.Current.CancellationToken);

        var outside = Temp("bundler-outside").FullName;
        var injected = Path.Combine(first.DirectoryPath, "injected-link");
        try
        {
            Directory.CreateSymbolicLink(injected, outside);
        }
        catch (IOException) { Assert.Skip("symlink creation not permitted"); }
        catch (UnauthorizedAccessException) { Assert.Skip("symlink creation not permitted"); }

        var reopened = false;
        var second = await ToolCache.ResolveAsync(cache, set,
            (spec, _) =>
            {
                reopened = true;
                return Task.FromResult<Stream>(new MemoryStream(map[spec.RelativePath]));
            },
            TestContext.Current.CancellationToken);

        // 校验枚举遇 reparse 点判缓存无效 → 重开源流整树重装，不拿带链缓存命中。
        Assert.True(reopened);
        Assert.False(Directory.Exists(Path.Combine(second.DirectoryPath, "injected-link")));
        // 清扫只删链接本身：链接目标目录不许被级联清掉。
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task AcquireLockAsync_DeterministicAbsence_ThrowsIOExceptionNotTimeout()
    {
        // 父目录确定性缺席：OpenOrCreate 抛 DirectoryNotFoundException(⊂IOException) 且
        // File.Exists 恒假 → 走 1s 缺席宽限后直抛底层错误，不误报 TimeoutException。
        var lockPath = Path.Combine(TempPath("no-such-parent"), "entry.lock");
        var elapsed = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            ToolCache.AcquireLockAsync(lockPath, TestContext.Current.CancellationToken));
        Assert.IsNotType<TimeoutException>(exception);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1));
    }
}

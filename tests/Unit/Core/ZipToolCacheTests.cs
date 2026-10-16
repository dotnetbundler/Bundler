using System.IO.Compression;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

/// <summary>
/// ZipToolCache：zip 形态内嵌工具的物化契约——整包 sha 校验、条目归一化、
/// 目录项跳过、链接/越界/重名/清单名拒收、required 件存在性门禁。
/// </summary>
public sealed class ZipToolCacheTests : TempBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WriteZip(string fileName, params (string Entry, byte[] Content)[] files) =>
        WriteZipRaw(fileName, zip =>
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                if (name.EndsWith('/') || name.EndsWith('\\'))
                {
                    continue; // 目录项无内容
                }
                using var stream = entry.Open();
                stream.Write(content);
            }
        });

    private string WriteZipRaw(string fileName, Action<ZipArchive> fill)
    {
        var path = TempPath(fileName);
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            fill(zip);
        }
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static ZipToolArchive Archive(
        string zipPath, string executable = "bin/tool", params string[] required) =>
        new("tool", "1.0", Hashes.Sha256HexFile(zipPath), executable,
            required.Length == 0 ? null : required);

    [Fact]
    public async Task ResolveToolAsync_MaterializesEntries_AndWritesManifest()
    {
        var zip = WriteZip("tool.zip",
            ("bin/tool", "exe"u8.ToArray()),
            ("etc/cfg", "cfg"u8.ToArray()),
            ("docs/", []));

        var resolved = await ZipToolCache.ResolveToolAsync(
            zip, Temp("cache").FullName, Archive(zip), Ct);

        // 内容寻址目录名 = {name}-{version}-{archiveSha12}
        var sha12 = Hashes.Sha256HexFile(zip).Substring(0, 12);
        Assert.Equal($"tool-1.0-{sha12}", Path.GetFileName(resolved.DirectoryPath));
        Assert.Equal(
            ToolCache.ResolveToolPath(resolved.DirectoryPath, "bin/tool"),
            resolved.ExecutablePath);
        Assert.Equal("exe", File.ReadAllText(resolved.ExecutablePath));
        Assert.Equal("cfg",
            File.ReadAllText(Path.Combine(resolved.DirectoryPath, "etc", "cfg")));
        // 清单契约：物化结果带 ToolCache 清单，缓存命中靠它复验。
        Assert.True(File.Exists(
            Path.Combine(resolved.DirectoryPath, ToolCache.ManifestFileName)));
        // 目录项只表结构不落文件——物化目录里不应出现名为 docs 的项。
        Assert.False(File.Exists(Path.Combine(resolved.DirectoryPath, "docs")));
        Assert.False(Directory.Exists(Path.Combine(resolved.DirectoryPath, "docs")));
    }

    [Fact]
    public async Task ResolveAsync_BackslashEntries_NormalizedToSlashes()
    {
        var zip = WriteZip("tool.zip", ("bin\\tool", "exe"u8.ToArray()));

        var executable = await ZipToolCache.ResolveAsync(
            zip, Temp("cache").FullName, Archive(zip), Ct);

        Assert.True(File.Exists(executable));
        Assert.EndsWith(Path.Combine("bin", "tool"), executable);
    }

    [Theory]
    [InlineData("bin\\tool.exe", "bin/tool.exe")]
    [InlineData("a/b/", "a/b")]
    [InlineData("a\\b\\c", "a/b/c")]
    public void NormalizeArchiveEntry_NormalizesSeparators(string entry, string expected) =>
        Assert.Equal(expected, ZipToolCache.NormalizeArchiveEntry(entry));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/abs/path")]
    [InlineData("\\abs")]
    [InlineData("C:/rooted")]
    [InlineData("C:\\rooted")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    public void NormalizeArchiveEntry_RejectsIllegalPaths(string entry) =>
        Assert.Throws<InvalidDataException>(() => ZipToolCache.NormalizeArchiveEntry(entry));

    [Fact]
    public async Task ResolveAsync_EscapingEntry_Rejected()
    {
        var zip = WriteZip("evil.zip", ("../evil.txt", "x"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, Archive(zip), Ct));
    }

    [Fact]
    public async Task ResolveAsync_ManifestNameEntry_Rejected()
    {
        // 归档里塞清单保留名会伪造缓存合法性——必须拒。
        var zip = WriteZipRaw("manifest.zip",
            z => AddEntry(z, ToolCache.ManifestFileName, "x"u8.ToArray()));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, Archive(zip), Ct));
        Assert.Contains("reserved", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateEntryPaths_Rejected()
    {
        var zip = WriteZipRaw("dup.zip", zip =>
        {
            AddEntry(zip, "a.txt", "1"u8.ToArray());
            AddEntry(zip, "a.txt", "2"u8.ToArray());
        });
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, Archive(zip, "a.txt"), Ct));
        Assert.Contains("duplicate", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_CaseVariantDuplicatePaths_HostSemantics()
    {
        // PathComparer 随宿主 FS：Windows 大小写不敏感 → 仅大小写之差即重名；
        // POSIX 敏感 → 两件并存合法。
        var zip = WriteZipRaw("dup.zip", zip =>
        {
            AddEntry(zip, "a.txt", "1"u8.ToArray());
            AddEntry(zip, "A.TXT", "2"u8.ToArray());
        });
        var archive = Archive(zip, "a.txt");
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, archive, Ct));
        }
        else
        {
            var resolved = await ZipToolCache.ResolveToolAsync(
                zip, Temp("cache").FullName, archive, Ct);
            Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, "a.txt")));
            Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, "A.TXT")));
        }
    }

    [Fact]
    public async Task ResolveAsync_LinkEntry_Rejected()
    {
        // unix  symlink（S_IFLNK）条目会越界落盘——工具归档一律禁链接。
        var zip = WriteZipRaw("link.zip", zip =>
        {
            var entry = zip.CreateEntry("linked");
            entry.ExternalAttributes = (0xA000 << 16) | 0x1FF;
            using var stream = entry.Open();
            stream.Write("target"u8.ToArray());
        });
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, Archive(zip, "linked"), Ct));
        Assert.Contains("links", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_ChecksumMismatch_Rejected()
    {
        var zip = WriteZip("tool.zip", ("bin/tool", "exe"u8.ToArray()));
        var tampered = new ZipToolArchive("tool", "1.0", new string('0', 64), "bin/tool");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, tampered, Ct));
        Assert.Contains("checksum", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_MissingArchiveFile_Throws()
    {
        var archive = new ZipToolArchive("tool", "1.0", new string('a', 64), "bin/tool");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ZipToolCache.ResolveAsync(TempPath("missing.zip"), Temp("cache").FullName, archive, Ct));
    }

    [Fact]
    public async Task ResolveAsync_ExecutableMissingFromArchive_Rejected()
    {
        var zip = WriteZip("tool.zip", ("bin/other", "x"u8.ToArray()));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveAsync(zip, Temp("cache").FullName, Archive(zip, "bin/tool"), Ct));
        Assert.Contains("bin/tool", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_RequiredRelativePaths_Enforced()
    {
        var zip = WriteZip("tool.zip",
            ("bin/tool", "x"u8.ToArray()), ("lib/a", "y"u8.ToArray()));
        var sha = Hashes.Sha256HexFile(zip);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipToolCache.ResolveToolAsync(zip, Temp("cache-miss").FullName,
                new ZipToolArchive("tool", "1.0", sha, "bin/tool", ["lib/missing"]), Ct));
        Assert.Contains("lib/missing", exception.Message);

        var resolved = await ZipToolCache.ResolveToolAsync(zip, Temp("cache-ok").FullName,
            new ZipToolArchive("tool", "1.0", sha, "bin/tool", ["lib/a"]), Ct);
        Assert.True(File.Exists(Path.Combine(resolved.DirectoryPath, "lib", "a")));
    }

    [Fact]
    public async Task ResolveAsync_InvalidSegments_Rejected()
    {
        var zip = WriteZip("tool.zip", ("bin/tool", "x"u8.ToArray()));
        var sha = Hashes.Sha256HexFile(zip);
        var cache = Temp("cache").FullName;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ZipToolCache.ResolveAsync(zip, cache,
                new ZipToolArchive("..", "1.0", sha, "bin/tool"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ZipToolCache.ResolveAsync(zip, cache,
                new ZipToolArchive("tool", "1.0", "not-hex", "bin/tool"), Ct));
    }
}

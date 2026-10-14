using System.IO.Compression;
using System.Text;
using DotNet.Bundler.Core;
using DotNet.Bundler.Updater;

namespace Bundler.UnitTests.Updater;

/// <summary>
/// <see cref="ArchiveExtractor"/> 的双格式提取面：managed 回退路径（本类全程零子进程，
/// ditto/bsdtar 只在 macOS 被尝试且失败时安全回退）、逃逸链接预扫拒收。
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ax-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ExtractZip_ManagedFallback_LayoutsFiles()
    {
        var zip = Path.Combine(_root, "payload.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("app/");
            var entry = archive.CreateEntry("app/data.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("payload-bytes");
        }
        var staging = Path.Combine(_root, "staging");

        var result = ArchiveExtractor.ExtractZipToDirectory(zip, staging, _ => { });

        Assert.Equal(staging, result);
        Assert.Equal("payload-bytes",
            File.ReadAllText(Path.Combine(staging, "app", "data.txt")));
    }

    [Fact]
    public void ExtractTarGz_ManagedFallback_LayoutsFiles()
    {
        var tarGz = Path.Combine(_root, "payload.tar.gz");
        Directory.CreateDirectory(_root);
        using (var output = File.Create(tarGz))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            // 0644 文件：提取路径不带执行位，不触 /bin/chmod 子进程。
            TarWriter.Write(gzip,
            [
                new TarEntry { Name = "pkg/", Kind = TarEntryKind.Directory, Mode = 493 },
                new TarEntry { Name = "pkg/blob.bin", Kind = TarEntryKind.File, Mode = 420, Content = "bz"u8.ToArray() },
            ]);
        }
        var staging = Path.Combine(_root, "staging2");

        ArchiveExtractor.ExtractTarGzToDirectory(tarGz, staging, _ => { });

        Assert.Equal("bz"u8.ToArray(),
            File.ReadAllBytes(Path.Combine(staging, "pkg", "blob.bin")));
    }

    [Fact]
    public void ExtractZip_EntryEscapingRoot_RejectedOrDropped()
    {
        var zip = Path.Combine(_root, "evil.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../escape.txt");
        }
        var staging = Path.Combine(_root, "staging3");

        // managed ExtractToDirectory 自身拒越界条目；清扫面兜底删逃逸链——
        // 两种防线都不让越根内容落盘，断言共同点：staging 之外无文件落地。
        try
        {
            ArchiveExtractor.ExtractZipToDirectory(zip, staging, _ => { });
        }
        catch (Exception)
        {
            // 拒绝同样满足"内容不落出 staging"。
        }
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }
}

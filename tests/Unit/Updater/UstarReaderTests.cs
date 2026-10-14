using System.Text;
using DotNet.Bundler.Core;
using DotNet.Bundler.Updater;

namespace Bundler.UnitTests.Updater;

/// <summary>
/// <see cref="UstarReader"/> 提取防线：常规文件/目录落地、路径逃逸拒绝、
/// 符号链接围界判定与预扫描。
/// </summary>
public sealed class UstarReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ustar-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream TarOf(params TarEntry[] entries)
    {
        var stream = new MemoryStream();
        TarWriter.Write(stream, entries);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Extract_FileAndDirectory_LayoutsContent()
    {
        var destination = Path.Combine(_root, "out");
        // 文件执行位会触发 TryChmod 的 /bin/chmod 子进程——单元层只用 0644 文件，
        // 可执行位保留与 chmod 线归集成层。
        using var tar = TarOf(
            new TarEntry { Name = "app/", Kind = TarEntryKind.Directory, Mode = 493 },
            new TarEntry { Name = "app/run", Kind = TarEntryKind.File, Mode = 420, Content = "run"u8.ToArray() },
            new TarEntry { Name = "app/data.txt", Kind = TarEntryKind.File, Mode = 420, Content = "d"u8.ToArray() });

        UstarReader.Extract(tar, destination);

        Assert.Equal("run"u8.ToArray(), File.ReadAllBytes(Path.Combine(destination, "app", "run")));
        Assert.True(File.Exists(Path.Combine(destination, "app", "data.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("/abs/escape.txt")]
    public void Extract_EscapingEntryName_Rejected(string name)
    {
        var destination = Path.Combine(_root, "esc-" + name.GetHashCode(StringComparison.Ordinal).ToString("x8"));
        Directory.CreateDirectory(destination);
        using var tar = TarOf(new TarEntry { Name = name, Kind = TarEntryKind.File, Content = "x"u8.ToArray() });
        Assert.Throws<UpdateException>(() => UstarReader.Extract(tar, destination));
    }

    [Fact]
    public void LinkEscapesRoot_PathRelations()
    {
        var root = Path.Combine(_root, "stage");
        var linkAt = Path.Combine(root, "sub", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(linkAt)!);

        Assert.False(UstarReader.LinkEscapesRoot("target.txt", linkAt, root));
        Assert.False(UstarReader.LinkEscapesRoot("../target.txt", linkAt, root));
        Assert.True(UstarReader.LinkEscapesRoot("../../escape.txt", linkAt, root));
        Assert.True(UstarReader.LinkEscapesRoot(
            Path.Combine(_root, "outside.txt"), linkAt, root));
    }

    [Fact]
    public void ContainsEscapingLink_SafeArchive_False()
    {
        var root = Path.Combine(_root, "safe");
        Directory.CreateDirectory(root);
        using var tar = TarOf(
            new TarEntry { Name = "app/", Kind = TarEntryKind.Directory, Mode = 493 },
            new TarEntry { Name = "app/link", Kind = TarEntryKind.Symlink, LinkTarget = "real.txt" });
        Assert.False(UstarReader.ContainsEscapingLink(tar, root));
    }

    [Fact]
    public void ContainsEscapingLink_EscapingTarget_True()
    {
        var root = Path.Combine(_root, "unsafe");
        Directory.CreateDirectory(root);
        using var tar = TarOf(
            new TarEntry { Name = "app/link", Kind = TarEntryKind.Symlink, LinkTarget = "../../../../etc/passwd" });
        Assert.True(UstarReader.ContainsEscapingLink(tar, root));
    }

    [Fact]
    public void Extract_SymlinkInsideRoot_Restored()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows 开发机通常无符号链接特权——CreateSymlink 内 ln 不可用即跳过。
            return;
        }
        var destination = Path.Combine(_root, "linked");
        using var tar = TarOf(
            new TarEntry { Name = "real.txt", Kind = TarEntryKind.File, Content = "c"u8.ToArray() },
            new TarEntry { Name = "link", Kind = TarEntryKind.Symlink, LinkTarget = "real.txt" });

        UstarReader.Extract(tar, destination);

        var link = Path.Combine(destination, "link");
        Assert.Equal("real.txt", new FileInfo(link).LinkTarget);
    }
}

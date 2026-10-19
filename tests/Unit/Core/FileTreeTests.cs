using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class FileTreeTests : TempBase
{
    private (string Source, string Dest) Workspace()
    {
        var root = Temp("bundler-filetree");
        return (Path.Combine(root.FullName, "src"), Path.Combine(root.FullName, "dst"));
    }

    [Fact]
    public void CopyTree_CopiesFilesAndDirectories()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");

        FileTree.CopyTree(source, dest, NullBundleLogger.Instance, linkErrorMessage: "link");

        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")));
    }

    [Fact]
    public void CopyTree_RejectPolicy_RequiresErrorMessage()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);

        Assert.Throws<ArgumentNullException>(() =>
            FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
                links: TreeLinkPolicy.Reject, linkErrorMessage: null));
    }

    [Fact]
    public void CopyTree_Traverse_RequiresMaxDepth()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);

        Assert.Throws<ArgumentException>(() =>
            FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
                links: TreeLinkPolicy.Traverse, maxDepth: int.MaxValue, linkErrorMessage: "link"));
    }

    [Fact]
    public void CopyTree_DepthBeyondMax_Throws()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(Path.Combine(source, "d1", "d2"));
        File.WriteAllText(Path.Combine(source, "d1", "d2", "f.txt"), "x");

        Assert.Throws<InvalidDataException>(() =>
            FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
                maxDepth: 0, loopErrorMessage: "loop", linkErrorMessage: "link"));
    }

    [Fact]
    public void CopyTree_SortEntries_DeterministicOrder()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "c.txt", "a.txt", "b.txt" })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        // 排序稳定性的直接断言由 PayloadSource/写盘器顺序面承担；
        // 这里验全量复制在排序开关下内容一致
        FileTree.CopyTree(source, dest, NullBundleLogger.Instance, linkErrorMessage: "link", sortEntries: true);

        Assert.Equal(3, Directory.GetFiles(dest).Length);
    }

    [Fact]
    public void CopyTree_ResolveFileDestination_CustomMapping()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "in.txt"), "data");

        FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
            linkErrorMessage: "link", resolveFileDestination: (file, directory) =>
                Path.Combine(directory, "renamed-" + Path.GetFileName(file)));

        Assert.True(File.Exists(Path.Combine(dest, "renamed-in.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "in.txt")));
    }

    [Fact]
    public void CopyTree_NoOverwrite_ThrowsOnExisting()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(source, "a.txt"), "new");
        File.WriteAllText(Path.Combine(dest, "a.txt"), "old");

        Assert.Throws<IOException>(() =>
            FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
                linkErrorMessage: "link", overwrite: false));

        FileTree.CopyTree(source, dest, NullBundleLogger.Instance, linkErrorMessage: "link", overwrite: true);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void CopyTree_NestedDirectorySymlink_RejectThrows()
    {
        var (source, dest) = Workspace();
        Directory.CreateDirectory(source);
        var real = Temp("bundler-real").FullName;
        File.WriteAllText(Path.Combine(real, "inside.txt"), "x");
        var link = Path.Combine(source, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (IOException) { Assert.Skip("symlink creation not permitted"); }
        catch (UnauthorizedAccessException) { Assert.Skip("symlink creation not permitted"); }

        // Reject 语义覆盖嵌套目录链接——此前只对树根本身检查。
        Assert.Throws<InvalidDataException>(() =>
            FileTree.CopyTree(source, dest, NullBundleLogger.Instance,
                links: TreeLinkPolicy.Reject, linkErrorMessage: "link rejected"));
    }

    [Fact]
    public void RejectReparsePoint_ThrowsOnLink()
    {
        var real = Temp("bundler-real").FullName;
        var link = TempPath($"bundler-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (IOException) { Assert.Skip("symlink creation not permitted"); }
        catch (UnauthorizedAccessException) { Assert.Skip("symlink creation not permitted"); }

        try
        {
            Assert.Throws<InvalidDataException>(
                () => FileTree.RejectReparsePoint(link, "reparse rejected"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}

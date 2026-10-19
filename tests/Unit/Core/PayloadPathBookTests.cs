using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

/// <summary>
/// 共享载荷路径登记簿：叶落位自动认领父目录链、文件/目录双向碰撞、
/// 共享系统目录边界与新增目录回调顺序。
/// </summary>
public sealed class PayloadPathBookTests
{
    [Fact]
    public void Leaf_ClaimsParentChain()
    {
        var book = new PayloadPathBook();
        book.ClaimFile("usr/lib/pkg/app");
        Assert.Equal(
            new[] { "usr", "usr/lib", "usr/lib/pkg" },
            book.Directories.OrderBy(d => d, StringComparer.Ordinal));
    }

    [Fact]
    public void Leaf_DoubleClaim_Throws()
    {
        var book = new PayloadPathBook();
        book.ClaimFile("usr/lib/pkg/app");
        var ex = Assert.Throws<InvalidOperationException>(() => book.ClaimFile("usr/lib/pkg/app"));
        Assert.Contains("claimed twice", ex.Message);
    }

    [Fact]
    public void Leaf_OverOwnedDirectory_Throws()
    {
        var book = new PayloadPathBook();
        book.ClaimDirectory("usr/lib/pkg");
        var ex = Assert.Throws<InvalidOperationException>(() => book.ClaimFile("usr/lib/pkg"));
        Assert.Contains("occupied by a directory", ex.Message);
    }

    [Fact]
    public void Leaf_UnderLeaf_Throws()
    {
        var book = new PayloadPathBook();
        book.ClaimFile("usr/lib/pkg");
        var ex = Assert.Throws<InvalidOperationException>(() => book.ClaimFile("usr/lib/pkg/x"));
        Assert.Contains("occupied by a file", ex.Message);
    }

    [Fact]
    public void Directory_OverLeaf_Throws()
    {
        var book = new PayloadPathBook();
        book.ClaimFile("usr/lib/pkg");
        var ex = Assert.Throws<InvalidOperationException>(() => book.ClaimDirectory("usr/lib/pkg"));
        Assert.Contains("occupied by a file", ex.Message);
    }

    [Fact]
    public void SharedBoundary_PrefixNotOwned()
    {
        var book = new PayloadPathBook(
            sharedBoundary: p => p == "usr/bin",
            systemLeaf: p => p == "usr/bin");
        book.ClaimFile("usr/bin/tool");
        Assert.DoesNotContain("usr/bin", book.Directories);
    }

    [Fact]
    public void Leaf_OnSystemDirectory_Rejected()
    {
        var book = new PayloadPathBook(
            sharedBoundary: p => p == "usr/bin",
            systemLeaf: p => p == "usr/bin");
        var ex = Assert.Throws<InvalidOperationException>(() => book.ClaimFile("usr/bin"));
        Assert.Contains("occupied by a directory", ex.Message);
    }

    [Fact]
    public void FileUnderSharedTree_Allowed()
    {
        // 共享边界只拦目录认领——共享树下的普通文件是合法载荷成员。
        var book = new PayloadPathBook(
            sharedBoundary: p => p == "usr/bin" || p.StartsWith("usr/share/icons/"));
        book.ClaimFile("usr/share/icons/hicolor/48x48/apps/app.png");
        Assert.DoesNotContain("usr/share/icons", book.Directories);
    }

    [Fact]
    public void OnNewDirectory_ParentsBeforeChildren()
    {
        var emitted = new List<string>();
        var book = new PayloadPathBook(onNewDirectory: emitted.Add);
        book.ClaimFile("a/b/c/f");
        Assert.Equal(new[] { "a", "a/b", "a/b/c" }, emitted);
    }
}

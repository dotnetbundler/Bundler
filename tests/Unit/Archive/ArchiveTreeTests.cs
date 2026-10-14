using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Archive;

/// <summary>
/// <see cref="ArchiveTree"/> 收集面：载荷树→归档条目（相对路径/模式/序），
/// BundlerArchiveFile 目的地校验与撞名拒收，stem 前缀包装与条目转写。
/// </summary>
public sealed class ArchiveTreeTests : TempBase
{

    public override void Dispose()
    {
        base.Dispose();
    }

    private static ArchiveBundleConfiguration Settings(
        IReadOnlyList<ArchiveFileEntry>? files = null) =>
        new() { Files = files ?? [] };

    private BundlePlanItem Item(string input)
    {
        File.WriteAllText(Path.Combine(input, "app"), "x");
        return new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Zip,
            input, "app", Root, false);
    }

    private static BundleConfiguration Bundle() => new()
    {
        ProductName = "Sample App",
        Identifier = "com.example.sample",
        Version = "1.0.0",
    };

    [Fact]
    public void Build_PayloadTree_RelativePathsSorted()
    {
        var input = Path.Combine(Root, "in");
        Directory.CreateDirectory(Path.Combine(input, "sub"));
        File.WriteAllText(Path.Combine(input, "b.txt"), "b");
        File.WriteAllText(Path.Combine(input, "a.txt"), "a");
        File.WriteAllText(Path.Combine(input, "sub", "c.txt"), "c");

        var entries = ArchiveTree.Build(Bundle(), Item(input), Settings(), Root,
            NullBundleLogger.Instance);

        var paths = entries.Select(e => e.ArchivePath).ToArray();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), paths);
        Assert.Contains("a.txt", paths);
        Assert.Contains("sub", paths);
        Assert.Contains("sub/c.txt", paths);
    }

    [Fact]
    public void Build_ExtraFile_MappedAtDestination()
    {
        var input = Path.Combine(Root, "in2");
        Directory.CreateDirectory(input);
        var extra = Path.Combine(Root, "license.txt");
        File.WriteAllText(extra, "L");
        var settings = Settings(
        [
            new ArchiveFileEntry { Source = extra, Destination = "docs/license.txt" }
        ]);

        var entries = ArchiveTree.Build(Bundle(), Item(input), settings, Root,
            NullBundleLogger.Instance);

        var mapped = entries.Single(e => e.ArchivePath == "docs/license.txt");
        Assert.Equal(TarEntryKind.File, mapped.Kind);
        Assert.Equal(Path.GetFullPath(extra), mapped.SourcePath);
    }

    [Theory]
    [InlineData("/abs/x")]
    [InlineData("../escape")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("back\\slash")]
    [InlineData("")]
    public void Build_ExtraFile_UnsafeDestination_Rejected(string destination)
    {
        var input = Path.Combine(Root, "in3-" + destination.Length);
        Directory.CreateDirectory(input);
        var extra = Path.Combine(Root, "x.txt");
        File.WriteAllText(extra, "x");
        var settings = Settings(
        [
            new ArchiveFileEntry { Source = extra, Destination = destination }
        ]);

        Assert.Throws<ArgumentException>(() => ArchiveTree.Build(
            Bundle(), Item(input), settings, Root, NullBundleLogger.Instance));
    }

    [Fact]
    public void Build_ExtraFile_CollidesWithPayload_Rejected()
    {
        var input = Path.Combine(Root, "in4");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "app"), "x");
        var extra = Path.Combine(Root, "dup.txt");
        File.WriteAllText(extra, "x");
        var settings = Settings(
        [
            new ArchiveFileEntry { Source = extra, Destination = "app" }
        ]);

        Assert.Throws<ArgumentException>(() => ArchiveTree.Build(
            Bundle(), Item(input), settings, Root, NullBundleLogger.Instance));
    }

    [Fact]
    public void Build_ExtraFile_MissingSource_Rejected()
    {
        var input = Path.Combine(Root, "in5");
        Directory.CreateDirectory(input);
        var settings = Settings(
        [
            new ArchiveFileEntry
            {
                Source = Path.Combine(Root, "ghost"), Destination = "g.txt"
            }
        ]);

        Assert.Throws<ArgumentException>(() => ArchiveTree.Build(
            Bundle(), Item(input), settings, Root, NullBundleLogger.Instance));
    }

    [Fact]
    public void CollectDirectory_StemsAllEntries()
    {
        var source = Path.Combine(Root, "App.app");
        Directory.CreateDirectory(Path.Combine(source, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(source, "Contents", "MacOS", "run"), "x");

        var entries = ArchiveTree.CollectDirectory(source, "App.app", NullBundleLogger.Instance);

        Assert.Equal("App.app", entries[0].ArchivePath);
        Assert.Equal(TarEntryKind.Directory, entries[0].Kind);
        Assert.Contains(entries, e => e.ArchivePath == "App.app/Contents/MacOS/run");
    }

    [Fact]
    public void UnderStem_PrependsStemDirectory()
    {
        var input = Path.Combine(Root, "in6");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "app"), "x");
        var flat = ArchiveTree.Build(Bundle(), Item(input), Settings(), Root,
            NullBundleLogger.Instance);

        var stemmed = ArchiveTree.UnderStem(flat, "sample-1.0.0-linux-x86_64").ToArray();

        Assert.Equal("sample-1.0.0-linux-x86_64", stemmed[0].ArchivePath);
        Assert.Equal(TarEntryKind.Directory, stemmed[0].Kind);
        Assert.Contains(stemmed, e => e.ArchivePath == "sample-1.0.0-linux-x86_64/app");
    }

    [Fact]
    public void ToTarEntry_DirectoryGetsTrailingSlash()
    {
        var input = Path.Combine(Root, "in7");
        Directory.CreateDirectory(Path.Combine(input, "d"));
        var flat = ArchiveTree.Build(Bundle(), Item(input), Settings(), Root,
            NullBundleLogger.Instance);

        var dir = flat.Single(e => e.ArchivePath == "d");
        Assert.Equal("d/", ArchiveTree.ToTarEntry(dir).Name);
        var file = flat.Single(e => e.ArchivePath == "app");
        Assert.Equal("app", ArchiveTree.ToTarEntry(file).Name);
        Assert.NotNull(ArchiveTree.ToTarEntry(file).OpenContent); // 文件件走流式装载
    }

    // ---- ArchiveIdentity ----

    [Fact]
    public void Identity_PackageName_KebabFallbackAndKnob()
    {
        Assert.Equal("sample-app", ArchiveIdentity.PackageName(Bundle(), Settings()));
        Assert.Equal("custom",
            ArchiveIdentity.PackageName(Bundle(), new ArchiveBundleConfiguration { PackageName = "custom" }));
        Assert.Throws<ArgumentException>(() => ArchiveIdentity.PackageName(
            Bundle(), new ArchiveBundleConfiguration { PackageName = "bad name" }));
    }

    [Fact]
    public void Identity_ArchiveStem_NameVersionOsArch()
    {
        var input = Path.Combine(Root, "in8");
        Directory.CreateDirectory(input);
        var item = Item(input);
        var stem = ArchiveIdentity.ArchiveStem(Settings(), "sample-app", "1.0.0", item);
        Assert.Equal("sample-app-1.0.0-linux-x86_64", stem);
        var named = ArchiveIdentity.ArchiveStem(
            new ArchiveBundleConfiguration { ArchiveName = "release.tar" },
            "sample-app", "1.0.0", item);
        Assert.Equal("release.tar", named);
        Assert.Throws<ArgumentException>(() => ArchiveIdentity.ArchiveStem(
            new ArchiveBundleConfiguration { ArchiveName = "a/b" }, "p", "1", item));
    }
}

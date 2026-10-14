using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class PayloadSourceTests
{
    private sealed class RecordingLogger : IBundleLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(BundleLogLevel level, string message) => Messages.Add(message);
    }

    [Fact]
    public void CollectInput_DirectoriesThenFiles_Ordered()
    {
        var input = Directory.CreateTempSubdirectory("bundler-payload");
        Directory.CreateDirectory(Path.Combine(input.FullName, "zdir"));
        Directory.CreateDirectory(Path.Combine(input.FullName, "adir"));
        File.WriteAllText(Path.Combine(input.FullName, "zdir", "z.txt"), "z");
        File.WriteAllText(Path.Combine(input.FullName, "adir", "a.txt"), "a");
        File.WriteAllText(Path.Combine(input.FullName, "app"), "main");

        var items = PayloadSource.CollectInput(input.FullName, "/opt/app", "app",
            NullBundleLogger.Instance).ToArray();

        // 目录项在前、文件项在后，各自字典序
        var dirEntries = items.Where(i => i.Kind == PayloadSourceKind.Directory).ToArray();
        var fileEntries = items.Where(i => i.Kind == PayloadSourceKind.File).ToArray();
        Assert.Equal(2, dirEntries.Length);
        Assert.EndsWith("/adir", dirEntries[0].ArchivePath);
        Assert.EndsWith("/zdir", dirEntries[1].ArchivePath);
        Assert.Equal(3, fileEntries.Length);
        Assert.Equal("/opt/app/adir/a.txt", fileEntries[0].ArchivePath);
    }

    [Fact]
    public void CollectInput_MainExecutable_Gets0755()
    {
        var input = Directory.CreateTempSubdirectory("bundler-payload");
        File.WriteAllText(Path.Combine(input.FullName, "app"), "main");
        File.WriteAllText(Path.Combine(input.FullName, "data.txt"), "d");

        var items = PayloadSource.CollectInput(input.FullName, "/opt/app", "app",
            NullBundleLogger.Instance).ToArray();

        var main = items.Single(i => i.ArchivePath == "/opt/app/app");
        var data = items.Single(i => i.ArchivePath == "/opt/app/data.txt");
        Assert.Equal(493 /* 0755 */, main.Mode);
        Assert.Equal(420 /* 0644 */, data.Mode);
    }

    [Fact]
    public void CollectResources_EscapesRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectResources(
                [new BundleResourceConfiguration { Source = "x", Destination = "../out" }],
                "/opt/app", NullBundleLogger.Instance).ToList());
        Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectResources(
                [new BundleResourceConfiguration { Source = "x", Destination = "/" }],
                "/opt/app", NullBundleLogger.Instance).ToList());
    }

    [Fact]
    public void CollectResources_FileSource_MapsDirect()
    {
        var source = Path.Combine(Path.GetTempPath(), $"res-{Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "r");
        var items = PayloadSource.CollectResources(
            [new BundleResourceConfiguration { Source = source, Destination = "share/doc.txt" }],
            "/opt/app", NullBundleLogger.Instance).ToArray();

        Assert.Single(items);
        Assert.Equal("/opt/app/share/doc.txt", items[0].ArchivePath);
        Assert.Equal(source, items[0].SourcePath);
    }

    [Fact]
    public void CollectResources_DirectorySource_MapsTree()
    {
        var root = Directory.CreateTempSubdirectory("bundler-res");
        Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
        File.WriteAllText(Path.Combine(root.FullName, "sub", "f.txt"), "f");

        var items = PayloadSource.CollectResources(
            [new BundleResourceConfiguration { Source = root.FullName, Destination = "share" }],
            "/opt/app", NullBundleLogger.Instance, includeDirectories: true).ToArray();

        Assert.Contains(items, i => i.Kind == PayloadSourceKind.Directory &&
            i.ArchivePath == "/opt/app/share/sub");
        Assert.Contains(items, i => i.ArchivePath == "/opt/app/share/sub/f.txt");
    }

    [Fact]
    public void CollectResources_MissingSource_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            PayloadSource.CollectResources(
                [new BundleResourceConfiguration
                {
                    Source = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}"),
                    Destination = "x"
                }],
                "/opt/app", NullBundleLogger.Instance).ToList());
    }
}

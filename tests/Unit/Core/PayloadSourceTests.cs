using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class PayloadSourceTests : TempBase
{
    private sealed class RecordingLogger : IBundleLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(BundleLogLevel level, string message) => Messages.Add(message);
    }

    [Fact]
    public void CollectInput_DirectoriesThenFiles_Ordered()
    {
        var input = Temp("bundler-payload");
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
        var input = Temp("bundler-payload");
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
    public void CollectResources_DotAndEmptySegments_Normalized()
    {
        // '.'/空段折叠进归档路径——与 ResolveArchiveTarget 归一化同口径，
        // 字面段不再混入出件名（覆盖核查/碰撞判定口径对齐）。
        var source = TempPath($"res-{Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "r");
        var items = PayloadSource.CollectResources(
            [new BundleResourceConfiguration { Source = source, Destination = "a/./b//doc.txt" }],
            "/opt/app", NullBundleLogger.Instance).ToArray();
        Assert.Equal("/opt/app/a/b/doc.txt", items[0].ArchivePath);
    }

    [Fact]
    public void CollectResources_FileSource_MapsDirect()
    {
        var source = TempPath($"res-{Guid.NewGuid():N}.txt");
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
        var root = Temp("bundler-res");
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
                    Source = TempPath($"nope-{Guid.NewGuid():N}"),
                    Destination = "x"
                }],
                "/opt/app", NullBundleLogger.Instance).ToList());
    }

    // ---- 链接契约：树内保留 / 树外拒绝 / 悬空拒绝 ----

    private static bool TryCreateLink(string path, string target, bool directory = false)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
            return true;
        }
        catch
        {
            // 文件系统/权限不支持建链——显式 Skip 而不是当断言通过。
            return false;
        }
    }

    [Fact]
    public void CollectInput_InTreeFileLink_Preserved()
    {
        var input = Temp("bundler-payload");
        File.WriteAllText(Path.Combine(input.FullName, "real.so"), "lib");
        var link = Path.Combine(input.FullName, "link.so");
        if (!TryCreateLink(link, "real.so"))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var items = PayloadSource.CollectInput(input.FullName, "/opt/app", "real.so",
            NullBundleLogger.Instance).ToArray();

        var item = items.Single(i => i.ArchivePath == "/opt/app/link.so");
        Assert.Equal(PayloadSourceKind.Symlink, item.Kind);
        Assert.Equal("real.so", item.LinkTarget); // 目标重写为相对链接目录的形式
        Assert.Equal(511 /* 0777 */, item.Mode);
    }

    [Fact]
    public void CollectInput_LinkOutsideTree_Rejected()
    {
        var input = Temp("bundler-payload");
        var outside = TempPath($"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "o");
        var link = Path.Combine(input.FullName, "link.txt");
        if (!TryCreateLink(link, outside))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var ex = Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
                NullBundleLogger.Instance).ToList());
        Assert.Contains("outside the payload tree", ex.Message);
    }

    [Fact]
    public void CollectInput_DanglingLink_Rejected()
    {
        var input = Temp("bundler-payload");
        var link = Path.Combine(input.FullName, "link.txt");
        if (!TryCreateLink(link, "missing.txt"))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var ex = Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
                NullBundleLogger.Instance).ToList());
        Assert.Contains("dangling", ex.Message);
    }

    [Fact]
    public void CollectInput_DirLink_PreservedAsLeaf()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 宿主上目录链接统一拒绝（junction 歧义）");
        var input = Temp("bundler-payload");
        var real = Path.Combine(input.FullName, "realdir");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "f.txt"), "f");
        var link = Path.Combine(input.FullName, "dirlink");
        if (!TryCreateLink(link, "realdir", directory: true))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var items = PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
            NullBundleLogger.Instance).ToArray();

        var leaf = items.Single(i => i.ArchivePath == "/opt/app/dirlink");
        Assert.Equal(PayloadSourceKind.Symlink, leaf.Kind);
        Assert.Equal("realdir", leaf.LinkTarget);
        // 目录链接是叶项——不穿透递归、不物化内容。
        Assert.DoesNotContain(items, i => i.ArchivePath.StartsWith("/opt/app/dirlink/", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectInput_DirLink_RejectedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "junction 拒绝支路只在 Windows 宿主触发");
        var input = Temp("bundler-payload");
        var real = Path.Combine(input.FullName, "realdir");
        Directory.CreateDirectory(real);
        var link = Path.Combine(input.FullName, "dirlink");
        if (!TryCreateLink(link, real, directory: true))
        {
            Assert.Skip("The host cannot create directory links; leg unverifiable.");
        }

        Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
                NullBundleLogger.Instance).ToList());
    }

    [Fact]
    public void CollectInput_SelfDirectoryLink_TargetDot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 上指向目录的链接统一按目录链接拒绝");
        var input = Temp("bundler-payload");
        var link = Path.Combine(input.FullName, "self");
        if (!TryCreateLink(link, "."))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var items = PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
            NullBundleLogger.Instance).ToArray();

        // 指向自身所在目录的链接目标是 "."——绝不产空 LinkTarget。
        var item = items.Single(i => i.ArchivePath == "/opt/app/self");
        Assert.Equal(PayloadSourceKind.Symlink, item.Kind);
        Assert.Equal(".", item.LinkTarget);
    }

    [Fact]
    public void CollectResources_LinkToUnemittedDir_Rejected()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 宿主上目录链接统一拒绝——本腿验的是包内悬空");
        var root = Temp("bundler-res");
        Directory.CreateDirectory(Path.Combine(root.FullName, "emptydir"));
        var link = Path.Combine(root.FullName, "link");
        if (!TryCreateLink(link, "emptydir", directory: true))
        {
            Assert.Skip("The host cannot create directory links; link leg unverifiable.");
        }

        // includeDirectories=false：emptydir 无出件后代→包内无该目录→链接悬空
        var ex = Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectResources(
                [new BundleResourceConfiguration { Source = root.FullName, Destination = "res" }],
                "/opt/app", NullBundleLogger.Instance).ToList());
        Assert.Contains("dangle", ex.Message);
    }

    [Fact]
    public void CollectResources_LinkToPopulatedDir_Allowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 宿主上目录链接统一拒绝");
        var root = Temp("bundler-res");
        Directory.CreateDirectory(Path.Combine(root.FullName, "libs"));
        File.WriteAllText(Path.Combine(root.FullName, "libs", "real.so"), "lib");
        var link = Path.Combine(root.FullName, "link");
        if (!TryCreateLink(link, "libs", directory: true))
        {
            Assert.Skip("The host cannot create directory links; link leg unverifiable.");
        }

        // 目录虽不出件但 libs/real.so 叶件会隐式物化 libs/——链接包内可达
        var items = PayloadSource.CollectResources(
            [new BundleResourceConfiguration { Source = root.FullName, Destination = "res" }],
            "/opt/app", NullBundleLogger.Instance).ToArray();

        var leaf = items.Single(i => i.ArchivePath == "/opt/app/res/link");
        Assert.Equal("libs", leaf.LinkTarget);
    }

    [Fact]
    public void CollectResources_LinkDirFilledByOtherResource_Allowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 宿主上目录链接统一拒绝");
        var rootA = Temp("bundler-res-a");
        Directory.CreateDirectory(Path.Combine(rootA.FullName, "plugins"));
        var link = Path.Combine(rootA.FullName, "link");
        if (!TryCreateLink(link, "plugins", directory: true))
        {
            Assert.Skip("The host cannot create directory links; link leg unverifiable.");
        }
        var rootB = Temp("bundler-res-b");
        File.WriteAllText(Path.Combine(rootB.FullName, "ext.so"), "x");

        // link→plugins/ 在本资源内空，但另一资源把内容装进同一归档路径——
        // 包内可达，不得误拒。
        var items = PayloadSource.CollectResources(
            [
                new BundleResourceConfiguration { Source = rootA.FullName, Destination = "app" },
                new BundleResourceConfiguration { Source = rootB.FullName, Destination = "app/plugins" }
            ],
            "/opt", NullBundleLogger.Instance).ToArray();

        Assert.Contains(items, i => i.ArchivePath == "/opt/app/link");
        Assert.Contains(items, i => i.ArchivePath == "/opt/app/plugins/ext.so");
    }

    [Fact]
    public void CollectResources_LinkDirMaskedBySiblingName_Allowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Windows 宿主上目录链接统一拒绝");
        var root = Temp("bundler-res");
        Directory.CreateDirectory(Path.Combine(root.FullName, "plugins"));
        File.WriteAllText(Path.Combine(root.FullName, "plugins", "real.so"), "lib");
        // 'plugins.bak' 排在 ('plugins', 'plugins/') 序区间里会盖住 naive 二分插入点——
        // 真实后代 plugins/real.so 不得被它遮蔽
        File.WriteAllText(Path.Combine(root.FullName, "plugins.bak"), "b");
        var link = Path.Combine(root.FullName, "link");
        if (!TryCreateLink(link, "plugins", directory: true))
        {
            Assert.Skip("The host cannot create directory links; link leg unverifiable.");
        }

        var items = PayloadSource.CollectResources(
            [new BundleResourceConfiguration { Source = root.FullName, Destination = "res" }],
            "/opt/app", NullBundleLogger.Instance).ToArray();

        Assert.Contains(items, i => i.ArchivePath == "/opt/app/res/link");
        Assert.Contains(items, i => i.ArchivePath == "/opt/app/res/plugins/real.so");
    }

    [Fact]
    public void CollectInput_ExceedsDepthLimit_Rejected()
    {
        var input = Temp("bundler-deep");
        var current = input.FullName;
        // 65 层真实嵌套（深度界 64）——与 FileTree.CopyTree 同口径封顶
        for (var i = 0; i <= 65; i++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }

        Assert.Throws<InvalidDataException>(() =>
            PayloadSource.CollectInput(input.FullName, "/opt/app", "x",
                NullBundleLogger.Instance).ToList());
    }

    [Fact]
    public void CollectResources_LinkSource_Rejected()
    {
        var real = TempPath($"res-{Guid.NewGuid():N}.txt");
        File.WriteAllText(real, "r");
        var link = TempPath($"res-link-{Guid.NewGuid():N}.txt");
        if (!TryCreateLink(link, real))
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        Assert.Throws<ArgumentException>(() =>
            PayloadSource.CollectResources(
                [new BundleResourceConfiguration { Source = link, Destination = "x.txt" }],
                "/opt/app", NullBundleLogger.Instance).ToList());
    }
}

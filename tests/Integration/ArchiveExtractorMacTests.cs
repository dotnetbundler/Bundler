// ditto 抽取腿：macOS 上 ArchiveBundler 产 zip 与 ArchiveExtractor 解 zip/tar.gz
// 都拉起宿主 ditto/bsdtar 子进程（xattr+符号链接保真通道），Unit 层零子进程规则不收。
// 原 ArchiveTests 的 xattr 两腿 + UpdateTests 的三条 ArchiveExtractor 腿。
// __MACOSX/ AppleDouble 树清除与 S_IFLNK 还原断的是"解出后的文件系统状态"，
// 宿主分叉只体现在提取路径（ditto），断言面不分叉。
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Updater;

public sealed class ArchiveExtractorMacFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; } = IntegrationWorkspace.Create(
        "archive-extractor-mac-integration", "BundlerArchiveExtractorMacIntegration");

    public void Dispose() => Ws.Dispose();
}

public sealed class ArchiveExtractorMacTests : IClassFixture<ArchiveExtractorMacFixture>
{
    private readonly IntegrationWorkspace _ws;

    public ArchiveExtractorMacTests(ArchiveExtractorMacFixture fixture) => _ws = fixture.Ws;

    [Fact]
    public void Zip_XattrPayload_UsesHostToolPreservingMetadata()
    {
        // macOS 上载荷带 xattr（签名件）时写端走 ditto——AppleDouble 入 __MACOSX/，
        // managed 写出器永远不会产这些条目，可区分新旧行为。
        Assert.SkipUnless(TestPlatform.IsMacOS, "ditto production is macOS-only");
        var input = CreateInputDirectory(_ws.Combine("xattr-produce"));
        var output = _ws.Combine("xattr-produce", "xattr-out");

        var target = Path.Combine(input, "signed.sh");
        File.WriteAllText(target, "#!/bin/sh\n");
        SetXattr(target, "com.bundler.test", "v");
        var artifact = new ArchiveBundler().BuildAsync(
            Configuration(input, output, formats: [PackageFormat.Zip]))
            .GetAwaiter().GetResult().Single();
        using var archive = ZipFile.OpenRead(artifact.Path);
        Assert.Contains(archive.Entries.Select(e => e.FullName),
            name => name.StartsWith("__MACOSX/", StringComparison.Ordinal));
    }

    [Fact]
    public void Zip_XattrPayload_RoundTrips_ThroughUpdateExtraction()
    {
        // 全链路往返断言（FLAG-1）：ditto 产件 → ArchiveExtractor 解包（mac 走
        // ditto -x -k）→ xattr 仍挂在提取出的文件上——只靠产件侧断言会漏掉解包侧丢签。
        Assert.SkipUnless(TestPlatform.IsMacOS, "ditto round-trip is macOS-only");
        var input = CreateInputDirectory(_ws.Combine("xattr-roundtrip"));
        var output = _ws.Combine("xattr-roundtrip", "xattr-rt-out");
        var staging = _ws.Combine("xattr-roundtrip", "xattr-rt-staging");

        var target = Path.Combine(input, "signed.sh");
        File.WriteAllText(target, "#!/bin/sh\n");
        SetXattr(target, "com.bundler.test", "v");
        // 目录与符号链接自身也带 xattr——不只文件级（D6 断言面）。
        var sub = Path.Combine(input, "sub");
        Directory.CreateDirectory(sub);
        SetXattr(sub, "com.bundler.testdir", "d");
        var link = Path.Combine(input, "link.sh");
        File.CreateSymbolicLink(link, "signed.sh");
        SetXattr(link, "com.bundler.testlink", "l");

        var artifact = new ArchiveBundler().BuildAsync(
            Configuration(input, output, formats: [PackageFormat.Zip]))
            .GetAwaiter().GetResult().Single();
        Directory.CreateDirectory(staging);
        ArchiveExtractor.ExtractZipToDirectory(artifact.Path, staging, _ => { });
        // ditto 解出的目录里还会带 __MACOSX/——按文件名定位提取件最稳。
        var extracted = Directory
            .GetFiles(staging, "signed.sh", SearchOption.AllDirectories)
            .Single();
        var buffer = new byte[64];
        var size = getxattr(extracted, "com.bundler.test", buffer, buffer.Length, 0, 1);
        Assert.Equal(1, size.ToInt64());
        Assert.Equal((byte)'v', buffer[0]);
        AssertXattrRoundTrip(staging, "sub", "com.bundler.testdir", (byte)'d', buffer);
        // ditto -x 不还原软链自身的 xattr（工具语义上限，Sparkle 同款；
        // .app 签名不含链级 xattr）——zip 腿只断软链本体还原，xattr 断言归 targz 腿。
        var zipLink = Directory
            .GetFileSystemEntries(staging, "link.sh", SearchOption.AllDirectories)
            .Single();
        Assert.Equal(FileAttributes.ReparsePoint,
            File.GetAttributes(zipLink) & FileAttributes.ReparsePoint);

        // targz 同路往返：bsdtar 产件 → ExtractTarGzToDirectory（mac 走 tar -x）→ xattr 存活。
        var tarArtifact = new ArchiveBundler().BuildAsync(
            Configuration(input, output, formats: [PackageFormat.TarGz]))
            .GetAwaiter().GetResult().Single();
        var tarStaging = staging + "-tgz";
        Directory.CreateDirectory(tarStaging);
        ArchiveExtractor.ExtractTarGzToDirectory(tarArtifact.Path, tarStaging, _ => { });
        var tarExtracted = Directory
            .GetFiles(tarStaging, "signed.sh", SearchOption.AllDirectories)
            .Single();
        var tarSize = getxattr(tarExtracted, "com.bundler.test", buffer, buffer.Length, 0, 1);
        Assert.Equal(1, tarSize.ToInt64());
        Assert.Equal((byte)'v', buffer[0]);
        AssertXattrRoundTrip(tarStaging, "sub", "com.bundler.testdir", (byte)'d', buffer);
        AssertXattrRoundTrip(tarStaging, "link.sh", "com.bundler.testlink", (byte)'l', buffer);
    }

    [Fact]
    public void Applier_ManagedExtract_RemovesNestedAppleDouble()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS,
            "macOS 上 ExtractZipToDirectory 拉起 ditto 子进程——本腿断 ditto 路径的 __MACOSX 清场。");
        var directory = _ws.Combine("nested-appledouble");
        Directory.CreateDirectory(directory);

        // wrapper 内嵌套 __MACOSX（载荷含预解压 macOS 目录树）——managed 解包必须
        // 全树清，否则会随换包落进安装目录。
        var source = Path.Combine(directory, "source");
        var wrapper = Path.Combine(source, "hello-1.0.0");
        Directory.CreateDirectory(Path.Combine(wrapper, "bin"));
        Directory.CreateDirectory(Path.Combine(wrapper, "__MACOSX", "bin"));
        File.WriteAllText(Path.Combine(wrapper, "bin", "app"), "x");
        File.WriteAllText(Path.Combine(wrapper, "__MACOSX", "bin", "._app"), "junk");
        var zipPath = Path.Combine(directory, "payload.zip");
        ZipFile.CreateFromDirectory(source, zipPath);

        var staging = Path.Combine(directory, "staging");
        Directory.CreateDirectory(staging);
        ArchiveExtractor.ExtractZipToDirectory(zipPath, staging, null);

        Assert.Empty(Directory.GetDirectories(
            staging, "__MACOSX", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(staging, "hello-1.0.0", "bin", "app")));
    }

    [Fact]
    public void ExtractZip_RestoresSymlinkEntries()
    {
        // managed ExtractToDirectory 把 S_IFLNK 条目落成普通文件——提取器必须还原真链接。
        // macOS 上走 ditto 原生物化，与 managed 回退同一断言面。
        Assert.SkipUnless(TestPlatform.IsMacOS,
            "macOS 上 ExtractZipToDirectory 拉起 ditto 子进程——本腿断 ditto 路径的软链还原。");
        var directory = _ws.Combine("symlink-restore");
        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(directory, "payload.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        {
            ZipWriter.Write(stream,
            [
                new ZipEntry
                {
                    Name = "stem/",
                    Kind = ZipEntryKind.Directory,
                    Mode = 493,
                },
                new ZipEntry
                {
                    Name = "stem/real.txt",
                    Kind = ZipEntryKind.File,
                    Mode = 420,
                    Content = Encoding.UTF8.GetBytes("linked"),
                },
                new ZipEntry
                {
                    Name = "stem/link.txt",
                    Kind = ZipEntryKind.Symlink,
                    Mode = 511,
                    LinkTarget = "real.txt",
                },
            ]);
        }
        var staging = Path.Combine(directory, "staging");
        Directory.CreateDirectory(staging);

        ArchiveExtractor.ExtractZipToDirectory(zipPath, staging, _ => { });

        var link = new FileInfo(Path.Combine(staging, "stem", "link.txt"));
        Assert.Equal("real.txt", link.LinkTarget);
        Assert.Equal("linked", File.ReadAllText(link.FullName));
    }

    [Fact]
    public void ExtractZip_DropsAppleDoubleTree()
    {
        // ditto 产 zip 自带 __MACOSX/ 资源叉目录——managed 提取下它是垃圾树，清掉。
        Assert.SkipUnless(TestPlatform.IsMacOS,
            "macOS 上 ExtractZipToDirectory 拉起 ditto 子进程——本腿断 ditto 路径的 __MACOSX 清场。");
        var directory = _ws.Combine("appledouble-drop");
        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(directory, "ditto.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        {
            ZipWriter.Write(stream,
            [
                new ZipEntry
                {
                    Name = "stem/app",
                    Kind = ZipEntryKind.File,
                    Mode = 493,
                    Content = Encoding.UTF8.GetBytes("binary"),
                },
                new ZipEntry
                {
                    Name = "__MACOSX/stem/._app",
                    Kind = ZipEntryKind.File,
                    Mode = 420,
                    Content = Encoding.UTF8.GetBytes("appledouble"),
                },
            ]);
        }
        var staging = Path.Combine(directory, "staging");
        Directory.CreateDirectory(staging);

        ArchiveExtractor.ExtractZipToDirectory(zipPath, staging, _ => { });

        Assert.False(Directory.Exists(Path.Combine(staging, "__MACOSX")));
        Assert.Equal("binary",
            File.ReadAllText(Path.Combine(staging, "stem", "app")));
    }

    // NOFOLLOW 读节点自身 xattr——目录/软链与文件共用同一断言形状。
    static void AssertXattrRoundTrip(
        string staging, string name, string xattrName, byte expected, byte[] buffer)
    {
        var node = Directory
            .GetFileSystemEntries(staging, name, SearchOption.AllDirectories)
            .Single();
        var size = getxattr(node, xattrName, buffer, buffer.Length, 0, 1 /* XATTR_NOFOLLOW */);
        Assert.Equal(1, size.ToInt64());
        Assert.Equal(expected, buffer[0]);
    }

    static void SetXattr(string path, string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var rc = OperatingSystem.IsMacOS()
            ? setxattr(path, name, bytes, bytes.Length, 0, 1 /* XATTR_NOFOLLOW */)
            : lsetxattr(path, name, bytes, bytes.Length, 0);
        Assert.True(rc == 0, $"setxattr failed rc={rc} errno={Marshal.GetLastWin32Error()}");
    }

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr getxattr(string path, string name, byte[] value, int size, int position, int options);

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern int setxattr(string path, string name, byte[] value, int size, int position, int options);

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern int lsetxattr(string path, string name, byte[] value, int size, int flags);

    static BundleConfiguration Configuration(string input, string output,
        string target = "linux-x86_64", IReadOnlyList<PackageFormat>? formats = null)
    {
        return new BundleConfiguration
        {
            ProductName = "Example App",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Publisher = "Example",
            Description = "Fixture",
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.Zip]
                }
            ]
        };
    }

    static string CreateInputDirectory(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "#!/bin/sh\necho ok\n");
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        // Mach-O thin-64 little-endian magic (CF FA ED FE) + padding: on Windows the
        // archive writer can only see magic bytes, on Unix the FS mode must agree.
        File.WriteAllBytes(Path.Combine(input, "ExampleMacApp"), [0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0]);
        if (!OperatingSystem.IsWindows())
        {
            var executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(Path.Combine(input, "ExampleApp"), executable);
            File.SetUnixFileMode(Path.Combine(input, "ExampleMacApp"), executable);
        }
        return input;
    }
}

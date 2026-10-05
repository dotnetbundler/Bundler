using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;
using System.IO.Compression;
using System.Text;

public static class ArchiveTests
{

    [Fact]
    static void RejectsNonArchiveFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var configuration = Configuration(input, "", formats: [PackageFormat.Deb]);
            AssertThrows<NotSupportedException>(
                () => new ArchiveBundler().BuildAsync(configuration).GetAwaiter().GetResult(),
                "non-archive formats must be rejected");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void WritesZip()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "zip-out");
        try
        {
            var artifacts = new ArchiveBundler(new ArchiveBundleConfiguration
            {
                ArchiveName = "my-app-1.0.0-linux-x64"
            }).BuildAsync(Configuration(input, output, formats: [PackageFormat.Zip]))
                .GetAwaiter().GetResult();
            var zip = artifacts.Single().Path;
            Assert.EndsWith("my-app-1.0.0-linux-x64.zip", zip);
            Assert.True(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert.Contains(names, n => n == "my-app-1.0.0-linux-x64/");
            Assert.True(names.Any(n => n == "my-app-1.0.0-linux-x64/ExampleApp") &&
                   names.Any(n => n == "my-app-1.0.0-linux-x64/ExampleApp.dll"),
                "payload files must land under the top-level directory");
            var executable = archive.GetEntry("my-app-1.0.0-linux-x64/ExampleApp")!;
            Assert.Equal(33261 /* 0100755 */, ((executable.ExternalAttributes >> 16) & 0xFFFF));
            var regular = archive.GetEntry("my-app-1.0.0-linux-x64/ExampleApp.dll")!;
            Assert.Equal(33188 /* 0100644 */, ((regular.ExternalAttributes >> 16) & 0xFFFF));
            var macho = archive.GetEntry("my-app-1.0.0-linux-x64/ExampleMacApp")!;
            Assert.Equal(33261 /* 0100755 */, ((macho.ExternalAttributes >> 16) & 0xFFFF));
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void WritesTarGz()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "targz-out");
        var link = Path.Combine(input, "linked.so");
        try
        {
            File.WriteAllText(Path.Combine(input, "real.so"), "lib");
            CreateSymlink(link, "real.so");
            var artifacts = new ArchiveBundler().BuildAsync(
                Configuration(input, output, formats: [PackageFormat.TarGz]))
                .GetAwaiter().GetResult();
            var tgz = artifacts.Single().Path;
            Assert.EndsWith("-linux-x64.tar.gz", tgz);
            using var gzip = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress);
            var entries = ReadTar(gzip);
            var names = entries.Select(e => e.Name).ToArray();
            Assert.Contains(names, n => n.EndsWith("/ExampleApp", StringComparison.Ordinal));
            var dir = entries.First(e => e.Name.EndsWith("linux-x64/", StringComparison.Ordinal));
            Assert.True(dir.Kind == TarEntryKind.Directory && dir.Mode == 493,
                "top-level directory must be a tar dir entry mode 0755");
            var executable = entries.First(e => e.Name.EndsWith("/ExampleApp", StringComparison.Ordinal));
            Assert.Equal(493, executable.Mode);
            var macho = entries.First(e => e.Name.EndsWith("/ExampleMacApp", StringComparison.Ordinal));
            Assert.Equal(493, macho.Mode);
            if (!File.Exists(link) ||
                !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            { /* symlink fixture skipped on this fs/host */ }
            else
            {
                var sym = entries.FirstOrDefault(e => e.Name.EndsWith("/linked.so", StringComparison.Ordinal));
                Assert.True(sym?.Kind == TarEntryKind.Symlink && sym.LinkTarget == "real.so",
                    "symlink payload must be a tar symlink entry with its target");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void StagesArbitraryFiles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "files-out");
        var extra = Path.Combine(Path.GetDirectoryName(input)!, "extra.conf");
        File.WriteAllText(extra, "key=value");
        try
        {
            var artifacts = new ArchiveBundler(new ArchiveBundleConfiguration
            {
                Files = [new ArchiveFileEntry { Source = extra, Destination = "docs/notes/extra.conf" }]
            }).BuildAsync(Configuration(input, output, formats: [PackageFormat.Zip]))
                .GetAwaiter().GetResult();
            using var archive = ZipFile.OpenRead(artifacts.Single().Path);
            var entry = archive.Entries.First(e => e.FullName.EndsWith("/docs/notes/extra.conf", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("key=value", reader.ReadToEnd());
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsInvalidSettings()
    {
        var input = CreateInputDirectory();
        try
        {
            AssertThrows<ArgumentException>(
                () => new ArchiveBundler(new ArchiveBundleConfiguration { PackageName = "a b" })
                    .BuildAsync(Configuration(input, "", formats: [PackageFormat.Zip])).GetAwaiter().GetResult(),
                "package name with whitespace must be rejected");
            AssertThrows<ArgumentException>(
                () => new ArchiveBundler(new ArchiveBundleConfiguration { ArchiveName = "../escape" })
                    .BuildAsync(Configuration(input, "", formats: [PackageFormat.Zip])).GetAwaiter().GetResult(),
                "ArchiveName escape must be rejected");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsInvalidFileMappings()
    {
        var input = CreateInputDirectory();
        var extra = Path.Combine(input, "extra.txt");
        File.WriteAllText(extra, "x");
        try
        {
            foreach (var destination in new[] { "/abs/x", "../escape", "a//b", "a/./b", "a/../b", "win\\sep", "" })
            {
                AssertThrows<ArgumentException>(
                    () => BuildWithFile(input, extra, destination),
                    $"destination '{destination}' must be rejected");
            }
            AssertThrows<ArgumentException>(
                () => BuildWithFile(input, extra, "ExampleApp.dll"),
                "collision with a payload file must be rejected");
            AssertThrows<ArgumentException>(
                () => BuildWithFile(input, Path.Combine(input, "missing.txt"), "docs/x.txt"),
                "missing source must be rejected");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void BuildWithFile(string input, string source, string destination)
    {
        new ArchiveBundler(new ArchiveBundleConfiguration
        {
            Files = [new ArchiveFileEntry { Source = source, Destination = destination }]
        }).BuildAsync(Configuration(input, "", formats: [PackageFormat.Zip]))
            .GetAwaiter().GetResult();
    }

    [Fact]
    static void FanoutProducesBoth()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "fanout-out");
        try
        {
            var artifacts = new ArchiveBundler().BuildAsync(Configuration(
                input, output, formats: [PackageFormat.Zip, PackageFormat.TarGz]))
                .GetAwaiter().GetResult();
            Assert.True(artifacts.Count == 2 &&
                   artifacts.Any(a => a.Path.EndsWith(".zip")) &&
                   artifacts.Any(a => a.Path.EndsWith(".tar.gz")),
                "zip+targz fanout must produce both artifacts");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void BuildsDeterministically()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "det-out");
        try
        {
            new ArchiveBundler().BuildAsync(Configuration(
                input, output, formats: [PackageFormat.Zip, PackageFormat.TarGz]))
                .GetAwaiter().GetResult();
            var dir = Path.Combine(output, "linux-x64");
            var zipHash1 = File.ReadAllText(Path.Combine(dir, "zip", "example-app-1.0.0-linux-x64.zip.sha256"));
            var tgzHash1 = File.ReadAllText(Path.Combine(dir, "targz", "example-app-1.0.0-linux-x64.tar.gz.sha256"));
            foreach (var leftover in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.Delete(leftover);
            new ArchiveBundler().BuildAsync(Configuration(
                input, output, formats: [PackageFormat.Zip, PackageFormat.TarGz]))
                .GetAwaiter().GetResult();
            Assert.Equal(zipHash1, File.ReadAllText(Path.Combine(dir, "zip", "example-app-1.0.0-linux-x64.zip.sha256")));
            Assert.Equal(tgzHash1, File.ReadAllText(Path.Combine(dir, "targz", "example-app-1.0.0-linux-x64.tar.gz.sha256")));
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsDirectorySource()
    {
        var input = CreateInputDirectory();
        var dir = Path.Combine(input, "a-dir");
        Directory.CreateDirectory(dir);
        try
        {
            AssertThrows<ArgumentException>(
                () => BuildWithFile(input, dir, "docs/x"),
                "a directory source must be rejected");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void BuildsWinX86Archive()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "x86-out");
        try
        {
            var artifacts = new ArchiveBundler().BuildAsync(
                Configuration(input, output, "win-x86", [PackageFormat.Zip]))
                .GetAwaiter().GetResult();
            Assert.EndsWith("-win-x86.zip", artifacts.Single().Path);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsZip64()
    {
        var entries = Enumerable.Range(0, 65536)
            .Select(i => new ZipEntry { Name = $"e{i}", Kind = ZipEntryKind.File, Mode = 420 })
            .ToArray();
        using var stream = new MemoryStream();
        AssertThrows<InvalidOperationException>(
            () => ZipWriter.Write(stream, entries),
            "more than 65535 entries must hit the Zip64 guard");
    }

    // tar.gz 载荷走流式写出：>2GiB 单文件不再触发 byte[] 尺寸上限。
    [Fact]
    static void StreamsFilePayloadsAboveTwoGiB()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "big-targz-out");
        var bigPath = Path.Combine(input, "big.bin");
        const long bigSize = 2_200_000_000L; // > Int32.MaxValue：内联 byte[] 装不下
        try
        {
            using (var file = new FileStream(bigPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.ASCII.GetBytes("BUNDLER_BIG"));
                file.SetLength(bigSize); // 稀疏文件：仅头 11 字节是真实数据
            }
            var artifact = new ArchiveBundler(new ArchiveBundleConfiguration { ArchiveName = "big-app" })
                .BuildAsync(Configuration(input, output, formats: [PackageFormat.TarGz]))
                .GetAwaiter().GetResult().Single();
            Assert.EndsWith("big-app.tar.gz", artifact.Path);
            Assert.True(File.Exists(artifact.Path + ".sha256"), "sha256 sidecar must exist");
            Assert.True(new FileInfo(artifact.Path).Length < bigSize,
                "a sparse payload must compress far below its logical size");
            using var gzip = new GZipStream(File.OpenRead(artifact.Path), CompressionMode.Decompress);
            var members = ReadTarHeaders(gzip);
            var big = members.Single(m => m.Name.EndsWith("/big.bin", StringComparison.Ordinal));
            Assert.Equal(bigSize, big.Size);
            Assert.StartsWith("BUNDLER_BIG", Encoding.ASCII.GetString(big.Head));
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void StreamsEntryContentIdenticalToInline()
    {
        var payload = Encoding.UTF8.GetBytes("streamed-payload-bytes");
        using var viaInline = new MemoryStream();
        TarWriter.Write(viaInline,
        [
            new TarEntry { Name = "a/f.bin", Kind = TarEntryKind.File, Mode = 420, Content = payload }
        ]);
        using var viaStream = new MemoryStream();
        TarWriter.Write(viaStream,
        [
            new TarEntry
            {
                Name = "a/f.bin", Kind = TarEntryKind.File, Mode = 420,
                OpenContent = () => new MemoryStream(payload, writable: false)
            }
        ]);
        Assert.Equal(viaInline.ToArray(), viaStream.ToArray());
    }

    [Fact]
    static void StreamedEntryEndingEarlyFails()
    {
        using var output = new MemoryStream();
        AssertThrows<EndOfStreamException>(
            () => TarWriter.Write(output,
            [
                new TarEntry
                {
                    Name = "f.bin", Kind = TarEntryKind.File, Mode = 420,
                    OpenContent = () => new ShortStream(declaredLength: 100, actualBytes: 10)
                }
            ]),
            "a content stream ending before its declared Length must fail, not write a corrupt tar");
    }

    // zip 载荷走流式写出：>2GiB 单文件不再触发 byte[] 尺寸上限；完整解压读回
    // 顺带验证回填头里的 CRC 与长度字段（ZipArchive 读到底时校验 CRC）。
    [Fact]
    static void StreamsZipFilePayloadsAboveTwoGiB()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "big-zip-out");
        var bigPath = Path.Combine(input, "big.bin");
        const long bigSize = 2_200_000_000L; // > Int32.MaxValue：内联 byte[] 装不下
        try
        {
            using (var file = new FileStream(bigPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.ASCII.GetBytes("BUNDLER_BIG"));
                file.SetLength(bigSize); // 稀疏文件：仅头 11 字节是真实数据
            }
            var artifact = new ArchiveBundler(new ArchiveBundleConfiguration { ArchiveName = "big-app" })
                .BuildAsync(Configuration(input, output, formats: [PackageFormat.Zip]))
                .GetAwaiter().GetResult().Single();
            Assert.EndsWith(".zip", artifact.Path);
            Assert.True(File.Exists(artifact.Path + ".sha256"), "sha256 sidecar must exist");
            Assert.True(new FileInfo(artifact.Path).Length < bigSize,
                "a sparse payload must compress far below its logical size");
            using var archive = ZipFile.OpenRead(artifact.Path);
            var big = archive.Entries.Single(e => e.FullName.EndsWith("/big.bin", StringComparison.Ordinal));
            Assert.Equal(bigSize, big.Length);
            using var entryStream = big.Open();
            var head = new byte[16];
            var got = entryStream.Read(head, 0, head.Length);
            Assert.StartsWith("BUNDLER_BIG", Encoding.ASCII.GetString(head, 0, got));
            // 读到底触发 ZipArchive 的 CRC 校验——回填的 crc 值必须与真实内容一致。
            entryStream.CopyTo(Stream.Null);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void StreamsZipEntryContentIdenticalToInline()
    {
        // 可压缩载荷让两条路径都选 deflate——同输入字节序在两条路径下应逐字节一致。
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("streamed-payload-bytes;", 2000)));
        using var viaInline = new MemoryStream();
        ZipWriter.Write(viaInline,
        [
            new ZipEntry { Name = "a/f.bin", Kind = ZipEntryKind.File, Mode = 420, Content = payload }
        ]);
        using var viaStream = new MemoryStream();
        ZipWriter.Write(viaStream,
        [
            new ZipEntry
            {
                Name = "a/f.bin", Kind = ZipEntryKind.File, Mode = 420,
                OpenContent = () => new MemoryStream(payload, writable: false)
            }
        ]);
        Assert.Equal(viaInline.ToArray(), viaStream.ToArray());
    }

    [Fact]
    static void StreamedZipEntryEndingEarlyFails()
    {
        using var output = new MemoryStream();
        AssertThrows<EndOfStreamException>(
            () => ZipWriter.Write(output,
            [
                new ZipEntry
                {
                    Name = "f.bin", Kind = ZipEntryKind.File, Mode = 420,
                    OpenContent = () => new ShortStream(declaredLength: 100, actualBytes: 10)
                }
            ]),
            "a content stream ending before its declared Length must fail, not write a corrupt zip");
    }

    [Fact]
    static void StreamedZipEntryAtOrBeyondFourGiBIsRejected()
    {
        using var output = new MemoryStream();
        AssertThrows<InvalidOperationException>(
            () => ZipWriter.Write(output,
            [
                new ZipEntry
                {
                    Name = "f.bin", Kind = ZipEntryKind.File, Mode = 420,
                    OpenContent = () => new ShortStream(declaredLength: uint.MaxValue, actualBytes: 0)
                }
            ]),
            "an entry whose size hits the Zip64 sentinel value must be rejected, not written as a corrupt header");
    }

    // Reports Length = declaredLength but only yields actualBytes of content.
    sealed class ShortStream(long declaredLength, int actualBytes) : Stream
    {
        private int _produced;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => declaredLength;
        public override long Position { get => _produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, actualBytes - _produced);
            _produced += Math.Max(0, n);
            return Math.Max(0, n);
        }
    }

    // Reads tar member headers without buffering payloads; captures the first
    // 16 content bytes of each file member for content spot-checks.
    sealed class TarHeaderRecord
    {
        internal string Name = "";
        internal TarEntryKind Kind;
        internal int Mode;
        internal long Size;
        internal byte[] Head = [];
    }

    static List<TarHeaderRecord> ReadTarHeaders(Stream stream)
    {
        var entries = new List<TarHeaderRecord>();
        var header = new byte[512];
        var skipBuffer = new byte[81920];
        while (true)
        {
            if (ReadExact(stream, header) == 0) break;
            if (header.All(b => b == 0)) break;
            var entry = new TarHeaderRecord
            {
                Name = ReadTarString(header, 0, 100),
                Mode = Convert.ToInt32(ReadTarString(header, 100, 8).Trim(), 8),
                Kind = header[156] switch
                {
                    (byte)'5' => TarEntryKind.Directory,
                    (byte)'2' => TarEntryKind.Symlink,
                    _ => TarEntryKind.File
                },
                Size = Convert.ToInt64(ReadTarString(header, 124, 12).Trim(), 8)
            };
            var prefix = ReadTarString(header, 345, 155);
            if (prefix.Length > 0) entry.Name = prefix + "/" + entry.Name;
            var toSkip = entry.Size;
            if (entry.Kind == TarEntryKind.File && toSkip > 0)
            {
                var head = new byte[(int)Math.Min(16, toSkip)];
                var got = ReadExact(stream, head);
                entry.Head = head[..got];
                toSkip -= got;
            }
            while (toSkip > 0)
            {
                var read = stream.Read(skipBuffer, 0, (int)Math.Min(skipBuffer.Length, toSkip));
                if (read == 0) break;
                toSkip -= read;
            }
            var remainder = entry.Size % 512;
            if (remainder != 0)
            {
                var pad = new byte[512 - remainder];
                ReadExact(stream, pad);
            }
            entries.Add(entry);
        }
        return entries;
    }

    [Fact]
    static void MapsArchiveSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("ArchivePackageName=\"$(BundlerArchivePackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("ArchiveFiles=\"@(BundlerArchiveFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerArchive* properties to the task.");
        Assert.Contains("<BundlerArchivePackageName", props);
        Assert.True(task.Contains("new ArchiveBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Zip", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.TarGz", StringComparison.Ordinal),
            "The MSBuild task does not construct the archive backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.Archive.dll", msbuildProject);
    }

    // Minimal tar reader for assertions (the writer's own TarWriter output).
    sealed class TarReadEntry
    {
        internal string Name = "";
        internal TarEntryKind Kind;
        internal int Mode;
        internal string LinkTarget = "";
        internal byte[] Content = [];
    }

    static List<TarReadEntry> ReadTar(Stream stream)
    {
        var entries = new List<TarReadEntry>();
        var header = new byte[512];
        while (true)
        {
            if (ReadExact(stream, header) == 0) break;
            if (header.All(b => b == 0)) break;
            var entry = new TarReadEntry
            {
                Name = ReadTarString(header, 0, 100),
                Mode = Convert.ToInt32(ReadTarString(header, 100, 8).Trim(), 8),
                LinkTarget = ReadTarString(header, 157, 100),
                Kind = header[156] switch
                {
                    (byte)'5' => TarEntryKind.Directory,
                    (byte)'2' => TarEntryKind.Symlink,
                    _ => TarEntryKind.File
                }
            };
            var prefix = ReadTarString(header, 345, 155);
            if (prefix.Length > 0) entry.Name = prefix + "/" + entry.Name;
            var size = Convert.ToInt64(ReadTarString(header, 124, 12).Trim(), 8);
            if (entry.Kind == TarEntryKind.File && size > 0)
            {
                entry.Content = new byte[size];
                ReadExact(stream, entry.Content);
            }
            var remainder = size % 512;
            if (remainder != 0)
            {
                var pad = new byte[512 - remainder];
                ReadExact(stream, pad);
            }
            entries.Add(entry);
        }
        return entries;
    }

    static int ReadExact(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    static string ReadTarString(byte[] header, int offset, int width)
    {
        var end = offset;
        while (end < offset + width && header[end] != 0) end++;
        return Encoding.UTF8.GetString(header, offset, end - offset);
    }

    static void CreateSymlink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); }
        catch { /* filesystem/permission does not allow links — the assertion tolerates it */ }
    }

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than archived.
    [Fact]
    static void SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "socket-skip-out");
        try
        {
            var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
            using (var socket = new System.Net.Sockets.Socket(
                       System.Net.Sockets.AddressFamily.Unix,
                       System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
                // .NET unlinks the bound path on dispose; move the node while bound
                File.Move(bindPath, Path.Combine(input, "agent.sock"));
            }
            var artifact = new ArchiveBundler(new ArchiveBundleConfiguration
            {
                ArchiveName = "socket-skip"
            }).BuildAsync(Configuration(input, output, formats: [PackageFormat.Zip]))
                .GetAwaiter().GetResult().Single();
            using var archive = ZipFile.OpenRead(artifact.Path);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert.Contains(names, name => name == "socket-skip/ExampleApp");
            Assert.False(names.Any(name => name.EndsWith("agent.sock", StringComparison.Ordinal)), $"a unix socket must not be archived: {string.Join(',', names)}");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static BundleConfiguration Configuration(string input, string output,
        string rid = "linux-x64", IReadOnlyList<PackageFormat>? formats = null)
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
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.Zip]
                }
            ]
        };
    }

    static string CreateInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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

    static void Cleanup(string input)
    {
        var root = Path.GetDirectoryName(input);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bundler.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }



    static void AssertThrows<T>(Action action, string because) where T : Exception
    {
        var exception = Record.Exception(action);
        Assert.True(exception is T,
            $"{because}: expected {typeof(T).Name}, got {exception?.GetType().Name}: {exception?.Message}");
    }
}

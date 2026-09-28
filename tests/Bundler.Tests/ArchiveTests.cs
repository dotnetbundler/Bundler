using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;
using System.IO.Compression;
using System.Text;

internal static class ArchiveTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-archive formats", () => RunSync(RejectsNonArchiveFormats));
            yield return ("Writes a valid .zip with unix modes", () => RunSync(WritesZip));
            yield return ("Writes a valid .tar.gz with modes and symlink", () => RunSync(WritesTarGz));
            yield return ("Stages arbitrary archive files", () => RunSync(StagesArbitraryFiles));
            yield return ("Skips non-regular payload files", () => RunSync(SkipsNonRegularPayloadFiles));
            yield return ("Rejects invalid archive settings", () => RunSync(RejectsInvalidSettings));
            yield return ("Rejects invalid archive file mappings", () => RunSync(RejectsInvalidFileMappings));
            yield return ("Produces zip and targz in one fanout", () => RunSync(FanoutProducesBoth));
            yield return ("Maps archive settings through MSBuild", () => RunSync(MapsArchiveSettingsThroughMsBuild));
            yield return ("Builds deterministically (identical sha256)", () => RunSync(BuildsDeterministically));
            yield return ("Rejects a directory as a mapped file source", () => RunSync(RejectsDirectorySource));
            yield return ("Zip writer rejects archives beyond classic zip limits", () => RunSync(RejectsZip64));
        }
    }

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
            Assert(zip.EndsWith("my-app-1.0.0-linux-x64.zip", StringComparison.Ordinal),
                "zip naming must use <stem>.zip");
            Assert(File.Exists(zip + ".sha256"), "sha256 sidecar must exist");
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert(names.Any(n => n == "my-app-1.0.0-linux-x64/"),
                "single top-level directory must wrap the payload");
            Assert(names.Any(n => n == "my-app-1.0.0-linux-x64/ExampleApp") &&
                   names.Any(n => n == "my-app-1.0.0-linux-x64/ExampleApp.dll"),
                "payload files must land under the top-level directory");
            var executable = archive.GetEntry("my-app-1.0.0-linux-x64/ExampleApp")!;
            Assert(((executable.ExternalAttributes >> 16) & 0xFFFF) == 33261 /* 0100755 */,
                "shebang payload must carry unix mode 0755 in external attributes");
            var regular = archive.GetEntry("my-app-1.0.0-linux-x64/ExampleApp.dll")!;
            Assert(((regular.ExternalAttributes >> 16) & 0xFFFF) == 33188 /* 0100644 */,
                "regular payload must carry unix mode 0644");
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(tgz.EndsWith("-linux-x64.tar.gz", StringComparison.Ordinal),
                "tar.gz naming must end with .tar.gz");
            using var gzip = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress);
            var entries = ReadTar(gzip);
            var names = entries.Select(e => e.Name).ToArray();
            Assert(names.Any(n => n.EndsWith("/ExampleApp", StringComparison.Ordinal)),
                "tar.gz must contain the payload under the top-level directory");
            var dir = entries.First(e => e.Name.EndsWith("linux-x64/", StringComparison.Ordinal));
            Assert(dir.Kind == TarEntryKind.Directory && dir.Mode == 493,
                "top-level directory must be a tar dir entry mode 0755");
            var executable = entries.First(e => e.Name.EndsWith("/ExampleApp", StringComparison.Ordinal));
            Assert(executable.Mode == 493, "shebang payload must carry mode 0755");
            if (!File.Exists(link) ||
                !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            { /* symlink fixture skipped on this fs/host */ }
            else
            {
                var sym = entries.FirstOrDefault(e => e.Name.EndsWith("/linked.so", StringComparison.Ordinal));
                Assert(sym?.Kind == TarEntryKind.Symlink && sym.LinkTarget == "real.so",
                    "symlink payload must be a tar symlink entry with its target");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(reader.ReadToEnd() == "key=value", "mapped file content must match");
        }
        finally
        {
            Cleanup(input);
        }
    }

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

    static void FanoutProducesBoth()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "fanout-out");
        try
        {
            var artifacts = new ArchiveBundler().BuildAsync(Configuration(
                input, output, formats: [PackageFormat.Zip, PackageFormat.TarGz]))
                .GetAwaiter().GetResult();
            Assert(artifacts.Count == 2 &&
                   artifacts.Any(a => a.Path.EndsWith(".zip")) &&
                   artifacts.Any(a => a.Path.EndsWith(".tar.gz")),
                "zip+targz fanout must produce both artifacts");
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(File.ReadAllText(Path.Combine(dir, "zip", "example-app-1.0.0-linux-x64.zip.sha256")) == zipHash1,
                "two builds of the same input must produce identical zip sha256");
            Assert(File.ReadAllText(Path.Combine(dir, "targz", "example-app-1.0.0-linux-x64.tar.gz.sha256")) == tgzHash1,
                "two builds of the same input must produce identical tar.gz sha256");
        }
        finally
        {
            Cleanup(input);
        }
    }

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

    static void MapsArchiveSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("ArchivePackageName=\"$(BundlerArchivePackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("ArchiveFiles=\"@(BundlerArchiveFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerArchive* properties to the task.");
        Assert(props.Contains("<BundlerArchivePackageName", StringComparison.Ordinal),
            "The BundlerArchive* properties lack defaults in the .props file.");
        Assert(task.Contains("new ArchiveBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Zip", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.TarGz", StringComparison.Ordinal),
            "The MSBuild task does not construct the archive backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert(msbuildProject.Contains("DotNet.Bundler.Archive.dll", StringComparison.Ordinal),
            "The MSBuild package must ship DotNet.Bundler.Archive.dll.");
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
    static void SkipsNonRegularPayloadFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var input = CreateInputDirectory();
        var output = Path.Combine(input, "..", "socket-skip-out");
        try
        {
            using (var socket = new System.Net.Sockets.Socket(
                       System.Net.Sockets.AddressFamily.Unix,
                       System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(
                    Path.Combine(input, "agent.sock")));
            }
            var artifact = new ArchiveBundler(new ArchiveBundleConfiguration
            {
                ArchiveName = "socket-skip"
            }).BuildAsync(Configuration(input, output, formats: [PackageFormat.Zip]))
                .GetAwaiter().GetResult().Single();
            using var archive = ZipFile.OpenRead(artifact.Path);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert(names.Any(name => name == "socket-skip/ExampleApp"),
                "regular payload files must still be archived");
            Assert(!names.Any(name => name.EndsWith("agent.sock", StringComparison.Ordinal)),
                $"a unix socket must not be archived: {string.Join(',', names)}");
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
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(input, "ExampleApp"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

    static Task RunSync(Action test)
    {
        test();
        return Task.CompletedTask;
    }

    static void Assert(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + because);
    }

    static void AssertThrows<T>(Action action, string because) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        catch (Exception other)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(T).Name} but got {other.GetType().Name}: {other.Message} — {because}");
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} — {because}");
    }
}

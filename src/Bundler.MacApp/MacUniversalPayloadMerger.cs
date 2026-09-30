using System.Runtime.InteropServices;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// Merges per-architecture payload directories into one universal directory feedable to the
/// `osx` bundle target. Rules per relative file:
/// - present on all sides and byte-identical → one copy;
/// - present on all sides and Mach-O → fat-merge all unique architecture slices;
/// - present on all sides, differs, not Mach-O → hard error (no generic arch-subdir fallback:
///   a standard .NET apphost does not probe RID subdirectories, unlike MAUI's runtime);
/// - present on one side only → copied verbatim.
/// Symlinks are copied as links; other special files are rejected.
/// </summary>
public static class MacUniversalPayloadMerger
{
    /// <summary>Merge <paramref name="sourceDirectories"/> (one per architecture) into <paramref name="outputDirectory"/>.</summary>
    public static void Merge(IReadOnlyList<string> sourceDirectories, string outputDirectory)
    {
        if (sourceDirectories.Count < 2)
        {
            throw new ArgumentException("Universal merge requires at least two input directories.");
        }
        foreach (var source in sourceDirectories)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"Universal merge input '{source}' does not exist.");
            }
        }
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException($"Universal merge output '{outputDirectory}' is not empty.");
        }

        var relativePaths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceDirectories)
        {
            foreach (var file in EnumerateRelativeFiles(source))
            {
                relativePaths.Add(file);
            }
        }

        foreach (var relative in relativePaths)
        {
            var sources = sourceDirectories
                .Select(dir => Path.Combine(dir, relative))
                .Where(File.Exists)
                .ToArray();
            var destination = Path.Combine(outputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (sources.Length == 1)
            {
                Copy(sources[0], destination);
                continue;
            }

            var first = sources[0];
            var allMachO = sources.All(MachO.IsMachO);
            if (allMachO)
            {
                MergeMachO(sources, destination);
            }
            else if (sources.Skip(1).All(other => BytesEqual(first, other)))
            {
                File.Copy(first, destination);
            }
            else if (IsPerArchMetadata(relative))
            {
                // Debug symbols embed per-RID paths so they always differ; keeping one side's copy
                // is the usual trade-off (managed debugging has no fat-symbol format).
                File.Copy(first, destination);
            }
            else
            {
                throw new InvalidDataException(
                    $"Universal merge conflict: '{relative}' differs between inputs and is not a Mach-O file " +
                    $"(first: {first}). Universal payloads need byte-identical non-Mach-O files or " +
                    "per-architecture Mach-O files to merge.");
            }
        }
    }

    /// <summary>Files allowed to differ per-RID: debug symbols and AOT debug bundles.</summary>
    private static bool IsPerArchMetadata(string relative) =>
        relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        relative.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase) ||
        relative.IndexOf(".dSYM/", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Fat-merge Mach-O files: union of architecture slices across all inputs.</summary>
    private static void MergeMachO(string[] sources, string destination)
    {
        var slices = new List<FatSlice>();
        var seen = new HashSet<uint>();
        foreach (var source in sources)
        {
            foreach (var info in MachO.ReadSliceInfos(source))
            {
                var cpuType = info.CpuType;
                if (!seen.Add(cpuType))
                {
                    continue;
                }
                var captured = info;
                slices.Add(new FatSlice(
                    captured.CpuType,
                    captured.CpuSubtype,
                    captured.Size,
                    output => CopyRange(source, captured.Offset, captured.Size, output)));
            }
        }
        MachOFat.Create(destination, slices);
        RestoreExecutableBit(sources[0], destination);
    }

    private static IEnumerable<string> EnumerateRelativeFiles(string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(prefix, "*", SearchOption.AllDirectories))
        {
            yield return file.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static void Copy(string source, string destination)
    {
        File.Copy(source, destination);
        RestoreExecutableBit(source, destination);
    }

    private static void CopyRange(string source, long offset, long size, Stream output)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read);
        input.Position = offset;
        var buffer = new byte[81920];
        var remaining = size;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of '{source}' while extracting Mach-O slice.");
            }
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool BytesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }
            if (leftRead == 0)
            {
                return true;
            }
            for (var index = 0; index < leftRead; index++)
            {
                if (leftBuffer[index] != rightBuffer[index])
                {
                    return false;
                }
            }
        }
    }

    /// <summary>Preserve the executable bit where POSIX modes exist; no-op on Windows.</summary>
    private static void RestoreExecutableBit(string source, string destination)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }
        try
        {
            if (UnixFiles.IsExecutable(source) == true)
            {
                UnixFiles.Chmod755(destination);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

/// <summary>libc file-mode helpers for netstandard2.0 (no UnixFileMode API).</summary>
internal static class UnixFiles
{
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int access(string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string path, ushort mode);

    internal static bool? IsExecutable(string path)
    {
        try
        {
            return access(path, 1 /* X_OK */) == 0;
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    internal static void Chmod755(string path) => chmod(path, 0x1ED /* 0755 */);
}

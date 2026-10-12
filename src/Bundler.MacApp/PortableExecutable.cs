namespace DotNet.Bundler.MacApp;

/// <summary>
/// Minimal PE/COFF reader — just enough to tell managed .NET assemblies from native binaries.
/// A managed assembly carries a non-empty CLI header (optional-header data directory #14);
/// a native PE has that directory zeroed. Hand-rolled like <see cref="MachO"/> so it works
/// on every build host without extra packages.
/// </summary>
internal static class PortableExecutable
{
    private const int CoffHeaderSize = 20;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int CliDirectoryIndex = 14;
    private const int Pe32DataDirectoryOffset = 96;
    private const int Pe32PlusDataDirectoryOffset = 112;

    /// <summary>True when <paramref name="path"/> is a PE file with a non-empty CLI header.</summary>
    internal static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 0x40 || ReadUInt16(stream, 0) != 0x5A4D /* MZ */)
            {
                return false;
            }
            var peOffset = ReadUInt32(stream, 0x3C);
            // PE 头必须在文件内且给可选头+数据目录留下空间。
            if (peOffset < 0x40 || peOffset + CoffHeaderSize + Pe32PlusDataDirectoryOffset > stream.Length)
            {
                return false;
            }
            if (ReadUInt32(stream, peOffset) != 0x00004550 /* PE\0\0 */)
            {
                return false;
            }
            var optionalMagic = ReadUInt16(stream, peOffset + 4 + CoffHeaderSize);
            var directoryBase = optionalMagic switch
            {
                Pe32Magic => peOffset + 4 + CoffHeaderSize + Pe32DataDirectoryOffset,
                Pe32PlusMagic => peOffset + 4 + CoffHeaderSize + Pe32PlusDataDirectoryOffset,
                _ => 0L,
            };
            if (directoryBase == 0)
            {
                return false;
            }
            var cliDirectory = directoryBase + CliDirectoryIndex * 8;
            if (cliDirectory + 8 > stream.Length)
            {
                return false;
            }
            return ReadUInt32(stream, cliDirectory) != 0 && ReadUInt32(stream, cliDirectory + 4) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static ushort ReadUInt16(Stream stream, long offset)
    {
        stream.Position = offset;
        var lo = stream.ReadByte();
        var hi = stream.ReadByte();
        return (ushort)((hi << 8) | lo);
    }

    private static uint ReadUInt32(Stream stream, long offset)
    {
        stream.Position = offset;
        var b0 = stream.ReadByte();
        var b1 = stream.ReadByte();
        var b2 = stream.ReadByte();
        var b3 = stream.ReadByte();
        return (uint)((b3 << 24) | (b2 << 16) | (b1 << 8) | b0);
    }
}

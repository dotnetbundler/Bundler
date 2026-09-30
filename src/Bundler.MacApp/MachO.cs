namespace DotNet.Bundler.MacApp;

internal static class MachO
{
    private const uint ThinLittleEndian32 = 0xCEFAEDFE; // MH_CIGAM: little-endian MH_MAGIC on disk
    private const uint ThinLittleEndian64 = 0xCFFAEDFE; // MH_CIGAM_64
    private const uint ThinBigEndian32 = 0xFEEDFACE;    // MH_MAGIC
    private const uint ThinBigEndian64 = 0xFEEDFACF;    // MH_MAGIC_64
    private const uint FatBigEndian32 = 0xCAFEBABE;     // FAT_MAGIC (always big-endian on disk)
    private const uint FatBigEndian64 = 0xCAFEBABF;     // FAT_MAGIC_64
    private const uint FatLittleEndian32 = 0xBEBAFECA;  // FAT_CIGAM
    private const uint FatLittleEndian64 = 0xBFBAFECA;  // FAT_CIGAM_64

    internal static bool IsMachO(string path) => ReadArchitectures(path).Count > 0;

    /// <summary>
    /// Parses the Mach-O/FAT headers in managed code (equivalent to `lipo -info` output) and
    /// returns the contained CPU architectures, for example "x86_64"/"arm64". Returns an empty
    /// list when the file is not a Mach-O or the header cannot be parsed. Being managed, this
    /// check works on every build host.
    /// </summary>
    internal static IReadOnlyList<string> ReadArchitectures(string path)
    {
        byte[] header;
        using (var stream = File.OpenRead(path))
        {
            header = new byte[8];
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return [];
            }
        }

        var magic = ReadBigEndian32(header, 0);
        switch (magic)
        {
            case ThinLittleEndian32:
            case ThinLittleEndian64:
                return [CpuTypeName(ReadUInt32(path, 4, bigEndian: false))];
            case ThinBigEndian32:
            case ThinBigEndian64:
                return [CpuTypeName(ReadUInt32(path, 4, bigEndian: true))];
            case FatBigEndian32:
                return ReadFatArchitectures(path, entrySize: 20, bigEndian: true);
            case FatBigEndian64:
                return ReadFatArchitectures(path, entrySize: 32, bigEndian: true);
            case FatLittleEndian32:
                return ReadFatArchitectures(path, entrySize: 20, bigEndian: false);
            case FatLittleEndian64:
                return ReadFatArchitectures(path, entrySize: 32, bigEndian: false);
            default:
                return [];
        }
    }

    /// <summary>
    /// Header detail for merging: each entry is one architecture slice — cpu type/subtype and the
    /// byte range (offset+size) of its code inside the file. Thin files produce a single entry
    /// covering the whole file. Empty list when the file is not Mach-O.
    /// </summary>
    internal static IReadOnlyList<MachOSliceInfo> ReadSliceInfos(string path)
    {
        byte[] header;
        using (var stream = File.OpenRead(path))
        {
            header = new byte[8];
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return [];
            }
        }

        var magic = ReadBigEndian32(header, 0);
        var fileSize = new FileInfo(path).Length;
        switch (magic)
        {
            case ThinLittleEndian32:
            case ThinLittleEndian64:
                return [new MachOSliceInfo(ReadUInt32(path, 4, bigEndian: false), ReadUInt32(path, 8, bigEndian: false), 0, fileSize)];
            case ThinBigEndian32:
            case ThinBigEndian64:
                return [new MachOSliceInfo(ReadUInt32(path, 4, bigEndian: true), ReadUInt32(path, 8, bigEndian: true), 0, fileSize)];
            case FatBigEndian32:
                return ReadFatSliceInfos(path, entrySize: 20, bigEndian: true, wideOffsets: false);
            case FatBigEndian64:
                return ReadFatSliceInfos(path, entrySize: 32, bigEndian: true, wideOffsets: true);
            case FatLittleEndian32:
                return ReadFatSliceInfos(path, entrySize: 20, bigEndian: false, wideOffsets: false);
            case FatLittleEndian64:
                return ReadFatSliceInfos(path, entrySize: 32, bigEndian: false, wideOffsets: true);
            default:
                return [];
        }
    }

    private static IReadOnlyList<MachOSliceInfo> ReadFatSliceInfos(string path, int entrySize, bool bigEndian, bool wideOffsets)
    {
        var count = ReadUInt32(path, 4, bigEndian);
        if (count == 0 || count > 64)
        {
            return [];
        }
        var buffer = new byte[count * entrySize];
        using (var stream = File.OpenRead(path))
        {
            stream.Position = 8;
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
            {
                return [];
            }
        }
        var slices = new List<MachOSliceInfo>((int)count);
        for (var index = 0; index < count; index++)
        {
            var at = index * entrySize;
            uint Read32(int rel) => bigEndian ? ReadBigEndian32(buffer, at + rel) : ReadLittleEndian32(buffer, at + rel);
            var cpuType = Read32(0);
            var cpuSubtype = Read32(4);
            long offset;
            long size;
            if (wideOffsets)
            {
                offset = ((long)Read32(8) << 32) | Read32(12);
                size = ((long)Read32(16) << 32) | Read32(20);
            }
            else
            {
                offset = Read32(8);
                size = Read32(12);
            }
            slices.Add(new MachOSliceInfo(cpuType, cpuSubtype, offset, size));
        }
        return slices;
    }

    private static IReadOnlyList<string> ReadFatArchitectures(string path, int entrySize, bool bigEndian)
    {
        var count = ReadUInt32(path, 4, bigEndian);
        if (count == 0 || count > 64)
        {
            return [];
        }
        var buffer = new byte[count * entrySize];
        using (var stream = File.OpenRead(path))
        {
            stream.Position = 8;
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
            {
                return [];
            }
        }
        var architectures = new List<string>((int)count);
        for (var index = 0; index < count; index++)
        {
            var offset = index * entrySize;
            var cpuType = bigEndian ? ReadBigEndian32(buffer, offset) : ReadLittleEndian32(buffer, offset);
            architectures.Add(CpuTypeName(cpuType));
        }
        return architectures;
    }

    private static uint ReadUInt32(string path, int offset, bool bigEndian)
    {
        var buffer = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            stream.Position = offset;
            if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                return uint.MaxValue;
            }
        }
        return bigEndian ? ReadBigEndian32(buffer, 0) : ReadLittleEndian32(buffer, 0);
    }

    private static uint ReadBigEndian32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static uint ReadLittleEndian32(byte[] buffer, int offset) =>
        buffer[offset] | ((uint)buffer[offset + 1] << 8) |
        ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);

    private static string CpuTypeName(uint cpuType) => cpuType switch
    {
        0x00000007 => "i386",
        0x01000007 => "x86_64",
        0x0000000B => "ppc",
        0x0100000B => "ppc64",
        0x0000000C => "arm",
        0x0100000C => "arm64",
        0x0200000C => "arm64_32",
        _ => $"0x{cpuType:X8}"
    };
}

/// <summary>One architecture slice inside a Mach-O: cpu identity plus byte range.</summary>
internal sealed class MachOSliceInfo
{
    internal MachOSliceInfo(uint cpuType, uint cpuSubtype, long offset, long size)
    {
        CpuType = cpuType;
        CpuSubtype = cpuSubtype;
        Offset = offset;
        Size = size;
    }

    internal uint CpuType { get; }
    internal uint CpuSubtype { get; }
    internal long Offset { get; }
    internal long Size { get; }
}

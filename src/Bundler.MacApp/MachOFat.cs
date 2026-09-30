using System.Runtime.InteropServices;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// Managed fat (universal) Mach-O writer — the equivalent of `lipo -create`, available on every
/// build host. Produces a FAT_MAGIC (32-bit) container: big-endian header + fat_arch records +
/// slices aligned per architecture page size.
/// </summary>
internal static class MachOFat
{
    private const uint FatMagic = 0xCAFEBABE;

    /// <summary>
    /// Writes a fat Mach-O at <paramref name="outputPath"/> containing one slice per input file.
    /// Each input may itself be a thin Mach-O (whole file becomes the slice) or a fat Mach-O
    /// (only the slice matching <paramref name="cpuType"/> is extracted — the merge already
    /// decomposed fat inputs, so callers pass thin files in practice).
    /// </summary>
    internal static void Create(string outputPath, IReadOnlyList<FatSlice> slices)
    {
        if (slices.Count == 0 || slices.Count > 64)
        {
            throw new ArgumentException($"Fat Mach-O requires 1..64 slices, got {slices.Count}.");
        }

        var entries = new (uint CpuType, uint CpuSubtype, long Offset, long Size, uint Align)[slices.Count];
        long offset = 8 + (20L * slices.Count);
        for (var index = 0; index < slices.Count; index++)
        {
            var align = AlignmentExponent(slices[index].CpuType);
            var alignment = 1L << (int)align;
            offset = (offset + alignment - 1) / alignment * alignment;
            entries[index] = (slices[index].CpuType, slices[index].CpuSubtype, offset, slices[index].Size, align);
            offset += slices[index].Size;
        }

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            var header = new byte[8 + (20 * slices.Count)];
            WriteBigEndian32(header, 0, FatMagic);
            WriteBigEndian32(header, 4, (uint)slices.Count);
            for (var index = 0; index < slices.Count; index++)
            {
                var at = 8 + (index * 20);
                WriteBigEndian32(header, at, entries[index].CpuType);
                WriteBigEndian32(header, at + 4, entries[index].CpuSubtype);
                WriteBigEndian32(header, at + 8, (uint)entries[index].Offset);
                WriteBigEndian32(header, at + 12, (uint)entries[index].Size);
                WriteBigEndian32(header, at + 16, entries[index].Align);
            }
            output.Write(header, 0, header.Length);

            for (var index = 0; index < slices.Count; index++)
            {
                var pad = entries[index].Offset - output.Position;
                for (var fill = 0L; fill < pad; fill++)
                {
                    output.WriteByte(0);
                }
                slices[index].WriteTo(output);
            }
        }
    }

    /// <summary>Slice alignment exponent (log2): arm64 needs 16KB pages, others 4KB.</summary>
    internal static uint AlignmentExponent(uint cpuType) => cpuType switch
    {
        0x0100000C => 14, // arm64 / arm64e variants: 16 KiB
        _ => 12,          // x86_64 and everything else: 4 KiB
    };

    private static void WriteBigEndian32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

/// <summary>One slice to write into a fat Mach-O: header fields plus a lazy content source.</summary>
internal sealed class FatSlice
{
    internal FatSlice(uint cpuType, uint cpuSubtype, long size, Action<Stream> writeTo)
    {
        CpuType = cpuType;
        CpuSubtype = cpuSubtype;
        Size = size;
        WriteTo = writeTo;
    }

    internal uint CpuType { get; }
    internal uint CpuSubtype { get; }
    internal long Size { get; }
    internal Action<Stream> WriteTo { get; }
}

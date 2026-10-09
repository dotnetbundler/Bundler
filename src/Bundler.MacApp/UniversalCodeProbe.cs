namespace DotNet.Bundler.MacApp;

/// <summary>
/// Signature-table probe for the `osx` (universal) payload contract: InspectPayload verifies
/// Mach-O slices against the RID, but a hand-assembled "pre-merged" directory can smuggle
/// per-architecture code through files the Mach-O check never inspects — stray ELF binaries,
/// native Windows executables, or ReadyToRun assemblies all produce payloads whose fat header
/// passes while the runtime rejects them (0x8007000B). FDD pure-IL assemblies pass; anything
/// carrying single-architecture native code is rejected.
/// </summary>
internal static class UniversalCodeProbe
{
    /// <summary>Null when the file carries no detectable per-architecture native code.</summary>
    internal static string? ForeignCodeDescription(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var header = new byte[8];
        var read = stream.Read(header, 0, header.Length);
        // ELF：Linux 原生代码不可能属于 mac 通用载荷。
        if (read >= 4 && header[0] == 0x7F && header[1] == 'E' && header[2] == 'L' && header[3] == 'F')
        {
            return "an ELF binary (Linux native code cannot run in a macOS payload)";
        }
        if (read >= 2 && header[0] == 'M' && header[1] == 'Z')
        {
            return DescribePortableExecutable(stream);
        }
        return null;
    }

    /// <summary>PE 三分支：无 CLI 描述符是原生 Windows 代码；带 CLI 且 ReadyToRun 段非空是单架构托管代码；纯 IL 放行。</summary>
    private static string? DescribePortableExecutable(FileStream stream)
    {
        var peOffset = ReadUInt32(stream, 0x3C);
        if (peOffset is null or > int.MaxValue)
        {
            return null;
        }
        var signature = ReadBlock(stream, (long)peOffset, 24);
        if (signature.Length < 24 || signature[0] != 'P' || signature[1] != 'E' ||
            signature[2] != 0 || signature[3] != 0)
        {
            // MZ 但无 PE 签名——不是可识别的原生代码，放行。
            return null;
        }
        var sectionCount = ReadUInt16(signature, 6);
        var optionalSize = ReadUInt16(signature, 20);
        var optional = ReadBlock(stream, (long)peOffset + 24, optionalSize);
        if (optional.Length < 112)
        {
            return null;
        }
        var directoryBase = ReadUInt16(optional, 0) switch
        {
            0x10B => 96,  // PE32
            0x20B => 112, // PE32+
            _ => -1
        };
        // CLI（COM descriptor）是第 15 个数据目录项。
        if (directoryBase < 0 || optional.Length < directoryBase + 15 * 8)
        {
            return null;
        }
        var cliRva = ReadUInt32(optional, directoryBase + 14 * 8);
        var cliSize = ReadUInt32(optional, directoryBase + 14 * 8 + 4);
        if (cliRva == 0)
        {
            return "a native Windows executable (non-managed PE carries per-architecture code)";
        }
        if (cliSize == 0)
        {
            return null;
        }
        // COR20 头的 ManagedNativeHeader（偏移 64 的数据目录）非空 = ReadyToRun：
        // 程序集内嵌某单一架构的机器码，通不过通用载荷契约。
        var corOffset = RvaToFileOffset(
            stream, (long)peOffset + 24 + optionalSize, sectionCount, cliRva);
        if (corOffset < 0)
        {
            return null;
        }
        var cor = ReadBlock(stream, corOffset, 72);
        if (cor.Length < 72)
        {
            return null;
        }
        if (ReadUInt32(cor, 64 + 4) != 0)
        {
            return "a ReadyToRun assembly (per-architecture native code inside a managed PE)";
        }
        return null;
    }

    private static long RvaToFileOffset(FileStream stream, long sectionBase, int sectionCount, uint rva)
    {
        var table = ReadBlock(stream, sectionBase, sectionCount * 40);
        if (table.Length < sectionCount * 40)
        {
            return -1;
        }
        for (var index = 0; index < sectionCount; index++)
        {
            var at = index * 40;
            var virtualAddress = ReadUInt32(table, at + 12);
            var rawSize = ReadUInt32(table, at + 16);
            var rawPointer = ReadUInt32(table, at + 20);
            if (rva >= virtualAddress && rva < virtualAddress + rawSize)
            {
                return (long)rawPointer + (rva - virtualAddress);
            }
        }
        return -1;
    }

    private static uint? ReadUInt32(Stream stream, long offset)
    {
        var block = ReadBlock(stream, offset, 4);
        return block.Length < 4 ? null : ReadUInt32(block, 0);
    }

    private static byte[] ReadBlock(Stream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length)
        {
            return [];
        }
        var block = new byte[Math.Min(count, stream.Length - offset)];
        stream.Position = offset;
        var read = stream.Read(block, 0, block.Length);
        if (read == block.Length)
        {
            return block;
        }
        var trimmed = new byte[read];
        Array.Copy(block, trimmed, read);
        return trimmed;
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
               (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
}

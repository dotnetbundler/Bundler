namespace DotNet.Bundler.MacApp;

internal static class MachO
{
    // MH_MAGIC / MH_MAGIC_64 (little-endian on disk) and FAT_MAGIC / FAT_CIGAM (big-endian).
    private static readonly byte[][] Signatures =
    [
        [0xCF, 0xFA, 0xED, 0xFE],
        [0xCE, 0xFA, 0xED, 0xFE],
        [0xCA, 0xFE, 0xBA, 0xBE],
        [0xBE, 0xBA, 0xFE, 0xCA]
    ];

    internal static bool IsMachO(string path)
    {
        var header = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return false;
            }
        }
        foreach (var signature in Signatures)
        {
            if (header[0] == signature[0] && header[1] == signature[1] &&
                header[2] == signature[2] && header[3] == signature[3])
            {
                return true;
            }
        }
        return false;
    }
}

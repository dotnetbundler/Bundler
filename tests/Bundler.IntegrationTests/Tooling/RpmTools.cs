// 宿主 rpm/rpm2cpio/cpio 缺一即对应腿 Skip（脚本可选工具口径）。
internal static class RpmTools
{
    public static bool HasRpm => ExternalTools.Has("rpm");
    public static bool HasCpio => ExternalTools.Has("rpm2cpio") && ExternalTools.Has("cpio");

    public static void RequireRpm() => ExternalTools.Require("rpm");
    public static void RequireCpio()
    {
        ExternalTools.Require("rpm2cpio");
        ExternalTools.Require("cpio");
    }

    // rpm -qp --qf %{TAG}
    public static string Field(string rpmPath, string tag)
    {
        var result = ProcessRunner.Run("rpm", ["-qp", "--qf", $"%{{{tag}}}", rpmPath]);
        ProcessRunner.AssertSuccess(result, $"rpm query {tag} failed on {rpmPath}");
        return result.StdOut.Trim();
    }

    public static string Query(string rpmPath, params string[] args)
    {
        var result = ProcessRunner.Run("rpm", [.. args, rpmPath]);
        ProcessRunner.AssertSuccess(result, $"rpm {string.Join(' ', args)} failed on {rpmPath}");
        return result.StdOut;
    }

    public static string Info(string rpmPath) => Query(rpmPath, "-qip");
    public static string ListFiles(string rpmPath) => Query(rpmPath, "-qpl");
    public static string ListFilesVerbose(string rpmPath) => Query(rpmPath, "-qplv");
    public static string Scripts(string rpmPath) => Query(rpmPath, "-qp", "--scripts");

    // rpm2cpio | cpio -idm 解到目标目录；GNU cpio 不为符号链接预建父目录，
    // 先按 -qplv 清单里的 l 行预建（脚本同款处理）。
    /// 无 rpm 工具链环境也可用的结构断言：签名 header 段是否携带 RPMSIGTAG_RSA(268)。
    /// 布局：96B lead → 4B magic(8e ad e8) + 1B ver + 4B reserved + 4B nindex + 4B hsize → nindex×16B 条目(tag,type,offset,count)。
    public static bool HasRsaSignatureTag(string rpmPath)
    {
        var bytes = File.ReadAllBytes(rpmPath);
        if (bytes.Length < 96 + 16)
        {
            return false;
        }
        var o = 96;
        if (bytes[o] != 0x8e || bytes[o + 1] != 0xad || bytes[o + 2] != 0xe8)
        {
            return false;
        }
        var nindex = ReadBe32(bytes, o + 8);
        var entries = o + 16;
        for (var i = 0; i < nindex; i++)
        {
            if (ReadBe32(bytes, entries + i * 16) == 268)
            {
                return true;
            }
        }
        return false;
    }

    private static int ReadBe32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    public static void ExtractPayload(string rpmPath, string destinationDir)
    {
        RequireCpio();
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }
        Directory.CreateDirectory(destinationDir);
        foreach (var line in ListFilesVerbose(rpmPath).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && line.StartsWith('l'))
            {
                var linkPath = parts[^3];
                Directory.CreateDirectory(Path.Combine(destinationDir,
                    Path.GetDirectoryName(linkPath)!.TrimStart('/')));
            }
        }
        var extract = ProcessRunner.Run("/bin/sh",
            ["-c", "cd \"$1\" && rpm2cpio \"$2\" | cpio -idm --quiet --no-absolute-filenames",
             "sh", destinationDir, rpmPath]);
        ProcessRunner.AssertSuccess(extract, $"rpm payload extraction failed on {rpmPath}");
    }
}

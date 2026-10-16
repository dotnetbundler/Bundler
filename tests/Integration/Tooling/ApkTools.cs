// .apk 工具：gzip 多成员逐段拆分与 tar 成员读取。
// GZipStream 会把拼接成员读成一条流，所以拆分沿用脚本的做法：
// 用 python3 zlib 的 unused_data 逐成员切分（独立实现的交叉验证），
// tar 成员读取走系统 tar（apk 测试宿主必备工具）。
internal static class ApkTools
{
    // 拆分 .apk 为 memberN.gz + memberN.tar；返回成员数。
    public static int SplitMembers(string apkPath, string destinationDir)
    {
        ExternalTools.Require("python3");
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }
        Directory.CreateDirectory(destinationDir);
        const string script = """
            import sys, zlib
            data = open(sys.argv[1], "rb").read()
            dest = sys.argv[2]
            offset = 0
            count = 0
            while offset < len(data):
                d = zlib.decompressobj(31)
                payload = d.decompress(data[offset:])
                consumed = len(data) - offset - len(d.unused_data)
                if consumed <= 0:
                    break
                open(f"{dest}/member{count}.gz", "wb").write(data[offset:offset + consumed])
                open(f"{dest}/member{count}.tar", "wb").write(payload)
                offset += consumed
                count += 1
            print(count)
            """;
        var result = ProcessRunner.Run("python3", ["-c", script, apkPath, destinationDir]);
        ProcessRunner.AssertSuccess(result, $"gzip member split failed on {apkPath}");
        return int.Parse(result.StdOut.Trim());
    }

    public static string TarMemberText(string tarPath, string member)
    {
        var result = ProcessRunner.Run("tar", ["-xOf", tarPath, member]);
        ProcessRunner.AssertSuccess(result, $"tar member '{member}' missing from {tarPath}");
        return result.StdOut;
    }

    public static string TarMemberBytesToFile(string tarPath, string member, string destination)
    {
        var result = ProcessRunner.Run("/bin/sh",
            ["-c", "tar -xOf \"$1\" \"$2\" > \"$3\"",
             "sh", tarPath, member, destination]);
        ProcessRunner.AssertSuccess(result, $"tar member '{member}' missing from {tarPath}");
        return destination;
    }

    public static ProcessRunner.Result TarListVerbose(string tarPath)
    {
        var result = ProcessRunner.Run("tar", ["-tvf", tarPath]);
        ProcessRunner.AssertSuccess(result, $"tar -tvf failed on {tarPath}");
        return result;
    }

    public static ProcessRunner.Result TarListNames(string tarPath)
    {
        var result = ProcessRunner.Run("tar", ["-tf", tarPath]);
        ProcessRunner.AssertSuccess(result, $"tar -tf failed on {tarPath}");
        return result;
    }

    // 末 1024 字节是否存在非零内容（控制 tar 无尾部零块 vs 数据 tar 有尾部零块）。
    public static bool HasNonzeroTail(string tarPath, int tailBytes = 1024)
    {
        var bytes = File.ReadAllBytes(tarPath);
        var start = Math.Max(0, bytes.Length - tailBytes);
        return bytes[start..].Any(b => b != 0);
    }

    // 校验 .PKGINFO 逐行精确包含期望字段（grep -qxF 等价）。
    public static void AssertPkgInfoFields(string pkgInfo, params string[] fields)
    {
        var lines = pkgInfo.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        foreach (var field in fields)
        {
            Assert.True(lines.Contains(field),
                $".PKGINFO lacks '{field}':\n{pkgInfo}");
        }
    }

    public static string? PkgInfoValue(string pkgInfo, string key)
        => pkgInfo.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(key + " = ", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 3)..].Trim())
            .FirstOrDefault();

    public static string Sha256Hex(string path)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}

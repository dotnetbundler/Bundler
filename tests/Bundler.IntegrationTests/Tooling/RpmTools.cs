// .rpm 工具：rpm 查询字段、rpm2cpio|cpio 载荷解包。
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
            ["-c", $"cd '{destinationDir}' && rpm2cpio '{rpmPath}' | cpio -idm --quiet --no-absolute-filenames"]);
        ProcessRunner.AssertSuccess(extract, $"rpm payload extraction failed on {rpmPath}");
    }
}

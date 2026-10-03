// .deb 工具：ar 成员、control/data 解包、dpkg-deb 元数据读取。
// 全部走系统工具（ar/dpkg-deb/tar）——这些就是验收的实现本身，
// 与脚本时代用同一批断言载体。
internal static class DebTools
{
    public static string ArList(string debPath)
    {
        var result = ProcessRunner.Run("ar", ["t", debPath]);
        ProcessRunner.AssertSuccess(result, $"ar t failed on {debPath}");
        return result.StdOut;
    }

    public static void ExtractControl(string debPath, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }
        Directory.CreateDirectory(destinationDir);
        var result = ProcessRunner.Run("dpkg-deb", ["-e", debPath, destinationDir]);
        ProcessRunner.AssertSuccess(result, $"dpkg-deb -e failed on {debPath}");
    }

    public static void ExtractData(string debPath, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }
        Directory.CreateDirectory(destinationDir);
        var result = ProcessRunner.Run("/bin/sh",
            ["-c", $"dpkg-deb --fsys-tarfile '{debPath}' | tar -xf - -C '{destinationDir}'"]);
        ProcessRunner.AssertSuccess(result, $"dpkg-deb data extraction failed on {debPath}");
    }

    public static string Info(string debPath)
    {
        var result = ProcessRunner.Run("dpkg-deb", ["-I", debPath]);
        ProcessRunner.AssertSuccess(result, $"dpkg-deb -I rejects {debPath}");
        return result.StdOut;
    }

    public static string ListContents(string debPath)
    {
        var result = ProcessRunner.Run("dpkg-deb", ["-c", debPath]);
        ProcessRunner.AssertSuccess(result, $"dpkg-deb -c failed on {debPath}");
        return result.StdOut;
    }

    public static string Field(string debPath, string field)
    {
        var result = ProcessRunner.Run("dpkg-deb", ["-f", debPath, field]);
        ProcessRunner.AssertSuccess(result, $"dpkg-deb -f {field} failed on {debPath}");
        return result.StdOut.Trim();
    }
}

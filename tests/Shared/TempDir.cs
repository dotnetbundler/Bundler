namespace Bundler.TestShared;

/// <summary>
/// 临时目录壳：Create 即建、Dispose 递归删。
/// 测试类以 _temps 收集或 _root 持有，保证 dotnet test 每轮不在 TEMP 留痕。
/// </summary>
public sealed class TempDir : IDisposable
{
    private TempDir(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    /// <summary>目录绝对路径。</summary>
    public string Path { get; }

    /// <summary>同路径的 <see cref="DirectoryInfo"/> 视图（兼容 CreateTempSubdirectory 旧用法）。</summary>
    public DirectoryInfo Info => new(Path);

    /// <summary>在系统 TEMP 下创建 prefix-随机名 目录。</summary>
    public static TempDir Create(string prefix = "bundler-test") =>
        new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"));

    /// <summary>在本目录下再开一个子目录（同样受本壳清理）。</summary>
    public string Sub(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 兜底：句柄晚释放时忽略——测试目录泄漏优于测试失败。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

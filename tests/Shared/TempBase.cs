namespace Bundler.TestShared;

/// <summary>
/// 临时目录收集基类：测试类继承后，<see cref="Temp"/>/<see cref="TempPath"/>
/// 开出的目录与散文件统一在 Dispose 递归删除——dotnet test 每轮不在 TEMP 留痕。
/// </summary>
public abstract class TempBase : IDisposable
{
    private readonly List<TempDir> _temps = [];
    private TempDir? _root;

    /// <summary>本测试用例的根目录（惰性开）。散文件放这里。</summary>
    protected string Root => (_root ??= TempDir.Create(GetType().Name.ToLowerInvariant())).Path;

    /// <summary>开独立子目录，等价原 Directory.CreateTempSubdirectory 语义。</summary>
    protected DirectoryInfo Temp(string prefix)
    {
        var dir = TempDir.Create(prefix);
        _temps.Add(dir);
        return dir.Info;
    }

    /// <summary>Root 下的散文件/子路径（不预建）。</summary>
    protected string TempPath(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            dir.Dispose();
        }
        _root?.Dispose();
        GC.SuppressFinalize(this);
    }
}

using DotNet.Bundler;

namespace Bundler.TestShared;

/// <summary>Api/Integration 共享的合法 bundle 构造器。</summary>
public static class BundleFixture
{
    /// <summary>
    /// 最小合法 bundle：<paramref name="root"/>/input 放主程序+readme，out 为输出根。
    /// </summary>
    public static BundleConfiguration Minimal(
        string root, string target, PackageFormat format,
        string productName = "Sample", string version = "1.2.3",
        string? mainExecutable = null,
        OutputLayout layout = OutputLayout.Flat,
        UpdateBundleConfiguration? update = null)
    {
        var input = Path.Combine(root, "input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        var exe = mainExecutable
            ?? (target.StartsWith("windows", StringComparison.Ordinal) ? "Sample.exe" : "sample");
        File.WriteAllText(Path.Combine(input, exe), "stub-binary");
        File.WriteAllText(Path.Combine(input, "readme.txt"), "doc\n");
        return new BundleConfiguration
        {
            ProductName = productName,
            Identifier = "com.example.sample",
            Version = version,
            Publisher = "Example",
            OutputDirectory = Path.Combine(root, "out"),
            OutputLayout = layout,
            Update = update,
            Targets = [new BundleTargetConfiguration
            {
                Target = target,
                InputDirectory = input,
                MainExecutable = exe,
                Formats = [format],
            }],
        };
    }

    /// <summary>包头 magic 断言。</summary>
    public static byte[] Magic(string path, int length)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[length];
        Assert.Equal(length, stream.Read(buffer, 0, length));
        return buffer;
    }
}

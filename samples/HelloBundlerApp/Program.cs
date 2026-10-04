var launchArguments = args.Length == 0 ? ["（没有启动参数）"] : args;
var launchKind = args.FirstOrDefault() switch
{
    string value when value.StartsWith("hello", StringComparison.OrdinalIgnoreCase) && value.Contains(':') => "深链接",
    string value when Path.GetExtension(value).Contains("hello", StringComparison.OrdinalIgnoreCase) => "关联文件",
    _ => "普通启动"
};

var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "com.example.hellobundlerapp");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "last-launch.txt");
var lines = new List<string>
{
    $"启动方式：{launchKind}",
    $"启动时间：{DateTimeOffset.Now:O}",
    "启动参数："
};
lines.AddRange(launchArguments.Select((argument, index) => $"  [{index}] {argument}"));

// 打包资源检查：覆盖各格式载荷路径（Windows 安装根 / Linux install-root / macOS Contents）。
var resourceChecks = new (string Label, string Path)[]
{
    ("演示资源（Windows NSIS/MSI）", Path.Combine(AppContext.BaseDirectory, "演示资源", "说明.txt")),
    ("DemoResources（Windows MSI）", Path.Combine(AppContext.BaseDirectory, "DemoResources", "Readme.txt")),
    ("docs（Linux/macOS Resources）", Path.Combine(AppContext.BaseDirectory, "docs", "readme.txt")),
    ("macOS Contents Resources", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "docs", "readme.txt"))),
    ("macOS SharedSupport", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SharedSupport", "shared-note.txt"))),
    ("macOS Frameworks", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libhello.dylib"))),
};
lines.Add("打包资源：");
foreach (var (label, path) in resourceChecks)
{
    lines.Add($"  {label}: {File.Exists(path)} ({path})");
}
File.WriteAllLines(logPath, lines);

Console.WriteLine("Hello from DotNet.Bundler!");
Console.WriteLine();
foreach (var line in lines)
{
    Console.WriteLine(line);
}
Console.WriteLine();
Console.WriteLine($"本次启动信息已写入：{logPath}");

Console.WriteLine();
Console.WriteLine("安装生命周期 Hook 标记：");
foreach (var hook in new[] { "preinstall", "postinstall" })
{
    var marker = Path.Combine(Path.GetTempPath(), $"HelloBundlerApp-hook-{hook}.txt");
    Console.WriteLine($"  {hook}: {File.Exists(marker)} ({marker})");
}
Console.WriteLine("按任意键退出……");
if (!Console.IsInputRedirected)
{
    Console.ReadKey(intercept: true);
}

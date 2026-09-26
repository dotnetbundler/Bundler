var launchArguments = args.Length == 0 ? ["（没有启动参数）"] : args;
var launchKind = args.FirstOrDefault() switch
{
    string value when value.StartsWith("hellomac:", StringComparison.OrdinalIgnoreCase) => "URL scheme",
    string value when Path.GetExtension(value).Equals(".hellomac", StringComparison.OrdinalIgnoreCase) => "关联文件",
    _ => "普通启动"
};

var macOsDirectory = AppContext.BaseDirectory;
var contentsDirectory = Path.GetFullPath(Path.Combine(macOsDirectory, ".."));
var bundledResource = Path.Combine(contentsDirectory, "Resources", "docs", "readme.txt");
var sharedContent = Path.Combine(contentsDirectory, "SharedSupport", "shared-note.txt");
var bundledFramework = Path.Combine(contentsDirectory, "Frameworks", "libhello.dylib");

var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "com.example.hellomacapp");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "last-launch.txt");
var lines = new List<string>
{
    $"启动方式：{launchKind}",
    $"启动时间：{DateTimeOffset.Now:O}",
    "启动参数："
};
lines.AddRange(launchArguments.Select((argument, index) => $"  [{index}] {argument}"));
lines.Add($"打包资源存在：{File.Exists(bundledResource)} ({bundledResource})");
lines.Add($"显式内容映射存在：{File.Exists(sharedContent)} ({sharedContent})");
lines.Add($"Framework 载荷存在：{File.Exists(bundledFramework)} ({bundledFramework})");
File.WriteAllLines(logPath, lines);

Console.WriteLine("Hello from DotNet.Bundler on macOS!");
Console.WriteLine();
foreach (var line in lines)
{
    Console.WriteLine(line);
}
Console.WriteLine();
Console.WriteLine($"本次启动信息已写入：{logPath}");

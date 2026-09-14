var launchArguments = args.Length == 0 ? ["（没有启动参数）"] : args;
var launchKind = args.FirstOrDefault() switch
{
    string value when value.StartsWith("hello-bundled:", StringComparison.OrdinalIgnoreCase) => "深链接",
    string value when Path.GetExtension(value).Equals(".hello", StringComparison.OrdinalIgnoreCase) => "关联文件",
    _ => "普通启动"
};

var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "com.example.hellobundledapp");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "last-launch.txt");
var lines = new List<string>
{
    $"启动方式：{launchKind}",
    $"启动时间：{DateTimeOffset.Now:O}",
    "启动参数："
};
lines.AddRange(launchArguments.Select((argument, index) => $"  [{index}] {argument}"));
File.WriteAllLines(logPath, lines);

Console.WriteLine("Hello from DotNet.Bundler!");
Console.WriteLine();
foreach (var line in lines)
{
    Console.WriteLine(line);
}
Console.WriteLine();
Console.WriteLine($"本次启动信息已写入：{logPath}");
var resourcePath = Path.Combine(AppContext.BaseDirectory, "演示资源", "说明.txt");
Console.WriteLine($"额外打包资源：{resourcePath}");
Console.WriteLine($"资源是否存在：{File.Exists(resourcePath)}");
Console.WriteLine();
Console.WriteLine("安装生命周期 Hook 标记：");
foreach (var hook in new[] { "preinstall", "postinstall" })
{
    var marker = Path.Combine(Path.GetTempPath(), $"HelloBundledApp-hook-{hook}.txt");
    Console.WriteLine($"  {hook}: {File.Exists(marker)} ({marker})");
}
Console.WriteLine("按任意键退出……");
if (!Console.IsInputRedirected)
{
    Console.ReadKey(intercept: true);
}

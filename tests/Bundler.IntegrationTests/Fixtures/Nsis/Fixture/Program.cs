if (args.Contains("--wait", StringComparer.Ordinal))
{
    Thread.Sleep(Timeout.Infinite);
}

var deepLink = args.FirstOrDefault(argument =>
    argument.StartsWith("bundlerfixture:", StringComparison.OrdinalIgnoreCase));
if (deepLink is not null)
{
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "DotNetBundler-deep-link.txt"), deepLink);
}

if (args.Contains("--protocol-marker", StringComparer.Ordinal))
{
    File.WriteAllLines(
        Path.Combine(Path.GetTempPath(), "DotNetBundler-command-line.txt"),
        args);
}

Console.WriteLine("DotNet.Bundler integration fixture");

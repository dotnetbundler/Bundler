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

Console.WriteLine("DotNet.Bundler integration fixture");

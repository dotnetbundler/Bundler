if (args.Contains("--wait", StringComparer.Ordinal))
{
    Thread.Sleep(Timeout.Infinite);
}

Console.WriteLine("DotNet.Bundler integration fixture");

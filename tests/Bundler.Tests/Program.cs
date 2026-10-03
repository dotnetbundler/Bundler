// 外部签名提供方 fixture 自调用入口：在 xUnit v3 runner 分流前处理
if (args.Length >= 5 && args[0] == "--external-sign-fixture")
{
    await File.AppendAllTextAsync(args[4], string.Join("|", args[1], args[2], args[3]) + Environment.NewLine);
    if (args.Length > 5)
    {
        Console.Error.WriteLine(args[5]);
        return 17;
    }
    return 0;
}
if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
    return await Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, Bundler.Tests.SelfRegisteredExtensions.AddSelfRegisteredExtensions);
return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);

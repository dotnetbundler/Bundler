if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
    return await Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(
        args, Bundler.IntegrationTests.SelfRegisteredExtensions.AddSelfRegisteredExtensions);
return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);

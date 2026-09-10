using System.Text.Json;
using System.Text.Json.Serialization;
using Bundler.Core.Configuration;
using Bundler.Core.Planning;
using Bundler.Core.Validation;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length != 2 || args[0] is not ("validate" or "plan"))
    {
        PrintUsage();
        return 2;
    }

    try
    {
        var configuration = await BundleConfigurationLoader.LoadAsync(args[1]);
        var checkFileSystem = args[0] == "validate";
        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem);
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
            {
                Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
            }

            return 1;
        }

        if (args[0] == "validate")
        {
            Console.WriteLine("Configuration is valid.");
            return 0;
        }

        var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
        Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        }));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  bundler validate <configuration.json>");
    Console.Error.WriteLine("  bundler plan <configuration.json>");
}

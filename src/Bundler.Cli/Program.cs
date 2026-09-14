using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Nsis;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        return args.FirstOrDefault() switch
        {
            "validate" when args.Length == 2 => await ValidateAsync(args[1]),
            "plan" when args.Length == 2 => await PlanAsync(args[1]),
            "bundle" => await BundleAsync(ParseOptions(args.Skip(1).ToArray())),
            _ => PrintUsage()
        };
    }
    catch (BundleValidationException exception)
    {
        PrintIssues(exception.Issues);
        return 1;
    }
    catch (Exception exception) when (
        exception is IOException or JsonException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> ValidateAsync(string configurationPath)
{
    var configuration = await BundleConfigurationLoader.LoadAsync(configurationPath);
    var issues = BundleConfigurationValidator.Validate(configuration);
    if (issues.Count > 0)
    {
        PrintIssues(issues);
        return 1;
    }

    Console.WriteLine("Configuration is valid.");
    return 0;
}

static async Task<int> PlanAsync(string configurationPath)
{
    var configuration = await BundleConfigurationLoader.LoadAsync(configurationPath);
    var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
    Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    }));
    return 0;
}

static async Task<int> BundleAsync(IReadOnlyDictionary<string, string> options)
{
    string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Option --{name} is required.");

    var formatName = Required("format");
    if (!Enum.TryParse<PackageFormat>(formatName, ignoreCase: true, out var format) || format != PackageFormat.Nsis)
    {
        throw new NotSupportedException("The first Bundler release supports --format nsis only.");
    }

    var configuration = new BundleConfiguration
    {
        ProductName = Required("product-name"),
        Identifier = Required("identifier"),
        Version = Required("version"),
        Publisher = options.GetValueOrDefault("publisher"),
        OutputDirectory = Path.GetFullPath(Required("output")),
        Targets =
        [
            new BundleTargetConfiguration
            {
                RuntimeIdentifier = Required("rid"),
                InputDirectory = Path.GetFullPath(Required("input")),
                MainExecutable = Required("main-executable"),
                Formats = [format]
            }
        ]
    };

    var allowDowngrades = options.TryGetValue("allow-downgrades", out var allowDowngradesValue)
        ? bool.Parse(allowDowngradesValue)
        : false;
    var bundler = new NsisBundler(
        new NsisBundleConfiguration { AllowDowngrades = allowDowngrades },
        options: new NsisBundlerOptions
        {
            ToolsetArchivePath = options.GetValueOrDefault("toolset-archive"),
            ToolCacheDirectory = options.GetValueOrDefault("tool-cache"),
            CompilerPath = options.GetValueOrDefault("compiler"),
            DataDirectory = options.GetValueOrDefault("nsis-data-dir"),
            TemplatePath = options.GetValueOrDefault("template"),
            LanguageDirectory = options.GetValueOrDefault("language-dir")
        });
    var artifacts = await bundler.BuildAsync(configuration);
    foreach (var artifact in artifacts)
    {
        Console.WriteLine($"Created {artifact.Path}");
    }

    return 0;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    if (arguments.Length % 2 != 0)
    {
        throw new ArgumentException("Bundle options must be provided as --name value pairs.");
    }

    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        var key = arguments[index];
        if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length == 2)
        {
            throw new ArgumentException($"Unexpected argument '{key}'.");
        }

        if (!options.TryAdd(key[2..], arguments[index + 1]))
        {
            throw new ArgumentException($"Option '{key}' was specified more than once.");
        }
    }

    return options;
}

static void PrintIssues(IEnumerable<ValidationIssue> issues)
{
    foreach (var issue in issues)
    {
        Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
    }
}

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  bundler validate <configuration.json>");
    Console.Error.WriteLine("  bundler plan <configuration.json>");
    Console.Error.WriteLine("  bundler bundle --name value [...]");
    return 2;
}

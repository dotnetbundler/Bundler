using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bundler.Core.Configuration;

public static class BundleConfigurationLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<BundleConfiguration> LoadAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        var fullPath = Path.GetFullPath(configurationPath);
        await using var stream = File.OpenRead(fullPath);
        var configuration = await JsonSerializer.DeserializeAsync<BundleConfiguration>(
            stream,
            Options,
            cancellationToken);

        if (configuration is null)
        {
            throw new InvalidDataException($"Configuration file '{fullPath}' is empty.");
        }

        return ResolvePaths(configuration, Path.GetDirectoryName(fullPath)!);
    }

    private static BundleConfiguration ResolvePaths(BundleConfiguration source, string baseDirectory)
    {
        static string Resolve(string baseDirectory, string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, baseDirectory);

        return new BundleConfiguration
        {
            ProductName = source.ProductName,
            Identifier = source.Identifier,
            Version = source.Version,
            Publisher = source.Publisher,
            Description = source.Description,
            OutputDirectory = Resolve(baseDirectory, source.OutputDirectory),
            Icons = source.Icons.Select(path => Resolve(baseDirectory, path)).ToArray(),
            Resources = source.Resources.Select(path => Resolve(baseDirectory, path)).ToArray(),
            Targets = source.Targets.Select(target => new BundleTargetConfiguration
            {
                RuntimeIdentifier = target.RuntimeIdentifier,
                InputDirectory = Resolve(baseDirectory, target.InputDirectory),
                MainExecutable = target.MainExecutable,
                Formats = target.Formats
            }).ToArray()
        };
    }
}

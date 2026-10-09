using System.Text.Json;
using System.Text.Json.Serialization;

using DotNet.Bundler;

namespace DotNet.Bundler.Core;

// bundler.json → BundleConfiguration 的 net10 加载入口（DeserializeAsync 等 net10 API）。
// 路径解析部分在 BundleConfigurationPaths，与 netstandard2.0 消费面共用。
internal static class BundleConfigurationLoader
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

        return BundleConfigurationPaths.Resolve(configuration, Path.GetDirectoryName(fullPath)!);
    }
}

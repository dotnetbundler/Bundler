using System.Text.Json;
using System.Text.Json.Serialization;

using DotNet.Bundler;

namespace DotNet.Bundler.Core;

// bundler.json → BundleConfiguration 的测试侧加载器：src 产物只保留 CLI 文档管线
// 一个装载面（CliConfig），不再内嵌仅服务测试的第二装载器。
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

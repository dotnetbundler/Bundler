using System.Text.Json;
using System.Text.Json.Serialization;

using DotNet.Bundler;

namespace DotNet.Bundler.Core;

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
            Homepage = source.Homepage,
            Copyright = source.Copyright,
            LicenseFile = source.LicenseFile is null ? null : Resolve(baseDirectory, source.LicenseFile),
            OutputDirectory = Resolve(baseDirectory, source.OutputDirectory),
            Icons = source.Icons.Select(path => Resolve(baseDirectory, path)).ToArray(),
            Resources = source.Resources.Select(resource => new BundleResourceConfiguration
            {
                Source = Resolve(baseDirectory, resource.Source),
                TargetPath = resource.TargetPath
            }).ToArray(),
            FileAssociations = source.FileAssociations.Select(association => new BundleFileAssociationConfiguration
            {
                Extensions = association.Extensions.ToArray(),
                Name = association.Name,
                Description = association.Description,
                MimeType = association.MimeType
            }).ToArray(),
            UrlProtocols = source.UrlProtocols.Select(protocol => new BundleUrlProtocolConfiguration
            {
                Schemes = protocol.Schemes.ToArray(),
                Name = protocol.Name
            }).ToArray(),
            Targets = source.Targets.Select(target => new BundleTargetConfiguration
            {
                RuntimeIdentifier = target.RuntimeIdentifier,
                InputDirectory = Resolve(baseDirectory, target.InputDirectory),
                MainExecutable = target.MainExecutable,
                SigningFiles = target.SigningFiles.ToArray(),
                Formats = target.Formats
            }).ToArray(),
            // update 节必须随加载走：签名密钥与引导件目录同样按配置目录解析。
            Update = source.Update is null ? null : new UpdateBundleConfiguration
            {
                FeedUrl = source.Update.FeedUrl,
                Channel = source.Update.Channel,
                SigningKeyFile = source.Update.SigningKeyFile is null
                    ? null : Resolve(baseDirectory, source.Update.SigningKeyFile),
                PublicKey = source.Update.PublicKey,
                Notes = source.Update.Notes,
                BootstrapperDirectory = source.Update.BootstrapperDirectory is null
                    ? null : Resolve(baseDirectory, source.Update.BootstrapperDirectory),
            }
        };
    }
}

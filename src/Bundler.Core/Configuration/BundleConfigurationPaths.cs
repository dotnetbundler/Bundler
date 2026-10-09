using DotNet.Bundler;

namespace DotNet.Bundler.Core;

// 配置内路径解析的统一出口：CLI 文档管线与 net10 LoadAsync 共用，
// 新增共享路径字段只需在这里登记一次。netstandard2.0 安全（只用 Path API）。
internal static class BundleConfigurationPaths
{
    internal static BundleConfiguration Resolve(BundleConfiguration source, string baseDirectory)
    {
        static string Resolve(string baseDirectory, string path) =>
            string.IsNullOrWhiteSpace(path) ? path
                : Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(baseDirectory, path));

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

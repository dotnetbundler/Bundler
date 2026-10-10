using DotNet.Bundler;

namespace DotNet.Bundler.Core;

// 配置内路径解析的统一出口：CLI 文档管线与 net10 LoadAsync 共用，
// 新增共享路径字段只需在这里登记一次。netstandard2.0 安全（只用 Path API）。
internal static class BundleConfigurationPaths
{
    // 共享字段拷贝的唯一出口：CLI 单格式配置/输入目录合并与 Core 路径解析
    // 都走这里——新增共享字段只需在本函数登记一次，不再逐处手拷（Update 曾因此被丢）。
    internal static BundleConfiguration WithTargets(
        BundleConfiguration source,
        IReadOnlyList<BundleTargetConfiguration> targets) => new()
    {
        ProductName = source.ProductName,
        Identifier = source.Identifier,
        Version = source.Version,
        Publisher = source.Publisher,
        Description = source.Description,
        Homepage = source.Homepage,
        Copyright = source.Copyright,
        LicenseFile = source.LicenseFile,
        OutputDirectory = source.OutputDirectory,
        Icons = source.Icons,
        Resources = source.Resources,
        FileAssociations = source.FileAssociations,
        UrlProtocols = source.UrlProtocols,
        Update = source.Update,
        Targets = targets
    };

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
            // 显式 null 元素按"未写"剥掉——与顶层 null 键同语义，防 [null] 数组 NRE。
            Icons = source.Icons?.Where(path => path is not null)
                .Select(path => Resolve(baseDirectory, path)).ToArray() ?? [],
            Resources = source.Resources?.Where(resource => resource is not null)
                .Select(resource => new BundleResourceConfiguration
                {
                    Source = Resolve(baseDirectory, resource.Source),
                    Destination = resource.Destination
                }).ToArray() ?? [],
            FileAssociations = source.FileAssociations?.Where(association => association is not null)
                .Select(association => new BundleFileAssociationConfiguration
                {
                    Extensions = association.Extensions?.Where(e => e is not null).ToArray() ?? [],
                    Name = association.Name,
                    Description = association.Description,
                    MimeType = association.MimeType
                }).ToArray() ?? [],
            UrlProtocols = source.UrlProtocols?.Where(protocol => protocol is not null)
                .Select(protocol => new BundleUrlProtocolConfiguration
                {
                    Schemes = protocol.Schemes?.Where(s => s is not null).ToArray() ?? [],
                    Name = protocol.Name
                }).ToArray() ?? [],
            Targets = source.Targets?.Where(target => target is not null)
                .Select(target => new BundleTargetConfiguration
                {
                    RuntimeIdentifier = target.RuntimeIdentifier,
                    InputDirectory = Resolve(baseDirectory, target.InputDirectory),
                    MainExecutable = target.MainExecutable,
                    SigningFiles = target.SigningFiles?.Where(s => s is not null).ToArray() ?? [],
                    Formats = target.Formats ?? []
                }).ToArray() ?? [],
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

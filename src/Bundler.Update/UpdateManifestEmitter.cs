using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Update;

/// <summary>
/// 更新清单发射器：对自更新适配格式的产物逐个产 `.sig` 旁车（ECDSA P-1363 64B），
/// 并聚合成一份 `bundler-update-feed.{channel}.json`——静态托管即可消费（Tauri latest.json 式）。
/// </summary>
public static class UpdateManifestEmitter
{
    public const string FeedNamePrefix = "bundler-update-feed";

    // 自更新适配格式：deb/rpm/apk 属包管理器领地，dmg/pkg 非更新载体——逐字决策见 update-roadmap §1.1。
    private static readonly HashSet<PackageFormat> CoveredFormats =
    [
        PackageFormat.Nsis, PackageFormat.Msi, PackageFormat.App,
        PackageFormat.AppImage, PackageFormat.Zip, PackageFormat.TarGz,
    ];

    [DataContract]
    private sealed class FeedDocument
    {
        [DataMember(Name = "version")] public string Version = "";
        [DataMember(Name = "notes", EmitDefaultValue = false)] public string? Notes;
        [DataMember(Name = "publishedAt")] public string PublishedAt = "";
        [DataMember(Name = "channel")] public string Channel = "";
        [DataMember(Name = "artifacts")] public List<FeedArtifact> Artifacts = [];
    }

    [DataContract]
    private sealed class FeedArtifact
    {
        [DataMember(Name = "rid")] public string RuntimeIdentifier = "";
        [DataMember(Name = "format")] public string Format = "";
        [DataMember(Name = "url")] public string Url = "";
        [DataMember(Name = "file")] public string File = "";
        [DataMember(Name = "size")] public long Size;
        [DataMember(Name = "sha256")] public string Sha256 = "";
        [DataMember(Name = "sig")] public string Signature = "";
    }

    public static string FeedFileName(string channel) =>
        $"{FeedNamePrefix}.{channel}.json";

    /// <summary>
    /// 对已产出的构件发射更新面：`.sig` 旁车 + 通道清单。返回新增产物路径（sig 与清单）。
    /// 开启更新面必须带私钥文件——签名是强制信任根（Tauri 同型）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> EmitAsync(
        BundleConfiguration configuration,
        IReadOnlyList<BundleArtifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        var update = configuration.Update
            ?? throw new ArgumentException("Update configuration is missing.", nameof(configuration));
        if (update.SigningKeyFile is not { Length: > 0 } keyFile)
        {
            throw new InvalidOperationException(
                "BundlerUpdate requires a signing key file (BundlerUpdateSigningKeyFile) — update signatures are mandatory.");
        }
        var key = UpdateKeyMaterial.Load(keyFile);
        var channel = string.IsNullOrWhiteSpace(update.Channel) ? "latest" : update.Channel;
        var feed = new FeedDocument
        {
            Version = configuration.Version,
            Notes = string.IsNullOrWhiteSpace(update.Notes) ? null : update.Notes,
            PublishedAt = DateTimeOffset.UtcNow.ToString("O"),
            Channel = channel,
        };
        var produced = new List<string>();
        var feedBase = update.FeedUrl.TrimEnd('/');

        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CoveredFormats.Contains(artifact.Format) || !File.Exists(artifact.Path))
            {
                continue;
            }
            var signature = await Task.Run(
                () => EcdsaSigner.SignFile(artifact.Path, key), cancellationToken);
            var sigPath = artifact.Path + ".sig";
            File.WriteAllBytes(sigPath, signature);
            produced.Add(sigPath);

            var fileName = Path.GetFileName(artifact.Path);
            feed.Artifacts.Add(new FeedArtifact
            {
                RuntimeIdentifier = artifact.RuntimeIdentifier,
                Format = artifact.Format.ToString().ToLowerInvariant(),
                Url = feedBase.Length == 0 ? fileName : feedBase + "/" + fileName,
                File = fileName,
                Size = new FileInfo(artifact.Path).Length,
                Sha256 = await Task.Run(() => Sha256Hex(artifact.Path), cancellationToken),
                Signature = Convert.ToBase64String(signature),
            });
        }

        if (feed.Artifacts.Count == 0)
        {
            return produced;
        }
        var feedPath = Path.Combine(configuration.OutputDirectory, FeedFileName(channel));
        using (var stream = File.Create(feedPath))
        {
            var serializer = new DataContractJsonSerializer(typeof(FeedDocument));
            serializer.WriteObject(stream, feed);
            stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
        }
        produced.Add(feedPath);
        return produced;
    }

    private static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}

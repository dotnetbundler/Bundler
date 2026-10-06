using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Update;

/// <summary>
/// 更新清单发射器：对自更新适配格式的产物逐个产 `.sig` 旁车（ECDSA P-1363 64B），
/// 并聚合成一份 `bundler-update-feed.{channel}.json`——静态托管即可消费（Tauri latest.json 式）。
/// </summary>
public static class UpdateManifestEmitter
{
    public const string FeedNamePrefix = UpdateFeed.FeedNamePrefix;

    // 自更新适配格式：deb/rpm/apk 属包管理器领地，dmg/pkg 非更新载体——逐字决策见 update-roadmap §1.1。
    private static readonly HashSet<PackageFormat> CoveredFormats =
    [
        PackageFormat.Nsis, PackageFormat.Msi, PackageFormat.App,
        PackageFormat.AppImage, PackageFormat.Zip, PackageFormat.TarGz,
    ];

    public static string FeedFileName(string channel) => UpdateFeed.FeedFileName(channel);

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
        var feed = new UpdateFeed
        {
            Version = configuration.Version,
            Notes = string.IsNullOrWhiteSpace(update.Notes) ? null : update.Notes,
            PublishedAt = DateTimeOffset.UtcNow.ToString("O"),
            Channel = channel,
        };
        var produced = new List<string>();
        // 产物 url = 相对清单所在目录（输出根）的路径——制品按 <rid>/<format>/ 分目录落盘；
        // file 恒为裸文件名，仅作下载落点文件名。

        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CoveredFormats.Contains(artifact.Format))
            {
                continue;
            }
            // .app 是目录件：用自家 ZipWriter 打成 <name>.app.zip 运输件（保 exec 位与软链），
            // 签名/块表/尺寸/哈希全部对运输件——客户端 format=app → 解包 → 单顶层 .app 换包。
            var artifactFile = artifact.Path;
            if (artifact.Format == PackageFormat.App && Directory.Exists(artifact.Path))
            {
                artifactFile = artifact.Path + ".zip";
                var entries = ArchiveTree.CollectDirectory(
                    artifact.Path, Path.GetFileName(artifact.Path), NullBundleLogger.Instance);
                using (var stream = new FileStream(
                           artifactFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ZipWriter.Write(stream, entries.Select(ArchiveTree.ToZipEntry));
                }
                produced.Add(artifactFile);
            }
            if (!File.Exists(artifactFile))
            {
                continue;
            }
            var signature = await Task.Run(
                () => EcdsaSigner.SignFile(artifactFile, key), cancellationToken);
            var sigPath = artifactFile + ".sig";
            File.WriteAllBytes(sigPath, signature);
            produced.Add(sigPath);

            // block-map 差分块表：定宽 64KiB + sha256 序列表，客户端按哈希匹配复用未变块。
            var blockMapPath = artifactFile + UpdateBlockMap.FileSuffix;
            var blockMap = await Task.Run(
                () => UpdateBlockMap.ComputeFile(artifactFile), cancellationToken);
            var serializer = new DataContractJsonSerializer(typeof(UpdateBlockMap));
            using (var stream = File.Create(blockMapPath))
            {
                serializer.WriteObject(stream, blockMap);
            }
            produced.Add(blockMapPath);

            var relativeUrl = RelativeUrl(configuration.OutputDirectory, artifactFile);
            var fileName = Path.GetFileName(artifactFile);
            feed.Artifacts.Add(new UpdateFeedArtifact
            {
                RuntimeIdentifier = artifact.RuntimeIdentifier,
                Format = artifact.Format.ToString().ToLowerInvariant(),
                Url = relativeUrl,
                File = fileName,
                Size = new FileInfo(artifactFile).Length,
                Sha256 = await Task.Run(() => Sha256Hex(artifactFile), cancellationToken),
                Signature = Convert.ToBase64String(signature),
                BlockMap = relativeUrl + UpdateBlockMap.FileSuffix,
            });
        }

        if (feed.Artifacts.Count == 0)
        {
            return produced;
        }
        var feedPath = Path.Combine(configuration.OutputDirectory, FeedFileName(channel));
        using (var stream = File.Create(feedPath))
        {
            var serializer = new DataContractJsonSerializer(typeof(UpdateFeed));
            serializer.WriteObject(stream, feed);
            stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
        }
        produced.Add(feedPath);
        // 清单同样签名：产物签名只绑字节，清单不签则换源可把旧签名件标成新版本（降级攻击）。
        var feedSignature = await Task.Run(
            () => EcdsaSigner.SignFile(feedPath, key), cancellationToken);
        var feedSigPath = feedPath + ".sig";
        File.WriteAllBytes(feedSigPath, feedSignature);
        produced.Add(feedSigPath);
        return produced;
    }

    // netstandard2.0 无 Path.GetRelativePath——URI 相对化产出 '/' 分隔的 url。
    // 必须保留转义形态：文件名含 '#'/'?'/空格时未转义字符会被 http 解析为
    // fragment/query 分隔符，产物地址直接解析错——OriginalString 即转义原文。
    private static string RelativeUrl(string feedDirectory, string artifactFile)
    {
        var feedUri = new Uri(
            Path.GetFullPath(feedDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar);
        return feedUri.MakeRelativeUri(new Uri(Path.GetFullPath(artifactFile))).OriginalString;
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

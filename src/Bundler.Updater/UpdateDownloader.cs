using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 更新清单与产物的获取面：http(s) 下载（`.part` 断点续传 + Range）与 `file://`/本地路径/UNC 解析，
/// 落盘后 sha256 必验——清单给什么就验什么，不猜。
/// </summary>
internal sealed class UpdateDownloader
{
    private static readonly HttpClient SharedHttp = new();

    internal async Task<UpdateFeed> FetchFeedAsync(string feedUrl, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(feedUrl, cancellationToken);
        using var stream = new MemoryStream(bytes);
        var serializer = new DataContractJsonSerializer(typeof(UpdateFeed));
        return serializer.ReadObject(stream) as UpdateFeed
            ?? throw new UpdateException($"update feed '{feedUrl}' is not a valid manifest.");
    }

    /// <summary>
    /// 产物地址解析：绝对 http(s)/file URI 直接用；相对地址对清单目录解析
    /// （file feed → 同目录文件；http feed → 同目录 URL）。
    /// </summary>
    internal static string ResolveArtifactLocation(string feedUrl, string artifactUrl)
    {
        if (Uri.TryCreate(artifactUrl, UriKind.Absolute, out var absolute) &&
            (absolute.IsFile || absolute.Scheme == Uri.UriSchemeHttp ||
             absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == "file"))
        {
            return artifactUrl;
        }
        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var feed) &&
            (feed.Scheme == Uri.UriSchemeHttp || feed.Scheme == Uri.UriSchemeHttps))
        {
            return new Uri(new Uri(feedUrl), artifactUrl).ToString();
        }
        // 本地清单：产物按清单目录相对路径解析。
        var feedPath = feedUrl.StartsWith("file://", StringComparison.Ordinal)
            ? new Uri(feedUrl).LocalPath
            : feedUrl;
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(feedPath) ?? ".", artifactUrl));
    }

    /// <summary>
    /// 下载/复制产物到 destinationPath 并验 sha256（声明了才验——清单缺哈希视为协议错误）。
    /// http(s) 支持 .part 续传；本地件直接复制。哈希不符即删档抛错，绝不放行。
    /// </summary>
    internal async Task DownloadAsync(
        string location, UpdateFeedArtifact artifact, string destinationPath,
        Action<string>? log, CancellationToken cancellationToken)
    {
        if (IsHttp(location))
        {
            await DownloadHttpAsync(location, destinationPath, log, cancellationToken);
        }
        else
        {
            var source = location.StartsWith("file://", StringComparison.Ordinal)
                ? new Uri(location).LocalPath
                : location;
            log?.Invoke($"update: copy '{source}' → '{destinationPath}'");
            File.Copy(source, destinationPath, overwrite: true);
        }

        if (artifact.Size > 0 && new FileInfo(destinationPath).Length != artifact.Size)
        {
            File.Delete(destinationPath);
            throw new UpdateException(
                $"downloaded artifact size mismatch: expected {artifact.Size} bytes.");
        }
        if (artifact.Sha256.Length == 0)
        {
            File.Delete(destinationPath);
            throw new UpdateException("manifest artifact carries no sha256 — refusing unverifiable payload.");
        }
        var actual = Sha256Hex(destinationPath);
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destinationPath);
            throw new UpdateException("downloaded artifact sha256 mismatch — payload refused.");
        }
        log?.Invoke($"update: sha256 verified ({actual.Substring(0, 12)}…)");
    }

    private static bool IsHttp(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // `.part` 续传：服务端给了 Accept-Ranges 就接着写；否则整档重下。
    private async Task DownloadHttpAsync(
        string url, string destinationPath, Action<string>? log, CancellationToken cancellationToken)
    {
        var partPath = destinationPath + ".part";
        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-");
        }
        using var response = await SharedHttp.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // 服务端不支持 Range 或文件已变（ETag/Last-Modified 失效）→ 重来。
            offset = 0;
        }
        response.EnsureSuccessStatusCode();

        var mode = offset > 0 ? FileMode.Append : FileMode.Create;
        using (var output = new FileStream(partPath, mode, FileAccess.Write, FileShare.None))
        {
            await (await response.Content.ReadAsStreamAsync())
                .CopyToAsync(output, 81920, cancellationToken);
        }
        File.Move(partPath, destinationPath);
        log?.Invoke($"update: downloaded '{url}' → '{destinationPath}'");
    }

    private async Task<byte[]> GetBytesAsync(string location, CancellationToken cancellationToken)
    {
        if (IsHttp(location))
        {
            return await SharedHttp.GetByteArrayAsync(location);
        }
        var path = location.StartsWith("file://", StringComparison.Ordinal)
            ? new Uri(location).LocalPath
            : location;
        return File.ReadAllBytes(path);
    }

    private static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var builder = new StringBuilder(64);
        foreach (var b in sha.ComputeHash(stream))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}

/// <summary>更新链任一环节的确定性失败（协议/校验/拒绝——不是环境异常）。</summary>
public sealed class UpdateException(string message) : Exception(message);

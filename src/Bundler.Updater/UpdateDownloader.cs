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

    /// <summary>
    /// 拉取并验签清单：`<feedUrl>.sig` 与清单同源，用安装身份公钥验过才解析——
    /// 清单本身不可信：签名只绑产物字节，不验清单则换源可把旧签名件标成新版本降级安装。
    /// 缺签名或验签失败一律拒绝。
    /// </summary>
    internal async Task<UpdateFeed> FetchFeedAsync(
        string feedUrl, string publicPointBase64, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(feedUrl, cancellationToken);
        byte[] signature;
        try
        {
            signature = await GetBytesAsync(SignatureUrl(feedUrl), cancellationToken);
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            throw new UpdateException(
                $"update feed signature '{SignatureUrl(feedUrl)}' is unreachable — refusing unsigned manifest.",
                exception);
        }
        if (!UpdateSignatureVerifier.Verify(
                bytes, signature, UpdateKeyMaterial.FromPublicPoint(publicPointBase64)))
        {
            throw new UpdateException(
                $"update feed '{feedUrl}' failed signature verification — refused.");
        }
        using var stream = new MemoryStream(bytes);
        var serializer = new DataContractJsonSerializer(typeof(UpdateFeed));
        return serializer.ReadObject(stream) as UpdateFeed
            ?? throw new UpdateException($"update feed '{feedUrl}' is not a valid manifest.");
    }

    /// <summary>
    /// 签名地址在 feed 的路径部分追加 ".sig"——query/fragment 留在尾部，
    /// "feed.json?v=1#x" → "feed.json.sig?v=1#x"，末位追加会被 http 解析错。
    /// </summary>
    internal static string SignatureUrl(string feedUrl)
    {
        var cut = feedUrl.Length;
        var query = feedUrl.IndexOf('?');
        var fragment = feedUrl.IndexOf('#');
        if (query >= 0)
        {
            cut = Math.Min(cut, query);
        }
        if (fragment >= 0)
        {
            cut = Math.Min(cut, fragment);
        }
        return string.Concat(feedUrl.Substring(0, cut), ".sig", feedUrl.Substring(cut));
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

    /// <summary>
    /// block-map 差分下载：新清单块表 vs 本地缓存旧件块表按哈希匹配——
    /// 命中块从旧件按偏移复制，缺失块合并为连续段走 HTTP Range（本地 feed 直接 seek 读）。
    /// 任一步异常返回 false，调用方回落全量下载；产出仍经 size+sha256 终验。
    /// </summary>
    internal async Task<bool> TryDownloadDeltaAsync(
        string artifactLocation, string blockMapLocation, UpdateFeedArtifact artifact,
        string sourcePath, string destinationPath,
        Action<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            var blockMap = await FetchBlockMapAsync(blockMapLocation, cancellationToken);
            if (blockMap.Version != 1 || blockMap.BlockSize <= 0 || blockMap.Hashes.Count == 0)
            {
                log?.Invoke("update: block-map malformed — falling back to full download");
                return false;
            }
            if (!File.Exists(sourcePath))
            {
                log?.Invoke("update: no delta source cached — full download");
                return false;
            }

            var oldMap = UpdateBlockMap.ComputeFile(sourcePath, blockMap.BlockSize);
            // 哈希→旧件偏移（首次出现）——按哈希匹配不依赖位置。
            var oldOffsets = new Dictionary<string, long>();
            for (var i = 0; i < oldMap.Hashes.Count; i++)
            {
                if (!oldOffsets.ContainsKey(oldMap.Hashes[i]))
                {
                    oldOffsets[oldMap.Hashes[i]] = (long)i * blockMap.BlockSize;
                }
            }

            // 计划：复制段与下载段顺序写出；同类相邻段合并（同文件的连续旧块合成一次拷贝，
            // 连续缺失块合成一次 Range）。
            var ops = new List<(bool copy, long srcOffset, long dstOffset, int length)>();
            for (var i = 0; i < blockMap.Hashes.Count; i++)
            {
                var dstOffset = (long)i * blockMap.BlockSize;
                var length = (int)Math.Min(blockMap.BlockSize, blockMap.FileSize - dstOffset);
                if (oldOffsets.TryGetValue(blockMap.Hashes[i], out var srcOffset))
                {
                    if (ops.Count > 0 && ops[ops.Count - 1].copy &&
                        ops[ops.Count - 1].srcOffset + ops[ops.Count - 1].length == srcOffset &&
                        ops[ops.Count - 1].dstOffset + ops[ops.Count - 1].length == dstOffset)
                    {
                        ops[ops.Count - 1] = (true, ops[ops.Count - 1].srcOffset, ops[ops.Count - 1].dstOffset,
                            ops[ops.Count - 1].length + length);
                    }
                    else
                    {
                        ops.Add((true, srcOffset, dstOffset, length));
                    }
                }
                else
                {
                    if (ops.Count > 0 && !ops[ops.Count - 1].copy &&
                        ops[ops.Count - 1].dstOffset + ops[ops.Count - 1].length == dstOffset)
                    {
                        ops[ops.Count - 1] = (false, 0, ops[ops.Count - 1].dstOffset, ops[ops.Count - 1].length + length);
                    }
                    else
                    {
                        ops.Add((false, 0, dstOffset, length));
                    }
                }
            }

            var downloaded = 0L;
            var copied = 0L;
            using (var source = new FileStream(
                       sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(
                       destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.SetLength(blockMap.FileSize);
                foreach (var op in ops)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Seek(op.dstOffset, SeekOrigin.Begin);
                    if (op.copy)
                    {
                        source.Seek(op.srcOffset, SeekOrigin.Begin);
                        await CopyExactlyAsync(source, output, op.length, cancellationToken);
                        copied += op.length;
                    }
                    else
                    {
                        await FetchRangeAsync(
                            artifactLocation, op.dstOffset, op.length, output, cancellationToken);
                        downloaded += op.length;
                    }
                }
            }
            log?.Invoke(
                $"update: delta applied — {copied} B reused, {downloaded} B fetched " +
                $"({blockMap.Hashes.Count} blocks)");

            if (new FileInfo(destinationPath).Length != blockMap.FileSize ||
                (artifact.Size > 0 && new FileInfo(destinationPath).Length != artifact.Size))
            {
                File.Delete(destinationPath);
                return false;
            }
            if (artifact.Sha256.Length == 0)
            {
                File.Delete(destinationPath);
                throw new UpdateException(
                    "manifest artifact carries no sha256 — refusing unverifiable payload.");
            }
            if (!string.Equals(Sha256Hex(destinationPath), artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("update: delta result sha256 mismatch — full download");
                File.Delete(destinationPath);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            log?.Invoke($"update: delta failed ({exception.Message}) — full download");
            TryDelete(destinationPath);
            return false;
        }
    }

    private async Task<UpdateBlockMap> FetchBlockMapAsync(
        string location, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(location, cancellationToken);
        using var stream = new MemoryStream(bytes);
        var serializer = new DataContractJsonSerializer(typeof(UpdateBlockMap));
        return serializer.ReadObject(stream) as UpdateBlockMap
            ?? throw new UpdateException($"block-map '{location}' is not valid.");
    }

    private async Task FetchRangeAsync(
        string location, long offset, int length,
        Stream output, CancellationToken cancellationToken)
    {
        if (IsHttp(location))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            request.Headers.TryAddWithoutValidation(
                "Range", $"bytes={offset}-{offset + length - 1}");
            using var response = await SharedHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // 服务端不认 Range 返回 200 整档——继续走会重复拉全量，直接抛回落全量路径。
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                throw new UpdateException(
                    $"server does not honor Range on '{location}' (HTTP {(int)response.StatusCode}).");
            }
            using var stream = await response.Content.ReadAsStreamAsync();
            await CopyExactlyAsync(stream, output, length, cancellationToken);
            return;
        }
        var path = location.StartsWith("file://", StringComparison.Ordinal)
            ? new Uri(location).LocalPath
            : location;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Seek(offset, SeekOrigin.Begin);
        await CopyExactlyAsync(input, output, length, cancellationToken);
    }

    private static async Task CopyExactlyAsync(
        Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(
                buffer, 0, (int)Math.Min(buffer.Length, remaining), cancellationToken);
            if (read == 0)
            {
                throw new UpdateException("delta source/range read ended early.");
            }
            await output.WriteAsync(buffer, 0, read, cancellationToken);
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
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
        // 目标已存在（同一更新重下/复用下载目录）也要能落——netstandard2.0 无 Move 覆写重载。
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
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
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }

    public UpdateException(string message, Exception inner) : base(message, inner)
    {
    }
}

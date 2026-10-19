using System.Net.Http;
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

    // 体读停滞上界（ms）：feed/产物 HTTP 读共用；env 覆盖是测试缝，生产恒 120s。
    internal static int BodyStallMilliseconds
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BUNDLER_UPDATER_BODY_STALL");
            return raw is { Length: > 0 } && int.TryParse(raw, out var ms) && ms > 0
                ? ms
                : 120_000;
        }
    }

    /// <summary>
    /// 拉取并验签清单：`<feedUrl>.sig` 与清单同源，用安装身份公钥验过才解析——
    /// 清单本身不可信：签名只绑产物字节，不验清单则换源可把旧签名件标成新版本降级安装。
    /// 缺签名或验签失败一律拒绝。
    /// </summary>
    internal async Task<UpdateFeed> FetchFeedAsync(
        string feedUrl, string publicPointBase64, CancellationToken cancellationToken)
    {
        // feed 本体与签名腿同一例外面：调用方契约"协议/校验/拒绝全部确定性抛
        // UpdateException"——裸 HttpRequestException/IOException 会把只捕
        // UpdateException 的调用方打穿。用户取消（ct 已请求）不算协议拒绝，原样
        // 穿透；HttpClient.Timeout 产生的 OCE 不带用户取消标记——超时归
        // UpdateException，不被误分类成"用户取消"静默漏出。
        byte[] bytes;
        try
        {
            bytes = await GetBytesAsync(feedUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            throw new UpdateException($"update feed '{feedUrl}' is unreachable.", exception);
        }
        byte[] signature;
        try
        {
            signature = await GetBytesAsync(SignatureUrl(feedUrl), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            throw new UpdateException(
                $"update feed signature '{SignatureUrl(feedUrl)}' is unreachable — refusing unsigned manifest.",
                exception);
        }
        UpdateKeyMaterial publicKey;
        try
        {
            publicKey = UpdateKeyMaterial.FromPublicPoint(publicPointBase64);
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            // 侧车公钥是协议输入——畸形 base64/非法点的 FormatException/
            // InvalidOperationException 也统一成 UpdateException，不破调用方契约。
            throw new UpdateException(
                "install identity public key is not a valid base64 P-256 point.", exception);
        }
        if (!UpdateSignatureVerifier.Verify(bytes, signature, publicKey))
        {
            throw new UpdateException(
                $"update feed '{feedUrl}' failed signature verification — refused.");
        }
        using var stream = new MemoryStream(bytes);
        UpdateFeed feed;
        try
        {
            feed = UpdateJson.ReadFeed(stream)
                ?? throw new UpdateException($"update feed '{feedUrl}' is not a valid manifest.");
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            // 畸形 JSON/编码错误统一包成 UpdateException——客户端契约"协议/校验/拒绝
            // 全部确定性抛 UpdateException"，漏 JsonException 会把调用方推向 catch-all。
            throw new UpdateException(
                $"update feed '{feedUrl}' is not a valid manifest.", exception);
        }
        // 畸形清单归一化先行：netstandard DCS 不跑字段初始化器——缺省键与显式
        // null 都产 null 字段（net10 STJ 侧显式 null 同样 null），消费面
        //（SelectArtifact 遍历/artifact.Url/Sha256.Length）裸 NRE 会打穿
        //"协议/校验/拒绝全归 UpdateException"的调用方契约。
        feed.Artifacts ??= new List<UpdateFeedArtifact>();
        // artifact.file 经 Path.Combine 拼本地下载名——词法必须是单段文件名：
        // 分隔符/../等会让签名清单把下载件写到下载目录之外（签名只保真不保意图，
        // 词法收口在客户端侧做）。url/sha256 同列必携——null/空在下游消费点
        //（ResolveArtifactLocation/Sha256.Length）同样以 NRE 形态漏出。
        foreach (var artifact in feed.Artifacts)
        {
            if (artifact is null || artifact.Url is not { Length: > 0 } ||
                artifact.Sha256 is not { Length: > 0 })
            {
                throw new UpdateException(
                    $"update feed '{feedUrl}' carries an artifact missing url/sha256.");
            }
            var file = artifact.File;
            if (file is not { Length: > 0 } || file is "." or ".." ||
                file.IndexOf('/') >= 0 || file.IndexOf('\\') >= 0)
            {
                throw new UpdateException(
                    $"update feed '{feedUrl}' carries artifact file '{file}' — " +
                    "not a plain single-segment file name.");
            }
        }
        return feed;
    }

    /// <summary>
    /// 签名地址在 feed 的路径部分追加 ".sig"。显式 URI（带 scheme）里 '#''?' 是
    /// 分隔符——文件名中的它们早已 %XX 转义，query/fragment 恒留尾
    /// （"feed.json?v=1" → "feed.json.sig?v=1"）；裸本地路径的 '#''?'
    /// 是合法文件名字符，恒定末位追加。
    /// </summary>
    internal static string SignatureUrl(string feedUrl)
    {
        var isExplicitUri = feedUrl.IndexOf("://", StringComparison.Ordinal) >= 0 ||
            feedUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (isExplicitUri && Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri);
            builder.Path += ".sig";
            return builder.Uri.ToString();
        }
        return feedUrl + ".sig";
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
        // 本地清单：产物按清单目录相对路径解析；url 字段以 URL 转义形式存储，还原真实文件名。
        var feedPath = feedUrl.StartsWith("file://", StringComparison.Ordinal)
            ? new Uri(feedUrl).LocalPath
            : feedUrl;
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(feedPath) ?? ".", Uri.UnescapeDataString(artifactUrl)));
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

        // 校验失败连同 `.part` 一并清——陈旧残件留存会让下轮按同偏移续传，
        // 服务端仍回 206 就再拼出同样的坏文件，永久循环。
        var partPath = destinationPath + ".part";
        if (artifact.Size > 0 && new FileInfo(destinationPath).Length != artifact.Size)
        {
            File.Delete(destinationPath);
            TryDelete(partPath);
            throw new UpdateException(
                $"downloaded artifact size mismatch: expected {artifact.Size} bytes.");
        }
        if (artifact.Sha256.Length == 0)
        {
            File.Delete(destinationPath);
            TryDelete(partPath);
            throw new UpdateException("manifest artifact carries no sha256 — refusing unverifiable payload.");
        }
        var actual = Sha256Hex(destinationPath);
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destinationPath);
            TryDelete(partPath);
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
            // 块表只是优化件——取不到/解析不了与块表畸形同待：回落全量
            // （成品 sha 由签名清单背书，块表不可信不升级成硬失败）。
            var blockMap = await FetchBlockMapAsync(blockMapLocation, cancellationToken);
            if (blockMap is null || blockMap.Version != 1 ||
                blockMap.BlockSize <= 0 || blockMap.Hashes.Count == 0)
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
            var fetchFailed = false;
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
                    try
                    {
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
                    catch (UpdateException exception)
                    {
                        log?.Invoke(
                            $"update: delta fetch failed ({exception.Message}) — full download");
                        fetchFailed = true;
                        break;
                    }
                }
            }
            if (fetchFailed)
            {
                TryDelete(destinationPath);
                return false;
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
            // 差分不消费 .part——成功落件后清掉陈旧残件，防它种子下轮坏续传。
            TryDelete(destinationPath + ".part");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not UpdateException)
        {
            // 非用户取消的 OCE（HttpClient.Timeout/非 ct 触发的取消）同归差分
            // 回落——超时态误当用户取消穿透会把调用方推向静默 catch(OCE)。
            log?.Invoke($"update: delta failed ({exception.Message}) — full download");
            TryDelete(destinationPath);
            return false;
        }
    }

    // 解析失败返回 null 走回落分支——不再以 UpdateException 穿透调用方的
    // "非 UpdateException 才回落"过滤器（差分链任一 UpdateException 本就该回落）。
    private async Task<UpdateBlockMap?> FetchBlockMapAsync(
        string location, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(location, cancellationToken);
        using var stream = new MemoryStream(bytes);
        return UpdateJson.ReadBlockMap(stream);
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

    // 体读有界读的共享腿：每块独立给停滞上界（CancelAfter 每趟重置）——
    // 挂起的响应体/Range 段不能拖死下载；用户取消（外层 ct）原样穿透，
    // 停滞超时归 UpdateException。差分腿与全量腿共用同一停滞语义。
    private static async Task<int> ReadBoundedAsync(
        Stream input, byte[] buffer, int count, CancellationTokenSource stall,
        CancellationToken cancellationToken, string what)
    {
        stall.CancelAfter(BodyStallMilliseconds);
        try
        {
            return await input.ReadAsync(buffer, 0, count, stall.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(
                $"{what} stalled — no bytes within {BodyStallMilliseconds / 1000}s.");
        }
    }

    private static async Task CopyExactlyAsync(
        Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        // Range/差分源读同样挂停滞界——HTTP 206 腿挂起与全量腿同险。
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (remaining > 0)
        {
            var read = await ReadBoundedAsync(
                input, buffer, (int)Math.Min(buffer.Length, remaining), stall,
                cancellationToken, "delta source/range read");
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
        try
        {
            await DownloadHttpCoreAsync(url, destinationPath, log, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout 的 OCE 不带用户取消标记——发送侧超时是协议
            // 事实不是用户取消，误穿透会被调用方 catch(OCE) 静默当取消。
            throw new UpdateException(
                $"download '{url}' timed out — server did not respond in time.");
        }
    }

    private async Task DownloadHttpCoreAsync(
        string url, string destinationPath, Action<string>? log, CancellationToken cancellationToken)
    {
        var partPath = destinationPath + ".part";
        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-");
        }
        var response = await SharedHttp.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && !ResumeUsable(response, offset))
        {
            // 续传无效：服务端不支持 Range、文件已变、416（.part 大于服务端文件），
            // 或 206 的 Content-Range 起点与 .part 偏移不符（起点错位的分片续写
            // 会在偏移界拼出坏文件）——丢弃 .part 从 0 重下；不删则 416/错位续写
            // 会永久循环。成功全档响应（忽略 Range 给了整件）直接取用；其余响应
            // （失败态或起点不符的 206）体不可用，须重发不带 Range 的请求。
            offset = 0;
            TryDelete(partPath);
            if (!response.IsSuccessStatusCode ||
                response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                request.Dispose();
                response.Dispose();
                request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await SharedHttp.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
        }
        using (request)
        using (response)
        {
            response.EnsureSuccessStatusCode();

            var mode = offset > 0 ? FileMode.Append : FileMode.Create;
            using (var input = await response.Content.ReadAsStreamAsync())
            using (var output = new FileStream(partPath, mode, FileAccess.Write, FileShare.None))
            {
                // 逐块停滞界：ResponseHeadersRead 下流读不受发送侧超时保护——
                // 慢速/挂起连接会把下载拖死；每块独立给停滞上界。
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await ReadBoundedAsync(
                        input, buffer, buffer.Length, stall, cancellationToken,
                        $"download '{url}'");
                    if (read == 0)
                    {
                        break;
                    }
                    await output.WriteAsync(buffer, 0, read, cancellationToken);
                }
            }
            // 目标已存在（同一更新重下/复用下载目录）也要能落——netstandard2.0 无 Move 覆写重载。
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(partPath, destinationPath);
            log?.Invoke($"update: downloaded '{url}' → '{destinationPath}'");
        }
    }

    // 206 续写前提：Content-Range 起点必须等于 .part 已持偏移——头缺失
    // 或起点不符即续传无效（服务端回错分片/陈旧残件不能盲续）。
    private static bool ResumeUsable(HttpResponseMessage response, long offset) =>
        response.StatusCode == System.Net.HttpStatusCode.PartialContent &&
        response.Content.Headers.ContentRange is { } range &&
        string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
        range.From == offset;

    private async Task<byte[]> GetBytesAsync(string location, CancellationToken cancellationToken)
    {
        if (IsHttp(location))
        {
            // netstandard2.0 的 GetByteArrayAsync 无 ct 重载：走 SendAsync 消化取消。
            using var response = await SharedHttp.GetAsync(
                location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            // 体读停滞界：ReadAsByteArrayAsync 不吃外层 ct——挂起的响应体会拖死取件。
            var content = response.Content.ReadAsByteArrayAsync();
            // 计时器挂独立 cts——分胜负即刻取消，不留 ≤120s 挂钟残件。
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (await Task.WhenAny(content, Task.Delay(BodyStallMilliseconds, timer.Token))
                    != content)
            {
                // 弃置腿观异常防 unobserved 上报；计时器已赢/随本次取消回收。
                _ = content.ContinueWith(
                    t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                timer.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new UpdateException(
                    $"fetch of '{location}' stalled — body not complete within " +
                    $"{BodyStallMilliseconds / 1000}s.");
            }
            timer.Cancel();
            return await content;
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

using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 应用内更新客户端门面：`UpdateInstallIdentity`（安装目录旁车）→ 查清单 → 下载 →
/// ECDSA 验签 → 两式应用（installer-replay / file-swap）→ 回滚钩子。
/// 消费方只需关心四个动词；协议/校验/拒绝全部确定性抛 <see cref="UpdateException"/>。
/// </summary>
public sealed class UpdateClient
{
    private readonly UpdateInstallIdentity _identity;
    private readonly string _installDirectory;
    private readonly string _currentVersion;
    private readonly UpdateDownloader _downloader = new();
    private readonly UpdateClientOptions _options;

    private UpdateClient(
        UpdateInstallIdentity identity, string installDirectory,
        string currentVersion, UpdateClientOptions options)
    {
        _identity = identity;
        _installDirectory = installDirectory;
        _currentVersion = currentVersion;
        _options = options;
    }

    /// <summary>
    /// 从安装目录读 `bundler-update.json` 构造客户端。
    /// 旁车缺失/损坏 → <see cref="UpdateException"/>（更新不可用，不猜不补）。
    /// </summary>
    public static UpdateClient FromInstallDirectory(
        string installDirectory, string currentVersion,
        UpdateClientOptions? options = null)
    {
        var identity = UpdateInstallIdentity.TryRead(installDirectory)
            ?? throw new UpdateException(
                $"no '{UpdateInstallIdentity.FileName}' under '{installDirectory}' — " +
                "this installation does not participate in updates.");
        if (identity.FeedUrl.Length == 0 || identity.PublicKey is not { Length: > 0 })
        {
            throw new UpdateException("install identity carries no feed url or public key.");
        }
        return new UpdateClient(identity, installDirectory, currentVersion,
            options ?? new UpdateClientOptions());
    }

    /// <summary>
    /// 用显式身份构造（旁车缺失但调用方持兜底身份的场景：注册表/plist 重建后传入）。
    /// </summary>
    public static UpdateClient FromIdentity(
        UpdateInstallIdentity identity, string installDirectory,
        string currentVersion, UpdateClientOptions? options = null)
        => new(identity, installDirectory, currentVersion,
            options ?? new UpdateClientOptions());

    /// <summary>拉清单→选件→比版本；有可用更新返回 UpdateInfo，否则 null。</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        var feed = await _downloader.FetchFeedAsync(FeedUrl(), cancellationToken);
        if (!UpdateVersion.TryParse(feed.Version, out var remote) ||
            !UpdateVersion.TryParse(_currentVersion, out var local))
        {
            throw new UpdateException(
                $"cannot compare versions '{_currentVersion}' vs '{feed.Version}'.");
        }
        if (remote.CompareTo(local) <= 0)
        {
            _options.Log?.Invoke($"update: '{_currentVersion}' is current ({feed.Version})");
            return null;
        }
        // 远端有新版但没有本平台件 = 无适用更新（增量滚动发布中常见），不算错误。
        var artifact = SelectArtifact(feed);
        if (artifact is null)
        {
            _options.Log?.Invoke(
                $"update: '{feed.Version}' has no artifact for " +
                $"{_identity.RuntimeIdentifier}/{_identity.Format}");
            return null;
        }
        _options.Log?.Invoke($"update: '{feed.Version}' available via {artifact.Format}");
        return new UpdateInfo(feed, artifact, ResolveArtifactUrl(feed, artifact));
    }

    /// <summary>下载产物到 destinationDirectory 并验 sha256；返回本地文件路径。</summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, info.Artifact.File);
        await _downloader.DownloadAsync(
            info.DownloadUrl, info.Artifact, destination, _options.Log, cancellationToken);
        return destination;
    }

    /// <summary>ECDSA 验签（公钥来自安装身份旁车）——不通过即抛，绝不放行。</summary>
    public void Verify(UpdateInfo info, string artifactPath)
    {
        if (!UpdateSignatureVerifier.VerifyFile(
                artifactPath, info.Artifact.Signature, _identity.PublicKey!))
        {
            throw new UpdateException("artifact signature verification failed — refused.");
        }
        _options.Log?.Invoke("update: signature verified");
    }

    /// <summary>
    /// 启动应用流程：nsis/msi 走安装器重跑，其他走引导件三段式换包。
    /// 返回已派生的进程——调用方应立即退出让 wait-pid 生效（file-swap）
    /// 或让安装器接管（installer-replay）。
    /// </summary>
    public Process Apply(UpdateInfo info, string artifactPath, ApplyOptions? options = null)
    {
        // WaitPid=null 即不等任何进程（调用方自行安排退出时机）；真实流程恒传当前进程。
        return UpdateApplier.Apply(
            info.Artifact, artifactPath, _installDirectory,
            options ?? new ApplyOptions(), _options.Log);
    }

    /// <summary>回滚钩子：上次换包留下的 .bundler-backup 倒回安装目录。</summary>
    public Process Rollback(ApplyOptions? options = null) =>
        UpdateApplier.Rollback(_installDirectory, options ?? new ApplyOptions(), _options.Log);

    /// <summary>一步到位：查→下→验→启。无更新返回 false。</summary>
    public async Task<bool> UpdateAsync(
        ApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (await CheckForUpdateAsync(cancellationToken) is not { } info)
        {
            return false;
        }
        var path = await DownloadAsync(info, _options.DownloadDirectory, cancellationToken);
        Verify(info, path);
        Apply(info, path, options);
        return true;
    }

    private string FeedUrl() => _identity.FeedUrl;

    private string ResolveArtifactUrl(UpdateFeed feed, UpdateFeedArtifact artifact) =>
        UpdateDownloader.ResolveArtifactLocation(_identity.FeedUrl, artifact.Url);

    // 选件：同 RID 同格式；RID 支持 "portable"/"any" 通配槽兜底。
    private UpdateFeedArtifact? SelectArtifact(UpdateFeed feed)
    {
        UpdateFeedArtifact? wildcard = null;
        foreach (var artifact in feed.Artifacts)
        {
            if (!string.Equals(artifact.Format, _identity.Format,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(artifact.RuntimeIdentifier, _identity.RuntimeIdentifier,
                    StringComparison.OrdinalIgnoreCase))
            {
                return artifact;
            }
            if (artifact.RuntimeIdentifier is "portable" or "any")
            {
                wildcard ??= artifact;
            }
        }
        return wildcard;
    }

}

/// <summary>一次可用更新的决议结果：清单头 + 选中的产物 + 解析后的下载地址。</summary>
public sealed class UpdateInfo(UpdateFeed feed, UpdateFeedArtifact artifact, string downloadUrl)
{
    public UpdateFeed Feed { get; } = feed;
    public UpdateFeedArtifact Artifact { get; } = artifact;
    /// <summary>解析到绝对地址的产物位置（相对清单目录已解析）。</summary>
    public string DownloadUrl { get; } = downloadUrl;
    public string Version => Feed.Version;
    public string? Notes => Feed.Notes;
}

/// <summary>客户端旋钮：日志、下载落点；身份来自安装旁车不在此处。</summary>
public sealed class UpdateClientOptions
{
    /// <summary>链路事件行输出（进度/决议/拒绝原因）。</summary>
    public Action<string>? Log { get; init; }
    /// <summary><see cref="UpdateClient.UpdateAsync"/> 一步法的下载落点目录。</summary>
    public string DownloadDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "bundler-update", "downloads");
}

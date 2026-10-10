using System.Runtime.Serialization;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// `bundler-update-feed.{channel}.json` 通道清单模型——打包侧写、应用侧读的开放协议。
/// 本文件无打包链依赖，可被应用内更新库直接链接编译。
/// </summary>
[DataContract]
public sealed class UpdateFeed
{
    [DataMember(Name = "version")] public string Version = "";
    [DataMember(Name = "notes", EmitDefaultValue = false)] public string? Notes;
    [DataMember(Name = "publishedAt")] public string PublishedAt = "";
    [DataMember(Name = "channel")] public string Channel = "";
    [DataMember(Name = "artifacts")] public List<UpdateFeedArtifact> Artifacts = [];

    public const string FeedNamePrefix = "bundler-update-feed";

    public static string FeedFileName(string channel) =>
        $"{FeedNamePrefix}.{channel}.json";
}

/// <summary>清单内单条产物：同 RID 同格式匹配，url 相对清单目录或绝对。</summary>
[DataContract]
public sealed class UpdateFeedArtifact
{
    [DataMember(Name = "rid")] public string Target = "";
    [DataMember(Name = "format")] public string Format = "";
    [DataMember(Name = "url")] public string Url = "";
    [DataMember(Name = "file")] public string File = "";
    [DataMember(Name = "size")] public long Size;
    [DataMember(Name = "sha256")] public string Sha256 = "";
    [DataMember(Name = "sig")] public string Signature = "";
    /// <summary>block-map 差分件（UPDATE-5 预留，未启用时为空）。</summary>
    [DataMember(Name = "blockmap", EmitDefaultValue = false)] public string? BlockMap;
}

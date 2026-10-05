using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// `bundler-update.json` 安装身份文档——打包侧写进载荷、应用侧读出以定位通道与验签公钥。
/// 本文件无打包链依赖，可被应用内更新库直接链接编译。
/// </summary>
[DataContract]
public sealed class UpdateInstallIdentity
{
    [DataMember(Name = "format", EmitDefaultValue = false)] public string Format = "";
    [DataMember(Name = "rid", EmitDefaultValue = false)] public string RuntimeIdentifier = "";
    [DataMember(Name = "channel", EmitDefaultValue = false)] public string Channel = "";
    [DataMember(Name = "feedUrl", EmitDefaultValue = false)] public string FeedUrl = "";
    [DataMember(Name = "publicKey", EmitDefaultValue = false)] public string? PublicKey;

    public const string FileName = "bundler-update.json";

    public static void Write(string payloadRoot, UpdateInstallIdentity identity)
    {
        var path = Path.Combine(payloadRoot, FileName);
        using var stream = File.Create(path);
        var serializer = new DataContractJsonSerializer(typeof(UpdateInstallIdentity));
        serializer.WriteObject(stream, identity);
        stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
    }

    /// <summary>读安装目录内的身份旁车；缺失或损坏返回 null（不猜不补）。</summary>
    public static UpdateInstallIdentity? TryRead(string payloadRoot)
    {
        var path = Path.Combine(payloadRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var serializer = new DataContractJsonSerializer(typeof(UpdateInstallIdentity));
            return serializer.ReadObject(stream) as UpdateInstallIdentity;
        }
        catch (SerializationException)
        {
            return null;
        }
    }
}

using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DotNet.Bundler.Core.Update;

/// <summary>
/// `bundler-update.json` 安装身份旁车：随载荷进入安装目录/载荷顶层，
/// 记录应用自身的格式/RID/通道/清单地址与验签公钥（electron `app-update.yml` 同型）。
/// 只写入已 staged 的载荷目录，绝不动用户的发布目录。
/// </summary>
public static class UpdateIdentitySidecar
{
    public const string FileName = "bundler-update.json";

    [DataContract]
    private sealed class SidecarDocument
    {
        [DataMember(Name = "format", EmitDefaultValue = false)] public string Format = "";
        [DataMember(Name = "rid", EmitDefaultValue = false)] public string RuntimeIdentifier = "";
        [DataMember(Name = "channel", EmitDefaultValue = false)] public string Channel = "";
        [DataMember(Name = "feedUrl", EmitDefaultValue = false)] public string FeedUrl = "";
        [DataMember(Name = "publicKey", EmitDefaultValue = false)] public string? PublicKey;
    }

    public static void WriteIfEnabled(
        string payloadRoot,
        UpdateBundleConfiguration? update,
        PackageFormat format,
        string runtimeIdentifier)
    {
        if (update is null)
        {
            return;
        }
        var document = new SidecarDocument
        {
            Format = FormatName(format),
            RuntimeIdentifier = runtimeIdentifier,
            Channel = update.Channel,
            FeedUrl = update.FeedUrl,
            PublicKey = ResolvePublicKey(update),
        };
        var path = Path.Combine(payloadRoot, FileName);
        using var stream = File.Create(path);
        var serializer = new DataContractJsonSerializer(typeof(SidecarDocument));
        serializer.WriteObject(stream, document);
        stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
    }

    public static string FormatName(PackageFormat format) => format.ToString().ToLowerInvariant();

    // 公钥显式给出时直接用；只给私钥文件时从私钥推导——二者至少其一，否则更新验签无根。
    public static string? ResolvePublicKey(UpdateBundleConfiguration update)
    {
        if (!string.IsNullOrWhiteSpace(update.PublicKey))
        {
            return update.PublicKey;
        }
        if (update.SigningKeyFile is { Length: > 0 } keyFile)
        {
            return UpdateKeyMaterial.Load(keyFile).PublicPointBase64();
        }
        return null;
    }
}

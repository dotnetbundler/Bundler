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

    /// <summary>
    /// 把身份写成文件级安装的旁车 `<installFile>.bundler-update.json`——
    /// AppImage 等单文件安装单元的身份放不进镜像内可读位置，由客户端应用时烙在旁边。
    /// </summary>
    public static void WriteSidecar(string installFilePath, UpdateInstallIdentity identity)
    {
        var path = SidecarPath(installFilePath);
        using var stream = File.Create(path);
        var serializer = new DataContractJsonSerializer(typeof(UpdateInstallIdentity));
        serializer.WriteObject(stream, identity);
        stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
    }

    /// <summary>文件级安装单元的身份旁车路径：`<installFile>.bundler-update.json`。</summary>
    public static string SidecarPath(string installFilePath) => installFilePath + ".bundler-update.json";

    /// <summary>
    /// 读安装目录内的身份旁车；缺失或损坏返回 null（不猜不补）。
    /// 传入现存文件路径（AppImage 类单件安装单元）时读 `<file>.bundler-update.json` 旁车；
    /// `.app` 传入 bundle 根时落到 Contents/——打包侧把旁车写在 Contents 顶层。
    /// </summary>
    public static UpdateInstallIdentity? TryRead(string payloadRoot)
    {
        string path;
        if (File.Exists(payloadRoot))
        {
            path = SidecarPath(payloadRoot);
            if (!File.Exists(path))
            {
                return null;
            }
        }
        else
        {
            path = Path.Combine(payloadRoot, FileName);
            if (!File.Exists(path))
            {
                var contents = Path.Combine(payloadRoot, "Contents", FileName);
                if (!File.Exists(contents))
                {
                    return null;
                }
                path = contents;
            }
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

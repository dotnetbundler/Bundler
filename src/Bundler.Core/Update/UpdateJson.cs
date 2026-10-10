#if NETSTANDARD2_0
using System.Runtime.Serialization.Json;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// UPDATE 协议 JSON 序列化单点。netstandard2.0 走 DataContractJsonSerializer
/// （运行时非 AOT 领域可用）；net10.0 走 System.Text.Json 源生成上下文——
/// Native AOT 下 DCS 需要动态代码不可用，源生成是唯一合规路径。
/// </summary>
internal static class UpdateJson
{
    public static void WriteKeyMaterial(Stream stream, UpdateKeyMaterial value)
    {
#if NETSTANDARD2_0
        new DataContractJsonSerializer(typeof(UpdateKeyMaterial))
            .WriteObject(stream, value);
#else
        JsonSerializer.Serialize(stream, value, UpdateJsonContext.Shared.UpdateKeyMaterial);
#endif
    }

    public static UpdateKeyMaterial? ReadKeyMaterial(Stream stream)
    {
#if NETSTANDARD2_0
        return (UpdateKeyMaterial?)new DataContractJsonSerializer(typeof(UpdateKeyMaterial))
            .ReadObject(stream);
#else
        return JsonSerializer.Deserialize(stream, UpdateJsonContext.Shared.UpdateKeyMaterial);
#endif
    }

    public static void WriteIdentity(Stream stream, UpdateInstallIdentity value)
    {
#if NETSTANDARD2_0
        new DataContractJsonSerializer(typeof(UpdateInstallIdentity))
            .WriteObject(stream, value);
#else
        JsonSerializer.Serialize(stream, value, UpdateJsonContext.Shared.UpdateInstallIdentity);
#endif
    }

    public static UpdateInstallIdentity? ReadIdentity(Stream stream)
    {
#if NETSTANDARD2_0
        return new DataContractJsonSerializer(typeof(UpdateInstallIdentity))
            .ReadObject(stream) as UpdateInstallIdentity;
#else
        return JsonSerializer.Deserialize(stream, UpdateJsonContext.Shared.UpdateInstallIdentity);
#endif
    }

    public static void WriteFeed(Stream stream, UpdateFeed value)
    {
#if NETSTANDARD2_0
        new DataContractJsonSerializer(typeof(UpdateFeed))
            .WriteObject(stream, value);
#else
        JsonSerializer.Serialize(stream, value, UpdateJsonContext.Shared.UpdateFeed);
#endif
    }

    public static UpdateFeed? ReadFeed(Stream stream)
    {
#if NETSTANDARD2_0
        return new DataContractJsonSerializer(typeof(UpdateFeed))
            .ReadObject(stream) as UpdateFeed;
#else
        return JsonSerializer.Deserialize(stream, UpdateJsonContext.Shared.UpdateFeed);
#endif
    }

    public static void WriteBlockMap(Stream stream, UpdateBlockMap value)
    {
#if NETSTANDARD2_0
        new DataContractJsonSerializer(typeof(UpdateBlockMap))
            .WriteObject(stream, value);
#else
        JsonSerializer.Serialize(stream, value, UpdateJsonContext.Shared.UpdateBlockMap);
#endif
    }

    public static UpdateBlockMap? ReadBlockMap(Stream stream)
    {
#if NETSTANDARD2_0
        return new DataContractJsonSerializer(typeof(UpdateBlockMap))
            .ReadObject(stream) as UpdateBlockMap;
#else
        return JsonSerializer.Deserialize(stream, UpdateJsonContext.Shared.UpdateBlockMap);
#endif
    }
}

#if !NETSTANDARD2_0
/// <summary>
/// 线路名映射：C# 名 → 协议字段名。多数与 camelCase 一致，仅三个自定义项
/// （rid/sig/blockmap）显式覆盖；无需在共享 DTO 上挂 STJ 特性——
/// 那些文件同时在无 STJ 包的 netstandard2.0 下编译。
/// </summary>
internal sealed class UpdateJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly UpdateJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) => name switch
    {
        "Target" => "rid",
        "Signature" => "sig",
        "BlockMap" => "blockmap",
        _ => JsonNamingPolicy.CamelCase.ConvertName(name),
    };
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    IncludeFields = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateKeyMaterial))]
[JsonSerializable(typeof(UpdateInstallIdentity))]
[JsonSerializable(typeof(UpdateFeed))]
[JsonSerializable(typeof(UpdateFeedArtifact))]
[JsonSerializable(typeof(UpdateBlockMap))]
[JsonSerializable(typeof(List<UpdateFeedArtifact>))]
[JsonSerializable(typeof(List<string>))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = UpdateJsonNamingPolicy.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };

    private static UpdateJsonContext? _shared;
    public static UpdateJsonContext Shared => _shared ??= new UpdateJsonContext(SerializerOptions);
}
#endif

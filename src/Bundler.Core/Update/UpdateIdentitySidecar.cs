namespace DotNet.Bundler.Core.Update;

/// <summary>
/// `bundler-update.json` 安装身份旁车：随载荷进入安装目录/载荷顶层，
/// 记录应用自身的格式/RID/通道/清单地址与验签公钥（electron `app-update.yml` 同型）。
/// 只写入已 staged 的载荷目录，绝不动用户的发布目录。
/// 文档模型见 <see cref="UpdateInstallIdentity"/>。
/// </summary>
public static class UpdateIdentitySidecar
{
    public const string FileName = UpdateInstallIdentity.FileName;

    public static void WriteIfEnabled(
        string payloadRoot,
        UpdateBundleConfiguration? update,
        PackageFormat format,
        string target,
        string? pkg = null)
    {
        if (update is null)
        {
            return;
        }
        UpdateInstallIdentity.Write(payloadRoot, BuildIdentity(update, format, target, pkg));
    }

    /// <summary>
    /// 身份文档序列化为旁车字节（UTF-8 JSON + 尾换行——与
    /// <see cref="UpdateInstallIdentity.Write"/> 落盘形态一致）。
    /// syspkg 后端（deb/rpm/alpineapk）在 writer 层以生成件注入载荷虚拟路径，
    /// 不经过磁盘 staging 目录。
    /// </summary>
    public static byte[] Serialize(
        UpdateBundleConfiguration update,
        PackageFormat format,
        string target,
        string? pkg = null)
    {
        using var stream = new MemoryStream();
        UpdateJson.WriteIdentity(stream, BuildIdentity(update, format, target, pkg));
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static UpdateInstallIdentity BuildIdentity(
        UpdateBundleConfiguration update, PackageFormat format, string target, string? pkg) =>
        new()
        {
            Format = FormatName(format),
            Target = target,
            // 与 UpdateManifestEmitter 同一归一规则：空 channel 按 "latest" 落地，
            // 否则包内身份与 feed 清单的 channel 字段会不一致。
            Channel = string.IsNullOrWhiteSpace(update.Channel) ? "latest" : update.Channel,
            FeedUrl = update.FeedUrl,
            PublicKey = ResolvePublicKey(update),
            // syspkg 包名只在 writer 层可推导（settings 覆盖/产品名清洗都发生在那），
            // 由后端显式传入；非 syspkg 格式恒 null——EmitDefaultValue=false 不写字段。
            Pkg = pkg,
        };

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

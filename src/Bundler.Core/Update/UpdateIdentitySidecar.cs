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
        string target)
    {
        if (update is null)
        {
            return;
        }
        UpdateInstallIdentity.Write(payloadRoot, new UpdateInstallIdentity
        {
            Format = FormatName(format),
            Target = target,
            Channel = update.Channel,
            FeedUrl = update.FeedUrl,
            PublicKey = ResolvePublicKey(update),
        });
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

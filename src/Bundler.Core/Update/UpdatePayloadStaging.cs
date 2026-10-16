namespace DotNet.Bundler.Core.Update;

/// <summary>
/// 身份旁车需要写进"最终进包的载荷目录"。后端已做 staging（如 signed-payload）
/// 时直接复用该目录；未做 staging 时把输入目录复制到工作目录再写旁车，
/// 绝不在用户的发布目录里落旁车文件。
/// </summary>
public static class UpdatePayloadStaging
{
    public static BundlePlanItem EnsureStaged(BundleBuildContext context, BundlePlanItem item)
    {
        var update = context.Configuration.Update;
        if (update is null)
        {
            return item;
        }

        var workRoot = Path.GetFullPath(context.WorkDirectory);
        var input = Path.GetFullPath(item.InputDirectory);
        // 已在工作目录内（例如 signed-payload staging）→ 直接写旁车。
        if (input.StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            InjectPayloadFiles(input, update, item);
            return item;
        }

        var staged = Path.Combine(workRoot, "update-payload");
        CopyDirectory(input, staged);
        InjectPayloadFiles(staged, update, item);
        context.Logger.Log(BundleLogLevel.Information,
            $"Staged update payload with bundler-update.json sidecar → {staged}");
        return item with { InputDirectory = staged };
    }

    // 旁车+引导件写进“最终进包的载荷目录”：旁车记录身份，引导件是换包执行体。
    // installer-replay 跳过表口径：只有"更新=重跑安装器"的格式才进表
    //（nsis 跑 /UPDATE、msi 走 msiexec major upgrade——安装器自身就是换包执行体，
    // 载荷里不再塞引导件）；其余格式更新=引导件三段式换包，载荷必须带执行体。
    private static void InjectPayloadFiles(
        string payloadRoot, UpdateBundleConfiguration update, BundlePlanItem item)
    {
        var target = item.Target.Target;
        UpdateIdentitySidecar.WriteIfEnabled(payloadRoot, update, item.Format, target);
        if (item.Format is PackageFormat.Nsis or PackageFormat.Msi)
        {
            return;
        }
        if (UpdateBootstrapper.Inject(payloadRoot, update, target) is null)
        {
            // Inject 返回 null 即解析链全空——ResolveOrThrow 复跑同一条链产出带探测位的诊断文案。
            UpdateBootstrapper.ResolveOrThrow(update, target);
        }
    }

    // 平铺复制：不保符号链接（解引用成实体内容）、不保执行位/xattr——
    // staging 只是"让旁车能写进载荷"的过渡目录，下游打包器按各自管线重建属性；
    // 需要保真链接/权限的格式（.app 等）必须走后端自有 staging 不经过这里。
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}

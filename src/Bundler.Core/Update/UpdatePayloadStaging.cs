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

        var staged = Path.Combine(context.WorkDirectory, "update-payload");
        CopyDirectory(input, staged);
        InjectPayloadFiles(staged, update, item);
        context.Logger.Log(BundleLogLevel.Information,
            $"Staged update payload with bundler-update.json sidecar → {staged}");
        return item with { InputDirectory = staged };
    }

    // 旁车+引导件写进“最终进包的载荷目录”：旁车记录身份，引导件是换包执行体。
    // installer-replay 格式（nsis/msi）更新=重跑安装器，载荷不需要换包执行体。
    private static void InjectPayloadFiles(
        string payloadRoot, UpdateBundleConfiguration update, BundlePlanItem item)
    {
        var rid = item.Target.RuntimeIdentifier;
        UpdateIdentitySidecar.WriteIfEnabled(payloadRoot, update, item.Format, rid);
        if (item.Format is PackageFormat.Nsis or PackageFormat.Msi)
        {
            return;
        }
        if (UpdateBootstrapper.Inject(payloadRoot, update, rid) is null)
        {
            throw new InvalidOperationException(
                $"BundlerUpdate enabled but no bootstrapper found for '{rid}' — no updater/{rid}/ binary is embedded in Bundler.Core and BundlerUpdateBootstrapperDirectory was not set.");
        }
    }

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

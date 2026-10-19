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
        // TrimEnd 先剥尾部分隔符：根目录输入（"C:\"、"/"）拼 sep 会出双分隔符永不命中。
        var workPrefix = workRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (input.StartsWith(workPrefix, PathText.Comparison))
        {
            InjectPayloadFiles(input, update, item);
            return item;
        }

        var staged = Path.Combine(workRoot, "update-payload");
        // 输入树包住 staged（如调用方把 OutputDirectory/work 指进 payload）时自复制会
        // 把 staged 再拷进自己——递归直到路径爆掉。前置拒掉，错误要直说；
        // 根目录输入同样靠 TrimEnd 归一（"C:\\"→"C:"）。
        var inputPrefix = input.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((staged + Path.DirectorySeparatorChar).StartsWith(inputPrefix, PathText.Comparison))
        {
            throw new InvalidOperationException(
                $"The update staging directory '{staged}' must not be inside the payload input directory '{input}'.");
        }
        CopyDirectory(input, staged, context.Logger);
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
        // 更新件是保留名：注入落位处用户载荷自带同路径条目时不静默覆盖——
        // 与 deb/rpm/apk/macOS 的 claimed 簿同一语义：撞名显式拒绝。
        // nsis/msi 不注引导件，但旁车恒写。
        RejectReservedName(payloadRoot, UpdateIdentitySidecar.FileName);
        if (item.Format is not (PackageFormat.Nsis or PackageFormat.Msi))
        {
            RejectReservedName(payloadRoot, UpdateBootstrapper.FileNameFor(target));
        }
        UpdateIdentitySidecar.WriteIfEnabled(payloadRoot, update, item.Format, target);
        if (item.Format is PackageFormat.Nsis or PackageFormat.Msi)
        {
            return;
        }
        UpdateBootstrapper.InjectOrThrow(payloadRoot, update, target);
    }

    private static void RejectReservedName(string payloadRoot, string fileName)
    {
        // File.Exists 漏掉同名目录——目录撞旁车会让写入炸成 IO 异常，一样是撞名要直说。
        var path = Path.Combine(payloadRoot, fileName);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"The payload already contains the reserved update file name '{fileName}' — " +
                "remove it from the input directory; update payload files are injected by the build.");
        }
    }

    // 平铺复制：不保符号链接（解引用成实体内容）、不保执行位/xattr——
    // staging 只是"让旁车能写进载荷"的过渡目录，下游打包器按各自管线重建属性；
    // 需要保真链接/权限的格式（.app 等）必须走后端自有 staging 不经过这里。
    // 复用 FileTree：目录链 Traverse+深度封顶防环，非常规文件（FIFO/socket）告警跳过。
    private static void CopyDirectory(string source, string destination, IBundleLogger logger)
    {
        FileTree.CopyTree(
            source, destination, logger,
            maxDepth: 64,
            loopErrorMessage: "Update payload staging exceeded the directory depth limit (symlink loop?)",
            links: TreeLinkPolicy.Traverse,
            overwrite: true);
    }
}

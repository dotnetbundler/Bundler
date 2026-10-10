using System;
using System.IO;
using System.Linq;
using DotNet.Bundler.MacApp;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

/// <summary>
/// 把逐架构 publish 目录合并成 `osx` 目标的 universal 载荷目录：Mach-O 由托管代码胖合并；
/// 非 Mach-O 文件在给了 <see cref="ReferenceDirectory"/>（无 RID 参考发布）时以参考件为准，
/// 否则要求逐字节一致；既不一致又无参考的文件报错。
/// </summary>
public sealed class MergeUniversalPayload : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] SourceDirectories { get; set; } = Array.Empty<ITaskItem>();
    [Required] public string OutputDirectory { get; set; } = "";
    public string ReferenceDirectory { get; set; } = "";
    [Output] public string MergedDirectory { get; private set; } = "";

    public override bool Execute()
    {
        try
        {
            var sources = SourceDirectories.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray();
            var output = Path.GetFullPath(OutputDirectory);
            var reference = string.IsNullOrEmpty(ReferenceDirectory) ? null : Path.GetFullPath(ReferenceDirectory);
            MacUniversalPayloadMerger.Merge(sources, output, reference);
            MergedDirectory = output;
            Log.LogMessage(MessageImportance.High,
                "Merged universal payload: {0} inputs -> {1}", sources.Length, output);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or DirectoryNotFoundException)
        {
            Log.LogError("Universal payload merge failed: {0}", ex.Message);
            return false;
        }
    }
}

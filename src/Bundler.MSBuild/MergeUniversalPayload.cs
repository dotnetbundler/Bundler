using System;
using System.IO;
using System.Linq;
using DotNet.Bundler.MacApp;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

/// <summary>
/// Merges per-architecture publish directories into one universal payload directory for the
/// `osx` bundle target: Mach-O files are fat-merged in managed code; non-Mach-O files come
/// from the RID-less reference publish when <see cref="ReferenceDirectory"/> is set, else
/// must be byte-identical between inputs; differing files without a reference copy fail.
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

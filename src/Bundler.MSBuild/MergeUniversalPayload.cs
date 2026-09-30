using System;
using System.IO;
using System.Linq;
using DotNet.Bundler.MacApp;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

/// <summary>
/// Merges per-architecture publish directories into one universal payload directory for the
/// `osx` bundle target: Mach-O files are fat-merged in managed code, byte-identical files are
/// copied once, differing non-Mach-O files fail the build.
/// </summary>
public sealed class MergeUniversalPayload : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] SourceDirectories { get; set; } = Array.Empty<ITaskItem>();
    [Required] public string OutputDirectory { get; set; } = "";
    [Output] public string MergedDirectory { get; private set; } = "";

    public override bool Execute()
    {
        try
        {
            var sources = SourceDirectories.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray();
            var output = Path.GetFullPath(OutputDirectory);
            MacUniversalPayloadMerger.Merge(sources, output);
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

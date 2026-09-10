using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bundler.Core.Backends;
using Bundler.Core.Backends.Windows;
using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Tools;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

public sealed class BundleDesktopApplication : Microsoft.Build.Utilities.Task
{
    [Required] public string ProductName { get; set; } = "";
    [Required] public string Identifier { get; set; } = "";
    [Required] public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    [Required] public string RuntimeIdentifier { get; set; } = "";
    [Required] public string InputDirectory { get; set; } = "";
    [Required] public string OutputDirectory { get; set; } = "";
    [Required] public string MainExecutable { get; set; } = "";
    [Required] public string Formats { get; set; } = "";
    public ITaskItem[] Icons { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] Resources { get; set; } = Array.Empty<ITaskItem>();
    public string NsisToolArchivePath { get; set; } = "";
    [Required] public string ToolCacheDirectory { get; set; } = "";
    public string NsisTemplatePath { get; set; } = "";
    [Output] public ITaskItem[] Artifacts { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var formats = ParseFormats();
            var configuration = new BundleConfiguration
            {
                ProductName = ProductName,
                Identifier = Identifier,
                Version = Version,
                Publisher = EmptyToNull(Publisher),
                Description = EmptyToNull(Description),
                OutputDirectory = Path.GetFullPath(OutputDirectory),
                Icons = Icons.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray(),
                Resources = Resources.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray(),
                Targets = new[]
                {
                    new BundleTargetConfiguration
                    {
                        RuntimeIdentifier = RuntimeIdentifier,
                        InputDirectory = Path.GetFullPath(InputDirectory),
                        MainExecutable = MainExecutable,
                        Formats = formats
                    }
                }
            };

            var artifacts = new BundlePipeline(CreateBackends(formats))
                .BuildAsync(configuration)
                .GetAwaiter()
                .GetResult();

            Artifacts = artifacts.Select(artifact =>
            {
                var item = new TaskItem(artifact.Path);
                item.SetMetadata("Format", artifact.Format.ToString());
                item.SetMetadata("RuntimeIdentifier", artifact.RuntimeIdentifier);
                return (ITaskItem)item;
            }).ToArray();

            foreach (var artifact in artifacts)
            {
                Log.LogMessage(MessageImportance.High, "Created {0}", artifact.Path);
            }

            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private IReadOnlyList<PackageFormat> ParseFormats()
    {
        var result = new List<PackageFormat>();
        foreach (var value in Formats.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            PackageFormat format;
            if (!Enum.TryParse(value.Trim(), true, out format))
            {
                throw new ArgumentException("Unknown bundle format '" + value.Trim() + "'.", nameof(Formats));
            }

            if (!result.Contains(format))
            {
                result.Add(format);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("At least one bundle format is required.", nameof(Formats));
        }

        return result;
    }

    private IReadOnlyList<IBundleBackend> CreateBackends(IReadOnlyList<PackageFormat> formats)
    {
        var backends = new List<IBundleBackend>();
        if (formats.Contains(PackageFormat.Nsis))
        {
            if (string.IsNullOrWhiteSpace(NsisToolArchivePath) || string.IsNullOrWhiteSpace(NsisTemplatePath))
            {
                throw new InvalidOperationException("NSIS packaging requires its bundled tool archive and script template.");
            }

            var compiler = NsisToolResolver.ResolveAsync(NsisToolArchivePath, ToolCacheDirectory)
                .GetAwaiter()
                .GetResult();
            backends.Add(new NsisBundleBackend(compiler, NsisTemplatePath));
        }

        return backends;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public static class BundlePlanner
{
    public static BundlePlan Create(BundleConfiguration configuration, bool checkFileSystem = true)
    {
        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem);
        if (issues.Count > 0)
        {
            throw new BundleValidationException(issues);
        }

        var items = new List<BundlePlanItem>();
        foreach (var configuredTarget in configuration.Targets)
        {
            BundleTarget.TryParse(configuredTarget.RuntimeIdentifier, out var target);
            var executable = configuredTarget.MainExecutable ??
                (target!.OperatingSystem == DesktopOperatingSystem.Windows
                    ? $"{configuration.ProductName}.exe"
                    : configuration.ProductName);

            var requestedFormats = configuredTarget.Formats.Distinct().ToArray();
            if (requestedFormats.Any(format => format is PackageFormat.Dmg or PackageFormat.Pkg) &&
                !requestedFormats.Contains(PackageFormat.App))
            {
                AddItem(PackageFormat.App, intermediate: true);
            }

            foreach (var format in requestedFormats.OrderBy(Priority))
            {
                AddItem(format, intermediate: false);
            }

            void AddItem(PackageFormat format, bool intermediate)
            {
                items.Add(new BundlePlanItem(
                    target!,
                    format,
                    configuredTarget.InputDirectory,
                    executable,
                    Path.Combine(configuration.OutputDirectory, target!.RuntimeIdentifier, FormatName(format)),
                    intermediate)
                {
                    SigningFiles = configuredTarget.SigningFiles.ToArray()
                });
            }
        }

        return new(items);
    }

    private static int Priority(PackageFormat format) => format switch
    {
        PackageFormat.App => 0,
        PackageFormat.Dmg => 1,
        PackageFormat.Pkg => 2,
        _ => 0
    };

    private static string FormatName(PackageFormat format) => format switch
    {
        PackageFormat.AppImage => "appimage",
        _ => format.ToString().ToLowerInvariant()
    };
}

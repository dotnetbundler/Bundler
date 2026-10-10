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
            BundleTarget.TryParse(configuredTarget.Target, out var target);
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
                    configuration.OutputLayout == OutputLayout.ByFormat
                        ? Path.Combine(configuration.OutputDirectory, FormatName(format))
                        : configuration.OutputDirectory,
                    intermediate)
                {
                    SigningFiles = configuredTarget.SigningFiles.ToArray()
                });
            }
        }

        // 规划期重名断言：同输出目录下产物名必须互异（新后端接入自动罩住）。
        var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => !i.Intermediate))
        {
            var name = ArtifactNaming.FileName(
                configuration.ProductName, configuration.Version, item.Target, item.Format, "1");
            if (!artifactNames.Add(Path.Combine(item.OutputDirectory, name)))
            {
                throw new BundleValidationException([new ValidationIssue(
                    "outputDirectory",
                    $"Artifact name collision: '{name}' is produced more than once into '{item.OutputDirectory}'.")]);
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
        PackageFormat.TarGz => "targz",
        _ => format.ToString().ToLowerInvariant()
    };
}

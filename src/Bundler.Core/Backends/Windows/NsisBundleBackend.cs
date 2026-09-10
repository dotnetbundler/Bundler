using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;
using Bundler.Core.Templates;
using Bundler.Core.Tools;
using System.Runtime.InteropServices;

namespace Bundler.Core.Backends.Windows;

public sealed class NsisBundleBackend(string compilerPath, string templatePath) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Nsis;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<BundleArtifact> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = context.Configuration;
        var item = context.Item;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("NSIS packages can currently be built only on Windows hosts.");
        }

        var fullCompilerPath = Path.GetFullPath(compilerPath);
        if (!File.Exists(fullCompilerPath))
        {
            throw new FileNotFoundException("makensis.exe was not found.", fullCompilerPath);
        }

        var fullTemplatePath = Path.GetFullPath(templatePath);
        if (!File.Exists(fullTemplatePath))
        {
            throw new FileNotFoundException("The NSIS script template was not found.", fullTemplatePath);
        }

        Directory.CreateDirectory(item.OutputDirectory);
        var safeProductName = SafeFileName(configuration.ProductName);
        var installerPath = Path.Combine(
            item.OutputDirectory,
            $"{safeProductName}-{configuration.Version}-setup.exe");
        var scriptPath = Path.Combine(context.WorkDirectory, "installer.nsi");
        cancellationToken.ThrowIfCancellationRequested();
        var template = File.ReadAllText(fullTemplatePath);
        File.WriteAllText(scriptPath, CreateScript(template, configuration, item, installerPath, safeProductName));

        await ProcessRunner.RunAsync(
            fullCompilerPath,
            ["-INPUTCHARSET", "UTF8", "-OUTPUTCHARSET", "UTF8", "/V2", scriptPath],
            context.WorkDirectory,
            cancellationToken);

        if (!File.Exists(installerPath))
        {
            throw new InvalidOperationException("NSIS reported success but did not create the installer.");
        }

        return new BundleArtifact(Format, item.Target.RuntimeIdentifier, installerPath);
    }

    internal static string CreateScript(
        string template,
        BundleConfiguration configuration,
        BundlePlanItem item,
        string installerPath,
        string safeProductName)
    {
        var publisher = configuration.Publisher ?? configuration.ProductName;
        var version = NumericVersion(configuration.Version);

        return TemplateRenderer.Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product_name"] = Escape(configuration.ProductName),
            ["version"] = Escape(configuration.Version),
            ["numeric_version"] = version,
            ["publisher"] = Escape(publisher),
            ["identifier"] = Escape(configuration.Identifier),
            ["main_executable"] = Escape(item.MainExecutable),
            ["process_name"] = Escape(Path.GetFileName(item.MainExecutable)),
            ["install_folder"] = Escape(safeProductName),
            ["input_glob"] = Escape(Path.Combine(item.InputDirectory, "*")),
            ["output_file"] = Escape(installerPath),
            ["estimated_size"] = EstimateSizeInKilobytes(item.InputDirectory).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["uninstall_payload"] = CreateUninstallPayload(item.InputDirectory)
        });
    }

    private static string CreateUninstallPayload(string inputDirectory)
    {
        var root = Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var lines = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            lines.Add($"  Delete /REBOOTOK \"$INSTDIR\\{Escape(RelativePath(root, file))}\"");
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            lines.Add($"  RMDir /REBOOTOK \"$INSTDIR\\{Escape(RelativePath(root, directory))}\"");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RelativePath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Payload path is outside the input directory: {fullPath}");
        }

        return fullPath.Substring(prefix.Length).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string Escape(string value) => value
        .Replace("$", "$$")
        .Replace("\"", "$\\\"")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static string SafeFileName(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? "Application" : result;
    }

    private static string NumericVersion(string version)
    {
        var components = version.Split(new[] { '-', '+' }, 2)[0].Split('.').Take(4).ToList();
        while (components.Count < 4)
        {
            components.Add("0");
        }

        return string.Join(".", components);
    }

    private static long EstimateSizeInKilobytes(string directory)
    {
        var bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        return Math.Max(1, (bytes + 1023) / 1024);
    }
}

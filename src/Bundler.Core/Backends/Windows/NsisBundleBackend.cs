using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;
using Bundler.Core.Templates;
using Bundler.Core.Tools;
using System.Runtime.InteropServices;
using System.Text;

namespace Bundler.Core.Backends.Windows;

public sealed class NsisBundleBackend(string compilerPath, string templatePath, string languageDirectory) : IBundleBackend
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
        var localization = PrepareLanguages(configuration.Nsis, context.WorkDirectory);
        File.WriteAllText(
            scriptPath,
            CreateScript(template, configuration, item, installerPath, safeProductName, localization),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

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
        return CreateScript(
            template,
            configuration,
            item,
            installerPath,
            safeProductName,
            new NsisLocalization(
                "!insertmacro MUI_LANGUAGE \"English\"",
                string.Empty,
                string.Empty));
    }

    private static string CreateScript(
        string template,
        BundleConfiguration configuration,
        BundlePlanItem item,
        string installerPath,
        string safeProductName,
        NsisLocalization localization)
    {
        var publisher = configuration.Publisher ?? configuration.ProductName;
        var description = configuration.Description ?? configuration.ProductName;
        var version = NumericVersion(configuration.Version);
        var resources = ExpandResources(configuration.Resources, item.InputDirectory);
        var iconDirectives = CreateIconDirectives(configuration.Icons);

        return TemplateRenderer.Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product_name"] = Escape(configuration.ProductName),
            ["version"] = Escape(configuration.Version),
            ["numeric_version"] = version,
            ["publisher"] = Escape(publisher),
            ["description"] = Escape(description),
            ["identifier"] = Escape(configuration.Identifier),
            ["main_executable"] = Escape(item.MainExecutable),
            ["process_name"] = Escape(Path.GetFileName(item.MainExecutable)),
            ["install_folder"] = Escape(safeProductName),
            ["input_glob"] = Escape(Path.Combine(item.InputDirectory, "*")),
            ["output_file"] = Escape(installerPath),
            ["estimated_size"] = EstimateSizeInKilobytes(item.InputDirectory).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["installer_icon_directives"] = iconDirectives,
            ["resource_install_commands"] = CreateResourceInstallCommands(resources),
            ["uninstall_payload"] = CreateUninstallPayload(item.InputDirectory, resources),
            ["language_macros"] = localization.LanguageMacros,
            ["language_files"] = localization.LanguageFiles,
            ["display_language_selector"] = localization.DisplayLanguageSelector
        });
    }

    private static string CreateIconDirectives(IReadOnlyList<string> icons)
    {
        var icon = icons.FirstOrDefault(path =>
            Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase));
        if (icon is null)
        {
            if (icons.Count > 0)
            {
                throw new InvalidOperationException("Windows NSIS packaging requires at least one .ico file when icons are configured.");
            }

            return string.Empty;
        }

        var escaped = Escape(Path.GetFullPath(icon));
        return string.Join(Environment.NewLine, new[]
        {
            $"!define MUI_ICON \"{escaped}\"",
            $"!define MUI_UNICON \"{escaped}\"",
            $"Icon \"{escaped}\""
        });
    }

    private static IReadOnlyList<PayloadResource> ExpandResources(
        IReadOnlyList<BundleResourceConfiguration> configuredResources,
        string inputDirectory)
    {
        var inputRoot = Path.GetFullPath(inputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targets = new HashSet<string>(
            Directory.EnumerateFiles(inputRoot, "*", SearchOption.AllDirectories)
                .Select(path => RelativePath(inputRoot, path)),
            StringComparer.OrdinalIgnoreCase);
        var resources = new List<PayloadResource>();

        foreach (var configured in configuredResources)
        {
            var source = Path.GetFullPath(configured.Source);
            var target = NormalizeTargetPath(configured.TargetPath);
            if (File.Exists(source))
            {
                Add(source, target);
                continue;
            }

            if (!Directory.Exists(source))
            {
                throw new FileNotFoundException("Bundle resource was not found.", source);
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                Add(file, Path.Combine(target, RelativePath(source, file)));
            }
        }

        return resources;

        void Add(string source, string target)
        {
            target = NormalizeTargetPath(target);
            if (target.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(".dotnet-bundler-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Resource target '{target}' is reserved by the installer.");
            }

            if (!targets.Add(target))
            {
                throw new InvalidOperationException($"More than one payload file targets '{target}'.");
            }

            resources.Add(new PayloadResource(Path.GetFullPath(source), target));
        }
    }

    private static string NormalizeTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || Path.IsPathRooted(targetPath))
        {
            throw new InvalidOperationException($"Resource target path must be relative: '{targetPath}'.");
        }

        var normalized = targetPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(component => component is "" or "." or ".."))
        {
            throw new InvalidOperationException($"Resource target path must stay inside the installation directory: '{targetPath}'.");
        }

        return normalized;
    }

    private static string CreateResourceInstallCommands(IReadOnlyList<PayloadResource> resources)
    {
        var lines = new List<string>();
        foreach (var resource in resources.OrderBy(resource => resource.TargetPath, StringComparer.OrdinalIgnoreCase))
        {
            var targetDirectory = Path.GetDirectoryName(resource.TargetPath);
            var destination = string.IsNullOrEmpty(targetDirectory)
                ? "$INSTDIR"
                : "$INSTDIR\\" + Escape(targetDirectory);
            lines.Add($"  SetOutPath \"{destination}\"");
            lines.Add($"  File \"/oname={Escape(Path.GetFileName(resource.TargetPath))}\" \"{Escape(resource.Source)}\"");
        }

        if (resources.Count > 0)
        {
            lines.Add("  SetOutPath \"$INSTDIR\"");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private NsisLocalization PrepareLanguages(NsisBundleConfiguration settings, string workDirectory)
    {
        if (settings.Languages.Count == 0)
        {
            throw new InvalidOperationException("At least one NSIS language is required.");
        }

        var languageMacros = new List<string>();
        var languageIncludes = new List<string>();
        for (var index = 0; index < settings.Languages.Count; index++)
        {
            var language = settings.Languages[index];
            if (string.IsNullOrWhiteSpace(language) ||
                !IsValidLanguageName(language))
            {
                throw new InvalidOperationException($"Invalid NSIS language name '{language}'.");
            }

            var customFile = settings.CustomLanguageFiles.FirstOrDefault(
                pair => pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase)).Value;
            var sourcePath = string.IsNullOrWhiteSpace(customFile)
                ? Path.Combine(Path.GetFullPath(languageDirectory), language + ".nsh")
                : Path.GetFullPath(customFile);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"No built-in or custom message file was found for NSIS language '{language}'.",
                    sourcePath);
            }

            var destinationPath = Path.Combine(workDirectory, $"language-{index:D2}-{language}.nsh");
            File.WriteAllText(
                destinationPath,
                File.ReadAllText(sourcePath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            languageMacros.Add($"!insertmacro MUI_LANGUAGE \"{language}\"");
            languageIncludes.Add($"!include \"{Escape(destinationPath)}\"");
        }

        var selector = settings.DisplayLanguageSelector && settings.Languages.Count > 1
            ? "  !define MUI_LANGDLL_ALWAYSSHOW" + Environment.NewLine +
              "  !insertmacro MUI_LANGDLL_DISPLAY" + Environment.NewLine +
              "  !undef MUI_LANGDLL_ALWAYSSHOW"
            : string.Empty;
        return new NsisLocalization(
            string.Join(Environment.NewLine, languageMacros),
            string.Join(Environment.NewLine, languageIncludes),
            selector);
    }

    private static bool IsValidLanguageName(string value) =>
        value.Length > 0 &&
        value[0] is >= 'A' and <= 'Z' &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

    private static string CreateUninstallPayload(
        string inputDirectory,
        IReadOnlyList<PayloadResource> resources)
    {
        var root = Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var lines = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            lines.Add($"  Delete /REBOOTOK \"$INSTDIR\\{Escape(RelativePath(root, file))}\"");
        }

        foreach (var resource in resources)
        {
            lines.Add($"  Delete /REBOOTOK \"$INSTDIR\\{Escape(resource.TargetPath)}\"");
        }

        var directories = new HashSet<string>(
            Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => RelativePath(root, path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            var directory = Path.GetDirectoryName(resource.TargetPath);
            while (!string.IsNullOrEmpty(directory))
            {
                directories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            lines.Add($"  RMDir /REBOOTOK \"$INSTDIR\\{Escape(directory)}\"");
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

    private sealed record NsisLocalization(
        string LanguageMacros,
        string LanguageFiles,
        string DisplayLanguageSelector);

    private sealed record PayloadResource(string Source, string TargetPath);
}

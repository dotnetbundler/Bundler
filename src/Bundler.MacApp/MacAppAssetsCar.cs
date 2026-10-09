using System.Runtime.InteropServices;
using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// Assets.car pipeline: a `.car` input is copied verbatim; a `.icon` (Icon Composer) directory is
/// compiled with actool (Xcode 26+). The app-icon name inside the produced Assets.car is read back
/// with assetutil and becomes CFBundleIconName. Every Apple-tool step degrades with a warning —
/// a missing tool never fails the build (the .icns/CFBundleIconFile fallback still applies).
/// </summary>
internal static class MacAppAssetsCar
{
    /// <summary>Builds Contents/Resources/Assets.car; returns the icon name for CFBundleIconName.</summary>
    internal static async Task<string?> BuildAsync(
        IReadOnlyList<string> icons,
        string resourcesDirectory,
        string workDirectory,
        IBundleLogger logger,
        CancellationToken cancellationToken)
    {
        var paths = icons.Select(Path.GetFullPath).ToArray();
        var car = paths.FirstOrDefault(path => path.EndsWith(".car", StringComparison.OrdinalIgnoreCase));
        var icon = paths.FirstOrDefault(path => path.EndsWith(".icon", StringComparison.OrdinalIgnoreCase));
        if (car is not null && icon is not null)
        {
            logger.Log(BundleLogLevel.Warning,
                $"Both a .car and a .icon were supplied; the compiled Assets.car input wins: {car}");
        }
        var destination = Path.Combine(resourcesDirectory, "Assets.car");
        if (car is not null)
        {
            if (!File.Exists(car))
            {
                throw new FileNotFoundException("The .car icon input does not exist.", car);
            }
            File.Copy(car, destination);
            return await ReadIconName(destination, logger, cancellationToken);
        }
        if (icon is null)
        {
            return null;
        }
        if (!Directory.Exists(icon))
        {
            throw new ArgumentException($"The .icon input must be an Icon Composer directory: {icon}");
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.Log(BundleLogLevel.Warning,
                "Skipping Assets.car: actool is an Xcode tool and this host is not macOS; " +
                "the .icns icon fallback applies.");
            return null;
        }
        var version = await ActoolVersion(workDirectory, cancellationToken);
        if (version is null)
        {
            logger.Log(BundleLogLevel.Warning,
                "Skipping Assets.car: actool --version is unavailable (Xcode 26+ is required).");
            return null;
        }
        if (version.Value < 26)
        {
            logger.Log(BundleLogLevel.Warning,
                $"Skipping Assets.car: actool {version} is older than 26 (Xcode 26+ is required).");
            return null;
        }

        var work = Path.Combine(workDirectory, "macapp-actool");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }
        var iconCopy = Path.Combine(work, "Icon.icon");
        var output = Path.Combine(work, "out");
        CopyDirectory(icon, iconCopy);
        Directory.CreateDirectory(output);

        var arguments = new[]
        {
            iconCopy,
            "--compile", output,
            "--output-format", "human-readable-text",
            "--notices", "--warnings",
            "--output-partial-info-plist", Path.Combine(output, "assetcatalog_generated_info.plist"),
            "--app-icon", "Icon",
            "--include-all-app-icons",
            "--accent-color", "AccentColor",
            "--enable-on-demand-resources", "NO",
            "--development-region", "en",
            "--target-device", "mac",
            "--minimum-deployment-target", "26.0",
            "--platform", "macosx"
        };
        var result = await MacProcessRunner.TryRunAsync("actool", arguments, workDirectory, cancellationToken);
        var produced = Path.Combine(output, "Assets.car");
        if (result is not { ExitCode: 0 } || !File.Exists(produced))
        {
            logger.Log(BundleLogLevel.Warning,
                "Skipping Assets.car: actool did not produce it" +
                (result is null ? " (tool failed to start)" :
                    $" (exit {result.ExitCode}: {result.StandardError.Trim()}{result.StandardOutput.Trim()})") + ".");
            return null;
        }
        File.Copy(produced, destination);
        return await ReadIconName(destination, logger, cancellationToken);
    }

    private static async Task<double?> ActoolVersion(string workDirectory, CancellationToken cancellationToken)
    {
        var result = await MacProcessRunner.TryRunAsync(
            "actool", ["--version", "--output-format", "human-readable-text"],
            workDirectory, cancellationToken);
        if (result is null or { ExitCode: not 0 })
        {
            return null;
        }
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            const string prefix = "short-bundle-version:";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
                double.TryParse(trimmed.Substring(prefix.Length).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var version))
            {
                return version;
            }
        }
        return null;
    }

    private static async Task<string?> ReadIconName(
        string assetsCarPath, IBundleLogger logger, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.Log(BundleLogLevel.Warning,
                "Assets.car was copied, but reading its icon name needs assetutil (macOS/Xcode); " +
                "CFBundleIconName is omitted.");
            return null;
        }
        var result = await MacProcessRunner.TryRunAsync(
            "assetutil", ["--info", assetsCarPath], Path.GetDirectoryName(assetsCarPath)!, cancellationToken);
        if (result is not { ExitCode: 0 })
        {
            logger.Log(BundleLogLevel.Warning,
                "assetutil could not inspect Assets.car; CFBundleIconName is omitted.");
            return null;
        }
        var name = IconImageName(result.StandardOutput);
        if (name is null)
        {
            logger.Log(BundleLogLevel.Warning,
                "No 'Icon Image' asset found in Assets.car; CFBundleIconName is omitted.");
        }
        return name;
    }

    /// <summary>
    /// Extracts "Name" of the assetutil --info JSON object whose "AssetType" is "Icon Image".
    /// The output is a flat array of objects; the match is scoped to the enclosing object.
    /// </summary>
    internal static string? IconImageName(string assetutilJson)
    {
        const string marker = "\"AssetType\"";
        var position = 0;
        while (true)
        {
            var found = assetutilJson.IndexOf(marker, position, StringComparison.Ordinal);
            if (found < 0)
            {
                return null;
            }
            position = found + marker.Length;
            var objectStart = assetutilJson.LastIndexOf('{', found);
            if (objectStart < 0)
            {
                continue;
            }
            var depth = 0;
            var objectEnd = -1;
            for (var index = objectStart; index < assetutilJson.Length; index++)
            {
                if (assetutilJson[index] == '{') depth++;
                else if (assetutilJson[index] == '}')
                {
                    if (--depth == 0)
                    {
                        objectEnd = index;
                        break;
                    }
                }
            }
            if (objectEnd < 0)
            {
                return null;
            }
            var body = assetutilJson.Substring(objectStart, objectEnd - objectStart + 1);
            var type = JsonScalar(body, "AssetType");
            if (type != "Icon Image")
            {
                continue;
            }
            return JsonScalar(body, "Name");
        }
    }

    private static string? JsonScalar(string jsonObject, string key)
    {
        var token = "\"" + key + "\"";
        var position = jsonObject.IndexOf(token, StringComparison.Ordinal);
        if (position < 0)
        {
            return null;
        }
        position += token.Length;
        while (position < jsonObject.Length && (jsonObject[position] == ':' || char.IsWhiteSpace(jsonObject[position])))
        {
            position++;
        }
        if (position >= jsonObject.Length || jsonObject[position] != '"')
        {
            return null;
        }
        var end = jsonObject.IndexOf('"', position + 1);
        return end < 0 ? null : jsonObject.Substring(position + 1, end - position - 1);
    }

    private static void CopyDirectory(string source, string destination, int depth = 0)
    {
        // 目录符号链接环会让递归失控——深度封顶显式报错。
        if (depth > 64)
        {
            throw new InvalidDataException(
                "Directory nesting too deep inside the .icon input (possible symlink loop): " + source);
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), depth + 1);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}

using System.Reflection;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Nsis;

internal static class NsisEmbeddedResources
{
    private const string Prefix = "DotNet.Bundler.Nsis.Resources.";

    public static async Task<NsisResourcePaths> MaterializeAsync(
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(NsisEmbeddedResources).Assembly;
        var informationVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "0";
        var version = new string(informationVersion
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-'
                ? character
                : '_')
            .ToArray());
        var resourceDirectory = Path.Combine(cacheDirectory, "resources", version);
        var archivePath = Path.Combine(resourceDirectory, "nsis-toolset-3.12-r1.zip");
        var templatePath = Path.Combine(resourceDirectory, "templates", "installer.nsi");
        var languageDirectory = Path.Combine(resourceDirectory, "templates", "languages");
        var pluginDirectory = Path.Combine(resourceDirectory, "plugins", "x86-unicode");
        var lockDirectory = Path.Combine(cacheDirectory, ".resource-locks");

        await WriteVerifiedAsync(assembly, Prefix + "nsis-toolset-3.12-r1.zip", archivePath, lockDirectory, cancellationToken);
        await WriteVerifiedAsync(assembly, Prefix + "installer.nsi", templatePath, lockDirectory, cancellationToken);
        foreach (var language in NsisLanguageCatalog.Definitions)
        {
            await WriteVerifiedAsync(
                assembly,
                Prefix + "languages." + language.Name + ".nsh",
                Path.Combine(languageDirectory, language.Name + ".nsh"),
                lockDirectory,
                cancellationToken);
        }
        await WriteVerifiedAsync(
            assembly,
            Prefix + "plugins.DotNetBundlerNsis.dll",
            Path.Combine(pluginDirectory, "DotNetBundlerNsis.dll"),
            lockDirectory,
            cancellationToken);

        return new NsisResourcePaths(archivePath, templatePath, languageDirectory, pluginDirectory);
    }

    private static async Task WriteVerifiedAsync(
        Assembly assembly,
        string resourceName,
        string destinationPath,
        string lockDirectory,
        CancellationToken cancellationToken)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"Embedded NSIS resource '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        await resource.CopyToAsync(memory, 81920, cancellationToken);
        var expected = ComputeHash(memory.GetBuffer(), checked((int)memory.Length));

        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, expected + ".lock");
        using var resourceLock = await ToolCache.AcquireLockAsync(lockPath, cancellationToken);

        if (File.Exists(destinationPath) && HashFile(destinationPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            memory.Position = 0;
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await memory.CopyToAsync(output, 81920, cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (
                File.Exists(destinationPath) &&
                HashFile(destinationPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                // Another process materialized the same immutable resource first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string HashFile(string path) => Hashes.Sha256HexFile(path);

    private static string ComputeHash(byte[] bytes, int count)
    {
        using var slice = new MemoryStream(bytes, 0, count, writable: false);
        return Hashes.Sha256Hex(slice);
    }

    internal sealed record NsisResourcePaths(
        string ToolsetArchivePath,
        string TemplatePath,
        string LanguageDirectory,
        string PluginDirectory);
}

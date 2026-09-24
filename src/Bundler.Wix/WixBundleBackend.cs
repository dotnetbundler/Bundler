using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DotNet.Bundler;

namespace DotNet.Bundler.Wix;

internal sealed class WixBundleBackend(WixToolset toolset, WixBundleConfiguration settings) : IBundleBackend
{
    private static readonly XNamespace Wix = "http://schemas.microsoft.com/wix/2006/wi";
    private const string GeneratorRevision = "win-msi-2-2026-09-24-1";

    public PackageFormat Format => PackageFormat.Msi;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<BundleArtifact> BuildAsync(BundleBuildContext context, CancellationToken cancellationToken = default)
    {
        var bundle = context.Configuration;
        var item = context.Item;
        var identity = WixIdentity.Create(bundle.Identifier, bundle.Version, item.Target.RuntimeIdentifier,
            settings.InstallScope, settings.UpgradeCode);
        var files = CollectFiles(bundle, item);
        var icon = SelectIcon(bundle.Icons);
        var definitionHash = DefinitionHash(bundle, item, files, icon);
        var product = new WixProductDocument(settings).Create(bundle, item, identity, files, icon, definitionHash);
        var outputName = WixProductDocument.SafeFileName(bundle.ProductName) + "-" + identity.ProductVersion + ".msi";
        var outputPath = Path.Combine(item.OutputDirectory, outputName);
        var fingerprint = Fingerprint(bundle, item, identity, files, icon, product);
        var manifestPath = outputPath + ".bundler-manifest";
        Directory.CreateDirectory(item.OutputDirectory);
        var lockPath = outputPath + ".bundler-lock";
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("MSI output lock must not be a reparse point: " + lockPath);
        }
        using var outputLock = await AcquireOutputLockAsync(lockPath, cancellationToken);
        if (File.Exists(outputPath) || File.Exists(manifestPath))
        {
            if (File.Exists(outputPath) && File.Exists(manifestPath) &&
                File.ReadAllText(manifestPath).Equals(
                    fingerprint + "\n" + HashFile(outputPath) + "\n", StringComparison.Ordinal))
            {
                return new BundleArtifact(Format, item.Target.RuntimeIdentifier, outputPath);
            }
            throw new IOException("An MSI with the same product version already exists with different or unverified contents: " + outputPath);
        }

        var source = Path.Combine(context.WorkDirectory, "product.wxs");
        var obj = Path.Combine(context.WorkDirectory, "product.wixobj");
        new XDocument(new XDeclaration("1.0", "utf-8", null), product)
            .Save(source);

        try
        {
            await WixProcessRunner.RunAsync(toolset.CandlePath,
                ["-nologo", "-arch", item.Target.Architecture == CpuArchitecture.Arm64 ? "arm64" : "x64",
                 "-out", obj, source], context.WorkDirectory, cancellationToken);
            await WixProcessRunner.RunAsync(toolset.LightPath,
                settings.InstallScope == WixInstallScope.CurrentUser
                    ? ["-nologo", "-sice:ICE91", "-out", outputPath, obj]
                    : ["-nologo", "-out", outputPath, obj],
                context.WorkDirectory, cancellationToken);
            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("WiX reported success without producing an MSI.");
            }
            File.WriteAllText(manifestPath, fingerprint + "\n" + HashFile(outputPath) + "\n", Encoding.ASCII);
            return new BundleArtifact(Format, item.Target.RuntimeIdentifier, outputPath);
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            throw;
        }
    }

    private static IReadOnlyList<InstallFile> CollectFiles(BundleConfiguration bundle, BundlePlanItem item)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTree(item.InputDirectory, "");
        foreach (var resource in bundle.Resources)
        {
            var target = WixPackagePaths.NormalizeTarget(resource.TargetPath);
            if (File.Exists(resource.Source))
            {
                AddFile(resource.Source, target);
            }
            else if (Directory.Exists(resource.Source))
            {
                AddTree(resource.Source, target);
            }
            else
            {
                throw new FileNotFoundException("MSI resource does not exist.", resource.Source);
            }
        }
        if (!files.ContainsKey(WixPackagePaths.NormalizeTarget(item.MainExecutable)))
        {
            throw new FileNotFoundException("The MSI main executable is missing from the input directory.", item.MainExecutable);
        }
        return files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new InstallFile(pair.Key, pair.Value)).ToArray();

        void AddTree(string root, string prefix)
        {
            var fullRoot = Path.GetFullPath(root);
            CheckReparse(fullRoot);
            Visit(fullRoot, "");
            void Visit(string directory, string relative)
            {
                CheckReparse(directory);
                foreach (var file in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = relative.Length == 0 ? Path.GetFileName(file) : relative + "/" + Path.GetFileName(file);
                    AddFile(file, prefix.Length == 0 ? rel : prefix + "/" + rel);
                }
                foreach (var child in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = relative.Length == 0 ? Path.GetFileName(child) : relative + "/" + Path.GetFileName(child);
                    Visit(child, rel);
                }
            }
        }

        void AddFile(string source, string target)
        {
            CheckReparse(source);
            target = WixPackagePaths.NormalizeTarget(target);
            if (files.ContainsKey(target))
            {
                throw new ArgumentException("Two MSI files map to the same installation path: " + target);
            }
            files.Add(target, Path.GetFullPath(source));
        }
    }

    private static void CheckReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("MSI input must not contain reparse points: " + path);
        }
    }

    private static string? SelectIcon(IReadOnlyList<string> icons)
    {
        if (icons.Count == 0) return null;
        if (icons.Any(icon => !Path.GetExtension(icon).Equals(".ico", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException("WIN-MSI-1 accepts .ico icons only.");
        }
        foreach (var icon in icons) CheckReparse(icon);
        return Path.GetFullPath(icons[0]);
    }

    private string Fingerprint(BundleConfiguration bundle, BundlePlanItem item,
        WixIdentity identity, IReadOnlyList<InstallFile> files, string? icon, XElement product)
    {
        var canonical = new XElement(product);
        canonical.Descendants(Wix + "Package").Single().SetAttributeValue("Id", "{00000000-0000-0000-0000-000000000000}");
        var text = new StringBuilder()
            .AppendLine(GeneratorRevision)
            .AppendLine(WixToolsetResolver.ArchiveSha256)
            .AppendLine(bundle.ProductName).AppendLine(bundle.Identifier.ToLowerInvariant())
            .AppendLine(identity.ProductVersion).AppendLine(bundle.Publisher)
            .AppendLine(bundle.Description).AppendLine(bundle.Homepage)
            .Append(settings.Codepage).AppendLine()
            .AppendLine(item.Target.RuntimeIdentifier).AppendLine(identity.UpgradeCode.ToString("D"));
        foreach (var file in files)
        {
            text.AppendLine(file.RelativePath.ToLowerInvariant()).AppendLine(HashFile(file.SourcePath));
        }
        if (icon is not null) text.AppendLine(HashFile(icon));
        text.AppendLine(canonical.ToString(SaveOptions.DisableFormatting));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private string DefinitionHash(BundleConfiguration bundle, BundlePlanItem item,
        IReadOnlyList<InstallFile> files, string? icon)
    {
        var text = new StringBuilder().AppendLine(GeneratorRevision).AppendLine(bundle.ProductName)
            .AppendLine(bundle.Identifier.ToLowerInvariant()).AppendLine(bundle.Version)
            .AppendLine(bundle.Publisher).AppendLine(bundle.Description).AppendLine(bundle.Homepage)
            .AppendLine(item.Target.RuntimeIdentifier).AppendLine(settings.InstallScope.ToString())
            .AppendLine(settings.StartMenuShortcut.ToString()).AppendLine(settings.DesktopShortcut.ToString())
            .AppendLine(settings.Codepage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var association in bundle.FileAssociations)
        {
            text.AppendLine(string.Join(",", association.Extensions)).AppendLine(association.Name)
                .AppendLine(association.Description).AppendLine(association.MimeType);
        }
        foreach (var protocol in bundle.UrlProtocols)
            text.AppendLine(string.Join(",", protocol.Schemes)).AppendLine(protocol.Name);
        foreach (var file in files)
            text.AppendLine(file.RelativePath.ToLowerInvariant()).AppendLine(HashFile(file.SourcePath));
        if (icon is not null) text.AppendLine(HashFile(icon));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }

    private static async Task<FileStream> AcquireOutputLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

}

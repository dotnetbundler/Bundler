using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DotNet.Bundler;

namespace DotNet.Bundler.Wix;

internal sealed class WixBundleBackend(WixToolset toolset, WixBundleConfiguration settings, IBundleSigner? signer) : IBundleBackend
{
    private const string GeneratorRevision = "win-msi-4-2026-09-25-1";

    public PackageFormat Format => PackageFormat.Msi;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<BundleArtifact> BuildAsync(BundleBuildContext context, CancellationToken cancellationToken = default)
    {
        var bundle = context.Configuration;
        var item = context.Item;
        var identity = WixIdentity.Create(bundle.Identifier, bundle.Version, item.Target.RuntimeIdentifier,
            settings.InstallScope, settings.UpgradeCode, settings.Language);
        var outputName = WixProductDocument.SafeFileName(bundle.ProductName) + "-" + identity.ProductVersion +
            settings.LanguageSuffix + ".msi";
        var outputPath = Path.Combine(item.OutputDirectory, outputName);
        var manifestPath = outputPath + ".bundler-manifest";
        Directory.CreateDirectory(item.OutputDirectory);
        var lockPath = outputPath + ".bundler-lock";
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("MSI output lock must not be a reparse point: " + lockPath);
        }
        using var outputLock = await AcquireOutputLockAsync(lockPath, cancellationToken);
        if (signer is not null && (File.Exists(outputPath) || File.Exists(manifestPath)))
            throw new IOException("An MSI with the same product version already exists with signed or unverified contents: " + outputPath);
        // Validate every source before copying payload into a private staging tree.
        CollectFiles(bundle, item);
        if (signer is not null)
            item = await PrepareSignedPayloadAsync(context, item, signer, cancellationToken);
        var files = CollectFiles(bundle, item);
        var icon = SelectIcon(bundle.Icons);
        var definitionHash = DefinitionHash(bundle, item, files, icon);
        var product = new WixProductDocument(settings).Create(bundle, item, identity, files, icon, definitionHash);
        var fingerprint = Fingerprint(bundle, item, identity, files, icon, product);
        if (File.Exists(outputPath) || File.Exists(manifestPath))
        {
            if (signer is null && File.Exists(outputPath) && File.Exists(manifestPath) &&
                File.ReadAllText(manifestPath).Equals(
                    fingerprint + "\n" + HashFile(outputPath) + "\n", StringComparison.Ordinal))
            {
                return new BundleArtifact(Format, item.Target.RuntimeIdentifier, outputPath);
            }
            throw new IOException("An MSI with the same product version already exists with different, signed, or unverified contents: " + outputPath);
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
            var lightArguments = new List<string> { "-nologo" };
            if (!string.IsNullOrWhiteSpace(bundle.LicenseFile))
                lightArguments.AddRange(["-ext", toolset.UiExtensionPath, "-cultures:" + settings.Culture]);
            if (settings.InstallScope == WixInstallScope.CurrentUser) lightArguments.Add("-sice:ICE91");
            lightArguments.AddRange(["-out", outputPath, obj]);
            await WixProcessRunner.RunAsync(toolset.LightPath, lightArguments, context.WorkDirectory, cancellationToken);
            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("WiX reported success without producing an MSI.");
            }
            if (signer is not null)
            {
                context.Logger.Log(BundleLogLevel.Information, "Signing the MSI installer.");
                await signer.SignAsync(new BundleSigningRequest(outputPath, BundleSigningArtifactKind.Installer,
                    bundle.ProductName, item.Target.RuntimeIdentifier), cancellationToken);
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

    private static async Task<BundlePlanItem> PrepareSignedPayloadAsync(
        BundleBuildContext context, BundlePlanItem item, IBundleSigner signer, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(item.InputDirectory);
        var destination = Path.Combine(context.WorkDirectory, "signed-payload");
        Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            CheckReparse(source);
            var relative = source.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var signingFiles = new[] { item.MainExecutable }.Concat(item.SigningFiles).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in signingFiles)
        {
            var normalized = WixPackagePaths.NormalizeTarget(relativePath);
            var path = Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException("The configured MSI signing file was not found in the input directory.", path);
            var kind = relativePath.Equals(item.MainExecutable, StringComparison.OrdinalIgnoreCase)
                ? BundleSigningArtifactKind.PayloadExecutable : BundleSigningArtifactKind.PayloadFile;
            context.Logger.Log(BundleLogLevel.Information, "Signing staged MSI payload file '" + normalized + "'.");
            await signer.SignAsync(new BundleSigningRequest(path, kind, context.Configuration.ProductName,
                item.Target.RuntimeIdentifier), cancellationToken);
        }
        return item with { InputDirectory = destination };
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
        var text = new StringBuilder()
            .AppendLine(GeneratorRevision)
            .AppendLine(WixToolsetResolver.ArchiveSha256)
            .AppendLine(bundle.ProductName).AppendLine(bundle.Identifier.ToLowerInvariant())
            .AppendLine(identity.ProductVersion).AppendLine(bundle.Publisher)
            .AppendLine(bundle.Description).AppendLine(bundle.Homepage)
            .Append(settings.EffectiveCodepage).AppendLine()
            .AppendLine(item.Target.RuntimeIdentifier).AppendLine(identity.UpgradeCode.ToString("D"));
        foreach (var file in files)
        {
            text.AppendLine(file.RelativePath.ToLowerInvariant()).AppendLine(HashFile(file.SourcePath));
        }
        if (icon is not null) text.AppendLine(HashFile(icon));
        if (bundle.LicenseFile is not null) text.AppendLine(HashFile(bundle.LicenseFile));
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
            .AppendLine(settings.EffectiveCodepage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(settings.Language.ToString());
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
        if (bundle.LicenseFile is not null) text.AppendLine(HashFile(bundle.LicenseFile));
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

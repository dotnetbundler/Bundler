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
        var product = CreateProduct(bundle, item, identity, files, icon, definitionHash);
        var outputName = SafeFileName(bundle.ProductName) + "-" + identity.ProductVersion + ".msi";
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

    private XElement CreateProduct(
        BundleConfiguration bundle,
        BundlePlanItem item,
        WixIdentity identity,
        IReadOnlyList<InstallFile> files,
        string? icon,
        string definitionHash)
    {
        var product = new XElement(Wix + "Product",
            new XAttribute("Id", identity.ProductCode.ToString("B").ToUpperInvariant()),
            new XAttribute("Codepage", settings.Codepage),
            new XAttribute("Name", bundle.ProductName),
            new XAttribute("Language", "1033"),
            new XAttribute("Version", identity.ProductVersion),
            new XAttribute("Manufacturer", bundle.Publisher ?? bundle.ProductName),
            new XAttribute("UpgradeCode", identity.UpgradeCode.ToString("B").ToUpperInvariant()),
            new XElement(Wix + "Package",
                new XAttribute("Id", identity.PackageCode.ToString("B").ToUpperInvariant()),
                new XAttribute("InstallerVersion", "500"),
                new XAttribute("Compressed", "yes"),
                new XAttribute("SummaryCodepage", settings.Codepage),
                new XAttribute("InstallScope", settings.InstallScope == WixInstallScope.CurrentUser ? "perUser" : "perMachine")),
            new XElement(Wix + "MediaTemplate", new XAttribute("EmbedCab", "yes")));
        product.Add(new XElement(Wix + "MajorUpgrade",
            new XAttribute("Schedule", "afterInstallInitialize"),
            new XAttribute("AllowSameVersionUpgrades", "no"),
            new XAttribute("DowngradeErrorMessage", "A newer version of [ProductName] is already installed.")));
        var registrationRoot = settings.InstallScope == WixInstallScope.CurrentUser ? "HKCU" : "HKLM";
        var definitionKey = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
            "\\" + item.Target.RuntimeIdentifier + "\\Components";
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_INSTALLED_DEFINITION"),
            new XElement(Wix + "RegistrySearch", new XAttribute("Id", "FindBundlerDefinition"),
                new XAttribute("Root", registrationRoot), new XAttribute("Key", definitionKey),
                new XAttribute("Name", "DefinitionHash"), new XAttribute("Type", "raw"))));
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_PACKAGE_DEFINITION"),
            new XAttribute("Value", definitionHash)));
        product.Add(new XElement(Wix + "Condition",
            new XAttribute("Message", "A different MSI package already uses this product version. Use a new product version."),
            "REMOVE=\"ALL\" OR NOT Installed OR NOT BUNDLER_INSTALLED_DEFINITION OR BUNDLER_INSTALLED_DEFINITION=BUNDLER_PACKAGE_DEFINITION"));
        if (!string.IsNullOrWhiteSpace(bundle.Description))
        {
            product.Add(new XElement(Wix + "Property", new XAttribute("Id", "ARPCOMMENTS"),
                new XAttribute("Value", bundle.Description)));
        }
        if (!string.IsNullOrWhiteSpace(bundle.Homepage))
        {
            product.Add(new XElement(Wix + "Property", new XAttribute("Id", "ARPURLINFOABOUT"),
                new XAttribute("Value", bundle.Homepage)));
        }
        if (icon is not null)
        {
            product.Add(new XElement(Wix + "Icon", new XAttribute("Id", "ProductIcon"),
                new XAttribute("SourceFile", icon)));
            product.Add(new XElement(Wix + "Property", new XAttribute("Id", "ARPPRODUCTICON"),
                new XAttribute("Value", "ProductIcon")));
        }

        var targetDir = new XElement(Wix + "Directory", new XAttribute("Id", "TARGETDIR"),
            new XAttribute("Name", "SourceDir"));
        var installRoot = new XElement(Wix + "Directory", new XAttribute("Id",
            settings.InstallScope == WixInstallScope.CurrentUser ? "LocalAppDataFolder" : "ProgramFiles64Folder"));
        var programs = settings.InstallScope == WixInstallScope.CurrentUser
            ? new XElement(Wix + "Directory", new XAttribute("Id", "BundlerProgramsDir"), new XAttribute("Name", "Programs"))
            : installRoot;
        var app = new XElement(Wix + "Directory", new XAttribute("Id", "INSTALLFOLDER"),
            new XAttribute("Name", bundle.Identifier.ToLowerInvariant() + "-" +
                (item.Target.Architecture == CpuArchitecture.Arm64 ? "arm64" : "x64")));
        programs.Add(app);
        if (settings.InstallScope == WixInstallScope.CurrentUser) installRoot.Add(programs);
        targetDir.Add(installRoot);
        if (settings.StartMenuShortcut)
        {
            targetDir.Add(new XElement(Wix + "Directory", new XAttribute("Id", "ProgramMenuFolder"),
                new XElement(Wix + "Directory", new XAttribute("Id", "BundlerStartMenuFolder"),
                    new XAttribute("Name", bundle.Identifier.ToLowerInvariant()))));
        }
        if (settings.DesktopShortcut)
            targetDir.Add(new XElement(Wix + "Directory", new XAttribute("Id", "DesktopFolder")));
        product.Add(targetDir);

        var directories = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = app
        };
        var directoryIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = "INSTALLFOLDER"
        };
        var feature = new XElement(Wix + "Feature", new XAttribute("Id", "Complete"),
            new XAttribute("Title", bundle.ProductName), new XAttribute("Level", "1"));
        var registryRoot = settings.InstallScope == WixInstallScope.CurrentUser ? "HKCU" : "HKLM";
        var registryKey = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
            "\\" + item.Target.RuntimeIdentifier + "\\Components";
        var mainExecutable = NormalizeTarget(item.MainExecutable!);

        foreach (var file in files)
        {
            var parentPath = ParentPath(file.RelativePath);
            var directory = GetDirectory(parentPath);
            var componentId = WixIdentity.StableId("Cmp", file.RelativePath);
            var fileElement = new XElement(Wix + "File", new XAttribute("Id", WixIdentity.StableId("Fil", file.RelativePath)),
                new XAttribute("Name", Path.GetFileName(file.RelativePath)),
                new XAttribute("Source", file.SourcePath));
            if (settings.InstallScope == WixInstallScope.PerMachine)
                fileElement.Add(new XAttribute("KeyPath", "yes"));
            if (file.RelativePath.Equals(mainExecutable, StringComparison.OrdinalIgnoreCase))
            {
                if (settings.StartMenuShortcut)
                    fileElement.Add(new XElement(Wix + "Shortcut", new XAttribute("Id", "StartMenuShortcut"),
                        new XAttribute("Directory", "BundlerStartMenuFolder"),
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName)),
                        new XAttribute("Advertise", settings.InstallScope == WixInstallScope.PerMachine ? "yes" : "no"),
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
                if (settings.DesktopShortcut)
                    fileElement.Add(new XElement(Wix + "Shortcut", new XAttribute("Id", "DesktopShortcut"),
                        new XAttribute("Directory", "DesktopFolder"),
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName) + " (" + bundle.Identifier.ToLowerInvariant() + ")"),
                        new XAttribute("Advertise", settings.InstallScope == WixInstallScope.PerMachine ? "yes" : "no"),
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
            }
            directory.Add(new XElement(Wix + "Component",
                new XAttribute("Id", componentId),
                new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                    item.Target.RuntimeIdentifier, settings.InstallScope, file.RelativePath).ToString("B").ToUpperInvariant()),
                fileElement,
                new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                    new XAttribute("Key", registryKey), new XAttribute("Name", componentId),
                    new XAttribute("Type", "integer"), new XAttribute("Value", "1"),
                    settings.InstallScope == WixInstallScope.CurrentUser
                        ? new XAttribute("KeyPath", "yes") : null)));
            feature.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", componentId)));
        }

        var cleanup = new XElement(Wix + "Component", new XAttribute("Id", "Cleanup"),
            new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                item.Target.RuntimeIdentifier, settings.InstallScope, "!cleanup").ToString("B").ToUpperInvariant()),
            new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                new XAttribute("Key", registryKey), new XAttribute("Name", "Cleanup"),
                new XAttribute("Type", "integer"), new XAttribute("Value", "1"),
                new XAttribute("KeyPath", "yes")),
            new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                new XAttribute("Key", registryKey), new XAttribute("Name", "DefinitionHash"),
                new XAttribute("Type", "string"), new XAttribute("Value", definitionHash)));
        foreach (var path in directoryIds.Keys.Where(path => path.Length > 0)
                     .OrderByDescending(path => path.Count(character => character == '/'))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cleanup.Add(new XElement(Wix + "RemoveFolder",
                new XAttribute("Id", WixIdentity.StableId("Rem", path)),
                new XAttribute("Directory", directoryIds[path]), new XAttribute("On", "uninstall")));
        }
        cleanup.Add(new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveAppFolder"),
            new XAttribute("Directory", "INSTALLFOLDER"), new XAttribute("On", "uninstall")));
        if (settings.InstallScope == WixInstallScope.CurrentUser)
        {
            cleanup.Add(new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveProgramsFolder"),
                new XAttribute("Directory", "BundlerProgramsDir"), new XAttribute("On", "uninstall")));
        }
        if (settings.StartMenuShortcut)
            cleanup.Add(new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveStartMenuFolder"),
                new XAttribute("Directory", "BundlerStartMenuFolder"), new XAttribute("On", "uninstall")));
        app.Add(cleanup);
        feature.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "Cleanup")));
        AddDesktopRegistrations();
        product.Add(feature);
        return new XElement(Wix + "Wix", product);

        XElement GetDirectory(string path)
        {
            if (directories.TryGetValue(path, out var existing)) return existing;
            var parent = ParentPath(path);
            var element = new XElement(Wix + "Directory",
                new XAttribute("Id", WixIdentity.StableId("Dir", path)),
                new XAttribute("Name", path.Substring(parent.Length == 0 ? 0 : parent.Length + 1)));
            GetDirectory(parent).Add(element);
            directories[path] = element;
            directoryIds[path] = (string)element.Attribute("Id")!;
            return element;
        }

        void AddDesktopRegistrations()
        {
            if (bundle.FileAssociations.Count == 0 && bundle.UrlProtocols.Count == 0) return;
            var applicationId = bundle.Identifier.ToLowerInvariant() + "." + item.Target.RuntimeIdentifier;
            var capabilities = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
                "\\" + item.Target.RuntimeIdentifier + "\\Capabilities";
            var command = "\"[INSTALLFOLDER]" + mainExecutable.Replace('/', '\\') + "\" \"%1\"";
            var entries = new List<(string Key, string? Name, string Value)> {
                (capabilities, "ApplicationName", bundle.ProductName),
                (capabilities, "ApplicationDescription", bundle.Description ?? bundle.ProductName),
                ("Software\\RegisteredApplications", applicationId, capabilities)
            };
            var mimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var association in bundle.FileAssociations)
            foreach (var rawExtension in association.Extensions)
            {
                var extension = rawExtension.Trim().TrimStart('.').ToLowerInvariant();
                var progId = applicationId + ".file." + extension;
                entries.Add(("Software\\Classes\\" + progId, null, association.Description ?? association.Name ?? bundle.ProductName));
                entries.Add(("Software\\Classes\\" + progId + "\\shell\\open\\command", null, command));
                entries.Add(("Software\\Classes\\." + extension + "\\OpenWithProgids", progId, ""));
                entries.Add((capabilities + "\\FileAssociations", "." + extension, progId));
                if (!string.IsNullOrWhiteSpace(association.MimeType) && mimeTypes.Add(association.MimeType!))
                    entries.Add((capabilities + "\\MIMEAssociations", association.MimeType, progId));
            }
            foreach (var protocol in bundle.UrlProtocols)
            foreach (var rawScheme in protocol.Schemes)
            {
                var scheme = rawScheme.Trim().ToLowerInvariant();
                var progId = applicationId + ".url." + scheme;
                entries.Add(("Software\\Classes\\" + progId, null, protocol.Name ?? bundle.ProductName));
                entries.Add(("Software\\Classes\\" + progId, "URL Protocol", ""));
                entries.Add(("Software\\Classes\\" + progId + "\\shell\\open\\command", null, command));
                entries.Add((capabilities + "\\UrlAssociations", scheme, progId));
            }
            var registration = new XElement(Wix + "Component", new XAttribute("Id", "DesktopRegistration"),
                new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                    item.Target.RuntimeIdentifier, settings.InstallScope, "!desktop-registration").ToString("B").ToUpperInvariant()));
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var value = new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                    new XAttribute("Key", entry.Key), new XAttribute("Type", "string"),
                    new XAttribute("Value", entry.Value));
                if (entry.Name is not null) value.Add(new XAttribute("Name", entry.Name));
                if (index == 0) value.Add(new XAttribute("KeyPath", "yes"));
                registration.Add(value);
            }
            app.Add(registration);
            feature.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "DesktopRegistration")));
        }
    }

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path.Substring(0, index);
    }

    private static IReadOnlyList<InstallFile> CollectFiles(BundleConfiguration bundle, BundlePlanItem item)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTree(item.InputDirectory, "");
        foreach (var resource in bundle.Resources)
        {
            var target = NormalizeTarget(resource.TargetPath);
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
        if (!files.ContainsKey(NormalizeTarget(item.MainExecutable)))
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
            target = NormalizeTarget(target);
            if (files.ContainsKey(target))
            {
                throw new ArgumentException("Two MSI files map to the same installation path: " + target);
            }
            files.Add(target, Path.GetFullPath(source));
        }
    }

    private static string NormalizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) ||
            target.Contains(":") || target.Contains("\0"))
        {
            throw new ArgumentException("MSI target paths must be non-empty relative paths.");
        }
        var parts = target.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part == "." || part == ".." ||
                              part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)))
        {
            throw new ArgumentException("MSI target path contains an unsafe segment: " + target);
        }
        return string.Join("/", parts);
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

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return safe.Length == 0 ? "application" : safe;
    }

    private static string SafeShortcutName(string name)
    {
        var safe = SafeFileName(name).TrimEnd('.', ' ');
        if (safe.Length == 0) return "application";
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
            "LPT7", "LPT8", "LPT9" };
        return reserved.Contains(safe, StringComparer.OrdinalIgnoreCase) ? safe + " app" : safe;
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

    private sealed record InstallFile(string RelativePath, string SourcePath);
}

using System.Xml.Linq;
using DotNet.Bundler;

namespace DotNet.Bundler.Wix;

internal sealed class WixProductDocument(WixBundleConfiguration settings)
{
    private static readonly XNamespace Wix = "http://schemas.microsoft.com/wix/2006/wi";

    internal XElement Create(
        BundleConfiguration bundle,
        BundlePlanItem item,
        WixIdentity identity,
        IReadOnlyList<InstallFile> files,
        string? icon,
        string definitionHash)
    {
        var product = new XElement(Wix + "Product",
            new XAttribute("Id", identity.ProductCode.ToString("B").ToUpperInvariant()),
            new XAttribute("Codepage", settings.EffectiveCodepage),
            new XAttribute("Name", bundle.ProductName),
            new XAttribute("Language", settings.ProductLanguage),
            new XAttribute("Version", identity.ProductVersion),
            new XAttribute("Manufacturer", bundle.Publisher ?? bundle.ProductName),
            new XAttribute("UpgradeCode", identity.UpgradeCode.ToString("B").ToUpperInvariant()),
            new XElement(Wix + "Package",
                new XAttribute("Id", identity.PackageCode.ToString("B").ToUpperInvariant()),
                new XAttribute("InstallerVersion", "500"),
                new XAttribute("Compressed", "yes"),
                new XAttribute("SummaryCodepage", settings.EffectiveCodepage),
                new XAttribute("InstallScope", settings.InstallScope == WixInstallScope.CurrentUser ? "perUser" : "perMachine")),
            new XElement(Wix + "MediaTemplate", new XAttribute("EmbedCab", "yes")));
        product.Add(new XElement(Wix + "MajorUpgrade",
            new XAttribute("Schedule", "afterInstallInitialize"),
            new XAttribute("AllowSameVersionUpgrades", "no"),
            new XAttribute("DowngradeErrorMessage", Localize(
                "A newer version of [ProductName] is already installed.",
                "已安装较新版本的 [ProductName]。"))));
        var registrationRoot = settings.InstallScope == WixInstallScope.CurrentUser ? "HKCU" : "HKLM";
        var definitionKey = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
            "\\" + item.Target.RuntimeIdentifier + settings.LanguageSuffix + "\\Components";
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_INSTALLED_DEFINITION"),
            new XElement(Wix + "RegistrySearch", new XAttribute("Id", "FindBundlerDefinition"),
                new XAttribute("Root", registrationRoot), new XAttribute("Key", definitionKey),
                new XAttribute("Name", "DefinitionHash"), new XAttribute("Type", "raw"))));
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_PACKAGE_DEFINITION"),
            new XAttribute("Value", definitionHash)));
        product.Add(new XElement(Wix + "Condition",
            new XAttribute("Message", Localize(
                "A different MSI package already uses this product version. Use a new product version.",
                "此产品版本已由另一个 MSI 包使用。请使用新的产品版本。")),
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
                (item.Target.Architecture == CpuArchitecture.Arm64 ? "arm64" : "x64") + settings.LanguageSuffix));
        programs.Add(app);
        if (settings.InstallScope == WixInstallScope.CurrentUser) installRoot.Add(programs);
        targetDir.Add(installRoot);
        if (settings.StartMenuShortcut)
        {
            targetDir.Add(new XElement(Wix + "Directory", new XAttribute("Id", "ProgramMenuFolder"),
                new XElement(Wix + "Directory", new XAttribute("Id", "BundlerStartMenuFolder"),
                    new XAttribute("Name", bundle.Identifier.ToLowerInvariant() + settings.LanguageSuffix))));
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
            "\\" + item.Target.RuntimeIdentifier + settings.LanguageSuffix + "\\Components";
        var mainExecutable = WixPackagePaths.NormalizeTarget(item.MainExecutable!);

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
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName) + settings.LanguageSuffix),
                        new XAttribute("Advertise", settings.InstallScope == WixInstallScope.PerMachine ? "yes" : "no"),
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
                if (settings.DesktopShortcut)
                    fileElement.Add(new XElement(Wix + "Shortcut", new XAttribute("Id", "DesktopShortcut"),
                        new XAttribute("Directory", "DesktopFolder"),
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName) + " (" + bundle.Identifier.ToLowerInvariant() + settings.LanguageSuffix + ")"),
                        new XAttribute("Advertise", settings.InstallScope == WixInstallScope.PerMachine ? "yes" : "no"),
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
            }
            directory.Add(new XElement(Wix + "Component",
                new XAttribute("Id", componentId),
                new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                    item.Target.RuntimeIdentifier, settings.InstallScope, file.RelativePath, settings.Language).ToString("B").ToUpperInvariant()),
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
                item.Target.RuntimeIdentifier, settings.InstallScope, "!cleanup", settings.Language).ToString("B").ToUpperInvariant()),
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
        if (!string.IsNullOrWhiteSpace(bundle.LicenseFile))
        {
            product.Add(new XElement(Wix + "UIRef", new XAttribute("Id", "WixUI_ErrorProgressText")));
            product.Add(new XElement(Wix + "WixVariable", new XAttribute("Id", "WixUILicenseRtf"),
                new XAttribute("Value", Path.GetFullPath(bundle.LicenseFile))));
            product.Add(new XElement(Wix + "UIRef", new XAttribute("Id", "WixUI_Minimal")));
        }
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
            var applicationId = bundle.Identifier.ToLowerInvariant() + "." + item.Target.RuntimeIdentifier + settings.LanguageSuffix;
            var capabilities = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
                "\\" + item.Target.RuntimeIdentifier + settings.LanguageSuffix + "\\Capabilities";
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
                    item.Target.RuntimeIdentifier, settings.InstallScope, "!desktop-registration", settings.Language).ToString("B").ToUpperInvariant()));
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

    private string Localize(string english, string chinese) =>
        settings.Language == WixPackageLanguage.ChineseSimplified ? chinese : english;

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path.Substring(0, index);
    }

    internal static string SafeFileName(string name)
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

}

internal sealed record InstallFile(string RelativePath, string SourcePath);

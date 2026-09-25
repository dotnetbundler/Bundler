using System.Xml.Linq;
using DotNet.Bundler;

namespace DotNet.Bundler.Wix;

internal sealed class WixProductDocument(WixBundleConfiguration settings, WixLanguageInfo language)
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
            new XAttribute("Codepage", language.Codepage),
            new XAttribute("Name", bundle.ProductName),
            new XAttribute("Language", language.Lcid),
            new XAttribute("Version", identity.ProductVersion),
            new XAttribute("Manufacturer", bundle.Publisher ?? bundle.ProductName),
            new XAttribute("UpgradeCode", identity.UpgradeCode.ToString("B").ToUpperInvariant()),
            new XElement(Wix + "Package",
                new XAttribute("InstallerVersion", "500"),
                new XAttribute("Compressed", "yes"),
                new XAttribute("SummaryCodepage", language.Codepage),
                new XAttribute("InstallScope", settings.InstallScope == WixInstallScope.CurrentUser ? "perUser" : "perMachine")),
            new XElement(Wix + "MediaTemplate", new XAttribute("EmbedCab", "yes")));
        product.Add(new XElement(Wix + "MajorUpgrade",
            new XAttribute("Schedule", "afterInstallInitialize"),
            new XAttribute("AllowSameVersionUpgrades", "no"),
            settings.AllowDowngrades
                ? new XAttribute("AllowDowngrades", "yes")
                : new XAttribute("DowngradeErrorMessage", Loc("DowngradeErrorMessage"))));
        var registrationRoot = settings.InstallScope == WixInstallScope.CurrentUser ? "HKCU" : "HKLM";
        var definitionKey = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
            "\\" + item.Target.RuntimeIdentifier + language.Suffix + "\\Components";
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_INSTALLED_DEFINITION"),
            new XElement(Wix + "RegistrySearch", new XAttribute("Id", "FindBundlerDefinition"),
                new XAttribute("Root", registrationRoot), new XAttribute("Key", definitionKey),
                new XAttribute("Name", "DefinitionHash"), new XAttribute("Type", "raw"))));
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "BUNDLER_PACKAGE_DEFINITION"),
            new XAttribute("Value", definitionHash)));
        product.Add(new XElement(Wix + "Condition",
            new XAttribute("Message", Loc("VersionConflictMessage")),
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

        var perUser = settings.InstallScope == WixInstallScope.CurrentUser;
        var scopeRoot = perUser ? "LocalAppDataFolder" :
            item.Target.Architecture == CpuArchitecture.X86 ? "ProgramFilesFolder" : "ProgramFiles64Folder";
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "INSTALLFOLDER"),
            new XElement(Wix + "RegistrySearch", new XAttribute("Id", "BundlerInstallDirSearch"),
                new XAttribute("Root", registrationRoot), new XAttribute("Key", definitionKey),
                new XAttribute("Name", "InstallDir"), new XAttribute("Type", "directory"),
                new XAttribute("Win64", item.Target.Architecture == CpuArchitecture.X86 ? "no" : "yes"))));
        product.Add(new XElement(Wix + "Property", new XAttribute("Id", "ARPCONTACT"),
            new XAttribute("Value", bundle.Publisher ?? bundle.ProductName)));
        product.Add(new XElement(Wix + "CustomAction", new XAttribute("Id", "BundlerSetArpInstallLocation"),
            new XAttribute("Property", "ARPINSTALLLOCATION"), new XAttribute("Value", "[INSTALLFOLDER]")));
        product.Add(new XElement(Wix + "CustomAction", new XAttribute("Id", "BundlerInstallDirScope"),
            new XAttribute("Error", Loc("InstallDirScopeError"))));
        product.Add(new XElement(Wix + "InstallExecuteSequence",
            new XElement(Wix + "Custom", new XAttribute("Action", "BundlerSetArpInstallLocation"),
                new XAttribute("After", "CostFinalize"), "1"),
            new XElement(Wix + "Custom", new XAttribute("Action", "BundlerInstallDirScope"),
                new XAttribute("After", "BundlerSetArpInstallLocation"),
                "NOT Installed AND (NOT (INSTALLFOLDER ~<< " + scopeRoot + ") OR INSTALLFOLDER ~= " + scopeRoot + ")")));

        var targetDir = new XElement(Wix + "Directory", new XAttribute("Id", "TARGETDIR"),
            new XAttribute("Name", "SourceDir"));
        var installRoot = new XElement(Wix + "Directory", new XAttribute("Id", scopeRoot));
        var programs = perUser
            ? new XElement(Wix + "Directory", new XAttribute("Id", "BundlerProgramsDir"), new XAttribute("Name", "Programs"))
            : installRoot;
        var app = new XElement(Wix + "Directory", new XAttribute("Id", "INSTALLFOLDER"),
            new XAttribute("Name", bundle.Identifier.ToLowerInvariant() + "-" +
                item.Target.RuntimeIdentifier.Substring(4) + language.Suffix));
        programs.Add(app);
        if (perUser) installRoot.Add(programs);
        targetDir.Add(installRoot);
        if (settings.StartMenuShortcut || settings.UninstallShortcut)
        {
            targetDir.Add(new XElement(Wix + "Directory", new XAttribute("Id", "ProgramMenuFolder"),
                new XElement(Wix + "Directory", new XAttribute("Id", "BundlerStartMenuFolder"),
                    new XAttribute("Name", bundle.Identifier.ToLowerInvariant() + language.Suffix))));
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
            "\\" + item.Target.RuntimeIdentifier + language.Suffix + "\\Components";
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
            directory.Add(new XElement(Wix + "Component",
                new XAttribute("Id", componentId),
                new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                    item.Target.RuntimeIdentifier, settings.InstallScope, file.RelativePath, language).ToString("B").ToUpperInvariant()),
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
                item.Target.RuntimeIdentifier, settings.InstallScope, "!cleanup", language).ToString("B").ToUpperInvariant()),
            new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                new XAttribute("Key", registryKey), new XAttribute("Name", "Cleanup"),
                new XAttribute("Type", "integer"), new XAttribute("Value", "1"),
                new XAttribute("KeyPath", "yes")),
            new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                new XAttribute("Key", registryKey), new XAttribute("Name", "DefinitionHash"),
                new XAttribute("Type", "string"), new XAttribute("Value", definitionHash)),
            new XElement(Wix + "RegistryValue", new XAttribute("Root", registryRoot),
                new XAttribute("Key", registryKey), new XAttribute("Name", "InstallDir"),
                new XAttribute("Type", "string"), new XAttribute("Value", "[INSTALLFOLDER]")));
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
        if (perUser)
        {
            cleanup.Add(new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveProgramsFolder"),
                new XAttribute("Directory", "BundlerProgramsDir"), new XAttribute("On", "uninstall")));
        }
        if (settings.StartMenuShortcut || settings.UninstallShortcut)
            cleanup.Add(new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveStartMenuFolder"),
                new XAttribute("Directory", "BundlerStartMenuFolder"), new XAttribute("On", "uninstall")));
        app.Add(cleanup);
        feature.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "Cleanup")));
        AddDesktopRegistrations();
        AddOptionalFeatures();
        var hasLicense = !string.IsNullOrWhiteSpace(bundle.LicenseFile);
        var usesCustomUi = settings.InstallDirectorySelection ||
            (!hasLicense && (settings.LaunchAfterInstall ||
                settings.BannerBitmap is not null || settings.DialogBitmap is not null));
        if (hasLicense || usesCustomUi)
            product.Add(new XElement(Wix + "UIRef", new XAttribute("Id", "WixUI_ErrorProgressText")));
        if (settings.BannerBitmap is not null)
            product.Add(new XElement(Wix + "WixVariable", new XAttribute("Id", "WixUIBannerBmp"),
                new XAttribute("Value", Path.GetFullPath(settings.BannerBitmap))));
        if (settings.DialogBitmap is not null)
            product.Add(new XElement(Wix + "WixVariable", new XAttribute("Id", "WixUIDialogBmp"),
                new XAttribute("Value", Path.GetFullPath(settings.DialogBitmap))));
        if (hasLicense)
            product.Add(new XElement(Wix + "WixVariable", new XAttribute("Id", "WixUILicenseRtf"),
                new XAttribute("Value", Path.GetFullPath(bundle.LicenseFile))));
        if (hasLicense && !usesCustomUi)
        {
            product.Add(new XElement(Wix + "UIRef", new XAttribute("Id", "WixUI_Minimal")));
        }
        var launchPublish = settings.LaunchAfterInstall
            ? new XElement(Wix + "Publish", new XAttribute("Dialog", "ExitDialog"),
                new XAttribute("Control", "Finish"), new XAttribute("Event", "DoAction"),
                new XAttribute("Value", "BundlerLaunchAfterInstall"), new XAttribute("Order", "2"),
                "WIXUI_EXITDIALOGOPTIONALCHECKBOX = \"1\" AND NOT Installed AND NOT WIX_UPGRADE_DETECTED")
            : null;
        if (usesCustomUi)
        {
            var nextAfterWelcome = hasLicense ? "LicenseAgreementDlg" :
                settings.InstallDirectorySelection ? "InstallDirDlg" : "VerifyReadyDlg";
            var ui = new XElement(Wix + "UI", new XAttribute("Id", "BundlerInstallDialogSet"),
                new XElement(Wix + "TextStyle", new XAttribute("Id", "WixUI_Font_Normal"),
                    new XAttribute("FaceName", "Tahoma"), new XAttribute("Size", "8")),
                new XElement(Wix + "TextStyle", new XAttribute("Id", "WixUI_Font_Bigger"),
                    new XAttribute("FaceName", "Tahoma"), new XAttribute("Size", "12")),
                new XElement(Wix + "TextStyle", new XAttribute("Id", "WixUI_Font_Title"),
                    new XAttribute("FaceName", "Tahoma"), new XAttribute("Size", "9"),
                    new XAttribute("Bold", "yes")),
                new XElement(Wix + "Property", new XAttribute("Id", "DefaultUIFont"),
                    new XAttribute("Value", "WixUI_Font_Normal")),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "ExitDialog"),
                    new XAttribute("Control", "Finish"), new XAttribute("Event", "EndDialog"),
                    new XAttribute("Value", "Return"), new XAttribute("Order", "999"), "1"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "WelcomeDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", nextAfterWelcome), "NOT Installed"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "WelcomeDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "VerifyReadyDlg"), "Installed AND PATCH"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "VerifyReadyDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", settings.InstallDirectorySelection ? "InstallDirDlg" : nextAfterWelcome),
                    new XAttribute("Order", "1"), "NOT Installed"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "VerifyReadyDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "MaintenanceTypeDlg"), new XAttribute("Order", "2"),
                    "Installed AND NOT PATCH"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "VerifyReadyDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "WelcomeDlg"), new XAttribute("Order", "3"),
                    "Installed AND PATCH"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "MaintenanceWelcomeDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "MaintenanceTypeDlg"), "1"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "MaintenanceTypeDlg"),
                    new XAttribute("Control", "RepairButton"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "VerifyReadyDlg"), "1"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "MaintenanceTypeDlg"),
                    new XAttribute("Control", "RemoveButton"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "VerifyReadyDlg"), "1"),
                new XElement(Wix + "Publish", new XAttribute("Dialog", "MaintenanceTypeDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "MaintenanceWelcomeDlg"), "1"),
                new XElement(Wix + "Property", new XAttribute("Id", "ARPNOMODIFY"),
                    new XAttribute("Value", "1")));
            var dialogs = new List<string>
            {
                "ErrorDlg", "FatalError", "FilesInUse", "MsiRMFilesInUse", "PrepareDlg",
                "ProgressDlg", "ResumeDlg", "UserExit", "WelcomeDlg", "VerifyReadyDlg",
                "MaintenanceWelcomeDlg", "MaintenanceTypeDlg", "ExitDialog", "CancelDlg",
                "OutOfDiskDlg", "OutOfRbDiskDlg", "WaitForCostingDlg"
            };
            if (hasLicense)
            {
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "LicenseAgreementDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "WelcomeDlg"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "LicenseAgreementDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", settings.InstallDirectorySelection ? "InstallDirDlg" : "VerifyReadyDlg"),
                    "LicenseAccepted = \"1\""));
                dialogs.Add("LicenseAgreementDlg");
            }
            if (settings.InstallDirectorySelection)
            {
                var scope = "INSTALLFOLDER ~<< " + scopeRoot + " AND INSTALLFOLDER ~<> " + scopeRoot;
                ui.Add(new XElement(Wix + "Property", new XAttribute("Id", "WIXUI_INSTALLDIR"),
                    new XAttribute("Value", "INSTALLFOLDER")));
                ui.Add(new XElement(Wix + "Property", new XAttribute("Id", "WixUI_Mode"),
                    new XAttribute("Value", "InstallDir")));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Back"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", hasLicense ? "LicenseAgreementDlg" : "WelcomeDlg"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "SetTargetPath"),
                    new XAttribute("Value", "[WIXUI_INSTALLDIR]"), new XAttribute("Order", "1"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "DoAction"),
                    new XAttribute("Value", "WixUIValidatePath"), new XAttribute("Order", "2"),
                    "NOT WIXUI_DONTVALIDATEPATH"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "SpawnDialog"),
                    new XAttribute("Value", "InvalidDirDlg"), new XAttribute("Order", "3"),
                    "NOT WIXUI_DONTVALIDATEPATH AND WIXUI_INSTALLDIR_VALID<>\"1\""));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "NewDialog"),
                    new XAttribute("Value", "VerifyReadyDlg"), new XAttribute("Order", "4"),
                    "(WIXUI_DONTVALIDATEPATH OR WIXUI_INSTALLDIR_VALID=\"1\") AND " + scope));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "Next"), new XAttribute("Event", "SpawnDialog"),
                    new XAttribute("Value", "InvalidDirDlg"), new XAttribute("Order", "5"),
                    "WIXUI_INSTALLDIR_VALID=\"1\" AND NOT (" + scope + ")"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "ChangeFolder"), new XAttribute("Property", "_BrowseProperty"),
                    new XAttribute("Value", "[WIXUI_INSTALLDIR]"), new XAttribute("Order", "1"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "InstallDirDlg"),
                    new XAttribute("Control", "ChangeFolder"), new XAttribute("Event", "SpawnDialog"),
                    new XAttribute("Value", "BrowseDlg"), new XAttribute("Order", "2"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "BrowseDlg"),
                    new XAttribute("Control", "OK"), new XAttribute("Event", "DoAction"),
                    new XAttribute("Value", "WixUIValidatePath"), new XAttribute("Order", "3"), "1"));
                ui.Add(new XElement(Wix + "Publish", new XAttribute("Dialog", "BrowseDlg"),
                    new XAttribute("Control", "OK"), new XAttribute("Event", "SpawnDialog"),
                    new XAttribute("Value", "InvalidDirDlg"), new XAttribute("Order", "4"),
                    "NOT WIXUI_DONTVALIDATEPATH AND WIXUI_INSTALLDIR_VALID<>\"1\""));
                dialogs.AddRange(["InstallDirDlg", "BrowseDlg", "InvalidDirDlg", "DiskCostDlg"]);
            }
            if (launchPublish is not null) ui.Add(launchPublish);
            foreach (var dialog in dialogs)
                ui.AddFirst(new XElement(Wix + "DialogRef", new XAttribute("Id", dialog)));
            product.Add(ui);
            product.Add(new XElement(Wix + "UIRef", new XAttribute("Id", "WixUI_Common")));
        }
        else if (launchPublish is not null)
        {
            product.Add(new XElement(Wix + "UI", launchPublish));
        }
        if (settings.LaunchAfterInstall)
        {
            product.Add(new XElement(Wix + "Property",
                new XAttribute("Id", "WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"),
                new XAttribute("Value", Loc("LaunchCheckboxText"))));
            product.Add(new XElement(Wix + "CustomAction", new XAttribute("Id", "BundlerLaunchAfterInstall"),
                new XAttribute("FileKey", WixIdentity.StableId("Fil", mainExecutable)),
                new XAttribute("ExeCommand", ""), new XAttribute("Return", "asyncNoWait"),
                new XAttribute("Impersonate", "yes")));
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
            var applicationId = bundle.Identifier.ToLowerInvariant() + "." + item.Target.RuntimeIdentifier + language.Suffix;
            var capabilities = "Software\\DotNetBundler\\Products\\" + bundle.Identifier.ToLowerInvariant() +
                "\\" + item.Target.RuntimeIdentifier + language.Suffix + "\\Capabilities";
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
                    item.Target.RuntimeIdentifier, settings.InstallScope, "!desktop-registration", language).ToString("B").ToUpperInvariant()));
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

        void AddOptionalFeatures()
        {
            var shortcutTarget = "[INSTALLFOLDER]" + mainExecutable.Replace('/', '\\');
            if (settings.StartMenuShortcut || settings.DesktopShortcut)
            {
                var shortcuts = FeatureElement("Shortcuts", Loc("ShortcutsFeature"));
                if (settings.StartMenuShortcut)
                {
                    var component = ComponentElement("CmpStartMenuShortcut", "!shortcut-startmenu");
                    component.Add(new XElement(Wix + "Shortcut",
                        new XAttribute("Id", "StartMenuShortcut"),
                        new XAttribute("Directory", "BundlerStartMenuFolder"),
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName) + language.Suffix),
                        new XAttribute("Target", shortcutTarget),
                        icon is not null ? new XAttribute("Icon", "ProductIcon") : null,
                        icon is not null ? new XAttribute("IconIndex", "0") : null,
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
                    component.Add(ShortcutKeyPath("CmpStartMenuShortcut"));
                    app.Add(component);
                    shortcuts.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "CmpStartMenuShortcut")));
                }
                if (settings.DesktopShortcut)
                {
                    var component = ComponentElement("CmpDesktopShortcut", "!shortcut-desktop");
                    component.Add(new XElement(Wix + "Shortcut",
                        new XAttribute("Id", "DesktopShortcut"),
                        new XAttribute("Directory", "DesktopFolder"),
                        new XAttribute("Name", SafeShortcutName(bundle.ProductName) + " (" +
                            bundle.Identifier.ToLowerInvariant() + language.Suffix + ")"),
                        new XAttribute("Target", shortcutTarget),
                        icon is not null ? new XAttribute("Icon", "ProductIcon") : null,
                        icon is not null ? new XAttribute("IconIndex", "0") : null,
                        new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
                    component.Add(ShortcutKeyPath("CmpDesktopShortcut"));
                    app.Add(component);
                    shortcuts.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "CmpDesktopShortcut")));
                }
                product.Add(shortcuts);
            }
            if (settings.AddToPath)
            {
                var component = ComponentElement("CmpPathEnvironment", "!path-environment");
                component.Add(new XElement(Wix + "Environment",
                    new XAttribute("Id", "BundlerPathEnvironment"),
                    new XAttribute("Name", "PATH"),
                    new XAttribute("Value", "[INSTALLFOLDER]"),
                    new XAttribute("Action", "set"), new XAttribute("Part", "last"),
                    new XAttribute("System", settings.InstallScope == WixInstallScope.PerMachine ? "yes" : "no"),
                    new XAttribute("Permanent", "no")));
                component.Add(RegistryKeyPath("CmpPathEnvironment"));
                app.Add(component);
                var pathFeature = FeatureElement("PathEnvironment", Loc("PathFeature"));
                pathFeature.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "CmpPathEnvironment")));
                product.Add(pathFeature);
            }
            if (settings.UninstallShortcut)
            {
                var component = ComponentElement("CmpUninstallShortcut", "!uninstall-shortcut");
                component.Add(new XElement(Wix + "Shortcut",
                    new XAttribute("Id", "UninstallShortcut"),
                    new XAttribute("Directory", "BundlerStartMenuFolder"),
                    new XAttribute("Name", Loc("UninstallShortcutPrefix") + SafeShortcutName(bundle.ProductName) + language.Suffix),
                    icon is not null ? new XAttribute("Icon", "ProductIcon") : null,
                    icon is not null ? new XAttribute("IconIndex", "0") : null,
                    new XAttribute("Advertise", "no"),
                    new XAttribute("Target", "[" + (item.Target.Architecture == CpuArchitecture.X86
                        ? "SystemFolder" : "System64Folder") + "]msiexec.exe"),
                    new XAttribute("Arguments", "/x [ProductCode]"),
                    new XAttribute("WorkingDirectory", "INSTALLFOLDER")));
                component.Add(ShortcutKeyPath("CmpUninstallShortcut"));
                app.Add(component);
                var uninstall = FeatureElement("UninstallShortcut", Loc("UninstallShortcutFeature"));
                uninstall.Add(new XElement(Wix + "ComponentRef", new XAttribute("Id", "CmpUninstallShortcut")));
                product.Add(uninstall);
            }
        }

        XElement FeatureElement(string id, string title) => new(Wix + "Feature",
            new XAttribute("Id", id), new XAttribute("Title", title), new XAttribute("Level", "1"));

        XElement ComponentElement(string id, string marker) => new(Wix + "Component",
            new XAttribute("Id", id),
            new XAttribute("Guid", WixIdentity.ComponentCode(bundle.Identifier,
                item.Target.RuntimeIdentifier, settings.InstallScope, marker, language).ToString("B").ToUpperInvariant()));

        XElement RegistryKeyPath(string name) => new(Wix + "RegistryValue",
            new XAttribute("Root", registryRoot), new XAttribute("Key", registryKey),
            new XAttribute("Name", name), new XAttribute("Type", "integer"),
            new XAttribute("Value", "1"), new XAttribute("KeyPath", "yes"));

        // Non-advertised shortcuts require an HKCU key path so each profile can repair its own entry (ICE43).
        XElement ShortcutKeyPath(string name) => new(Wix + "RegistryValue",
            new XAttribute("Root", "HKCU"), new XAttribute("Key", registryKey),
            new XAttribute("Name", name), new XAttribute("Type", "integer"),
            new XAttribute("Value", "1"), new XAttribute("KeyPath", "yes"));
    }

    // WiX localization reference resolved from the merged per-language .wxl
    // at link time; Bundler default texts live in WixLocale.
    private static string Loc(string id) => "!(loc.Bundler" + id + ")";

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

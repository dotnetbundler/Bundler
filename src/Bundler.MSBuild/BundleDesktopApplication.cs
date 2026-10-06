using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
using DotNet.Bundler.Wix;
using DotNet.Bundler.Signing.Windows;
using DotNet.Bundler.Update;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

public sealed class BundleDesktopApplication : Microsoft.Build.Utilities.Task
{
    [Required] public string ProductName { get; set; } = "";
    [Required] public string Identifier { get; set; } = "";
    [Required] public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Copyright { get; set; } = "";
    public string LicenseFile { get; set; } = "";
    [Required] public string RuntimeIdentifier { get; set; } = "";
    [Required] public string InputDirectory { get; set; } = "";
    [Required] public string OutputDirectory { get; set; } = "";
    [Required] public string MainExecutable { get; set; } = "";
    [Required] public string Formats { get; set; } = "";
    public ITaskItem[] Icons { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] Resources { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] FileAssociations { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] UrlProtocols { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] WindowsSigningFiles { get; set; } = Array.Empty<ITaskItem>();
    public string NsisToolsetArchivePath { get; set; } = "";
    public string NsisCompilerPath { get; set; } = "";
    public string NsisDataDirectory { get; set; } = "";
    public string ToolCacheDirectory { get; set; } = "";
    public string WixToolsetArchivePath { get; set; } = "";
    public string WixInstallScope { get; set; } = "currentUser";
    public string WixUpgradeCode { get; set; } = "";
    public string WixMsiVersion { get; set; } = "";
    public bool WixAllowDowngrades { get; set; }
    public int WixCodepage { get; set; }
    public string WixLanguage { get; set; } = "en-US";
    public string WixLanguages { get; set; } = "";
    public ITaskItem[] WixLanguageFiles { get; set; } = Array.Empty<ITaskItem>();
    public bool WixFipsCompliant { get; set; }
    public bool WixStartMenuShortcut { get; set; }
    public bool WixDesktopShortcut { get; set; }
    public bool WixInstallDirectorySelection { get; set; }
    public string WixBannerBitmap { get; set; } = "";
    public string WixDialogBitmap { get; set; } = "";
    public bool WixAddToPath { get; set; }
    public bool WixUninstallShortcut { get; set; }
    public bool WixLaunchAfterInstall { get; set; }
    public ITaskItem[] WixExtensionFragments { get; set; } = Array.Empty<ITaskItem>();
    public string WixExtensionIdPrefix { get; set; } = "";
    public ITaskItem[] WixExtensionComponentRefs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] WixExtensionComponentGroupRefs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] WixExtensionFeatureRefs { get; set; } = Array.Empty<ITaskItem>();
    public string WixExpertTemplate { get; set; } = "";
    public ITaskItem[] WixExpertMergeModules { get; set; } = Array.Empty<ITaskItem>();
    public string NsisTemplatePath { get; set; } = "";
    public string NsisInstallMode { get; set; } = "currentUser";
    public string NsisCompression { get; set; } = "lzma";
    public string NsisInstallerIcon { get; set; } = "";
    public string NsisUninstallerIcon { get; set; } = "";
    public string NsisHeaderImage { get; set; } = "";
    public string NsisSidebarImage { get; set; } = "";
    public string NsisUninstallerHeaderImage { get; set; } = "";
    public string NsisInstallerHooks { get; set; } = "";
    public string NsisLanguages { get; set; } = "English";
    public bool NsisDisplayLanguageSelector { get; set; }
    public bool NsisAllowDowngrades { get; set; }
    public bool NsisShortcutDesktop { get; set; } = true;
    public bool NsisShortcutStartMenu { get; set; } = true;
    public string NsisShortcutArguments { get; set; } = "";
    public string NsisShortcutWorkingDirectory { get; set; } = "";
    public string NsisShortcutIcon { get; set; } = "";
    public string NsisShortcutAppUserModelId { get; set; } = "";
    public string NsisShortcutStartMenuFolder { get; set; } = "";
    public string NsisShortcutLegacyProductNames { get; set; } = "";
    public string NsisShortcutLegacyMainExecutables { get; set; } = "";
    public string NsisLegacyMsiProductCodes { get; set; } = "";
    public string NsisLegacyMsiUpgradeCodes { get; set; } = "";
    public bool NsisLegacyMsiAutoDetect { get; set; }
    public string WindowsSigningPfxFile { get; set; } = "";
    public string WindowsSigningPfxPasswordEnvironmentVariable { get; set; } = "";
    public string WindowsSigningCertificateThumbprint { get; set; } = "";
    public string WindowsSigningCertificateStoreLocation { get; set; } = "CurrentUser";
    public string WindowsSigningTimestampUrl { get; set; } = "";
    public string WindowsSigningCommand { get; set; } = "";
    public ITaskItem[] WindowsSigningCommandArguments { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] NsisLanguageFiles { get; set; } = Array.Empty<ITaskItem>();
    public string MacAppBundleName { get; set; } = "";
    public string MacAppDisplayName { get; set; } = "";
    public string MacAppShortVersion { get; set; } = "";
    public string MacAppBuildVersion { get; set; } = "";
    public string MacAppMinimumSystemVersion { get; set; } = "";
    public string MacAppCategory { get; set; } = "";
    public string MacAppIconName { get; set; } = "";
    public string MacAppExceptionDomain { get; set; } = "";
    public string MacAppInfoPlistFile { get; set; } = "";
    public string MacAppInfoPlistXml { get; set; } = "";
    public string MacAppSignIdentity { get; set; } = "";
    public string MacAppSigningCertificatePath { get; set; } = "";
    public string MacAppSigningCertificatePassword { get; set; } = "";
    public bool MacAppHardenedRuntime { get; set; }
    public string MacAppEntitlementsFile { get; set; } = "";
    public bool MacAppNotarize { get; set; }
    public bool MacAppNotaryWait { get; set; } = true;
    public bool MacAppSkipStapling { get; set; }
    public string MacAppNotaryProfile { get; set; } = "";
    public string MacAppAppleId { get; set; } = "";
    public string MacAppApplePassword { get; set; } = "";
    public string MacAppAppleTeamId { get; set; } = "";
    public string MacAppNotaryApiKeyPath { get; set; } = "";
    public string MacAppNotaryApiKeyId { get; set; } = "";
    public string MacAppNotaryApiIssuer { get; set; } = "";
    public string MacDmgCompression { get; set; } = "";
    public string MacDmgVolumeName { get; set; } = "";
    public string MacDmgSkipWindowLayout { get; set; } = "";
    public string MacDmgWindowX { get; set; } = "";
    public string MacDmgWindowY { get; set; } = "";
    public string MacDmgWindowWidth { get; set; } = "";
    public string MacDmgWindowHeight { get; set; } = "";
    public string MacDmgAppIconX { get; set; } = "";
    public string MacDmgAppIconY { get; set; } = "";
    public string MacDmgApplicationsIconX { get; set; } = "";
    public string MacDmgApplicationsIconY { get; set; } = "";
    public string MacDmgIconSize { get; set; } = "";
    public string MacDmgBackgroundFile { get; set; } = "";
    public string MacDmgVolumeIconFile { get; set; } = "";
    public string MacDmgSignIdentity { get; set; } = "";
    public string MacDmgSignCertificatePath { get; set; } = "";
    public string MacDmgSignCertificatePassword { get; set; } = "";
    public string MacPkgIdentifier { get; set; } = "";
    public string MacPkgVersion { get; set; } = "";
    public string MacPkgInstallLocation { get; set; } = "";
    public ITaskItem[] MacPkgPayloadItems { get; set; } = Array.Empty<ITaskItem>();
    public string MacPkgTitle { get; set; } = "";
    public string MacPkgWelcomeFile { get; set; } = "";
    public string MacPkgConclusionFile { get; set; } = "";
    public string MacPkgDomain { get; set; } = "";
    public string MacPkgScriptsDirectory { get; set; } = "";
    public string MacPkgSignIdentity { get; set; } = "";
    public string MacPkgSignCertificatePath { get; set; } = "";
    public string MacPkgSignCertificatePassword { get; set; } = "";
    public bool MacPkgNotarize { get; set; }
    public bool MacPkgNotaryWait { get; set; } = true;
    public bool MacPkgSkipStapling { get; set; }
    public string MacPkgNotaryProfile { get; set; } = "";
    public string MacPkgAppleId { get; set; } = "";
    public string MacPkgApplePassword { get; set; } = "";
    public string MacPkgAppleTeamId { get; set; } = "";
    public string MacPkgNotaryApiKeyPath { get; set; } = "";
    public string MacPkgNotaryApiKeyId { get; set; } = "";
    public string MacPkgNotaryApiIssuer { get; set; } = "";
    public string DebPackageName { get; set; } = "";
    public string DebVersion { get; set; } = "";
    public string DebRevision { get; set; } = "";
    public string DebEpoch { get; set; } = "";
    public string DebArchitecture { get; set; } = "";
    public string DebMaintainer { get; set; } = "";
    public string DebInstallRoot { get; set; } = "";
    public string DebBinLink { get; set; } = "";
    public string DebDepends { get; set; } = "";
    public string DebRecommends { get; set; } = "";
    public string DebProvides { get; set; } = "";
    public string DebConflicts { get; set; } = "";
    public string DebReplaces { get; set; } = "";
    public string DebSection { get; set; } = "";
    public string DebPriority { get; set; } = "";
    public string DebCategories { get; set; } = "";
    public string DebDesktopFile { get; set; } = "";
    public string DebMetainfoFile { get; set; } = "";
    public string DebChangelogFile { get; set; } = "";
    public string DebPreinstFile { get; set; } = "";
    public string DebPostinstFile { get; set; } = "";
    public string DebPrermFile { get; set; } = "";
    public string DebPostrmFile { get; set; } = "";
    public string DebSystemdServiceFile { get; set; } = "";
    public string DebConffiles { get; set; } = "";
    public string DebCompression { get; set; } = "";
    public ITaskItem[] DebFiles { get; set; } = Array.Empty<ITaskItem>();
    public string RpmPackageName { get; set; } = "";
    public string RpmVersion { get; set; } = "";
    public string RpmRelease { get; set; } = "";
    public string RpmEpoch { get; set; } = "";
    public string RpmArchitecture { get; set; } = "";
    public string RpmVendor { get; set; } = "";
    public string RpmInstallRoot { get; set; } = "";
    public string RpmBinLink { get; set; } = "";
    public string RpmRequires { get; set; } = "";
    public string RpmProvides { get; set; } = "";
    public string RpmConflicts { get; set; } = "";
    public string RpmObsoletes { get; set; } = "";
    public string RpmRecommends { get; set; } = "";
    public string RpmSuggests { get; set; } = "";
    public string RpmLicense { get; set; } = "";
    public string RpmGroup { get; set; } = "";
    public string RpmUrl { get; set; } = "";
    public string RpmCategories { get; set; } = "";
    public string RpmDesktopFile { get; set; } = "";
    public string RpmMetainfoFile { get; set; } = "";
    public string RpmChangelogFile { get; set; } = "";
    public ITaskItem[] RpmFiles { get; set; } = Array.Empty<ITaskItem>();
    public string RpmConfigFiles { get; set; } = "";
    public string RpmSystemdServiceFile { get; set; } = "";
    public string RpmPreInstallFile { get; set; } = "";
    public string RpmPostInstallFile { get; set; } = "";
    public string RpmPreUninstallFile { get; set; } = "";
    public string RpmPostUninstallFile { get; set; } = "";
    public string RpmPreInstallProgram { get; set; } = "";
    public string RpmPostInstallProgram { get; set; } = "";
    public string RpmPreUninstallProgram { get; set; } = "";
    public string RpmPostUninstallProgram { get; set; } = "";
    public string RpmCompression { get; set; } = "";
    public string RpmSigningKeyFile { get; set; } = "";
    public string RpmSigningKeyPassphrase { get; set; } = "";
    public string AppImagePackageName { get; set; } = "";
    public string AppImageVersion { get; set; } = "";
    public string AppImageArchitecture { get; set; } = "";
    public string AppImageInstallRoot { get; set; } = "";
    public string AppImageBinLink { get; set; } = "";
    public string AppImageIconFile { get; set; } = "";
    public string AppImageDesktopFile { get; set; } = "";
    public string AppImageCategories { get; set; } = "";
    public string AppImageMetainfoFile { get; set; } = "";
    public string AppImageSigningKeyFile { get; set; } = "";
    public string AppImageSigningKeyPassphrase { get; set; } = "";
    public ITaskItem[] AppImageFiles { get; set; } = Array.Empty<ITaskItem>();
    public string ArchivePackageName { get; set; } = "";
    public string ArchiveVersion { get; set; } = "";
    public string ArchiveName { get; set; } = "";
    public ITaskItem[] ArchiveFiles { get; set; } = Array.Empty<ITaskItem>();
    public string AlpineApkPackageName { get; set; } = "";
    public string AlpineApkVersion { get; set; } = "";
    public string AlpineApkArchitecture { get; set; } = "";
    public string AlpineApkOrigin { get; set; } = "";
    public string AlpineApkDescription { get; set; } = "";
    public string AlpineApkUrl { get; set; } = "";
    public string AlpineApkBinLink { get; set; } = "";
    public string AlpineApkRelease { get; set; } = "";
    public string AlpineApkLicense { get; set; } = "";
    public string AlpineApkBuildDate { get; set; } = "";
    public string AlpineApkDepends { get; set; } = "";
    public string AlpineApkProvides { get; set; } = "";
    public string AlpineApkTriggers { get; set; } = "";
    public string AlpineApkPreInstallScript { get; set; } = "";
    public string AlpineApkPostInstallScript { get; set; } = "";
    public string AlpineApkPreDeinstallScript { get; set; } = "";
    public string AlpineApkPostDeinstallScript { get; set; } = "";
    public string AlpineApkPreUpgradeScript { get; set; } = "";
    public string AlpineApkPostUpgradeScript { get; set; } = "";
    public ITaskItem[] AlpineApkFiles { get; set; } = Array.Empty<ITaskItem>();
    public string AlpineApkSigningKeyFile { get; set; } = "";
    public string AlpineApkSigningKeyPassphrase { get; set; } = "";
    public ITaskItem[] MacContents { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] MacFrameworks { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] MacDocumentTypes { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] MacUrlTypes { get; set; } = Array.Empty<ITaskItem>();
    public bool UpdateEnabled { get; set; }
    public string UpdateFeedUrl { get; set; } = "";
    public string UpdateChannel { get; set; } = "";
    public string UpdateSigningKeyFile { get; set; } = "";
    public string UpdatePublicKey { get; set; } = "";
    public string UpdateNotes { get; set; } = "";
    [Output] public ITaskItem[] Artifacts { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var formats = ParseFormats();
            BundleConfiguration Configure(IReadOnlyList<PackageFormat> targetFormats) =>
                new BundleConfiguration
            {
                ProductName = ProductName,
                Identifier = Identifier,
                Version = Version,
                Publisher = EmptyToNull(Publisher),
                Description = EmptyToNull(Description),
                Homepage = EmptyToNull(Homepage),
                Copyright = EmptyToNull(Copyright),
                LicenseFile = OptionalFullPath(LicenseFile),
                OutputDirectory = Path.GetFullPath(OutputDirectory),
                Update = UpdateEnabled ? new UpdateBundleConfiguration
                {
                    FeedUrl = UpdateFeedUrl,
                    Channel = UpdateChannel,
                    SigningKeyFile = OptionalFullPath(UpdateSigningKeyFile),
                    PublicKey = EmptyToNull(UpdatePublicKey),
                    Notes = EmptyToNull(UpdateNotes)
                } : null,
                Icons = Icons.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray(),
                Resources = Resources.Select(item => new BundleResourceConfiguration
                {
                    Source = Path.GetFullPath(item.ItemSpec),
                    TargetPath = ResourceTargetPath(item)
                }).ToArray(),
                FileAssociations = FileAssociations.Select(item => new BundleFileAssociationConfiguration
                {
                    Extensions = new[] { item.ItemSpec },
                    Name = EmptyMetadataToNull(item, "Name"),
                    Description = EmptyMetadataToNull(item, "Description"),
                    MimeType = EmptyMetadataToNull(item, "MimeType")
                }).ToArray(),
                UrlProtocols = UrlProtocols.Select(item => new BundleUrlProtocolConfiguration
                {
                    Schemes = new[] { item.ItemSpec },
                    Name = EmptyMetadataToNull(item, "Name")
                }).ToArray(),
                Targets = new[]
                {
                    new BundleTargetConfiguration
                    {
                        RuntimeIdentifier = RuntimeIdentifier,
                        InputDirectory = Path.GetFullPath(InputDirectory),
                        MainExecutable = MainExecutable,
                        SigningFiles = WindowsSigningFiles.Select(item => item.ItemSpec).ToArray(),
                        Formats = targetFormats
                    }
                }
            };

            // Multi-format fanout: each backend runs once per requested format
            // against a configuration whose targets carry only that format.
            var artifacts = new List<BundleArtifact>();
            foreach (var format in formats.Distinct())
            {
                var configuration = Configure([format]);
                IReadOnlyList<BundleArtifact> produced;
                if (format == PackageFormat.Msi)
            {
                if (!Enum.TryParse<DotNet.Bundler.Wix.WixInstallScope>(WixInstallScope, true, out var scope) ||
                    !Enum.IsDefined(typeof(DotNet.Bundler.Wix.WixInstallScope), scope))
                {
                    throw new ArgumentException("BundlerWixInstallScope must be currentUser or perMachine.");
                }
                var languages = (string.IsNullOrWhiteSpace(WixLanguages) ? WixLanguage : WixLanguages)
                    .Split(';').Select(entry => entry.Trim())
                    .Where(entry => entry.Length > 0).ToArray();
                produced = new WixBundler(
                    new WixBundleConfiguration
                    {
                        InstallScope = scope,
                        UpgradeCode = EmptyToNull(WixUpgradeCode),
                        MsiVersion = EmptyToNull(WixMsiVersion),
                        AllowDowngrades = WixAllowDowngrades,
                        Codepage = WixCodepage,
                        Languages = languages,
                        LocaleFiles = ParseWixLanguageFiles(),
                        FipsCompliant = WixFipsCompliant,
                        StartMenuShortcut = WixStartMenuShortcut,
                        DesktopShortcut = WixDesktopShortcut,
                        InstallDirectorySelection = WixInstallDirectorySelection,
                        BannerBitmap = OptionalFullPath(WixBannerBitmap),
                        DialogBitmap = OptionalFullPath(WixDialogBitmap),
                        AddToPath = WixAddToPath,
                        UninstallShortcut = WixUninstallShortcut,
                        LaunchAfterInstall = WixLaunchAfterInstall,
                        ExtensionFragments = WixExtensionFragments
                            .Select(item => Path.GetFullPath(item.ItemSpec)).ToArray(),
                        ExtensionIdPrefix = EmptyToNull(WixExtensionIdPrefix),
                        ExtensionComponentRefs = WixExtensionComponentRefs
                            .Select(item => item.ItemSpec).ToArray(),
                        ExtensionComponentGroupRefs = WixExtensionComponentGroupRefs
                            .Select(item => item.ItemSpec).ToArray(),
                        ExtensionFeatureRefs = WixExtensionFeatureRefs
                            .Select(item => item.ItemSpec).ToArray(),
                        ExpertTemplate = OptionalFullPath(WixExpertTemplate),
                        ExpertMergeModules = WixExpertMergeModules
                            .Select(item => Path.GetFullPath(item.ItemSpec)).ToArray()
                    },
                    new WixBundlerOptions
                    {
                        ToolsetArchivePath = EmptyToNull(WixToolsetArchivePath),
                        ToolCacheDirectory = EmptyToNull(ToolCacheDirectory),
                        Signer = CreateWindowsSigner(),
                        Logger = new MsBuildBundleLogger(Log)
                    }).BuildAsync(configuration).GetAwaiter().GetResult();
            }
            else if (format == PackageFormat.Nsis)
            {
            var nsisConfiguration = new NsisBundleConfiguration
            {
                InstallMode = ParseInstallMode(),
                Compression = ParseCompression(),
                InstallerIcon = OptionalFullPath(NsisInstallerIcon),
                UninstallerIcon = OptionalFullPath(NsisUninstallerIcon),
                HeaderImage = OptionalFullPath(NsisHeaderImage),
                SidebarImage = OptionalFullPath(NsisSidebarImage),
                UninstallerHeaderImage = OptionalFullPath(NsisUninstallerHeaderImage),
                InstallerHooks = OptionalFullPath(NsisInstallerHooks),
                Languages = ParseLanguages(),
                DisplayLanguageSelector = NsisDisplayLanguageSelector,
                AllowDowngrades = NsisAllowDowngrades,
                Shortcuts = new NsisShortcutConfiguration
                {
                    Desktop = NsisShortcutDesktop,
                    StartMenu = NsisShortcutStartMenu,
                    Arguments = EmptyToNull(NsisShortcutArguments),
                    WorkingDirectory = EmptyToNull(NsisShortcutWorkingDirectory),
                    Icon = EmptyToNull(NsisShortcutIcon),
                    AppUserModelId = EmptyToNull(NsisShortcutAppUserModelId),
                    StartMenuFolder = EmptyToNull(NsisShortcutStartMenuFolder),
                    LegacyProductNames = ParseSemicolonList(NsisShortcutLegacyProductNames),
                    LegacyMainExecutables = ParseSemicolonList(NsisShortcutLegacyMainExecutables)
                },
                LegacyMsiProductCodes = ParseSemicolonList(NsisLegacyMsiProductCodes),
                LegacyMsiUpgradeCodes = ParseSemicolonList(NsisLegacyMsiUpgradeCodes),
                LegacyMsiAutoDetect = NsisLegacyMsiAutoDetect,
                CustomLanguageFiles = ParseCustomLanguageFiles()
            };
            var nsisOptions = new NsisBundlerOptions
            {
                ToolsetArchivePath = EmptyToNull(NsisToolsetArchivePath),
                ToolCacheDirectory = EmptyToNull(ToolCacheDirectory),
                CompilerPath = EmptyToNull(NsisCompilerPath),
                DataDirectory = EmptyToNull(NsisDataDirectory),
                TemplatePath = EmptyToNull(NsisTemplatePath),
                Signer = CreateWindowsSigner(),
                Logger = new MsBuildBundleLogger(Log)
            };
            produced = new NsisBundler(nsisConfiguration, nsisOptions)
                .BuildAsync(configuration)
                .GetAwaiter()
                .GetResult();
            }
            else if (format == PackageFormat.App)
            {
                produced = new MacAppBundler(
                    BuildMacAppConfiguration(),
                    new MacAppBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.Pkg)
            {
                produced = new MacPkgBundler(
                    new MacPkgBundleConfiguration
                    {
                        Identifier = EmptyToNull(MacPkgIdentifier),
                        Version = EmptyToNull(MacPkgVersion),
                        InstallLocation = string.IsNullOrWhiteSpace(MacPkgInstallLocation)
                            ? "/Applications"
                            : MacPkgInstallLocation.Trim(),
                        PayloadItems = MacPkgPayloadItems.Select(item => new MacPkgPayloadItem
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = EmptyMetadataToNull(item, "Destination")
                        }).ToArray(),
                        Title = EmptyToNull(MacPkgTitle),
                        WelcomeFile = OptionalFullPath(MacPkgWelcomeFile),
                        ConclusionFile = OptionalFullPath(MacPkgConclusionFile),
                        Domain = string.IsNullOrWhiteSpace(MacPkgDomain)
                            ? MacPkgInstallDomain.System
                            : (MacPkgInstallDomain)Enum.Parse(
                                typeof(MacPkgInstallDomain), MacPkgDomain.Trim(), true),
                        ScriptsDirectory = OptionalFullPath(MacPkgScriptsDirectory),
                        Signing = new MacPkgSigningConfiguration
                        {
                            Identity = EmptyToNull(MacPkgSignIdentity),
                            TemporaryCertificatePath = OptionalFullPath(MacPkgSignCertificatePath),
                            TemporaryCertificatePassword = EmptyToNull(MacPkgSignCertificatePassword),
                            Notarize = MacPkgNotarize,
                            NotaryWait = MacPkgNotaryWait,
                            SkipStapling = MacPkgSkipStapling,
                            KeychainProfile = EmptyToNull(MacPkgNotaryProfile),
                            AppleId = EmptyToNull(MacPkgAppleId),
                            ApplePassword = EmptyToNull(MacPkgApplePassword),
                            AppleTeamId = EmptyToNull(MacPkgAppleTeamId),
                            ApiKeyPath = OptionalFullPath(MacPkgNotaryApiKeyPath),
                            ApiKeyId = EmptyToNull(MacPkgNotaryApiKeyId),
                            ApiIssuer = EmptyToNull(MacPkgNotaryApiIssuer)
                        }
                    },
                    BuildMacAppConfiguration(),
                    new MacPkgBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.Dmg)
            {
                produced = new MacDmgBundler(
                    new MacDmgBundleConfiguration
                    {
                        Compression = MacEnumValue<DotNet.Bundler.MacDmg.MacDmgCompression>(
                                MacDmgCompression, "BundlerMacDmgCompression")
                            ?? DotNet.Bundler.MacDmg.MacDmgCompression.Ulmo,
                        VolumeName = EmptyToNull(MacDmgVolumeName),
                        SkipWindowLayout = string.Equals(
                            MacDmgSkipWindowLayout, "true", StringComparison.OrdinalIgnoreCase),
                        WindowX = MacIntValue(MacDmgWindowX, "BundlerMacDmgWindowX", 200),
                        WindowY = MacIntValue(MacDmgWindowY, "BundlerMacDmgWindowY", 120),
                        WindowWidth = MacIntValue(MacDmgWindowWidth, "BundlerMacDmgWindowWidth", 660),
                        WindowHeight = MacIntValue(MacDmgWindowHeight, "BundlerMacDmgWindowHeight", 400),
                        AppIconX = MacIntValue(MacDmgAppIconX, "BundlerMacDmgAppIconX", 180),
                        AppIconY = MacIntValue(MacDmgAppIconY, "BundlerMacDmgAppIconY", 170),
                        ApplicationsIconX = MacIntValue(
                            MacDmgApplicationsIconX, "BundlerMacDmgApplicationsIconX", 480),
                        ApplicationsIconY = MacIntValue(
                            MacDmgApplicationsIconY, "BundlerMacDmgApplicationsIconY", 170),
                        IconSize = MacIntValue(MacDmgIconSize, "BundlerMacDmgIconSize", 128),
                        BackgroundFile = EmptyToNull(MacDmgBackgroundFile),
                        VolumeIconFile = EmptyToNull(MacDmgVolumeIconFile),
                        Signing = BuildMacDmgSigning()
                    },
                    BuildMacAppConfiguration(),
                    new MacDmgBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.Deb)
            {
                produced = new DebBundler(
                    new DebBundleConfiguration
                    {
                        PackageName = EmptyToNull(DebPackageName),
                        Version = EmptyToNull(DebVersion),
                        Revision = EmptyToNull(DebRevision),
                        Epoch = EmptyToNull(DebEpoch),
                        Architecture = EmptyToNull(DebArchitecture),
                        Maintainer = EmptyToNull(DebMaintainer),
                        InstallRoot = EmptyToNull(DebInstallRoot),
                        BinLink = string.Equals(DebBinLink, "none", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : EmptyToNull(DebBinLink),
                        Depends = SplitList(DebDepends),
                        Recommends = SplitList(DebRecommends),
                        Provides = SplitList(DebProvides),
                        Conflicts = SplitList(DebConflicts),
                        Replaces = SplitList(DebReplaces),
                        Section = EmptyToNull(DebSection),
                        Priority = EmptyToNull(DebPriority),
                        Categories = EmptyToNull(DebCategories),
                        DesktopFile = OptionalFullPath(DebDesktopFile),
                        MetainfoFile = OptionalFullPath(DebMetainfoFile),
                        ChangelogFile = OptionalFullPath(DebChangelogFile),
                        PreinstFile = OptionalFullPath(DebPreinstFile),
                        PostinstFile = OptionalFullPath(DebPostinstFile),
                        PrermFile = OptionalFullPath(DebPrermFile),
                        PostrmFile = OptionalFullPath(DebPostrmFile),
                        SystemdServiceFile = OptionalFullPath(DebSystemdServiceFile),
                        Conffiles = SplitList(DebConffiles),
                        Compression = EmptyToNull(DebCompression),
                        Files = DebFiles.Select(item => new DebFileEntry
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = item.GetMetadata("Destination")
                        }).ToArray()
                    },
                    new DebBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.Rpm)
            {
                produced = new RpmBundler(
                    new RpmBundleConfiguration
                    {
                        PackageName = EmptyToNull(RpmPackageName),
                        Version = EmptyToNull(RpmVersion),
                        Release = EmptyToNull(RpmRelease),
                        Epoch = EmptyToNull(RpmEpoch),
                        Architecture = EmptyToNull(RpmArchitecture),
                        Vendor = EmptyToNull(RpmVendor),
                        InstallRoot = EmptyToNull(RpmInstallRoot),
                        BinLink = string.Equals(RpmBinLink, "none", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : EmptyToNull(RpmBinLink),
                        Requires = SplitList(RpmRequires),
                        Provides = SplitList(RpmProvides),
                        Conflicts = SplitList(RpmConflicts),
                        Obsoletes = SplitList(RpmObsoletes),
                        Recommends = SplitList(RpmRecommends),
                        Suggests = SplitList(RpmSuggests),
                        License = EmptyToNull(RpmLicense),
                        Group = EmptyToNull(RpmGroup),
                        Url = EmptyToNull(RpmUrl),
                        Categories = EmptyToNull(RpmCategories),
                        DesktopFile = OptionalFullPath(RpmDesktopFile),
                        MetainfoFile = OptionalFullPath(RpmMetainfoFile),
                        ChangelogFile = OptionalFullPath(RpmChangelogFile),
                        ConfigFiles = SplitList(RpmConfigFiles),
                        SystemdServiceFile = OptionalFullPath(RpmSystemdServiceFile),
                        PreInstallFile = OptionalFullPath(RpmPreInstallFile),
                        PostInstallFile = OptionalFullPath(RpmPostInstallFile),
                        PreUninstallFile = OptionalFullPath(RpmPreUninstallFile),
                        PostUninstallFile = OptionalFullPath(RpmPostUninstallFile),
                        PreInstallProgram = EmptyToNull(RpmPreInstallProgram),
                        PostInstallProgram = EmptyToNull(RpmPostInstallProgram),
                        PreUninstallProgram = EmptyToNull(RpmPreUninstallProgram),
                        PostUninstallProgram = EmptyToNull(RpmPostUninstallProgram),
                        Compression = EmptyToNull(RpmCompression),
                        SigningKeyFile = OptionalFullPath(RpmSigningKeyFile),
                        SigningKeyPassphrase = EmptyToNull(RpmSigningKeyPassphrase),
                        Files = RpmFiles.Select(item => new RpmFileEntry
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = item.GetMetadata("Destination")
                        }).ToArray()
                    },
                    new RpmBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format is PackageFormat.Zip or PackageFormat.TarGz)
            {
                produced = new ArchiveBundler(
                    new ArchiveBundleConfiguration
                    {
                        PackageName = EmptyToNull(ArchivePackageName),
                        Version = EmptyToNull(ArchiveVersion),
                        ArchiveName = EmptyToNull(ArchiveName),
                        Files = ArchiveFiles.Select(item => new ArchiveFileEntry
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = item.GetMetadata("Destination")
                        }).ToArray()
                    },
                    new ArchiveBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.AlpineApk)
            {
                produced = new AlpineApkBundler(
                    new AlpineApkBundleConfiguration
                    {
                        PackageName = EmptyToNull(AlpineApkPackageName),
                        Version = EmptyToNull(AlpineApkVersion),
                        Architecture = EmptyToNull(AlpineApkArchitecture),
                        Origin = EmptyToNull(AlpineApkOrigin),
                        Description = EmptyToNull(AlpineApkDescription),
                        Url = EmptyToNull(AlpineApkUrl),
                        BinLink = string.Equals(AlpineApkBinLink, "none", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : EmptyToNull(AlpineApkBinLink),
                        Release = EmptyToNull(AlpineApkRelease),
                        License = EmptyToNull(AlpineApkLicense),
                        BuildDate = EmptyToNull(AlpineApkBuildDate),
                        Depends = SplitList(AlpineApkDepends),
                        Provides = SplitList(AlpineApkProvides),
                        Triggers = SplitList(AlpineApkTriggers),
                        PreInstallScript = OptionalFullPath(AlpineApkPreInstallScript),
                        PostInstallScript = OptionalFullPath(AlpineApkPostInstallScript),
                        PreDeinstallScript = OptionalFullPath(AlpineApkPreDeinstallScript),
                        PostDeinstallScript = OptionalFullPath(AlpineApkPostDeinstallScript),
                        PreUpgradeScript = OptionalFullPath(AlpineApkPreUpgradeScript),
                        PostUpgradeScript = OptionalFullPath(AlpineApkPostUpgradeScript),
                        Files = AlpineApkFiles.Select(item => new AlpineApkFileEntry
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = item.GetMetadata("Destination")
                        }).ToArray(),
                        SigningKeyFile = OptionalFullPath(AlpineApkSigningKeyFile),
                        SigningKeyPassphrase = EmptyToNull(AlpineApkSigningKeyPassphrase)
                    },
                    new AlpineApkBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else if (format == PackageFormat.AppImage)
            {
                produced = new AppImageBundler(
                    new AppImageBundleConfiguration
                    {
                        PackageName = EmptyToNull(AppImagePackageName),
                        Version = EmptyToNull(AppImageVersion),
                        Architecture = EmptyToNull(AppImageArchitecture),
                        InstallRoot = EmptyToNull(AppImageInstallRoot),
                        BinLink = string.Equals(AppImageBinLink, "none", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : EmptyToNull(AppImageBinLink),
                        IconFile = OptionalFullPath(AppImageIconFile),
                        DesktopFile = OptionalFullPath(AppImageDesktopFile),
                        Categories = EmptyToNull(AppImageCategories),
                        MetainfoFile = OptionalFullPath(AppImageMetainfoFile),
                        SigningKeyFile = OptionalFullPath(AppImageSigningKeyFile),
                        SigningKeyPassphrase = EmptyToNull(AppImageSigningKeyPassphrase),
                        Files = AppImageFiles.Select(item => new AppImageFileEntry
                        {
                            Source = Path.GetFullPath(item.ItemSpec),
                            Destination = item.GetMetadata("Destination")
                        }).ToArray()
                    },
                    new AppImageBundlerOptions { Logger = new MsBuildBundleLogger(Log) })
                    .BuildAsync(configuration)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                throw new NotSupportedException($"Bundler does not support the '{format}' package format.");
            }
                artifacts.AddRange(produced);
            }

            if (UpdateEnabled)
            {
                var feed = UpdateManifestEmitter.EmitAsync(
                    Configure(formats), artifacts).GetAwaiter().GetResult();
                foreach (var path in feed)
                {
                    Log.LogMessage(MessageImportance.High, $"Bundler update: {path}");
                }
            }

            Artifacts = artifacts.Select(artifact =>
            {
                var item = new TaskItem(artifact.Path);
                item.SetMetadata("Format", artifact.Format.ToString());
                item.SetMetadata("RuntimeIdentifier", artifact.RuntimeIdentifier);
                return (ITaskItem)item;
            }).ToArray();

            return true;
        }
        catch (BundleValidationException validation)
        {
            foreach (var issue in validation.Issues)
            {
                Log.LogError($"{issue.Path}: {issue.Message}");
            }
            return false;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private MacAppBundleConfiguration BuildMacAppConfiguration() =>
        new()
        {
            BundleName = EmptyToNull(MacAppBundleName),
            BundleDisplayName = EmptyToNull(MacAppDisplayName),
            ShortVersion = EmptyToNull(MacAppShortVersion),
            BuildVersion = EmptyToNull(MacAppBuildVersion),
            MinimumSystemVersion = EmptyToNull(MacAppMinimumSystemVersion),
            Category = EmptyToNull(MacAppCategory),
            IconName = EmptyToNull(MacAppIconName),
            Contents = MacContents.Select(item => new MacAppContentConfiguration
            {
                Source = Path.GetFullPath(item.ItemSpec),
                TargetPath = item.GetMetadata("TargetPath").Trim()
            }).ToArray(),
            Frameworks = MacFrameworks.Select(item => Path.GetFullPath(item.ItemSpec)).ToArray(),
            DocumentTypes = MacDocumentTypes.Select(item => new MacAppDocumentTypeConfiguration
            {
                Extensions = MacListMetadata(item, "Extensions", item.ItemSpec),
                Name = EmptyMetadataToNull(item, "Name"),
                Description = EmptyMetadataToNull(item, "Description"),
                Role = MacEnumMetadata<MacAppTypeRole>(item, "Role"),
                Rank = MacEnumMetadata<MacAppHandlerRank>(item, "Rank"),
                ContentTypes = MacListMetadata(item, "ContentTypes"),
                MimeType = EmptyMetadataToNull(item, "MimeType"),
                ExportedTypeIdentifier = EmptyMetadataToNull(item, "ExportedTypeIdentifier"),
                ExportedTypeConformsTo = MacListMetadata(item, "ExportedTypeConformsTo")
            }).ToArray(),
            UrlTypes = MacUrlTypes.Select(item => new MacAppUrlTypeConfiguration
            {
                Schemes = MacListMetadata(item, "Schemes", item.ItemSpec),
                Name = EmptyMetadataToNull(item, "Name"),
                Role = MacEnumMetadata<MacAppTypeRole>(item, "Role")
            }).ToArray(),
            ExceptionDomain = EmptyToNull(MacAppExceptionDomain),
            InfoPlistFile = OptionalFullPath(MacAppInfoPlistFile),
            InfoPlistXml = EmptyToNull(MacAppInfoPlistXml),
            Signing = new MacAppSigningConfiguration
            {
                Identity = EmptyToNull(MacAppSignIdentity),
                TemporaryCertificatePath = OptionalFullPath(MacAppSigningCertificatePath),
                TemporaryCertificatePassword = EmptyToNull(MacAppSigningCertificatePassword),
                HardenedRuntime = MacAppHardenedRuntime,
                EntitlementsFile = OptionalFullPath(MacAppEntitlementsFile),
                Notarize = MacAppNotarize,
                NotaryWait = MacAppNotaryWait,
                SkipStapling = MacAppSkipStapling,
                KeychainProfile = EmptyToNull(MacAppNotaryProfile),
                AppleId = EmptyToNull(MacAppAppleId),
                ApplePassword = EmptyToNull(MacAppApplePassword),
                AppleTeamId = EmptyToNull(MacAppAppleTeamId),
                ApiKeyPath = OptionalFullPath(MacAppNotaryApiKeyPath),
                ApiKeyId = EmptyToNull(MacAppNotaryApiKeyId),
                ApiIssuer = EmptyToNull(MacAppNotaryApiIssuer)
            }
        };

    private MacDmgSigningConfiguration? BuildMacDmgSigning()
    {
        var identity = EmptyToNull(MacDmgSignIdentity);
        var certificatePath = OptionalFullPath(MacDmgSignCertificatePath);
        if (identity is null && certificatePath is null)
        {
            return null;
        }
        return new MacDmgSigningConfiguration
        {
            Identity = identity,
            TemporaryCertificatePath = certificatePath,
            TemporaryCertificatePassword = EmptyToNull(MacDmgSignCertificatePassword)
        };
    }

    private IReadOnlyList<PackageFormat> ParseFormats()
    {
        var result = new List<PackageFormat>();
        foreach (var value in Formats.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            PackageFormat format;
            if (!Enum.TryParse(value.Trim(), true, out format))
            {
                throw new ArgumentException("Unknown bundle format '" + value.Trim() + "'.", nameof(Formats));
            }

            if (!result.Contains(format))
            {
                result.Add(format);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("At least one bundle format is required.", nameof(Formats));
        }

        return result;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? OptionalFullPath(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

    // Debian relation fields hold comma-separated clauses, so the MSBuild-facing
    // list separator is ';' (e.g. "libc6 (>= 2.35);libssl3").
    private static IReadOnlyList<string>? SplitList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(';').Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0).ToArray();

    private static string ResourceTargetPath(ITaskItem item)
    {
        var targetPath = item.GetMetadata("TargetPath").Trim();
        return targetPath.Length > 0 ? targetPath : Path.GetFileName(item.ItemSpec);
    }

    private static string? EmptyMetadataToNull(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name).Trim();
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlyList<string> MacListMetadata(ITaskItem item, string name, string? fallback = null)
    {
        var value = item.GetMetadata(name).Trim();
        var source = value.Length == 0 ? (fallback ?? "") : value;
        return source.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToArray();
    }

    private static TEnum? MacEnumValue<TEnum>(string value, string propertyName) where TEnum : struct
    {
        if (value.Trim().Length == 0)
        {
            return null;
        }
        if (!Enum.TryParse<TEnum>(value.Trim(), true, out var parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
        {
            throw new ArgumentException(
                $"{propertyName} must be one of {string.Join(", ", Enum.GetNames(typeof(TEnum)))}; got '{value}'.");
        }
        return parsed;
    }

    private static int MacIntValue(string value, string propertyName, int fallback)
    {
        if (value.Trim().Length == 0)
        {
            return fallback;
        }
        if (!int.TryParse(value.Trim(), out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"{propertyName} must be a non-negative integer; got '{value}'.");
        }
        return parsed;
    }

    private static TEnum? MacEnumMetadata<TEnum>(ITaskItem item, string name) where TEnum : struct
    {
        var value = item.GetMetadata(name).Trim();
        if (value.Length == 0)
        {
            return null;
        }
        if (!Enum.TryParse<TEnum>(value, true, out var parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
        {
            throw new ArgumentException(
                $"BundlerMac* metadata {name} must be one of " +
                $"{string.Join(", ", Enum.GetNames(typeof(TEnum)))}; got '{value}'.");
        }
        return parsed;
    }

    private IReadOnlyList<string> ParseLanguages()
    {
        var languages = NsisLanguages
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(language => language.Trim())
            .Where(language => language.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return languages.Length > 0
            ? languages
            : throw new ArgumentException("At least one NSIS language is required.", nameof(NsisLanguages));
    }

    private static IReadOnlyList<string> ParseSemicolonList(string value) => value
        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim())
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private NsisInstallMode ParseInstallMode()
    {
        if (Enum.TryParse<NsisInstallMode>(NsisInstallMode, true, out var mode))
        {
            return mode;
        }

        throw new ArgumentException(
            "BundlerNsisInstallMode must be currentUser, perMachine, or both.",
            nameof(NsisInstallMode));
    }

    private DotNet.Bundler.Nsis.NsisCompression ParseCompression()
    {
        if (Enum.TryParse<DotNet.Bundler.Nsis.NsisCompression>(NsisCompression, true, out var compression) &&
            Enum.IsDefined(typeof(DotNet.Bundler.Nsis.NsisCompression), compression))
        {
            return compression;
        }

        throw new ArgumentException(
            "BundlerNsisCompression must be lzma, zlib, bzip2, or none.",
            nameof(NsisCompression));
    }

    private IReadOnlyDictionary<string, string> ParseWixLanguageFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in WixLanguageFiles)
        {
            var language = item.GetMetadata("Language").Trim();
            if (language.Length == 0)
            {
                throw new ArgumentException(
                    "Each BundlerWixLanguageFile item requires Language metadata.",
                    nameof(WixLanguageFiles));
            }

            if (result.ContainsKey(language))
            {
                throw new ArgumentException("Duplicate MSI locale file for '" + language + "'.");
            }

            result.Add(language, Path.GetFullPath(item.ItemSpec));
        }

        return result;
    }

    private IReadOnlyDictionary<string, string> ParseCustomLanguageFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in NsisLanguageFiles)
        {
            var language = item.GetMetadata("Language").Trim();
            if (language.Length == 0)
            {
                throw new ArgumentException(
                    "Each BundlerNsisLanguageFile item requires Language metadata.",
                    nameof(NsisLanguageFiles));
            }

            if (result.ContainsKey(language))
            {
                throw new ArgumentException("Duplicate custom NSIS language file for '" + language + "'.");
            }

            result.Add(language, Path.GetFullPath(item.ItemSpec));
        }

        return result;
    }

    private IBundleSigner? CreateWindowsSigner()
    {
        var pfxFile = EmptyToNull(WindowsSigningPfxFile);
        var thumbprint = EmptyToNull(WindowsSigningCertificateThumbprint);
        var command = EmptyToNull(WindowsSigningCommand);
        var configuredSources = new[] { pfxFile, thumbprint, command }.Count(value => value is not null);
        if (configuredSources == 0)
        {
            if (!string.IsNullOrWhiteSpace(WindowsSigningPfxPasswordEnvironmentVariable) ||
                !string.IsNullOrWhiteSpace(WindowsSigningTimestampUrl))
            {
                throw new ArgumentException(
                    "A signing PFX file or certificate thumbprint is required when Windows signing options are set.");
            }
            return null;
        }
        if (configuredSources != 1)
        {
            throw new ArgumentException(
                "Configure exactly one Windows signer: PFX, certificate thumbprint, or external command.");
        }
        if (command is not null)
        {
            if (!string.IsNullOrWhiteSpace(WindowsSigningPfxPasswordEnvironmentVariable) ||
                !string.IsNullOrWhiteSpace(WindowsSigningTimestampUrl))
            {
                throw new ArgumentException("PFX password and timestamp options cannot be combined with an external signing command.");
            }
            return new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
            {
                Command = command,
                Arguments = WindowsSigningCommandArguments.Select(item => item.ItemSpec).ToArray()
            });
        }
        if (!Enum.TryParse(
                WindowsSigningCertificateStoreLocation,
                true,
                out System.Security.Cryptography.X509Certificates.StoreLocation storeLocation))
        {
            throw new ArgumentException(
                "BundlerWindowsSigningCertificateStoreLocation must be CurrentUser or LocalMachine.");
        }

        string? password = null;
        if (!string.IsNullOrWhiteSpace(WindowsSigningPfxPasswordEnvironmentVariable))
        {
            password = Environment.GetEnvironmentVariable(WindowsSigningPfxPasswordEnvironmentVariable);
            if (password is null)
            {
                throw new InvalidOperationException(
                    $"Signing password environment variable '{WindowsSigningPfxPasswordEnvironmentVariable}' is not set.");
            }
        }

        return new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
        {
            PfxFile = pfxFile is null ? null : Path.GetFullPath(pfxFile),
            PfxPassword = password,
            CertificateThumbprint = thumbprint,
            CertificateStoreLocation = storeLocation,
            TimestampUrl = EmptyToNull(WindowsSigningTimestampUrl)
        });
    }

    private sealed class MsBuildBundleLogger(TaskLoggingHelper log) : IBundleLogger
    {
        public void Log(BundleLogLevel level, string message)
        {
            switch (level)
            {
                case BundleLogLevel.Warning:
                    log.LogWarning(message);
                    break;
                case BundleLogLevel.Error:
                    log.LogError(message);
                    break;
                case BundleLogLevel.Trace:
                    log.LogMessage(MessageImportance.Low, message);
                    break;
                default:
                    log.LogMessage(MessageImportance.High, message);
                    break;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNet.Bundler;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Wix;
using DotNet.Bundler.Signing.Windows;
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
    public bool WixStartMenuShortcut { get; set; }
    public bool WixDesktopShortcut { get; set; }
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
    public string WindowsSigningPfxFile { get; set; } = "";
    public string WindowsSigningPfxPasswordEnvironmentVariable { get; set; } = "";
    public string WindowsSigningCertificateThumbprint { get; set; } = "";
    public string WindowsSigningCertificateStoreLocation { get; set; } = "CurrentUser";
    public string WindowsSigningTimestampUrl { get; set; } = "";
    public string WindowsSigningCommand { get; set; } = "";
    public ITaskItem[] WindowsSigningCommandArguments { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] NsisLanguageFiles { get; set; } = Array.Empty<ITaskItem>();
    [Output] public ITaskItem[] Artifacts { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var formats = ParseFormats();
            var configuration = new BundleConfiguration
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
                        Formats = formats
                    }
                }
            };

            IReadOnlyList<BundleArtifact> artifacts;
            if (formats.All(format => format == PackageFormat.Msi))
            {
                if (!Enum.TryParse<DotNet.Bundler.Wix.WixInstallScope>(WixInstallScope, true, out var scope) ||
                    !Enum.IsDefined(typeof(DotNet.Bundler.Wix.WixInstallScope), scope))
                {
                    throw new ArgumentException("BundlerWixInstallScope must be currentUser or perMachine.");
                }
                var language = WixLanguage.ToLowerInvariant() switch
                {
                    "en-us" => WixPackageLanguage.English,
                    "zh-cn" => WixPackageLanguage.ChineseSimplified,
                    _ => throw new ArgumentException("BundlerWixLanguage must be en-US or zh-CN.")
                };
                artifacts = new WixBundler(
                    new WixBundleConfiguration
                    {
                        InstallScope = scope,
                        UpgradeCode = EmptyToNull(WixUpgradeCode),
                        MsiVersion = EmptyToNull(WixMsiVersion),
                        AllowDowngrades = WixAllowDowngrades,
                        Codepage = WixCodepage,
                        Language = language,
                        StartMenuShortcut = WixStartMenuShortcut,
                        DesktopShortcut = WixDesktopShortcut
                    },
                    new WixBundlerOptions
                    {
                        ToolsetArchivePath = EmptyToNull(WixToolsetArchivePath),
                        ToolCacheDirectory = EmptyToNull(ToolCacheDirectory),
                        Signer = CreateWindowsSigner(),
                        Logger = new MsBuildBundleLogger(Log)
                    }).BuildAsync(configuration).GetAwaiter().GetResult();
            }
            else if (formats.All(format => format == PackageFormat.Nsis))
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
            artifacts = new NsisBundler(nsisConfiguration, nsisOptions)
                .BuildAsync(configuration)
                .GetAwaiter()
                .GetResult();
            }
            else
            {
                throw new NotSupportedException("WIN-MSI-1 accepts either NSIS or MSI per MSBuild invocation; mixed formats are not supported yet.");
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
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
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

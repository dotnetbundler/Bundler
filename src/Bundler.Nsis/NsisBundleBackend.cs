using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;
using System.Text;

namespace DotNet.Bundler.Nsis;

internal sealed class NsisBundleBackend(
    NsisToolset toolset,
    string templatePath,
    string languageDirectory,
    string pluginDirectory,
    NsisBundleConfiguration settings,
    IBundleSigner? signer) : IBundleBackend
{
    private static readonly string[] RequiredLanguageKeys =
    [
        "ShortcutPageTitle", "ShortcutPageSubtitle", "DesktopShortcutLabel", "StartMenuShortcutLabel",
        "NonEmptyDirectoryWarning", "AppRunningPrompt", "AppCloseFailed", "PayloadWriteFailed",
        "AppDataPageTitle", "AppDataPageSubtitle", "DeleteAppDataLabel", "SameVersionDetected",
        "UpgradeDetected", "DowngradeDetected", "DowngradeBlocked", "UnknownVersionDetected",
        "SilentDowngradeBlocked", "SilentUnknownVersionBlocked", "RemovingExistingVersion",
        "ExistingUninstallFailed", "LegacyMsiDetected", "RemovingLegacyMsiVersion", "InvalidRestartMode",
        "ArgumentsRequireRestart", "AutomatedNonEmptyDirectoryBlocked", "ApplicationLaunchFailed",
        "RecoveryManifestMismatch", "RecoveryManifestMismatchDetail"
    ];

    public PackageFormat Format => PackageFormat.Nsis;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = context.Configuration;
        var item = context.Item;
        var fullCompilerPath = Path.GetFullPath(toolset.CompilerPath);
        if (!File.Exists(fullCompilerPath))
        {
            throw new FileNotFoundException("The NSIS compiler was not found.", fullCompilerPath);
        }
        var fullDataDirectory = string.IsNullOrWhiteSpace(toolset.DataDirectory)
            ? null
            : Path.GetFullPath(toolset.DataDirectory!);
        if (fullDataDirectory is not null && !Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException($"The NSIS data directory was not found: {fullDataDirectory}");
        }

        InspectDirectoryTree(item.InputDirectory);

        // 更新开启时先落身份旁车——已签名 staging 的复制会把旁车一并带进载荷。
        item = UpdatePayloadStaging.EnsureStaged(context, item);

        var fullTemplatePath = Path.GetFullPath(templatePath);
        if (!File.Exists(fullTemplatePath))
        {
            throw new FileNotFoundException("The NSIS script template was not found.", fullTemplatePath);
        }

        Directory.CreateDirectory(item.OutputDirectory);
        var safeProductName = SafeFileName(configuration.ProductName);
        var installerPath = Path.Combine(
            item.OutputDirectory,
            ArtifactNaming.FileName(safeProductName, configuration.Version, item.Target, PackageFormat.Nsis));
        var scriptPath = Path.Combine(context.WorkDirectory, "installer.nsi");
        cancellationToken.ThrowIfCancellationRequested();
        var template = File.ReadAllText(fullTemplatePath);
        var localization = PrepareLanguages(settings, context.WorkDirectory);
        var effectiveItem = item;
        var effectivePluginDirectory = pluginDirectory;
        try
        {
            if (signer is null)
            {
                WriteScript(UninstallerMode.Unsigned);
                await CompileAsync();
            }
            else
            {
                effectiveItem = await PrepareSignedPayloadAsync(context, item, signer, configuration.ProductName, cancellationToken);
                effectivePluginDirectory = await PrepareSignedPluginsAsync(
                    context,
                    pluginDirectory,
                    signer,
                    configuration.ProductName,
                    item.Target.Target,
                    cancellationToken);
                var uninstallerPath = Path.Combine(context.WorkDirectory, "signed-uninstaller.exe");
                WriteScript(new UninstallerMode(
                    CreateUninstallerFinalizeCommand(uninstallerPath),
                    string.Empty,
                    string.Empty));
                await CompileAsync();
                if (!File.Exists(uninstallerPath))
                {
                    throw new InvalidOperationException("NSIS did not export the uninstaller for signing.");
                }

                context.Logger.Log(BundleLogLevel.Information, "Signing the NSIS uninstaller.");
                await signer.SignAsync(
                    new BundleSigningRequest(uninstallerPath, BundleSigningArtifactKind.Uninstaller, configuration.ProductName, item.Target.Target),
                    cancellationToken);

                WriteScript(new UninstallerMode(
                    string.Empty,
                    "!define BUNDLER_IMPORT_SIGNED_UNINSTALLER",
                    Escape(uninstallerPath)));
                await CompileAsync();
                context.Logger.Log(BundleLogLevel.Information, "Signing the NSIS installer.");
                await signer.SignAsync(
                    new BundleSigningRequest(installerPath, BundleSigningArtifactKind.Installer, configuration.ProductName, item.Target.Target),
                    cancellationToken);
            }

            if (!File.Exists(installerPath))
            {
                throw new InvalidOperationException("NSIS reported success but did not create the installer.");
            }

            return [new BundleArtifact(Format, item.Target.Target, installerPath)];
        }
        catch
        {
            TryDeleteFile(installerPath);
            throw;
        }

        void WriteScript(UninstallerMode mode) => File.WriteAllText(
            scriptPath,
            CreateScript(
                template,
                configuration,
                settings,
                effectiveItem,
                installerPath,
                safeProductName,
                localization,
                effectivePluginDirectory,
                mode),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        async Task CompileAsync()
        {
            context.Logger.Log(BundleLogLevel.Trace, $"Running NSIS compiler '{fullCompilerPath}'.");
            await ProcessRunner.RunAsync(
                fullCompilerPath,
                ["-INPUTCHARSET", "UTF8", "-OUTPUTCHARSET", "UTF8", "-V2", scriptPath],
                context.WorkDirectory,
                cancellationToken,
                fullDataDirectory is null
                    ? null
                    : new Dictionary<string, string> { ["NSISDIR"] = fullDataDirectory });
        }
    }

    private static async Task<BundlePlanItem> PrepareSignedPayloadAsync(
        BundleBuildContext context,
        BundlePlanItem item,
        IBundleSigner signer,
        string productName,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(context.WorkDirectory, "signed-payload");
        CopyDirectory(item.InputDirectory, destination);
        var signingFiles = new List<(string RelativePath, BundleSigningArtifactKind Kind)>
        {
            (item.MainExecutable, BundleSigningArtifactKind.PayloadExecutable)
        };
        signingFiles.AddRange(item.SigningFiles.Select(path => (path, BundleSigningArtifactKind.PayloadFile)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, kind) in signingFiles)
        {
            var path = ResolvePayloadPath(destination, relativePath);
            if (!seen.Add(path))
            {
                continue;
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The configured payload signing file '{relativePath}' was not found.", path);
            }
            context.Logger.Log(BundleLogLevel.Information, $"Signing staged payload file '{relativePath}'.");
            await signer.SignAsync(
                new BundleSigningRequest(path, kind, productName, item.Target.Target),
                cancellationToken);
        }
        return item with { InputDirectory = destination };
    }

    private static async Task<string> PrepareSignedPluginsAsync(
        BundleBuildContext context,
        string sourceDirectory,
        IBundleSigner signer,
        string productName,
        string targetTarget,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(context.WorkDirectory, "signed-plugins");
        CopyDirectory(sourceDirectory, destination);
        foreach (var path in InspectDirectoryTree(destination).Files
                     .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            context.Logger.Log(BundleLogLevel.Information, $"Signing Bundler native component '{Path.GetFileName(path)}'.");
            await signer.SignAsync(
                new BundleSigningRequest(path, BundleSigningArtifactKind.NativeComponent, productName, targetTarget),
                cancellationToken);
        }
        return destination;
    }

    private static string ResolvePayloadPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Payload signing paths must be non-empty relative paths: '{relativePath}'.");
        }
        var fullRoot = DirectoryPrefix(root);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Payload signing path must stay inside the input directory: '{relativePath}'.");
        }
        return fullPath;
    }

    private static void CopyDirectory(string source, string destination)
    {
        var tree = InspectDirectoryTree(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in tree.Directories)
        {
            Directory.CreateDirectory(Path.Combine(destination, RelativePath(source, directory)));
        }
        foreach (var file in tree.Files)
        {
            var target = Path.Combine(destination, RelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static string CreateScript(
        string template,
        BundleConfiguration configuration,
        NsisBundleConfiguration settings,
        BundlePlanItem item,
        string installerPath,
        string safeProductName)
    {
        return CreateScript(
            template,
            configuration,
            settings,
            item,
            installerPath,
            safeProductName,
            new NsisLocalization(
                "!insertmacro MUI_LANGUAGE \"English\"",
                string.Empty,
                string.Empty),
            ".",
            UninstallerMode.Unsigned);
    }

    private static string CreateScript(
        string template,
        BundleConfiguration configuration,
        NsisBundleConfiguration settings,
        BundlePlanItem item,
        string installerPath,
        string safeProductName,
        NsisLocalization localization,
        string nsisPluginDirectory,
        UninstallerMode uninstallerMode)
    {
        var publisher = configuration.Publisher ?? configuration.ProductName;
        var description = configuration.Description ?? configuration.ProductName;
        var copyright = configuration.Copyright ?? publisher;
        var version = NumericVersion(configuration.Version);
        var resources = ExpandResources(configuration.Resources, item.InputDirectory);
        var visualDirectives = CreateVisualDirectives(configuration.Icons, settings);
        var shortcuts = PrepareShortcuts(configuration, settings.Shortcuts, item, resources);
        var transaction = CreateTransactionRendering(configuration, shortcuts);

        return TemplateRenderer.Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product_name"] = Escape(configuration.ProductName),
            ["version"] = Escape(configuration.Version),
            ["numeric_version"] = version,
            ["publisher"] = Escape(publisher),
            ["description"] = Escape(description),
            ["homepage"] = Escape(configuration.Homepage ?? string.Empty),
            ["copyright"] = Escape(copyright),
            ["identifier"] = Escape(configuration.Identifier),
            ["main_executable"] = Escape(item.MainExecutable),
            ["install_folder"] = Escape(safeProductName),
            ["install_mode"] = InstallModeName(settings.InstallScope),
            ["compression_directive"] = CompressionDirective(settings.Compression),
            ["target_architecture"] = TargetArchitectureName(item.Target.Architecture),
            ["allow_downgrades"] = settings.AllowDowngrades ? "true" : "false",
            ["legacy_msi_product_codes"] = Escape(CreateMsiCodeList(settings.LegacyMsiProductCodes)),
            ["legacy_msi_upgrade_codes"] = Escape(CreateMsiCodeList(settings.LegacyMsiUpgradeCodes)),
            ["legacy_msi_autodetect_name"] = Escape(settings.LegacyMsiAutoDetect ? configuration.ProductName : ""),
            ["legacy_msi_autodetect_publisher"] = Escape(settings.LegacyMsiAutoDetect ? publisher : ""),
            ["input_glob"] = Escape(Path.Combine(item.InputDirectory, "*")),
            ["output_file"] = Escape(installerPath),
            ["estimated_size"] = EstimateSizeInKilobytes(item.InputDirectory).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["plugin_directory"] = Escape(Path.GetFullPath(nsisPluginDirectory)),
            ["installer_icon_directives"] = visualDirectives,
            ["license_page"] = CreateLicensePage(configuration.LicenseFile),
            ["homepage_registry"] = CreateHomepageRegistry(configuration.Homepage),
            ["association_install_commands"] = CreateAssociationInstallCommands(configuration),
            ["association_uninstall_commands"] = CreateAssociationUninstallCommands(configuration, item.MainExecutable),
            ["installer_hooks_include"] = CreateInstallerHooksInclude(settings.InstallerHooksFile),
            ["shortcut_desktop_default"] = shortcuts.DesktopDefault ? "1" : "0",
            ["shortcut_start_menu_default"] = shortcuts.StartMenuDefault ? "1" : "0",
            ["shortcut_arguments"] = Escape(shortcuts.Arguments),
            ["shortcut_working_directory"] = shortcuts.WorkingDirectory,
            ["shortcut_icon"] = shortcuts.Icon,
            ["shortcut_app_user_model_id"] = Escape(shortcuts.AppUserModelId),
            ["shortcut_start_menu_directory"] = shortcuts.StartMenuDirectory,
            ["shortcut_start_menu_path"] = shortcuts.StartMenuPath,
            ["shortcut_desktop_path"] = shortcuts.DesktopPath,
            ["shortcut_owned_targets"] = shortcuts.OwnedTargets,
            ["shortcut_migration_commands"] = shortcuts.MigrationCommands,
            ["shortcut_legacy_cleanup_commands"] = shortcuts.LegacyCleanupCommands,
            ["transaction_snapshot_commands"] = transaction.SnapshotCommands,
            ["transaction_validation_commands"] = transaction.ValidationCommands,
            ["transaction_restore_commands"] = transaction.RestoreCommands,
            ["transaction_registry_snapshot_count"] = transaction.RegistryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["transaction_file_snapshot_count"] = transaction.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resource_install_commands"] = CreateResourceInstallCommands(resources),
            ["uninstall_payload"] = CreateUninstallPayload(item.InputDirectory, resources),
            ["language_macros"] = localization.LanguageMacros,
            ["language_files"] = localization.LanguageFiles,
            ["display_language_selector"] = localization.DisplayLanguageSelector,
            ["uninstaller_finalize_command"] = uninstallerMode.FinalizeCommand,
            ["uninstaller_import_define"] = uninstallerMode.ImportDefine,
            ["signed_uninstaller"] = uninstallerMode.SignedUninstallerPath
        });
    }

    internal static string CreateUninstallerFinalizeCommand(string destinationPath)
    {
        if (destinationPath.Contains('\''))
        {
            throw new InvalidOperationException("The NSIS signing work directory cannot contain an apostrophe.");
        }
        // finalize 命令经 system() 进入 cmd 命令行模式：该模式下 % 无转义（%% 仅在批处理中折叠），
        // 引号内的 %VAR% 仍会展开，含 % 的路径只能拒绝。
        if (destinationPath.Contains('%'))
        {
            throw new InvalidOperationException("The NSIS signing work directory cannot contain a percent sign.");
        }

        var destination = Escape(destinationPath);
        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? $"!uninstfinalize 'cmd.exe /D /C copy /Y \"%1\" \"{destination}\" >NUL'"
            : $"!uninstfinalize '/bin/cp \"%1\" \"{destination}\"'";
    }

    private static string CreateVisualDirectives(
        IReadOnlyList<string> icons,
        NsisBundleConfiguration settings)
    {
        var fallbackIcon = icons.FirstOrDefault(path =>
            Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase));
        if (fallbackIcon is null && icons.Count > 0)
        {
            throw new InvalidOperationException("Windows NSIS packaging requires at least one .ico file when icons are configured.");
        }

        var installerIconFile = settings.InstallerIconFile ?? fallbackIcon;
        var uninstallerIconFile = settings.UninstallerIconFile ?? installerIconFile;
        var lines = new List<string>();
        if (installerIconFile is not null)
        {
            var escaped = Escape(Path.GetFullPath(installerIconFile));
            lines.Add($"!define MUI_ICON \"{escaped}\"");
            lines.Add($"Icon \"{escaped}\"");
        }
        if (uninstallerIconFile is not null)
        {
            lines.Add($"!define MUI_UNICON \"{Escape(Path.GetFullPath(uninstallerIconFile))}\"");
        }
        if (settings.SidebarFile is not null)
        {
            var path = Escape(Path.GetFullPath(settings.SidebarFile));
            lines.Add($"!define MUI_WELCOMEFINISHPAGE_BITMAP \"{path}\"");
            lines.Add($"!define MUI_UNWELCOMEFINISHPAGE_BITMAP \"{path}\"");
        }
        if (settings.HeaderFile is not null || settings.UninstallerHeaderFile is not null)
        {
            lines.Add("!define MUI_HEADERIMAGE");
        }
        if (settings.HeaderFile is not null)
        {
            lines.Add($"!define MUI_HEADERIMAGE_BITMAP \"{Escape(Path.GetFullPath(settings.HeaderFile))}\"");
        }
        var uninstallerHeader = settings.UninstallerHeaderFile ?? settings.HeaderFile;
        if (uninstallerHeader is not null)
        {
            lines.Add($"!define MUI_HEADERIMAGE_UNBITMAP \"{Escape(Path.GetFullPath(uninstallerHeader))}\"");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string CreateLicensePage(string? licenseFile) =>
        string.IsNullOrWhiteSpace(licenseFile)
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                "!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfPassive",
                $"!insertmacro MUI_PAGE_LICENSE \"{Escape(Path.GetFullPath(licenseFile!))}\"");

    private static string CreateHomepageRegistry(string? homepage) =>
        string.IsNullOrWhiteSpace(homepage)
            ? string.Empty
            : $"  WriteRegStr SHCTX \"${{UNINSTALL_KEY}}\" \"URLInfoAbout\" \"{Escape(homepage!)}\"";

    private static string CreateAssociationInstallCommands(BundleConfiguration configuration)
    {
        if (configuration.FileAssociations.Count == 0 && configuration.UrlProtocols.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "  ; 注册到 Windows 默认应用列表，但不强制替换用户已经选择的默认程序。",
            "  WriteRegStr SHCTX \"${CAPABILITIES_KEY}\" \"ApplicationName\" \"${PRODUCT_NAME}\"",
            "  WriteRegStr SHCTX \"${CAPABILITIES_KEY}\" \"ApplicationDescription\" \"${PRODUCT_DESCRIPTION}\"",
            "  WriteRegStr SHCTX \"${CAPABILITIES_KEY}\" \"ApplicationCompany\" \"${PRODUCT_PUBLISHER}\"",
            "  WriteRegStr SHCTX \"${CAPABILITIES_KEY}\" \"ApplicationIcon\" '\"$INSTDIR\\${MAIN_EXECUTABLE}\",0'",
            "  WriteRegStr SHCTX \"Software\\RegisteredApplications\" \"${PRODUCT_ID}\" \"${CAPABILITIES_KEY}\""
        };

        foreach (var association in configuration.FileAssociations)
        {
            foreach (var configuredExtension in association.Extensions)
            {
                var extension = NormalizeExtension(configuredExtension);
                var progId = $"${{PRODUCT_ID}}.File.{extension}.1";
                var description = Escape(association.Description ?? association.Name ?? $"{configuration.ProductName} .{extension} file");
                lines.Add($"  ; 注册 .{extension} 的应用专属文件类型和“打开方式”候选项。");
                lines.Add($"  WriteRegStr SHCTX \"Software\\Classes\\{progId}\" \"\" \"{description}\"");
                lines.Add($"  WriteRegStr SHCTX \"Software\\Classes\\{progId}\\DefaultIcon\" \"\" '\"$INSTDIR\\${{MAIN_EXECUTABLE}}\",0'");
                lines.Add($"  WriteRegStr SHCTX \"Software\\Classes\\{progId}\\shell\\open\\command\" \"\" '\"$INSTDIR\\${{MAIN_EXECUTABLE}}\" \"%1\"'");
                lines.Add($"  WriteRegStr SHCTX \"Software\\Classes\\.{extension}\\OpenWithProgids\" \"{progId}\" \"\"");
                lines.Add($"  WriteRegStr SHCTX \"${{CAPABILITIES_KEY}}\\FileAssociations\" \".{extension}\" \"{progId}\"");
            }
        }

        foreach (var protocol in configuration.UrlProtocols)
        {
            foreach (var configuredScheme in protocol.Schemes)
            {
                var scheme = configuredScheme.Trim().ToLowerInvariant();
                var progId = $"${{PRODUCT_ID}}.Url.{scheme}.1";
                var description = Escape(protocol.Name ?? $"{configuration.ProductName} {scheme} protocol");
                lines.Add($"  ; 注册 {scheme}: 深链接，并保留应用专属 ProgID 供默认应用界面使用。");
                AddUrlProtocolRegistration(lines, $"Software\\Classes\\{progId}", description);
                AddUrlProtocolRegistration(lines, $"Software\\Classes\\{scheme}", description);
                lines.Add($"  WriteRegStr SHCTX \"${{CAPABILITIES_KEY}}\\UrlAssociations\" \"{scheme}\" \"{progId}\"");
            }
        }

        lines.Add("  ; 通知 Shell 重新读取文件类型和协议关联缓存。");
        lines.Add("  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0x1000, p 0, p 0)' ");
        return string.Join(Environment.NewLine, lines);
    }

    private static string CreateAssociationUninstallCommands(
        BundleConfiguration configuration,
        string mainExecutable)
    {
        if (configuration.FileAssociations.Count == 0 && configuration.UrlProtocols.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "  ; 只移除本应用注册的候选项，不清除用户为扩展名选择的其他默认程序。"
        };
        foreach (var association in configuration.FileAssociations)
        {
            foreach (var configuredExtension in association.Extensions)
            {
                var extension = NormalizeExtension(configuredExtension);
                var progId = $"${{PRODUCT_ID}}.File.{extension}.1";
                lines.Add($"  DeleteRegValue SHCTX \"Software\\Classes\\.{extension}\\OpenWithProgids\" \"{progId}\"");
                lines.Add($"  DeleteRegKey /ifempty SHCTX \"Software\\Classes\\.{extension}\\OpenWithProgids\"");
                lines.Add($"  DeleteRegKey /ifempty SHCTX \"Software\\Classes\\.{extension}\"");
                lines.Add($"  DeleteRegKey SHCTX \"Software\\Classes\\{progId}\"");
            }
        }

        foreach (var protocol in configuration.UrlProtocols)
        {
            foreach (var configuredScheme in protocol.Schemes)
            {
                var scheme = configuredScheme.Trim().ToLowerInvariant();
                var progId = $"${{PRODUCT_ID}}.Url.{scheme}.1";
                lines.Add($"  ; 只有协议仍指向本次安装的程序时才删除，避免破坏后来接管协议的应用。");
                lines.Add($"  ReadRegStr $0 SHCTX \"Software\\Classes\\{scheme}\\shell\\open\\command\" \"\"");
                lines.Add($"  ${{If}} $0 == '\"$INSTDIR\\{Escape(mainExecutable)}\" \"%1\"'");
                lines.Add($"    DeleteRegKey SHCTX \"Software\\Classes\\{scheme}\"");
                lines.Add("  ${EndIf}");
                lines.Add($"  DeleteRegKey SHCTX \"Software\\Classes\\{progId}\"");
            }
        }

        lines.Add("  DeleteRegValue SHCTX \"Software\\RegisteredApplications\" \"${PRODUCT_ID}\"");
        lines.Add("  DeleteRegKey SHCTX \"${CAPABILITIES_KEY}\"");
        lines.Add("  DeleteRegKey /ifempty SHCTX \"Software\\${PRODUCT_ID}\"");
        lines.Add("  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0x1000, p 0, p 0)' ");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddUrlProtocolRegistration(List<string> lines, string key, string description)
    {
        lines.Add($"  WriteRegStr SHCTX \"{key}\" \"\" \"URL:{description}\"");
        lines.Add($"  WriteRegStr SHCTX \"{key}\" \"URL Protocol\" \"\" ");
        lines.Add($"  WriteRegStr SHCTX \"{key}\\DefaultIcon\" \"\" '\"$INSTDIR\\${{MAIN_EXECUTABLE}}\",0'");
        lines.Add($"  WriteRegStr SHCTX \"{key}\\shell\\open\\command\" \"\" '\"$INSTDIR\\${{MAIN_EXECUTABLE}}\" \"%1\"'");
    }

    private static string NormalizeExtension(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant();

    private static string CreateInstallerHooksInclude(string? hooksFile) =>
        string.IsNullOrWhiteSpace(hooksFile)
            ? string.Empty
            : $"!include \"{Escape(Path.GetFullPath(hooksFile!))}\"";

    private static TransactionRendering CreateTransactionRendering(
        BundleConfiguration configuration,
        ShortcutRendering shortcuts)
    {
        var snapshots = new List<string>();
        var validations = new List<string>();
        var restores = new List<string>();
        var index = 0;
        var registryCount = 0;
        var fileCount = 0;

        void BackupKey(string subKey)
        {
            var name = $"key-{index++:D3}";
            registryCount++;
            snapshots.Add($"  DotNetBundlerNsis::BackupTransactionRegistryKey \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\"");
            snapshots.Add("  !insertmacro CheckTransactionResult");
            validations.Add($"  DotNetBundlerNsis::ValidateTransactionRegistryKeySnapshot \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\"");
            validations.Add("  !insertmacro CheckRecoveryResult");
            restores.Add($"  DotNetBundlerNsis::RestoreTransactionRegistryKey \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\"");
            restores.Add("  !insertmacro CheckRecoveryResult");
        }

        void BackupValue(string subKey, string valueName)
        {
            var name = $"value-{index++:D3}";
            registryCount++;
            snapshots.Add($"  DotNetBundlerNsis::BackupTransactionRegistryValue \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\" \"{valueName}\"");
            snapshots.Add("  !insertmacro CheckTransactionResult");
            validations.Add($"  DotNetBundlerNsis::ValidateTransactionRegistryValueSnapshot \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\" \"{valueName}\"");
            validations.Add("  !insertmacro CheckRecoveryResult");
            restores.Add($"  DotNetBundlerNsis::RestoreTransactionRegistryValue \"$TransactionDirectory\" \"{name}\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"{subKey}\" \"{valueName}\"");
            restores.Add("  !insertmacro CheckRecoveryResult");
        }

        void BackupFile(string path)
        {
            var name = $"file-{index++:D3}";
            fileCount++;
            snapshots.Add($"  DotNetBundlerNsis::BackupTransactionFile \"$TransactionDirectory\" \"{name}\" \"{path}\"");
            snapshots.Add("  !insertmacro CheckTransactionResult");
            validations.Add($"  DotNetBundlerNsis::ValidateTransactionFileSnapshot \"$TransactionDirectory\" \"{name}\" \"{path}\"");
            validations.Add("  !insertmacro CheckRecoveryResult");
            restores.Add($"  DotNetBundlerNsis::RestoreTransactionFile \"$TransactionDirectory\" \"{name}\" \"{path}\"");
            restores.Add("  !insertmacro CheckRecoveryResult");
        }

        BackupKey("${UNINSTALL_KEY}");
        BackupKey("Software\\${PRODUCT_ID}");
        BackupValue("Software\\RegisteredApplications", "${PRODUCT_ID}");

        foreach (var association in configuration.FileAssociations)
        {
            foreach (var configuredExtension in association.Extensions)
            {
                var extension = NormalizeExtension(configuredExtension);
                var progId = $"${{PRODUCT_ID}}.File.{extension}.1";
                BackupKey($"Software\\Classes\\{progId}");
                BackupValue($"Software\\Classes\\.{extension}\\OpenWithProgids", progId);
            }
        }

        foreach (var protocol in configuration.UrlProtocols)
        {
            foreach (var configuredScheme in protocol.Schemes)
            {
                var scheme = configuredScheme.Trim().ToLowerInvariant();
                BackupKey($"Software\\Classes\\${{PRODUCT_ID}}.Url.{scheme}.1");
                BackupKey($"Software\\Classes\\{scheme}");
            }
        }

        BackupFile(shortcuts.DesktopPath);
        BackupFile(shortcuts.StartMenuPath);
        foreach (var legacyPath in shortcuts.LegacyPaths)
        {
            BackupFile(legacyPath);
        }

        return new TransactionRendering(
            string.Join(Environment.NewLine, snapshots),
            string.Join(Environment.NewLine, validations),
            string.Join(Environment.NewLine, restores),
            registryCount,
            fileCount);
    }

    private static string InstallModeName(NsisInstallScope mode) => mode switch
    {
        NsisInstallScope.CurrentUser => "currentUser",
        NsisInstallScope.PerMachine => "perMachine",
        NsisInstallScope.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown NSIS install mode.")
    };

    private static string CompressionDirective(NsisCompression compression) => compression switch
    {
        NsisCompression.Lzma => "SetCompressor /SOLID lzma",
        NsisCompression.Zlib => "SetCompressor /SOLID zlib",
        NsisCompression.Bzip2 => "SetCompressor /SOLID bzip2",
        NsisCompression.None => "SetCompress off",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown NSIS compression mode.")
    };

    private static string CreateMsiCodeList(IReadOnlyList<string> codes) => string.Join(
        ";",
        codes.Select(code => Guid.Parse(code).ToString("B").ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static ShortcutRendering PrepareShortcuts(
        BundleConfiguration configuration,
        NsisShortcutConfiguration shortcuts,
        BundlePlanItem item,
        IReadOnlyList<PayloadResource> resources)
    {
        var mainExecutable = NormalizeTargetPath(item.MainExecutable);
        EnsurePayloadFile(mainExecutable, item.InputDirectory, resources, nameof(item.MainExecutable));

        var workingDirectory = string.IsNullOrWhiteSpace(shortcuts.WorkingDirectory) || shortcuts.WorkingDirectory == "."
            ? "$INSTDIR"
            : "$INSTDIR\\" + Escape(NormalizeTargetPath(shortcuts.WorkingDirectory!));
        if (!string.IsNullOrWhiteSpace(shortcuts.WorkingDirectory) && shortcuts.WorkingDirectory != ".")
        {
            EnsurePayloadDirectory(NormalizeTargetPath(shortcuts.WorkingDirectory!), item.InputDirectory, resources);
        }

        var icon = string.IsNullOrWhiteSpace(shortcuts.Icon)
            ? "$INSTDIR\\" + Escape(mainExecutable)
            : "$INSTDIR\\" + Escape(NormalizeTargetPath(shortcuts.Icon!));
        if (!string.IsNullOrWhiteSpace(shortcuts.Icon))
        {
            EnsurePayloadFile(NormalizeTargetPath(shortcuts.Icon!), item.InputDirectory, resources, nameof(shortcuts.Icon));
        }

        var appUserModelId = shortcuts.AppUserModelId ?? configuration.Identifier;
        if (appUserModelId.Length > 128 || appUserModelId.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("The shortcut AppUserModelId must contain no whitespace and be at most 128 characters.");
        }

        var startMenuFolder = shortcuts.StartMenuFolder;
        if (startMenuFolder is null)
        {
            startMenuFolder = configuration.ProductName;
        }
        var startMenuDirectory = string.IsNullOrWhiteSpace(startMenuFolder) || startMenuFolder == "."
            ? "$SMPROGRAMS"
            : "$SMPROGRAMS\\" + Escape(ToInstallerPath(startMenuFolder).Trim('\\'));
        var productFileName = SafeFileName(configuration.ProductName);
        var desktopPath = "$DESKTOP\\" + Escape(productFileName) + ".lnk";
        var startMenuPath = startMenuDirectory + "\\" + Escape(productFileName) + ".lnk";

        var legacyExecutables = shortcuts.LegacyMainExecutables
            .Select(NormalizeTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownedTargets = string.Join(
            "|",
            new[] { mainExecutable }.Concat(legacyExecutables)
                .Select(executable => "$INSTDIR\\" + Escape(executable)));

        var legacyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var legacyNameValue in shortcuts.LegacyProductNames)
        {
            var legacyName = Escape(SafeFileName(legacyNameValue));
            legacyPaths.Add("$DESKTOP\\" + legacyName + ".lnk");
            legacyPaths.Add("$SMPROGRAMS\\" + legacyName + ".lnk");
            legacyPaths.Add("$SMPROGRAMS\\" + legacyName + "\\" + legacyName + ".lnk");
            legacyPaths.Add(startMenuDirectory + "\\" + legacyName + ".lnk");
            legacyPaths.Add("$SMPROGRAMS\\" + legacyName + "\\" + Escape(productFileName) + ".lnk");
        }
        legacyPaths.Remove(desktopPath);
        legacyPaths.Remove(startMenuPath);

        var target = "$INSTDIR\\" + Escape(mainExecutable);
        string UpdateCall(string action, string source, string destination) =>
            $"  DotNetBundlerNsis::{action} \"{source}\" \"{destination}\" \"{ownedTargets}\" \"{target}\" \"{Escape(shortcuts.Arguments ?? string.Empty)}\" \"{workingDirectory}\" \"{icon}\" \"{Escape(appUserModelId)}\"" + Environment.NewLine +
            "  Pop $0" + Environment.NewLine +
            "  !insertmacro CheckShortcutResult";
        var migrations = new List<string>();
        foreach (var path in legacyPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var destination = path.StartsWith("$DESKTOP", StringComparison.OrdinalIgnoreCase)
                ? desktopPath
                : startMenuPath;
            migrations.Add(UpdateCall("MoveShortcutIfOwned", path, destination));
        }

        var cleanup = legacyPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
                $"  DotNetBundlerNsis::DeleteShortcutIfOwned \"{path}\" \"{ownedTargets}\"" + Environment.NewLine +
                "  Pop $0" + Environment.NewLine +
                "  !insertmacro CheckUninstallShortcutResult")
            .ToArray();

        return new ShortcutRendering(
            shortcuts.Desktop,
            shortcuts.StartMenu,
            shortcuts.Arguments ?? string.Empty,
            workingDirectory,
            icon,
            appUserModelId,
            startMenuDirectory,
            startMenuPath,
            desktopPath,
            ownedTargets,
            string.Join(Environment.NewLine, migrations),
            string.Join(Environment.NewLine, cleanup),
            legacyPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void EnsurePayloadFile(
        string relativePath,
        string inputDirectory,
        IReadOnlyList<PayloadResource> resources,
        string propertyName)
    {
        var hostPath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(inputDirectory, hostPath)) ||
            resources.Any(resource => resource.Destination.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        throw new InvalidOperationException($"Shortcut {propertyName} '{relativePath}' is not present in the final installed payload.");
    }

    private static void EnsurePayloadDirectory(
        string relativePath,
        string inputDirectory,
        IReadOnlyList<PayloadResource> resources)
    {
        var hostPath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
        if (Directory.Exists(Path.Combine(inputDirectory, hostPath)) ||
            resources.Any(resource => resource.Destination.StartsWith(relativePath + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        throw new InvalidOperationException($"Shortcut working directory '{relativePath}' is not present in the final installed payload.");
    }

    private static string TargetArchitectureName(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X86 => "x86",
        CpuArchitecture.X64 => "x64",
        CpuArchitecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unknown target architecture.")
    };

    private static IReadOnlyList<PayloadResource> ExpandResources(
        IReadOnlyList<BundleResourceConfiguration> configuredResources,
        string inputDirectory)
    {
        var inputRoot = Path.GetFullPath(inputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var inputTree = InspectDirectoryTree(inputRoot);
        var targets = new HashSet<string>(
            inputTree.Files
                .Select(path => ToInstallerPath(RelativePath(inputRoot, path))),
            StringComparer.OrdinalIgnoreCase);
        var resources = new List<PayloadResource>();

        foreach (var configured in configuredResources)
        {
            var source = Path.GetFullPath(configured.Source);
            var target = NormalizeTargetPath(configured.Destination);
            if (File.Exists(source))
            {
                RejectReparsePoint(source, "Bundle resource");
                Add(source, target);
                continue;
            }

            if (!Directory.Exists(source))
            {
                throw new FileNotFoundException("Bundle resource was not found.", source);
            }

            foreach (var file in InspectDirectoryTree(source).Files)
            {
                Add(file, CombineInstallerPath(target, RelativePath(source, file)));
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
        if (string.IsNullOrWhiteSpace(targetPath) || IsRootedInstallerPath(targetPath))
        {
            throw new InvalidOperationException($"Resource target path must be relative: '{targetPath}'.");
        }

        var normalized = ToInstallerPath(targetPath).Trim('\\');
        var components = normalized.Split('\\');
        if (components.Any(component => component is "" or "." or ".."))
        {
            throw new InvalidOperationException($"Resource target path must stay inside the installation directory: '{targetPath}'.");
        }
        if (components.Any(component => WindowsFileNames.IsInvalidName(component)))
        {
            throw new InvalidOperationException($"Resource target path contains a name that is not valid on Windows: '{targetPath}'.");
        }

        return normalized;
    }

    private static string CreateResourceInstallCommands(IReadOnlyList<PayloadResource> resources)
    {
        var lines = new List<string>();
        foreach (var resource in resources.OrderBy(resource => resource.Destination, StringComparer.OrdinalIgnoreCase))
        {
            var targetDirectory = InstallerDirectoryName(resource.Destination);
            var destination = string.IsNullOrEmpty(targetDirectory)
                ? "$INSTDIR"
                : "$INSTDIR\\" + Escape(targetDirectory!);
            lines.Add($"  SetOutPath \"{destination}\"");
            lines.Add($"  File \"/oname={Escape(InstallerFileName(resource.Destination))}\" \"{Escape(resource.Source)}\"");
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
            var configuredLanguage = settings.Languages[index];
            var language = NsisLanguageCatalog.Resolve(configuredLanguage);

            var customFile = settings.CustomLanguageFiles.FirstOrDefault(
                pair => pair.Key.Equals(configuredLanguage, StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Equals(language.Name, StringComparison.OrdinalIgnoreCase)).Value;
            var sourcePath = string.IsNullOrWhiteSpace(customFile)
                ? Path.Combine(Path.GetFullPath(languageDirectory), language.Name + ".nsh")
                : Path.GetFullPath(customFile);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"No built-in or custom message file was found for NSIS language '{language.Name}'.",
                    sourcePath);
            }
            ValidateLanguageFile(sourcePath, language);

            var destinationPath = Path.Combine(workDirectory, $"language-{index:D2}-{language.Name}.nsh");
            File.WriteAllText(
                destinationPath,
                File.ReadAllText(sourcePath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            languageMacros.Add($"!insertmacro MUI_LANGUAGE \"{language.NsisName}\"");
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

    private static void ValidateLanguageFile(string path, NsisLanguageDefinition language)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }
            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                "^LangString\\s+([A-Za-z][A-Za-z0-9_]*)\\s+\\$\\{LANG_([A-Za-z0-9_]+)\\}\\s+\".*\"\\s*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidDataException($"NSIS language file '{path}' contains an invalid line: {line}");
            }
            var key = match.Groups[1].Value;
            if (!RequiredLanguageKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"NSIS language file '{path}' contains unknown key '{key}'.");
            }
            if (!match.Groups[2].Value.Equals(language.ConstantName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"NSIS language file '{path}' uses LANG_{match.Groups[2].Value} for '{language.Name}'.");
            }
            if (!keys.Add(key))
            {
                throw new InvalidDataException($"NSIS language file '{path}' contains duplicate key '{key}'.");
            }
        }
        var missing = RequiredLanguageKeys.Where(key => !keys.Contains(key)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"NSIS language file '{path}' is missing required keys: {string.Join(", ", missing)}.");
        }
    }

    private static string CreateUninstallPayload(
        string inputDirectory,
        IReadOnlyList<PayloadResource> resources)
    {
        var root = Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tree = InspectDirectoryTree(root);
        var lines = new List<string>();

        foreach (var file in tree.Files)
        {
            lines.Add($"  Delete /REBOOTOK \"$INSTDIR\\{Escape(ToInstallerPath(RelativePath(root, file)))}\"");
        }

        foreach (var resource in resources)
        {
            lines.Add($"  Delete /REBOOTOK \"$INSTDIR\\{Escape(resource.Destination)}\"");
        }

        var directories = new HashSet<string>(
            tree.Directories
                .Select(path => ToInstallerPath(RelativePath(root, path))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            var directory = InstallerDirectoryName(resource.Destination);
            while (!string.IsNullOrEmpty(directory))
            {
                directories.Add(directory!);
                directory = InstallerDirectoryName(directory!);
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
        var prefix = DirectoryPrefix(root);
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Payload path is outside the input directory: {fullPath}");
        }

        return fullPath.Substring(prefix.Length).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string DirectoryPrefix(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > pathRoot.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return fullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string ToInstallerPath(string path) => path.Replace('/', '\\');

    private static string CombineInstallerPath(string left, string right) =>
        string.IsNullOrEmpty(left)
            ? ToInstallerPath(right).TrimStart('\\')
            : ToInstallerPath(left).TrimEnd('\\') + "\\" + ToInstallerPath(right).TrimStart('\\');

    private static bool IsRootedInstallerPath(string path) =>
        path.StartsWith("\\", StringComparison.Ordinal) ||
        path.StartsWith("/", StringComparison.Ordinal) ||
        (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private static string? InstallerDirectoryName(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? null : path.Substring(0, separator);
    }

    private static string InstallerFileName(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? path : path.Substring(separator + 1);
    }

    private static string Escape(string value) => value
        .Replace("$", "$$")
        .Replace("\"", "$\\\"")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static string SafeFileName(string value)
    {
        var result = WindowsFileNames.Sanitize(value).Trim();
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? "Application" : result;
    }

    private static string NumericVersion(string version)
    {
        if (!SemanticVersion.TryParse(version, out var semanticVersion) ||
            !semanticVersion!.TryGetWindowsNumericVersion(out var numericVersion))
        {
            throw new InvalidOperationException(
                $"NSIS version '{version}' must be SemVer 2.0 with numeric components between 0 and 65535.");
        }
        return numericVersion;
    }

    private static long EstimateSizeInKilobytes(string directory)
    {
        var bytes = InspectDirectoryTree(directory).Files
            .Sum(path => new FileInfo(path).Length);
        return Math.Max(1, (bytes + 1023) / 1024);
    }

    private static DirectoryTree InspectDirectoryTree(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Bundle input directory was not found: {fullRoot}");
        }
        RejectReparsePoint(fullRoot, "Bundle directory");
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                // 纯字符串名校验先于 GetAttributes——NUL/尾随点这类病态名上
                // GetAttributes 直接抛原生 Win32 错，干净拒绝必须先执行。
                if (WindowsFileNames.IsInvalidName(Path.GetFileName(path)))
                {
                    throw new InvalidDataException($"Bundle input contains a name that is not valid on Windows: {path}");
                }
                RejectReparsePoint(path, "Bundle input");
                if (Directory.Exists(path))
                {
                    directories.Add(path);
                    pending.Push(path);
                }
                else
                {
                    files.Add(path);
                }
            }
        }
        return new DirectoryTree(fullRoot, directories, files);
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} must not contain a symbolic link, junction, or other reparse point: {path}");
        }
    }

    private sealed record NsisLocalization(
        string LanguageMacros,
        string LanguageFiles,
        string DisplayLanguageSelector);

    private sealed record TransactionRendering(
        string SnapshotCommands,
        string ValidationCommands,
        string RestoreCommands,
        int RegistryCount,
        int FileCount);

    private sealed record ShortcutRendering(
        bool DesktopDefault,
        bool StartMenuDefault,
        string Arguments,
        string WorkingDirectory,
        string Icon,
        string AppUserModelId,
        string StartMenuDirectory,
        string StartMenuPath,
        string DesktopPath,
        string OwnedTargets,
        string MigrationCommands,
        string LegacyCleanupCommands,
        IReadOnlyList<string> LegacyPaths);

    private sealed record PayloadResource(string Source, string Destination);

    private sealed record DirectoryTree(
        string Root,
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> Files);

    private sealed record UninstallerMode(
        string FinalizeCommand,
        string ImportDefine,
        string SignedUninstallerPath)
    {
        public static UninstallerMode Unsigned { get; } = new(string.Empty, string.Empty, string.Empty);
    }
}

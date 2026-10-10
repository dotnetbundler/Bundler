using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotNet.Bundler;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Wix;

internal sealed class WixBundleBackend(WixToolset toolset, WixBundleConfiguration settings, IBundleSigner? signer) : IBundleBackend
{
    private const string GeneratorRevision = "win-msi-8-2026-10-02-1";

    public PackageFormat Format => PackageFormat.Msi;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken = default)
    {
        var bundle = context.Configuration;
        var item = context.Item;
        var languages = settings.ResolveLanguages();
        // A signed build must fail on pre-existing outputs before invoking the
        // signer, so preflight every language's output path first.
        if (signer is not null)
        {
            foreach (var language in languages)
            {
                var existing = Path.Combine(item.OutputDirectory,
                    WixProductDocument.SafeFileName(bundle.ProductName) + "-" +
                    WixIdentity.Create(bundle.Identifier, bundle.Version, item.Target.RuntimeIdentifier,
                        settings.InstallScope, settings.UpgradeCode, language, settings.Version)
                        .ProductVersion + language.Suffix + ".msi");
                if (File.Exists(existing) || File.Exists(existing + ".bundler-manifest"))
                    throw new IOException(
                        "An MSI with the same product version already exists with signed or unverified contents: " + existing);
            }
        }
        // Validate every source before copying payload into a private staging tree.
        CollectFiles(bundle, item);
        item = UpdatePayloadStaging.EnsureStaged(context, item);
        var extensionInputs = await PrepareExtensionInputs(context, cancellationToken);
        if (signer is not null)
            item = await PrepareSignedPayloadAsync(context, item, signer, cancellationToken);
        var files = CollectFiles(bundle, item);
        var icon = SelectIcon(bundle.Icons);
        var artifacts = new List<BundleArtifact>();
        foreach (var language in languages)
        {
            artifacts.Add(await BuildLanguageAsync(context, bundle, item, files, icon, language,
                extensionInputs, cancellationToken));
        }
        return artifacts;
    }

    private sealed record ExtensionInputs(
        IReadOnlyList<string> FragmentWixObjects,
        IReadOnlyList<string> FragmentBindPaths,
        IReadOnlyList<string> ExtensionDlls,
        string? ExpertTemplatePath,
        IReadOnlyList<string> MergeModules);

    // Fragments are language-neutral: compile once, then each language's light
    // run links them with that language's merged .wxl. Expert templates carry
    // the whole product document so they compile per language instead.
    private async Task<ExtensionInputs> PrepareExtensionInputs(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var toolsetDirectory = Path.GetDirectoryName(toolset.CandlePath)!;
        var extensionDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fragmentObjects = new List<string>();
        if (settings.IsExpertMode)
        {
            var template = settings.ExpertTemplateFile!;
            if (!File.Exists(template) ||
                (File.GetAttributes(template) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "MSI expert template must be a real .wxs file: " + template);
            }
            foreach (var dll in WixExtensionValidator.DetectExtensionDlls(
                         File.ReadAllText(template), toolsetDirectory))
            {
                extensionDlls.Add(dll);
            }
            foreach (var module in settings.ExpertMergeModuleFiles)
            {
                if (!File.Exists(module) ||
                    (File.GetAttributes(module) & FileAttributes.ReparsePoint) != 0 ||
                    !Path.GetExtension(module).Equals(".msm", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "MSI expert merge module must be a real .msm file: " + module);
                }
            }
            return new ExtensionInputs(fragmentObjects,
                [Path.GetDirectoryName(Path.GetFullPath(template))!], [.. extensionDlls],
                Path.GetFullPath(template), settings.ExpertMergeModuleFiles.Select(Path.GetFullPath).ToArray());
        }
        var prefix = settings.ExtensionIdPrefix ?? "";
        for (var index = 0; index < settings.ExtensionFragmentFiles.Count; index++)
        {
            var fragment = settings.ExtensionFragmentFiles[index];
            var content = WixExtensionValidator.ValidateFragment(fragment, prefix);
            foreach (var dll in WixExtensionValidator.DetectExtensionDlls(content, toolsetDirectory))
            {
                extensionDlls.Add(dll);
            }
            var obj = Path.Combine(context.WorkDirectory, "fragment-" + index + ".wixobj");
            await WixProcessRunner.RunAsync(toolset.CandlePath,
                ["-nologo", "-out", obj, fragment], context.WorkDirectory, cancellationToken);
            fragmentObjects.Add(obj);
        }
        return new ExtensionInputs(fragmentObjects,
            settings.ExtensionFragmentFiles
                .Select(fragment => Path.GetDirectoryName(Path.GetFullPath(fragment))!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            [.. extensionDlls], null, []);
    }

    private async Task<BundleArtifact> BuildLanguageAsync(
        BundleBuildContext context, BundleConfiguration bundle, BundlePlanItem item,
        IReadOnlyList<InstallFile> files, string? icon, WixLanguageInfo requested,
        ExtensionInputs extensionInputs, CancellationToken cancellationToken)
    {
        var language = requested with { Codepage = settings.EffectiveCodepage(requested) };
        var identity = WixIdentity.Create(bundle.Identifier, bundle.Version, item.Target.RuntimeIdentifier,
            settings.InstallScope, settings.UpgradeCode, language, settings.Version);
        var outputName = WixProductDocument.SafeFileName(bundle.ProductName) + "-" + identity.ProductVersion +
            language.Suffix + ".msi";
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
        var definitionHash = DefinitionHash(bundle, item, files, icon, language);
        var product = new WixProductDocument(settings, language)
            .Create(bundle, item, identity, files, icon, definitionHash);
        var fingerprint = Fingerprint(bundle, item, identity, files, icon, product, language);
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

        var languageTag = language.Culture.ToLowerInvariant();
        var source = Path.Combine(context.WorkDirectory, "product-" + languageTag + ".wxs");
        var obj = Path.Combine(context.WorkDirectory, "product-" + languageTag + ".wixobj");
        if (extensionInputs.ExpertTemplatePath is null)
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), product)
                .Save(source);
        }
        else
        {
            File.Copy(extensionInputs.ExpertTemplatePath, source, overwrite: true);
        }
        var scopeRoot = settings.InstallScope == WixInstallScope.CurrentUser ? "LocalAppDataFolder" :
            item.Target.Architecture == CpuArchitecture.X86 ? "ProgramFilesFolder" : "ProgramFiles64Folder";
        var callerStrings = FindLocaleFile(language) is { } localeFile
            ? WixLocale.ReadCallerStrings(localeFile, language, language.Codepage)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localePath = WixLocale.WriteMerged(
            Path.Combine(context.WorkDirectory, "locale-" + languageTag + ".wxl"),
            language, language.Codepage, scopeRoot, callerStrings);

        try
        {
            var candleArguments = new List<string> { "-nologo" };
            if (settings.FipsCompliant) candleArguments.Add("-fips");
            candleArguments.AddRange(
            [
                "-arch", item.Target.Architecture switch
                 {
                     CpuArchitecture.X86 => "x86",
                     CpuArchitecture.X64 => "x64",
                     CpuArchitecture.Arm64 => "arm64",
                     _ => throw new NotSupportedException("Unknown MSI target architecture.")
                 }]);
            foreach (var dll in extensionInputs.ExtensionDlls)
                candleArguments.AddRange(["-ext", dll]);
            if (extensionInputs.ExpertTemplatePath is not null)
            {
                // Identity stays Bundler-derived; the expert template consumes
                // these as $(var.Bundler.*) preprocessor values.
                candleArguments.AddRange(
                [
                    "-dBundler.ProductCode=" + identity.ProductCode.ToString("B").ToUpperInvariant(),
                    "-dBundler.UpgradeCode=" + identity.UpgradeCode.ToString("B").ToUpperInvariant(),
                    "-dBundler.ProductVersion=" + identity.ProductVersion,
                    "-dBundler.ProductName=" + bundle.ProductName,
                    "-dBundler.ProductLanguage=" +
                        language.Lcid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-dBundler.Codepage=" + language.Codepage,
                    "-dBundler.InstallScope=" +
                        (settings.InstallScope == WixInstallScope.CurrentUser ? "perUser" : "perMachine"),
                    "-dBundler.Manufacturer=" + (bundle.Publisher ?? bundle.ProductName)
                ]);
            }
            candleArguments.AddRange(["-out", obj, source]);
            await WixProcessRunner.RunAsync(toolset.CandlePath, candleArguments,
                context.WorkDirectory, cancellationToken);
            var lightArguments = new List<string> { "-nologo" };
            if (!string.IsNullOrWhiteSpace(bundle.LicenseFile) || settings.InstallDirectorySelection ||
                settings.LaunchAfterInstall || settings.BannerFile is not null || settings.DialogFile is not null)
            {
                // Non-English MSIs fall back to en-US for strings the language
                // resources do not override, mirroring the reference bundlers.
                lightArguments.AddRange([
                    "-ext", toolset.UiExtensionPath,
                    "-cultures:" + language.Culture +
                        (language.Culture.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "" : ";en-US")]);
            }
            foreach (var dll in extensionInputs.ExtensionDlls)
                lightArguments.AddRange(["-ext", dll]);
            foreach (var bindPath in extensionInputs.FragmentBindPaths)
                lightArguments.AddRange(["-b", bindPath]);
            lightArguments.AddRange(["-loc", localePath]);
            if (settings.InstallScope == WixInstallScope.CurrentUser) lightArguments.Add("-sice:ICE91");
            // ICE61 拒绝移除较新产品；显式允许降级时，这正是调用方选择的行为。
            if (settings.AllowDowngrades) lightArguments.Add("-sice:ICE61");
            lightArguments.Add("-out");
            lightArguments.Add(outputPath);
            lightArguments.Add(obj);
            lightArguments.AddRange(extensionInputs.FragmentWixObjects);
            lightArguments.AddRange(extensionInputs.MergeModules);
            await WixProcessRunner.RunAsync(toolset.LightPath, lightArguments, context.WorkDirectory, cancellationToken);
            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("WiX reported success without producing an MSI.");
            }
            if (extensionInputs.ExpertTemplatePath is not null)
            {
                MsiIdentityProbe.VerifyExpertMsi(outputPath, identity, language, settings.InstallScope);
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
        finally
        {
            // 锁文件随构建清理；Dispose 后再删才能在 Windows 下生效。
            outputLock.Dispose();
            try { File.Delete(lockPath); } catch (IOException) { }
        }
    }

    private string? FindLocaleFile(WixLanguageInfo language) =>
        settings.LocaleFiles.FirstOrDefault(pair =>
            pair.Key.Equals(language.Culture, StringComparison.OrdinalIgnoreCase)).Value;

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
            var target = WixPackagePaths.NormalizeTarget(resource.Destination);
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
            throw new NotSupportedException("MSI icons accept .ico files only.");
        }
        foreach (var icon in icons) CheckReparse(icon);
        var path = Path.GetFullPath(icons[0]);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The MSI icon file does not exist.", path);
        }
        var header = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
                throw new InvalidDataException("The MSI icon file is not a valid .ico: " + path);
        }
        if (header[0] != 0 || header[1] != 0 || header[2] != 1 || header[3] != 0)
        {
            throw new InvalidDataException("The MSI icon file is not a valid .ico: " + path);
        }
        return path;
    }

    private string Fingerprint(BundleConfiguration bundle, BundlePlanItem item,
        WixIdentity identity, IReadOnlyList<InstallFile> files, string? icon, XElement product,
        WixLanguageInfo language)
    {
        var canonical = new XElement(product);
        var text = new StringBuilder()
            .AppendLine(GeneratorRevision)
            .AppendLine(WixToolsetResolver.ArchiveSha256)
            .AppendLine(bundle.ProductName).AppendLine(bundle.Identifier.ToLowerInvariant())
            .AppendLine(identity.ProductVersion).AppendLine(bundle.Publisher)
            .AppendLine(bundle.Description).AppendLine(bundle.Homepage)
            .Append(language.Codepage).AppendLine().AppendLine(language.Culture)
            .AppendLine(item.Target.RuntimeIdentifier).AppendLine(identity.UpgradeCode.ToString("D"));
        if (FindLocaleFile(language) is { } localeFilePath) text.AppendLine(HashFile(localeFilePath));
        foreach (var file in files)
        {
            text.AppendLine(file.RelativePath.ToLowerInvariant()).AppendLine(HashFile(file.SourcePath));
        }
        if (icon is not null) text.AppendLine(HashFile(icon));
        if (bundle.LicenseFile is not null) text.AppendLine(HashFile(bundle.LicenseFile));
        if (settings.BannerFile is not null) text.AppendLine(HashFile(settings.BannerFile));
        if (settings.DialogFile is not null) text.AppendLine(HashFile(settings.DialogFile));
        AppendExtensionFingerprint(text);
        text.AppendLine(canonical.ToString(SaveOptions.DisableFormatting));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private void AppendExtensionFingerprint(StringBuilder text)
    {
        if (settings.ExtensionIdPrefix is not null) text.AppendLine(settings.ExtensionIdPrefix);
        foreach (var fragment in settings.ExtensionFragmentFiles)
        {
            text.AppendLine(HashFile(fragment));
            AppendReferencedSourceHashes(text, fragment);
        }
        foreach (var id in settings.ExtensionComponentRefs
                     .Concat(settings.ExtensionComponentGroupRefs)
                     .Concat(settings.ExtensionFeatureRefs)) text.AppendLine(id);
        if (settings.ExpertTemplateFile is not null)
        {
            text.AppendLine(HashFile(settings.ExpertTemplateFile));
            AppendReferencedSourceHashes(text, settings.ExpertTemplateFile);
        }
        foreach (var module in settings.ExpertMergeModuleFiles) text.AppendLine(HashFile(module));
    }

    private static readonly Regex SourceReferencePattern =
        new("\\bSource(?:File)?\\s*=\\s*[\"'](?<source>[^\"']+)[\"']", RegexOptions.Compiled);

    // 片段/专家模板经 Source/SourceFile 引用的文件不在载荷清单内，逐一把磁盘上存在的
    // 被引用文件哈希进指纹；相对路径以该 .wxs 所在目录解析，预处理器变量引用自然跳过。
    private static void AppendReferencedSourceHashes(StringBuilder text, string wixSource)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(wixSource))!;
        foreach (Match match in SourceReferencePattern.Matches(File.ReadAllText(wixSource)))
        {
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, match.Groups["source"].Value));
            if (File.Exists(fullPath)) text.AppendLine(fullPath).AppendLine(HashFile(fullPath));
        }
    }

    private string DefinitionHash(BundleConfiguration bundle, BundlePlanItem item,
        IReadOnlyList<InstallFile> files, string? icon, WixLanguageInfo language)
    {
        var text = new StringBuilder().AppendLine(GeneratorRevision).AppendLine(bundle.ProductName)
            .AppendLine(bundle.Identifier.ToLowerInvariant()).AppendLine(bundle.Version)
            .AppendLine(bundle.Publisher).AppendLine(bundle.Description).AppendLine(bundle.Homepage)
            .AppendLine(item.Target.RuntimeIdentifier).AppendLine(settings.InstallScope.ToString())
            .AppendLine(settings.StartMenuShortcut.ToString()).AppendLine(settings.DesktopShortcut.ToString())
            .AppendLine(settings.InstallDirectorySelection.ToString()).AppendLine(settings.AddToPath.ToString())
            .AppendLine(settings.UninstallShortcut.ToString()).AppendLine(settings.LaunchAfterInstall.ToString())
            .AppendLine(language.Codepage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(language.Culture);
        if (FindLocaleFile(language) is { } localeFilePath) text.AppendLine(HashFile(localeFilePath));
        if (settings.AllowDowngrades) text.AppendLine("allow-downgrades=true");
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
        if (settings.BannerFile is not null) text.AppendLine(HashFile(settings.BannerFile));
        if (settings.DialogFile is not null) text.AppendLine(HashFile(settings.DialogFile));
        AppendExtensionFingerprint(text);
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

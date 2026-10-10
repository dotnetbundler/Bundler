using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// codesign (inside-out) + explicit notarization for the produced .app.
/// Everything is opt-in: an empty <see cref="MacAppSigningConfiguration"/> leaves the bundle
/// unsigned. Signing requires a macOS host; notarization requires a real identity (not "-").
/// </summary>
internal static class MacAppSigning
{
    /// <summary>Bundle directories whose contents codesign treats as nested code.</summary>
    private static readonly string[] CodeDirectoryNames =
        ["MacOS", "Frameworks", "PlugIns", "Helpers", "XPCServices", "Libraries", "SharedFrameworks"];

    /// <summary>Apple 嵌套 bundle 形态：按 bundle 维度签名而不是按散文件签。</summary>
    private static readonly string[] NestedBundleExtensions =
        [".app", ".appex", ".bundle", ".framework", ".xpc"];

    internal static bool Configured(MacAppSigningConfiguration signing) =>
        signing.Identity is not null || !string.IsNullOrEmpty(signing.TemporaryCertificateFile);

    /// <summary>Preflight checks; the bundler calls this before any payload work.</summary>
    internal static void Validate(MacAppSigningConfiguration signing)
    {
        var signingRequested = Configured(signing);
        if (signing.Identity is not null && !string.IsNullOrEmpty(signing.TemporaryCertificateFile))
        {
            throw new ArgumentException(
                "SignIdentity and TemporaryCertificateFile are mutually exclusive.");
        }
        if (signing.Identity is { Length: 0 })
        {
            throw new ArgumentException("SignIdentity cannot be empty (use \"-\" for ad-hoc).");
        }
        if (signing.TemporaryCertificateFile is { Length: > 0 } certificatePath &&
            !File.Exists(Path.GetFullPath(certificatePath)))
        {
            throw new FileNotFoundException("TemporaryCertificateFile does not exist.", certificatePath);
        }
        if (signing.EntitlementsFile is { Length: > 0 } entitlements &&
            !File.Exists(Path.GetFullPath(entitlements)))
        {
            throw new FileNotFoundException("EntitlementsFile does not exist.", entitlements);
        }
        if (signing.Notarize)
        {
            var adHoc = signing.Identity == "-";
            if (!signingRequested || adHoc)
            {
                throw new ArgumentException(
                    "Notarization requires a real signing identity; ad-hoc signatures cannot be notarized.");
            }
            ResolveCredentials(signing); // throws when no credential combination is complete
        }
        else if (signing.SkipStapling || !signing.NotaryWait)
        {
            throw new ArgumentException("SkipStapling/NotaryWait require Notarize to be enabled.");
        }
        if (signingRequested && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException(".app signing requires a macOS host (codesign).");
        }
    }

    /// <summary>Sign the staged .app inside-out, verify, then notarize when enabled.</summary>
    internal static async Task RunAsync(
        BundleBuildContext context, string appPath, string mainExecutable,
        MacAppSigningConfiguration signing, CancellationToken cancellationToken)
    {
        if (!Configured(signing))
        {
            return;
        }
        var logger = context.Logger;
        TemporaryKeychain? keychain = null;
        string identity;
        try
        {
            if (signing.TemporaryCertificateFile is { Length: > 0 } certificatePath)
            {
                keychain = await TemporaryKeychain.CreateAsync(
                    context.WorkDirectory, Path.GetFullPath(certificatePath),
                    signing.TemporaryCertificatePassword ?? "", logger, cancellationToken);
                identity = keychain.Identity;
            }
            else
            {
                identity = signing.Identity!;
            }

            logger.Log(BundleLogLevel.Information,
                identity == "-"
                    ? "Signing the .app ad-hoc (codesign -s -)."
                    : $"Signing the .app with identity '{identity}'.");
            await MacProcessRunner.RunAsync(
                "xattr", ["-crs", appPath], context.WorkDirectory, cancellationToken);

            var contentsDirectory = Path.Combine(appPath, "Contents");
            var mainExecutablePath = Path.Combine(contentsDirectory, "MacOS", mainExecutable);
            // Inside-out order: every regular file under the code-bearing directories must be
            // signed before the main executable (which seals the whole bundle). codesign treats
            // anything under MacOS/Frameworks/... as nested code — including managed .dlls, which
            // are not Mach-O but still get rejected as unsigned subcomponents.
            var nestedBundles = FindNestedBundles(contentsDirectory);
            var nestedFiles = Directory
                .EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories)
                .Where(file => CodeDirectoryNames.Any(dir =>
                    file.StartsWith(
                        Path.Combine(contentsDirectory, dir) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
                // 嵌套 bundle 内的文件归该 bundle 的签名管，不按顶层散文件签。
                .Where(file => !nestedBundles.Any(bundle =>
                    file.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .Where(file => !string.Equals(file, mainExecutablePath, StringComparison.Ordinal))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();

            foreach (var file in nestedFiles)
            {
                await MacProcessRunner.RunAsync("codesign",
                    CodesignArguments(identity, signing, entitlements: false, file),
                    context.WorkDirectory, cancellationToken);
            }
            foreach (var nested in nestedBundles)
            {
                await SignNestedBundleAsync(
                    context, signing, identity, nested, cancellationToken);
            }
            await MacProcessRunner.RunAsync("codesign",
                CodesignArguments(identity, signing, entitlements: true, mainExecutablePath),
                context.WorkDirectory, cancellationToken);
            await MacProcessRunner.RunAsync("codesign",
                CodesignArguments(identity, signing, entitlements: true, appPath),
                context.WorkDirectory, cancellationToken);

            await MacProcessRunner.RunAsync("codesign",
                ["--verify", "--deep", "--strict", "--verbose=4", appPath],
                context.WorkDirectory, cancellationToken);
            if (identity != "-")
            {
                var assessment = await MacProcessRunner.TryRunAsync("spctl",
                    ["-a", "-t", "execute", "-vv", appPath],
                    context.WorkDirectory, cancellationToken);
                if (assessment is { ExitCode: 0 })
                {
                    logger.Log(BundleLogLevel.Information, "spctl accepted the signed bundle.");
                }
                else
                {
                    logger.Log(BundleLogLevel.Warning,
                        "spctl rejected the bundle (expected for self-signed or untrusted " +
                        "identities): " + (assessment?.StandardError ?? assessment?.StandardOutput ?? "unavailable"));
                }
            }
        }
        finally
        {
            if (keychain is not null)
            {
                await keychain.DisposeAsync(context.WorkDirectory, cancellationToken);
            }
        }

        if (signing.Notarize)
        {
            await NotarizeAsync(context, appPath, signing, cancellationToken);
        }
    }

    /// <summary>嵌套 bundle（.app/.framework/…）的 inside-out 递归签名：内部散文件→更深的嵌套→bundle 根。</summary>
    private static async Task SignNestedBundleAsync(
        BundleBuildContext context, MacAppSigningConfiguration signing, string identity,
        string bundlePath, CancellationToken cancellationToken)
    {
        // .app 的载荷在 Contents/ 下；.framework 无 Contents，载荷在
        // Versions/Current 下（符号链接，枚举时自然穿透）；扁平嵌套包退回包根。
        var contentsDirectory = new[]
            {
                Path.Combine(bundlePath, "Contents"),
                Path.Combine(bundlePath, "Versions", "Current"),
                bundlePath,
            }
            .First(Directory.Exists);
        {
            var deeper = FindNestedBundles(contentsDirectory);
            var loose = Directory
                .EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories)
                .Where(file => CodeDirectoryNames.Any(dir =>
                    file.StartsWith(
                        Path.Combine(contentsDirectory, dir) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
                .Where(file => !deeper.Any(bundle =>
                    file.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
            foreach (var file in loose)
            {
                await MacProcessRunner.RunAsync("codesign",
                    CodesignArguments(identity, signing, entitlements: false, file),
                    context.WorkDirectory, cancellationToken);
            }
            foreach (var nested in deeper)
            {
                await SignNestedBundleAsync(context, signing, identity, nested, cancellationToken);
            }
        }
        // bundle 根签名封住其主可执行与资源——不叠 entitlements（属主 app 的旋钮）。
        await MacProcessRunner.RunAsync("codesign",
            CodesignArguments(identity, signing, entitlements: false, bundlePath),
            context.WorkDirectory, cancellationToken);
    }

    /// <summary>找出 Contents/ 下的顶层嵌套 bundle；更深一层的由递归自己发现。</summary>
    private static IReadOnlyList<string> FindNestedBundles(string contentsDirectory)
    {
        var bundles = new List<string>();
        var claimed = new List<string>();
        foreach (var directory in Directory
            .EnumerateDirectories(contentsDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (claimed.Any(bundle =>
                    directory.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                continue;
            }
            if (NestedBundleExtensions.Any(extension =>
                    directory.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                bundles.Add(directory);
                claimed.Add(directory);
            }
        }
        return bundles;
    }

    /// <summary>codesign argument list; separated for test assertion.</summary>
    internal static IReadOnlyList<string> CodesignArguments(
        string identity, MacAppSigningConfiguration signing, bool entitlements, string target)
    {
        var arguments = new List<string> { "--force", "--sign", identity };
        if (signing.HardenedRuntime)
        {
            arguments.Add("--options");
            arguments.Add("runtime");
        }
        if (entitlements && signing.EntitlementsFile is { Length: > 0 })
        {
            arguments.Add("--entitlements");
            arguments.Add(Path.GetFullPath(signing.EntitlementsFile));
        }
        arguments.Add(identity == "-" ? "--timestamp=none" : "--timestamp");
        arguments.Add(target);
        return arguments;
    }

    /// <summary>
    /// Resolves notary credentials: explicit config first, then APPLE_* env vars
    /// (the names other bundlers use). Returns the credential argument set or throws.
    /// </summary>
    internal static IReadOnlyList<string> ResolveCredentials(MacAppSigningConfiguration signing) =>
        ResolveCredentials(
            signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId);

    /// <summary>Field-level overload shared with the .pkg backend's signing configuration.</summary>
    internal static IReadOnlyList<string> ResolveCredentials(
        string? keychainProfile, string? apiKeyFile, string? apiKeyId, string? apiIssuer,
        string? appleId, string? applePassword, string? appleTeamId)
    {
        var profile = keychainProfile ?? Env("APPLE_PROFILE");
        if (profile is { Length: > 0 })
        {
            return ["--keychain-profile", profile];
        }
        var keyPath = apiKeyFile ?? Env("APPLE_API_KEY_PATH");
        var keyId = apiKeyId ?? Env("APPLE_API_KEY");
        var issuer = apiIssuer ?? Env("APPLE_API_ISSUER");
        if (keyPath is { Length: > 0 } || keyId is { Length: > 0 } || issuer is { Length: > 0 })
        {
            if (keyPath is not { Length: > 0 } || keyId is not { Length: > 0 } ||
                issuer is not { Length: > 0 })
            {
                throw new ArgumentException(
                    "Notarization API-key credentials need all of ApiKeyFile/ApiKeyId/ApiKeyIssuer " +
                    "(or APPLE_API_KEY_PATH/APPLE_API_KEY/APPLE_API_ISSUER).");
            }
            return ["--key", keyPath, "--key-id", keyId, "--issuer", issuer];
        }
        var id = appleId ?? Env("APPLE_ID");
        var password = applePassword ?? Env("APPLE_PASSWORD");
        var team = appleTeamId ?? Env("APPLE_TEAM_ID");
        if (id is { Length: > 0 } || password is { Length: > 0 } || team is { Length: > 0 })
        {
            if (id is not { Length: > 0 } || password is not { Length: > 0 } ||
                team is not { Length: > 0 })
            {
                throw new ArgumentException(
                    "Notarization Apple-ID credentials need all of AppleId/ApplePassword/AppleTeamId " +
                    "(or APPLE_ID/APPLE_PASSWORD/APPLE_TEAM_ID).");
            }
            return ["--apple-id", id, "--password", password, "--team-id", team];
        }
        throw new ArgumentException(
            "Notarization is enabled but no credentials were provided: set a keychain profile " +
            "(Signing.KeychainProfile/APPLE_PROFILE), an API key (ApiKeyFile/ApiKeyId/ApiKeyIssuer " +
            "or APPLE_API_KEY_PATH/APPLE_API_KEY/APPLE_API_ISSUER), or Apple ID credentials " +
            "(AppleId/ApplePassword/AppleTeamId or APPLE_ID/APPLE_PASSWORD/APPLE_TEAM_ID).");
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>ditto → notarytool submit → stapler. The zip is always cleaned up.</summary>
    private static async Task NotarizeAsync(
        BundleBuildContext context, string appPath, MacAppSigningConfiguration signing,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(signing);
        var zipPath = Path.Combine(
            context.WorkDirectory, Path.GetFileName(appPath) + ".notarize.zip");
        try
        {
            context.Logger.Log(BundleLogLevel.Information, "Archiving the bundle for notarization (ditto -c -k --keepParent).");
            await MacProcessRunner.RunAsync(
                "ditto", ["-c", "-k", "--keepParent", appPath, zipPath],
                context.WorkDirectory, cancellationToken);
            var submitArguments = new List<string> { "notarytool", "submit", zipPath };
            submitArguments.AddRange(credentials);
            submitArguments.Add("--output-format");
            submitArguments.Add("json");
            if (signing.NotaryWait)
            {
                submitArguments.Add("--wait");
            }
            context.Logger.Log(BundleLogLevel.Information,
                signing.NotaryWait
                    ? "Submitting to notarytool and waiting for the verdict."
                    : "Submitting to notarytool without waiting (stapling is skipped).");
            var result = await MacProcessRunner.TryRunAsync(
                "xcrun", submitArguments, context.WorkDirectory, cancellationToken)
                ?? throw new InvalidOperationException(
                    "xcrun/notarytool is unavailable; notarization requires Xcode 13+ (host macOS 11.3+).");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"notarytool submission failed.{Environment.NewLine}" +
                    $"{result.StandardOutput}{result.StandardError}".TrimEnd());
            }
            var submissionId = NotarySubmissionId(result.StandardOutput);
            if (submissionId is not null)
            {
                context.Logger.Log(BundleLogLevel.Information, $"notarytool submission id: {submissionId}");
            }
            if (!signing.NotaryWait || signing.SkipStapling)
            {
                if (signing.SkipStapling && signing.NotaryWait)
                {
                    context.Logger.Log(BundleLogLevel.Information,
                        "SkipStapling: the ticket is accepted; stapling skipped per configuration.");
                }
                return;
            }
            await MacProcessRunner.RunAsync(
                "xcrun", ["stapler", "staple", appPath],
                context.WorkDirectory, cancellationToken);
            context.Logger.Log(BundleLogLevel.Information, "stapler attached the notarization ticket.");
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    /// <summary>Pull the submission id from notarytool's JSON output; null when absent.</summary>
    internal static string? NotarySubmissionId(string json)
    {
        var match = Regex.Match(json, "\"id\"\\s*:\\s*\"(?<id>[0-9A-Fa-f-]{8,})\"");
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>
    /// Throwaway keychain holding an imported signing certificate for the duration of one build.
    /// The keychain is prepended to the user search list so codesign resolves the identity, then
    /// the search list is restored and the keychain deleted on dispose.
    /// Shared with the .dmg backend, which signs the image with the same certificate story.
    /// </summary>
    internal sealed class TemporaryKeychain
    {
        private TemporaryKeychain(string path, string password, string identity,
            IReadOnlyList<string> previousKeychains)
        {
            Path = path;
            Password = password;
            Identity = identity;
            PreviousKeychains = previousKeychains;
        }

        /// <summary>Keychain file path; exposed so sibling backends can pass --keychain.</summary>
        internal string Path { get; }
        private string Password { get; }
        internal string Identity { get; }
        private IReadOnlyList<string> PreviousKeychains { get; }

        internal static async Task<TemporaryKeychain> CreateAsync(
            string workDirectory, string certificatePath, string certificatePassword,
            IBundleLogger logger, CancellationToken cancellationToken)
        {
            var path = System.IO.Path.Combine(
                workDirectory, $"bundler-sign-{Guid.NewGuid():N}.keychain-db");
            var password = BitConverter.ToString(Guid.NewGuid().ToByteArray()).Replace("-", "");

            var keychain = new TemporaryKeychain(path, password, "", []);
            try
            {
                keychain = await CreateCoreAsync(path, password, certificatePath,
                    certificatePassword, workDirectory, logger, cancellationToken);
                return keychain;
            }
            catch
            {
                await keychain.DisposeAsync(workDirectory, cancellationToken);
                throw;
            }
        }

        private static async Task<TemporaryKeychain> CreateCoreAsync(
            string path, string password, string certificatePath, string certificatePassword,
            string workDirectory, IBundleLogger logger, CancellationToken cancellationToken)
        {
            var existing = await ListUserKeychains(workDirectory, cancellationToken);
            var keychain = new TemporaryKeychain(path, password, "", existing);
            try
            {
            await MacProcessRunner.RunAsync(
                "security", ["create-keychain", "-p", password, path],
                workDirectory, cancellationToken);
            var searchList = new List<string> { "list-keychains", "-d", "user", "-s", path };
            searchList.AddRange(existing);
            await MacProcessRunner.RunAsync(
                "security", searchList, workDirectory, cancellationToken);
            await MacProcessRunner.RunAsync(
                "security", ["unlock-keychain", "-p", password, path],
                workDirectory, cancellationToken);
            // p12 口令不进 argv：先经 openssl 转成无口令 PEM 再导入。
            var imported = false;
            MacProcessRunner.Result? unwrapped = null;
            var pemPath = System.IO.Path.Combine(
                workDirectory, $"bundler-cert-{Guid.NewGuid():N}.pem");
            var passwordFile = System.IO.Path.Combine(
                workDirectory, $"bundler-cert-{Guid.NewGuid():N}.pass");
            try
            {
                // 口令文件与解包 PEM 含明文材料：以 0600 创建杜绝宽松 umask 下
                // 他用户读取；openssl -out 截断既有文件时保留其权限位。
                await CreateSecretFileAsync(pemPath, null, workDirectory, cancellationToken);
                await CreateSecretFileAsync(passwordFile, certificatePassword,
                    workDirectory, cancellationToken);
                unwrapped = await MacProcessRunner.TryRunAsync(
                    "openssl",
                    ["pkcs12", "-in", certificatePath, "-nodes",
                     "-passin", "file:" + passwordFile, "-out", pemPath],
                    workDirectory, cancellationToken);
                if (unwrapped is { ExitCode: 0 } && File.Exists(pemPath))
                {
                    await MacProcessRunner.RunAsync(
                        "security", ["import", pemPath, "-k", path, "-T", "/usr/bin/codesign"],
                        workDirectory, cancellationToken);
                    imported = true;
                }
            }
            finally
            {
                // 中间产物含明文私钥与口令文件，立即清除。
                if (File.Exists(pemPath))
                {
                    File.Delete(pemPath);
                }
                if (File.Exists(passwordFile))
                {
                    File.Delete(passwordFile);
                }
            }
            if (!imported)
            {
                // 不退回 `security import -P`：口令进 argv 可被本机任意进程读取，
                // 与仓规"秘密不入普通命令行"冲突——解包失败如实报错。
                throw new InvalidOperationException(
                    $"openssl could not convert '{certificatePath}' to PEM for keychain import: " +
                    (unwrapped is null ? "openssl is unavailable" :
                        unwrapped.StandardError.Trim() is { Length: > 0 } error ? error :
                        $"exit code {unwrapped.ExitCode}") +
                    "; the certificate password must not appear in process arguments.");
            }
            await MacProcessRunner.RunAsync(
                "security", ["set-key-partition-list", "-S", "apple-tool:,apple:",
                    "-s", "-k", password, path],
                workDirectory, cancellationToken);
            var identity = await FindIdentityAsync(path, workDirectory, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No signing identity was found in {certificatePath} after importing it " +
                    "into the temporary keychain.");
            logger.Log(BundleLogLevel.Information,
                $"Imported the signing certificate into a temporary keychain (identity '{identity}').");
            return new TemporaryKeychain(path, password, identity, existing);
            }
            catch
            {
                // 中途失败也要还原用户钥匙串搜索列表（list-keychains -s 已改过）。
                await keychain.DisposeAsync(workDirectory, cancellationToken);
                throw;
            }
        }

        // 含明文秘密的临时文件一律 0600 落盘——宽松 umask 下默认可被本机他用户读取。
        // netstandard2.0 无 UnixCreateMode：先建空文件再 chmod——0600 生效前文件为空，
        // 无秘密窗口；openssl -out 截断既有文件时保留其权限位。
        private static async Task CreateSecretFileAsync(
            string filePath, string? contents, string workDirectory, CancellationToken cancellationToken)
        {
            using (new FileStream(filePath, FileMode.CreateNew, FileAccess.Write)) { }
            await MacProcessRunner.RunAsync(
                "chmod", ["600", filePath], workDirectory, cancellationToken);
            if (contents is not null)
            {
                File.WriteAllText(filePath, contents);
            }
        }

        internal async Task DisposeAsync(string workDirectory, CancellationToken cancellationToken)
        {
            if (PreviousKeychains.Count > 0)
            {
                var restore = new List<string> { "list-keychains", "-d", "user", "-s" };
                restore.AddRange(PreviousKeychains);
                await MacProcessRunner.TryRunAsync("security", restore, workDirectory, cancellationToken);
            }
            await MacProcessRunner.TryRunAsync(
                "security", ["delete-keychain", Path], workDirectory, cancellationToken);
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }

        private static async Task<IReadOnlyList<string>> ListUserKeychains(
            string workDirectory, CancellationToken cancellationToken)
        {
            var result = await MacProcessRunner.TryRunAsync(
                "security", ["list-keychains", "-d", "user"], workDirectory, cancellationToken);
            if (result is not { ExitCode: 0 })
            {
                return [];
            }
            return result.StandardOutput
                .Split('\n')
                .Select(line => line.Trim().Trim('"'))
                .Where(line => line.Length > 0)
                .Where(line => line.EndsWith(".keychain-db", StringComparison.Ordinal) ||
                               line.EndsWith(".keychain", StringComparison.Ordinal))
                .ToArray();
        }

        private static async Task<string?> FindIdentityAsync(
            string keychainPath, string workDirectory, CancellationToken cancellationToken)
        {
            var result = await MacProcessRunner.TryRunAsync("security",
                ["find-identity", "-v", "-p", "codesigning", keychainPath],
                workDirectory, cancellationToken);
            if (result is not { ExitCode: 0 })
            {
                return null;
            }
            var match = Regex.Match(result.StandardOutput,
                "\\)\\s+[0-9A-Fa-f]{40}\\s+\"(?<name>[^\"]+)\"");
            return match.Success ? match.Groups["name"].Value : null;
        }
    }
}

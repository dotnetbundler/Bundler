using System.Text.RegularExpressions;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// Shared signing/notarization machinery for the macOS backends (.app/.dmg/.pkg).
/// Credential resolution, throwaway keychain lifecycle, tool argument construction,
/// and notarytool submit/wait/staple live here once so the three format backends
/// cannot drift apart.
/// </summary>
internal static class MacSigningSupport
{
    /// <summary>Identity/certificate argument-shape checks shared by all three signing knobs.</summary>
    internal static void ValidateIdentityAndCertificate(
        string? identity, string? temporaryCertificateFile, bool allowAdHoc, string formatLabel)
    {
        if (identity is not null && !string.IsNullOrEmpty(temporaryCertificateFile))
        {
            throw new ArgumentException(
                $"{formatLabel} Signing.Identity and Signing.TemporaryCertificateFile are mutually exclusive.");
        }
        if (identity is { Length: 0 })
        {
            throw new ArgumentException(allowAdHoc
                ? $"{formatLabel} Signing.Identity cannot be empty (use \"-\" for ad-hoc)."
                : $"{formatLabel} Signing.Identity cannot be empty.");
        }
        if (!allowAdHoc && identity == "-")
        {
            throw new ArgumentException(
                $"{formatLabel} Signing.Identity must be a real signing identity; " +
                "this package format has no ad-hoc signature equivalent.");
        }
        if (temporaryCertificateFile is { Length: > 0 } certificatePath &&
            !File.Exists(Path.GetFullPath(certificatePath)))
        {
            throw new FileNotFoundException(
                $"{formatLabel} Signing.TemporaryCertificateFile does not exist.", certificatePath);
        }
    }

    /// <summary>Notarization knob gating shared by the .app/.pkg Validate paths.</summary>
    internal static void ValidateNotarizationKnobs(
        bool notarize, bool signingConfigured, bool adHocIdentity,
        string? keychainProfile, string? apiKeyFile, string? apiKeyId, string? apiKeyIssuer,
        string? appleId, string? applePassword, string? appleTeamId,
        bool skipStapling, bool notaryWait, string missingSigningMessage)
    {
        if (!notarize)
        {
            if (skipStapling || !notaryWait)
            {
                throw new ArgumentException("SkipStapling/NotaryWait require Notarize to be enabled.");
            }
            return;
        }
        if (!signingConfigured || adHocIdentity)
        {
            throw new ArgumentException(missingSigningMessage);
        }
        _ = ResolveCredentials(
            keychainProfile, apiKeyFile, apiKeyId, apiKeyIssuer, appleId, applePassword, appleTeamId);
    }

    /// <summary>
    /// codesign argument list shared by .app payload signing and .dmg image signing.
    /// A temporary keychain is passed explicitly via --keychain instead of mutating the
    /// user search list: no global state, no restore window, no parallel-build races.
    /// </summary>
    internal static IReadOnlyList<string> CodesignArguments(
        string identity, bool hardenedRuntime, string? entitlementsFile, bool applyEntitlements,
        TemporaryKeychain? keychain, string target)
    {
        var arguments = new List<string> { "--force", "--sign", identity };
        if (hardenedRuntime)
        {
            arguments.Add("--options");
            arguments.Add("runtime");
        }
        if (applyEntitlements && entitlementsFile is { Length: > 0 })
        {
            arguments.Add("--entitlements");
            arguments.Add(Path.GetFullPath(entitlementsFile));
        }
        if (keychain is not null)
        {
            arguments.Add("--keychain");
            arguments.Add(keychain.Path);
        }
        arguments.Add(identity == "-" ? "--timestamp=none" : "--timestamp");
        arguments.Add(target);
        return arguments;
    }

    /// <summary>pkgbuild flags appended when the component package is signed at build time.</summary>
    internal static IReadOnlyList<string> PkgbuildSignArguments(
        string identity, TemporaryKeychain? keychain)
    {
        var arguments = new List<string> { "--sign", identity, "--timestamp" };
        AddKeychain(arguments, keychain);
        return arguments;
    }

    /// <summary>productsign option list (input/output paths are appended by the caller).</summary>
    internal static IReadOnlyList<string> ProductsignArguments(
        string identity, TemporaryKeychain? keychain)
    {
        var arguments = new List<string> { "--sign", identity };
        AddKeychain(arguments, keychain);
        arguments.Add("--timestamp");
        return arguments;
    }

    private static void AddKeychain(List<string> arguments, TemporaryKeychain? keychain)
    {
        if (keychain is not null)
        {
            arguments.Add("--keychain");
            arguments.Add(keychain.Path);
        }
    }

    /// <summary>
    /// Resolve the effective identity: an imported certificate opens a temporary keychain,
    /// a configured identity passes through. The caller disposes the returned keychain.
    /// </summary>
    internal static async Task<(string Identity, TemporaryKeychain? Keychain)> ResolveIdentityAsync(
        string workDirectory, IBundleLogger logger, string? identity,
        string? temporaryCertificateFile, string? temporaryCertificatePassword,
        CancellationToken cancellationToken)
    {
        if (temporaryCertificateFile is { Length: > 0 } certificatePath)
        {
            var keychain = await TemporaryKeychain.CreateAsync(
                workDirectory, Path.GetFullPath(certificatePath),
                temporaryCertificatePassword ?? "", logger, cancellationToken);
            return (keychain.Identity, keychain);
        }
        return (identity!, null);
    }

    /// <summary>
    /// Resolves notary credentials: explicit config first, then APPLE_* env vars
    /// (the names other bundlers use). Returns the credential argument set plus whether
    /// it places a secret on the process command line (--password route); throws when no
    /// credential combination is complete.
    /// </summary>
    internal static (IReadOnlyList<string> Arguments, bool PasswordOnArgv) ResolveCredentials(
        string? keychainProfile, string? apiKeyFile, string? apiKeyId, string? apiIssuer,
        string? appleId, string? applePassword, string? appleTeamId)
    {
        var profile = keychainProfile ?? Env("APPLE_PROFILE");
        if (profile is { Length: > 0 })
        {
            return (["--keychain-profile", profile], false);
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
            return (["--key", keyPath, "--key-id", keyId, "--issuer", issuer], false);
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
            return (["--apple-id", id, "--password", password, "--team-id", team], true);
        }
        throw new ArgumentException(
            "Notarization is enabled but no credentials were provided: set a keychain profile " +
            "(Signing.KeychainProfile/APPLE_PROFILE), an API key (ApiKeyFile/ApiKeyId/ApiKeyIssuer " +
            "or APPLE_API_KEY_PATH/APPLE_API_KEY/APPLE_API_ISSUER), or Apple ID credentials " +
            "(AppleId/ApplePassword/AppleTeamId or APPLE_ID/APPLE_PASSWORD/APPLE_TEAM_ID).");
    }

    /// <summary>Field-level convenience overloads for the two notarizing configuration types.</summary>
    internal static (IReadOnlyList<string> Arguments, bool PasswordOnArgv) ResolveCredentials(
        MacAppSigningConfiguration signing) =>
        ResolveCredentials(
            signing.KeychainProfile, signing.ApiKeyFile, signing.ApiKeyId, signing.ApiKeyIssuer,
            signing.AppleId, signing.ApplePassword, signing.AppleTeamId);

    /// <summary>
    /// notarytool submit (with bounded --wait) + stapler. <paramref name="submitPath"/> is the
    /// uploaded artifact (a ditto zip for .app, the package itself for .pkg),
    /// <paramref name="stapleTarget"/> is what stapler attaches the ticket to.
    /// </summary>
    internal static async Task NotarizeAsync(
        BundleBuildContext context, string submitPath, string stapleTarget,
        bool wait, bool skipStapling, string? keychainProfile,
        string? apiKeyFile, string? apiKeyId, string? apiKeyIssuer,
        string? appleId, string? applePassword, string? appleTeamId,
        CancellationToken cancellationToken)
    {
        var (credentials, passwordOnArgv) = ResolveCredentials(
            keychainProfile, apiKeyFile, apiKeyId, apiKeyIssuer, appleId, applePassword, appleTeamId);
        if (passwordOnArgv)
        {
            context.Logger.Log(BundleLogLevel.Warning,
                "Apple-ID notarization places the app-specific password on the notarytool " +
                "command line (visible to other processes on shared hosts); prefer " +
                "Signing.KeychainProfile or an API key.");
        }
        var submitArguments = new List<string> { "notarytool", "submit", submitPath };
        submitArguments.AddRange(credentials);
        submitArguments.Add("--output-format");
        submitArguments.Add("json");
        if (wait)
        {
            submitArguments.Add("--wait");
        }
        context.Logger.Log(BundleLogLevel.Information,
            wait
                ? "Submitting to notarytool and waiting for the verdict."
                : "Submitting to notarytool without waiting (stapling is skipped).");
        // notarytool --wait legitimately queues for tens of minutes on busy days — the
        // default tool timeout would kill healthy long-running submissions mid-flight.
        var result = await ToolRunner.TryRunAsync(
            "xcrun", submitArguments, context.WorkDirectory, cancellationToken,
            timeout: TimeSpan.FromHours(1))
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
        if (!wait || skipStapling)
        {
            if (skipStapling && wait)
            {
                context.Logger.Log(BundleLogLevel.Information,
                    "SkipStapling: the ticket is accepted; stapling skipped per configuration.");
            }
            return;
        }
        await ToolRunner.RunAsync(
            "xcrun", ["stapler", "staple", stapleTarget],
            context.WorkDirectory, cancellationToken);
        context.Logger.Log(BundleLogLevel.Information, "stapler attached the notarization ticket.");
    }

    /// <summary>Pull the submission id from notarytool's JSON output; null when absent.</summary>
    internal static string? NotarySubmissionId(string json)
    {
        var match = Regex.Match(json, "\"id\"\\s*:\\s*\"(?<id>[0-9A-Fa-f-]{8,})\"");
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Throwaway keychain holding an imported signing certificate for the duration of one build.
    /// The keychain is never added to the user search list: codesign/pkgbuild/productsign
    /// resolve the identity through an explicit --keychain argument, so parallel builds
    /// cannot race the global list and nothing needs restoring on teardown.
    /// </summary>
    internal sealed class TemporaryKeychain
    {
        /// <summary>
        /// Test seam: the real lifecycle is <see cref="CreateAsync"/> (create + import +
        /// identity probe); this ctor exists so argument-shape tests can carry a keychain
        /// handle without spawning security(1).
        /// </summary>
        internal TemporaryKeychain(string path, string identity)
        {
            Path = path;
            Identity = identity;
        }

        /// <summary>Keychain file path; consumed via --keychain arguments on the signing tools.</summary>
        internal string Path { get; }
        internal string Identity { get; }

        internal static async Task<TemporaryKeychain> CreateAsync(
            string workDirectory, string certificatePath, string certificatePassword,
            IBundleLogger logger, CancellationToken cancellationToken)
        {
            var path = System.IO.Path.Combine(
                workDirectory, $"bundler-sign-{Guid.NewGuid():N}.keychain-db");
            var password = BitConverter.ToString(Guid.NewGuid().ToByteArray()).Replace("-", "");

            var keychain = new TemporaryKeychain(path, "");
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
            var keychain = new TemporaryKeychain(path, "");
            try
            {
            await ToolRunner.RunAsync(
                "security", ["create-keychain", "-p", password, path],
                workDirectory, cancellationToken);
            await ToolRunner.RunAsync(
                "security", ["unlock-keychain", "-p", password, path],
                workDirectory, cancellationToken);
            // p12 口令不进 argv：先经 openssl 转成无口令 PEM 再导入。
            var imported = false;
            ToolRunner.Result? unwrapped = null;
            var pemPath = System.IO.Path.Combine(
                workDirectory, $"bundler-cert-{Guid.NewGuid():N}.pem");
            var certPemPath = System.IO.Path.Combine(
                workDirectory, $"bundler-leaf-{Guid.NewGuid():N}.pem");
            var keyPemPath = System.IO.Path.Combine(
                workDirectory, $"bundler-key-{Guid.NewGuid():N}.pem");
            var passwordFile = System.IO.Path.Combine(
                workDirectory, $"bundler-cert-{Guid.NewGuid():N}.pass");
            try
            {
                // 口令文件与解包 PEM 含明文材料：以 0600 创建杜绝宽松 umask 下
                // 他用户读取；openssl -out 截断既有文件时保留其权限位。
                await CreateSecretFileAsync(pemPath, null, workDirectory, cancellationToken);
                await CreateSecretFileAsync(certPemPath, null, workDirectory, cancellationToken);
                await CreateSecretFileAsync(keyPemPath, null, workDirectory, cancellationToken);
                await CreateSecretFileAsync(passwordFile, certificatePassword,
                    workDirectory, cancellationToken);
                unwrapped = await ToolRunner.TryRunAsync(
                    "openssl",
                    ["pkcs12", "-in", certificatePath, "-nodes",
                     "-passin", "file:" + passwordFile, "-out", pemPath],
                    workDirectory, cancellationToken);
                if (unwrapped is { ExitCode: 0 } && File.Exists(pemPath))
                {
                    await ToolRunner.RunAsync(
                        "security", ["import", pemPath, "-k", path, "-T", "/usr/bin/codesign"],
                        workDirectory, cancellationToken);
                    // security import 对 key+cert 同件 PEM 在部分宿主只进部分条目
                    //（macos-26 CI 实证 rc=0 但 find-identity 0 valid identities）——
                    // 社区自签 codesign 配方即 cert/key 分件导入；再分件补一遍，
                    // 已存在的条目重复导入按无害略过（TryRun 不判 dup 报错），
                    // 身份是否成立由下方 find-identity 读回裁定。
                    var leafOut = await ToolRunner.TryRunAsync(
                        "openssl",
                        ["pkcs12", "-in", certificatePath, "-clcerts", "-nokeys",
                         "-passin", "file:" + passwordFile, "-out", certPemPath],
                        workDirectory, cancellationToken);
                    var keyOut = await ToolRunner.TryRunAsync(
                        "openssl",
                        ["pkcs12", "-in", certificatePath, "-nocerts", "-nodes",
                         "-passin", "file:" + passwordFile, "-out", keyPemPath],
                        workDirectory, cancellationToken);
                    if (leafOut is { ExitCode: 0 } && File.Exists(certPemPath))
                    {
                        _ = await ToolRunner.TryRunAsync(
                            "security", ["import", certPemPath, "-k", path,
                                "-T", "/usr/bin/codesign"],
                            workDirectory, cancellationToken);
                    }
                    if (keyOut is { ExitCode: 0 } && File.Exists(keyPemPath))
                    {
                        _ = await ToolRunner.TryRunAsync(
                            "security", ["import", keyPemPath, "-k", path,
                                "-T", "/usr/bin/codesign"],
                            workDirectory, cancellationToken);
                    }
                    imported = true;
                }
            }
            finally
            {
                // 中间产物含明文私钥与口令文件，立即清除。
                foreach (var secret in new[] { pemPath, certPemPath, keyPemPath, passwordFile })
                {
                    if (File.Exists(secret))
                    {
                        File.Delete(secret);
                    }
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
            await ToolRunner.RunAsync(
                "security", ["set-key-partition-list", "-S", "apple-tool:,apple:,codesign:",
                    "-s", "-k", password, path],
                workDirectory, cancellationToken);
            var (identity, probe) = await FindIdentityAsync(
                path, workDirectory, cancellationToken);
            if (identity is null)
            {
                throw new InvalidOperationException(
                    $"No signing identity was found in {certificatePath} after importing it " +
                    $"into the temporary keychain.{probe}");
            }
            logger.Log(BundleLogLevel.Information,
                $"Imported the signing certificate into a temporary keychain (identity '{identity}').");
            return new TemporaryKeychain(path, identity);
            }
            catch
            {
                // 中途失败也要删掉已建的 keychain 件（含半成品导入条目）。
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
            await ToolRunner.RunAsync(
                "chmod", ["600", filePath], workDirectory, cancellationToken);
            if (contents is not null)
            {
                File.WriteAllText(filePath, contents);
            }
        }

        internal async Task DisposeAsync(string workDirectory, CancellationToken cancellationToken)
        {
            // 清理不可取消：取消时杀子进程会留下残留 keychain 文件。
            await ToolRunner.TryRunAsync(
                "security", ["delete-keychain", Path], workDirectory, CancellationToken.None);
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }

        // 返回（身份名, 探测现场）：未命中时第二路无策略全列帮助区分
        // "条目根本没导进 keychain"（0 identities）与"有身份但 codesigning
        // 策略面不认可"（有 identities、0 valid）——现场随异常进 Skip 理由。
        private static async Task<(string? Identity, string Probe)> FindIdentityAsync(
            string keychainPath, string workDirectory, CancellationToken cancellationToken)
        {
            var result = await ToolRunner.TryRunAsync("security",
                ["find-identity", "-v", "-p", "codesigning", keychainPath],
                workDirectory, cancellationToken);
            var probe = " find-identity(-v -p codesigning): " + (result is null
                ? "<security unavailable>"
                : $"rc={result.ExitCode} [{(result.StandardOutput + result.StandardError).Trim()}]");
            if (result is { ExitCode: 0 })
            {
                var match = Regex.Match(result.StandardOutput,
                    "\\)\\s+[0-9A-Fa-f]{40}\\s+\"(?<name>[^\"]+)\"");
                if (match.Success)
                {
                    return (match.Groups["name"].Value, probe);
                }
            }
            var all = await ToolRunner.TryRunAsync("security",
                ["find-identity", keychainPath], workDirectory, cancellationToken);
            probe += "; find-identity(no policy): " + (all is null
                ? "<security unavailable>"
                : $"rc={all.ExitCode} [{(all.StandardOutput + all.StandardError).Trim()}]");
            return (null, probe);
        }
    }
}

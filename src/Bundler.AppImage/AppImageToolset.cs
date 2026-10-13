using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

/// <summary>
/// Materializes the embedded appimagetool binaries (and the aarch64 cross-arch
/// runtime) to a hash-stamped cache directory. Every embedded resource is
/// verified against the SHA-256 recorded in
/// <c>third_party/appimagetool/appimagetool-provenance.md</c> before use.
/// </summary>
internal sealed class AppImageToolset
{
    // SHA-256 of the embedded third_party files (see the provenance doc).
    internal const string ToolX64Sha256 =
        "a6d71e2b6cd66f8e8d16c37ad164658985e0cf5fcaa950c90a482890cb9d13e0";
    internal const string ToolArm64Sha256 =
        "1b00524ba8c6b678dc15ef88a5c25ec24def36cdfc7e3abb32ddcd068e8007fe";
    internal const string RuntimeX64Sha256 =
        "1cc49bcf1e2ccd593c379adb17c9f85a36d619088296504de95b1d06215aebbf";
    internal const string RuntimeAarch64Sha256 =
        "7d5d772b7c32f0c84caf0a452a3072a5709027d7eac5856feb89a7a7a8881372";

    // 钉死的 appimagetool continuous build 号（provenance 同源）。
    internal const string ToolsetVersion = "295";

    internal string ToolPath { get; }
    internal string RuntimePath { get; }

    private AppImageToolset(string toolPath, string runtimePath)
    {
        ToolPath = toolPath;
        RuntimePath = runtimePath;
    }

    /// <summary>
    /// Resolves the tool for the current Linux host and the requested target
    /// architecture (appimagetool names: x86_64/aarch64/i686/...).
    /// </summary>
    internal static void RequireLinuxHost()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException(
                "DotNet.Bundler.AppImage requires a Linux build host: it invokes the bundled appimagetool binary.");
        }
    }

    internal static async Task<AppImageToolset> ResolveAsync(
        string targetArchitecture,
        string? cacheDirectory,
        CancellationToken cancellationToken)
    {
        RequireLinuxHost();

        var hostArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException(
                $"DotNet.Bundler.AppImage supports x86_64 and aarch64 build hosts; got '{RuntimeInformation.ProcessArchitecture}'.")
        };

        var cache = ToolCache.ResolveCacheDirectory(cacheDirectory);
        var toolName = "appimagetool-" + hostArch + ".AppImage";
        // This pinned appimagetool build does not embed a runtime: without
        // --runtime-file it downloads one at build time, so the target
        // runtime is always supplied from the bundle.
        var (runtimeName, runtimeResource, runtimeHash) = targetArchitecture switch
        {
            "x86_64" => ("runtime-x86_64", "DotNet.Bundler.AppImage.Runtime.x86_64", RuntimeX64Sha256),
            "aarch64" => ("runtime-aarch64", "DotNet.Bundler.AppImage.Runtime.aarch64", RuntimeAarch64Sha256),
            _ => throw new NotSupportedException(
                $"AppImage output for '{targetArchitecture}' needs a matching runtime file; " +
                "only x86_64 and aarch64 runtimes are bundled.")
        };
        var toolResource = hostArch == "x86_64"
            ? "DotNet.Bundler.AppImage.Toolset.x86_64"
            : "DotNet.Bundler.AppImage.Toolset.aarch64";
        var toolHash = hostArch == "x86_64" ? ToolX64Sha256 : ToolArm64Sha256;

        var files = new[]
        {
            new ToolFileSpec(toolName, toolHash),
            new ToolFileSpec(runtimeName, runtimeHash)
        };
        var resources = new Dictionary<string, string>
        {
            [toolName] = toolResource,
            [runtimeName] = runtimeResource
        };
        var assembly = typeof(AppImageToolset).Assembly;
        var resolved = await ToolCache.ResolveAsync(
            cache,
            new ToolFileSet("appimagetool", ToolsetVersion, ToolCache.ComputeContentId(files), files),
            (file, _) => Task.FromResult<Stream>(
                assembly.GetManifestResourceStream(resources[file.RelativePath]) ??
                throw new InvalidOperationException(
                    $"Embedded AppImage resource '{resources[file.RelativePath]}' was not found.")),
            cancellationToken);

        var toolPath = ToolCache.ResolveToolPath(resolved.DirectoryPath, toolName);
        var runtimePath = ToolCache.ResolveToolPath(resolved.DirectoryPath, runtimeName);
        Chmod(toolPath, "+x");
        return new AppImageToolset(toolPath, runtimePath);
    }

    internal static byte[] DefaultIcon()
    {
        using var stream = typeof(AppImageToolset).Assembly
            .GetManifestResourceStream("DotNet.Bundler.AppImage.DefaultIcon")
            ?? throw new InvalidOperationException("Embedded default icon resource not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // netstandard2.0 lacks Unix file-mode APIs; chmod/ln are small, fixed
    // argument-shape helper calls (no shell, no user input in argv[0]).
    internal static void Chmod(string path, string mode) =>
        ToolRunner.RunSync("chmod", new[] { mode, path });

    internal static void Symlink(string target, string linkPath) =>
        ToolRunner.RunSync("ln", new[] { "-sfn", target, linkPath });

}

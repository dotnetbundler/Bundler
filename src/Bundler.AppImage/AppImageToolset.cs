using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    internal static AppImageToolset Resolve(
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

        var cache = cacheDirectory is { Length: > 0 }
            ? Path.GetFullPath(cacheDirectory)
            : Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
                "dotnet-bundler", "appimagetool");

        var (resource, hash) = hostArch == "x86_64"
            ? ("DotNet.Bundler.AppImage.Toolset.x86_64", ToolX64Sha256)
            : ("DotNet.Bundler.AppImage.Toolset.aarch64", ToolArm64Sha256);
        var toolPath = Materialize(resource, "appimagetool-" + hostArch + ".AppImage", hash, cache, executable: true);

        // This pinned appimagetool build does not embed a runtime: without
        // --runtime-file it downloads one at build time, so the target
        // runtime is always supplied from the bundle.
        var (runtimeResource, runtimeHash, runtimeName) = targetArchitecture switch
        {
            "x86_64" => ("DotNet.Bundler.AppImage.Runtime.x86_64", RuntimeX64Sha256, "runtime-x86_64"),
            "aarch64" => ("DotNet.Bundler.AppImage.Runtime.aarch64", RuntimeAarch64Sha256, "runtime-aarch64"),
            _ => throw new NotSupportedException(
                $"AppImage output for '{targetArchitecture}' needs a matching runtime file; " +
                "only x86_64 and aarch64 runtimes are bundled.")
        };
        var runtimePath = Materialize(runtimeResource, runtimeName, runtimeHash, cache, executable: false);
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

    private static string Materialize(
        string resourceName, string fileName, string expectedSha256, string cacheDirectory, bool executable)
    {
        var assembly = typeof(AppImageToolset).Assembly;
        using var resource = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"Embedded AppImage resource '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var actual = Sha256(memory.ToArray());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Embedded tool '{fileName}' failed its pinned SHA-256 check ({actual}).");
        }

        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, fileName);
        if (!File.Exists(destination) ||
            !string.Equals(Sha256(File.ReadAllBytes(destination)), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, memory.ToArray());
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // 并发构建同物化同一缓存件：另一进程先落位，丢弃副本再核哈希。
                File.Delete(temporary);
            }
            if (!string.Equals(Sha256(File.ReadAllBytes(destination)), expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Embedded tool '{fileName}' failed its pinned SHA-256 check after materialization.");
            }
        }
        if (executable)
        {
            Chmod(destination, "+x");
        }
        return destination;
    }

    // netstandard2.0 lacks Unix file-mode APIs; chmod/ln are small, fixed
    // argument-shape helper calls (no shell, no user input in argv[0]).
    internal static void Chmod(string path, string mode) =>
        RunHelper("chmod", mode, path);

    internal static void Symlink(string target, string linkPath) =>
        RunHelper("ln", "-sfn", target, linkPath);

    private static void RunHelper(string name, params string[] args)
    {
        var info = new ProcessStartInfo(name)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.Arguments += (info.Arguments.Length == 0 ? "" : " ") + "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start '{name}'.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{name}' exited with code {process.ExitCode}: {error.Trim()}");
        }
    }

    private static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}

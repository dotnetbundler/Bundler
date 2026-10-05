using System.Runtime.InteropServices;

namespace DotNet.Bundler.Core.Update;

/// <summary>
/// 更新引导件的随包注入：按 RID 在工具目录里选 per-RID Native AOT 件，
/// 找不到时降级 POSIX shell 实现——老宿主（裸 POSIX）天然走脚本件。
/// </summary>
public static class UpdateBootstrapper
{
    /// <summary>注入到载荷里的引导件文件名（Windows 宿主追加 .exe）。</summary>
    public static string FileNameFor(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? "bundler-updater.exe"
            : "bundler-updater";

    /// <summary>
    /// 把引导件复制进 <paramref name="targetDirectory"/>；返回目标文件名，未找到任何件时返回 null。
    /// 选取顺序：<c>&lt;dir&gt;/&lt;rid&gt;/bundler-updater[.exe]</c> → <c>&lt;dir&gt;/posix/bundler-updater.sh</c>。
    /// </summary>
    public static string? Inject(string targetDirectory, UpdateBundleConfiguration update,
        string runtimeIdentifier)
    {
        var fileName = FileNameFor(runtimeIdentifier);
        if (TryResolve(update, runtimeIdentifier, out var source))
        {
            var destination = Path.Combine(targetDirectory, fileName);
            File.Copy(source, destination, overwrite: true);
            if (source.EndsWith(".sh", StringComparison.Ordinal))
            {
                // POSIX 脚本件：改名进包时保留执行位。
                Chmod755(destination);
            }
            else
            {
                Chmod755(destination);
            }
            return fileName;
        }
        return null;
    }

    /// <summary>仅解析选件路径，不落盘——供校验与测试。</summary>
    public static bool TryResolve(UpdateBundleConfiguration update, string runtimeIdentifier,
        out string source)
    {
        // 约定目录：显式配置优先，否则探测本程序集旁的 updater/（MSBuild tasks 目录与 CLI 根同型；
        // 不能用 AppContext.BaseDirectory——MSBuild 任务进程里它是 SDK 宿主目录而非程序集目录）。
        var directory = update.BootstrapperDirectory;
        if (directory is not { Length: > 0 })
        {
            var probe = Path.GetDirectoryName(typeof(UpdateBootstrapper).Assembly.Location);
            if (probe is { Length: > 0 } && Directory.Exists(Path.Combine(probe, "updater")))
            {
                directory = Path.Combine(probe, "updater");
            }
        }
        if (directory is { Length: > 0 })
        {
            var perRid = Path.Combine(directory, runtimeIdentifier,
                FileNameFor(runtimeIdentifier));
            if (File.Exists(perRid))
            {
                source = perRid;
                return true;
            }
            var script = Path.Combine(directory, "posix", "bundler-updater.sh");
            if (File.Exists(script))
            {
                source = script;
                return true;
            }
        }
        source = "";
        return false;
    }

    // netstandard2.0 无 Unix mode API；Windows 上权限位由文件系统无关化、跳过。
    internal static void Chmod755(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        chmod(path, 0x1ED /* 0755 */);
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int chmod(string path, ushort mode);
}

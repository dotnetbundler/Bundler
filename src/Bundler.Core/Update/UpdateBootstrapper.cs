using System.Runtime.InteropServices;

namespace DotNet.Bundler.Core.Update;

/// <summary>
/// 更新引导件的随包注入：按 target 选 per-target Native AOT 件，非 Windows 宿主可降级 POSIX shell 件——
/// 老宿主（裸 POSIX）天然走脚本件。工具源二选一：显式 <see cref="UpdateBundleConfiguration.BootstrapperDirectory"/>
/// 工具目录（开发覆盖），或 Bundler.Core 程序集内嵌资源（打包/直引/NuGet 全形态可达）。
/// </summary>
public static class UpdateBootstrapper
{
    /// <summary>注入到载荷里的引导件文件名（Windows 宿主追加 .exe）。</summary>
    public static string FileNameFor(string target) =>
        target.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? "bundler-updater.exe"
            : "bundler-updater";

    /// <summary>
    /// 把引导件复制进 <paramref name="targetDirectory"/>；返回目标文件名，未找到任何件时返回 null。
    /// 选取顺序：<c>&lt;dir&gt;/&lt;target&gt;/bundler-updater[.exe]</c> →（非 win）<c>&lt;dir&gt;/posix/bundler-updater.sh</c>。
    /// </summary>
    public static string? Inject(string targetDirectory, UpdateBundleConfiguration update,
        string target)
    {
        var fileName = FileNameFor(target);
        if (TryResolve(update, target, out var source))
        {
            var destination = Path.Combine(targetDirectory, fileName);
            File.Copy(source, destination, overwrite: true);
            Chmod755(destination);
            return fileName;
        }
        return null;
    }

    /// <summary>仅解析选件路径，不落盘——供校验与测试（内嵌件解析为临时解出路径）。</summary>
    public static bool TryResolve(UpdateBundleConfiguration update, string target,
        out string source)
    {
        var fileName = FileNameFor(target);
        var isWindows = target.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        // Windows 宿主绝不能拿到 POSIX 脚本（.sh 改名 .exe 无法执行）——只认 per-target 二进制。
        if (update.BootstrapperDirectory is { Length: > 0 } directory)
        {
            var perTarget = Path.Combine(directory, target, fileName);
            if (File.Exists(perTarget))
            {
                source = perTarget;
                return true;
            }
            var script = Path.Combine(directory, "posix", "bundler-updater.sh");
            if (!isWindows && File.Exists(script))
            {
                source = script;
                return true;
            }
            source = "";
            return false;
        }
        // 内嵌资源兜底：updater/<target>/<name> → updater/posix/bundler-updater.sh，
        // 解到临时缓存目录供 File.Copy/归档条目引用。
        if (TryExtractEmbedded($"updater/{target}/{fileName}", out source))
        {
            return true;
        }
        if (!isWindows &&
            TryExtractEmbedded("updater/posix/bundler-updater.sh", out source))
        {
            return true;
        }
        source = "";
        return false;
    }

    // 引导件工具已内嵌进本程序集（Core csproj EmbeddedResource）——任何消费形态都不依赖盘符约定。
    private static bool TryExtractEmbedded(string resourceName, out string path)
    {
        using (var stream = typeof(UpdateBootstrapper).Assembly
                   .GetManifestResourceStream(resourceName))
        {
            if (stream is null)
            {
                path = "";
                return false;
            }
            // 按宿主用户隔离提取根：共享 /tmp 下同名路径会被别的本地用户预置/替换，
            // 解出的可执行体随后会被打进应用——目录 0700 且恒定覆盖写。
            // 并发打包同 uid 同路径恒定覆写会互踩：进程级唯一目录名规避（文件名不变）。
            var destination = Path.Combine(
                ExtractionRoot(), "." + Guid.NewGuid().ToString("N"),
                resourceName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = File.Create(destination))
            {
                stream.CopyTo(output);
            }
            path = destination;
            return true;
        }
    }

    private static string ExtractionRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bundler-updater-{UserScope()}");
        Directory.CreateDirectory(root);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            chmod(root, 0x1C0 /* 0700 */);
        }
        SweepStaleExtractions(root);
        return root;
    }

    // `.<guid>` 提取目录按进程唯一命名——打包进程死在半路时无清理方，残件
    // 在提取根下无限积累。每次新建前按 mtime 清扫 24h 前的同类目录；删不动
    // 的（被占用/权限）尽力跳过，全路径 best-effort 不挡打包。
    private static void SweepStaleExtractions(string root)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var directory in Directory.GetDirectories(root, ".*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }

    // Windows 临时目录本就按用户隔离；POSIX /tmp 共享——用 uid 划清属主。
    private static string UserScope()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var name = Environment.UserName;
            return string.IsNullOrEmpty(name) ? "user" : name;
        }
        return getuid().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "getuid", SetLastError = true)]
    private static extern uint getuid();

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

using System.Runtime.InteropServices;

namespace DotNet.Bundler.Core.Update;

/// <summary>
/// 更新引导件的随包注入：按 RID 选 per-RID Native AOT 件，非 Windows 宿主可降级 POSIX shell 件——
/// 老宿主（裸 POSIX）天然走脚本件。工具源二选一：显式 <see cref="UpdateBundleConfiguration.BootstrapperDirectory"/>
/// 工具目录（开发覆盖），或 Bundler.Core 程序集内嵌资源（打包/直引/NuGet 全形态可达）。
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
    /// 选取顺序：<c>&lt;dir&gt;/&lt;rid&gt;/bundler-updater[.exe]</c> →（非 win）<c>&lt;dir&gt;/posix/bundler-updater.sh</c>。
    /// </summary>
    public static string? Inject(string targetDirectory, UpdateBundleConfiguration update,
        string runtimeIdentifier)
    {
        var fileName = FileNameFor(runtimeIdentifier);
        if (TryResolve(update, runtimeIdentifier, out var source))
        {
            var destination = Path.Combine(targetDirectory, fileName);
            File.Copy(source, destination, overwrite: true);
            Chmod755(destination);
            return fileName;
        }
        return null;
    }

    /// <summary>仅解析选件路径，不落盘——供校验与测试（内嵌件解析为临时解出路径）。</summary>
    public static bool TryResolve(UpdateBundleConfiguration update, string runtimeIdentifier,
        out string source)
    {
        var fileName = FileNameFor(runtimeIdentifier);
        var isWindows = runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        // Windows 宿主绝不能拿到 POSIX 脚本（.sh 改名 .exe 无法执行）——只认 per-RID 二进制。
        if (update.BootstrapperDirectory is { Length: > 0 } directory)
        {
            var perRid = Path.Combine(directory, runtimeIdentifier, fileName);
            if (File.Exists(perRid))
            {
                source = perRid;
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
        // 内嵌资源兜底：updater/<rid>/<name> → updater/posix/bundler-updater.sh，
        // 解到临时缓存目录供 File.Copy/归档条目引用。
        if (TryExtractEmbedded($"updater/{runtimeIdentifier}/{fileName}", out source))
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
            var destination = Path.Combine(
                ExtractionRoot(), resourceName.Replace('/', Path.DirectorySeparatorChar));
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
        return root;
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

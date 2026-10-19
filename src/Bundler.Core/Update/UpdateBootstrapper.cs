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
    /// 把引导件复制进 <paramref name="targetDirectory"/>；返回目标文件名。
    /// 更新面已声明而引导件无件可选 = 死更新链——全后端统一走
    /// <see cref="ResolveOrThrow"/> 诊断抛出，不容忍静默缺件。
    /// </summary>
    public static string InjectOrThrow(string targetDirectory, UpdateBundleConfiguration update,
        string target)
    {
        var fileName = FileNameFor(target);
        var source = ResolveOrThrow(update, target);
        var destination = Path.Combine(targetDirectory, fileName);
        File.Copy(source, destination, overwrite: true);
        Chmod755(destination);
        return fileName;
    }

    /// <summary>是否配置了显式引导件目录（探测谓词的唯一出处，诊断文案同用）。</summary>
    internal static bool HasExplicitDirectory(UpdateBundleConfiguration update) =>
        update.BootstrapperDirectory is { Length: > 0 };

    /// <summary>
    /// 仅解析选件路径、不复制进载荷——供校验与测试。注意内嵌资源件会先流式解出到
    /// 进程级临时缓存目录再回路径：GetManifestResourceStream 没有"仅探存在不解出"
    /// 的轻路径（记账：现调用方全是"解出即用"，不另开免落盘探针链防双链漂移）。
    /// </summary>
    public static bool TryResolve(UpdateBundleConfiguration update, string target,
        out string source)
    {
        // target 直拼工具目录段：分隔符/上跳/根路径会把解析带出自有目录，
        // 把任意宿主文件当引导件拷进载荷——canonical target 词只含字母数字与 '-'，
        // 含 '.'/'/'/'\' 或根路径即拒。
        if (string.IsNullOrEmpty(target) ||
            target.IndexOfAny(new[] { '/', '\\', '.' }) >= 0 ||
            Path.IsPathRooted(target))
        {
            throw new ArgumentException(
                $"The target '{target}' must be a canonical target token such as 'linux-x86_64'.",
                nameof(target));
        }
        var fileName = FileNameFor(target);
        var isWindows = target.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        // Windows 宿主绝不能拿到 POSIX 脚本（.sh 改名 .exe 无法执行）——只认 per-target 二进制。
        if (HasExplicitDirectory(update))
        {
            var directory = update.BootstrapperDirectory!;
            var perTarget = Path.Combine(directory, target, fileName);
            if (File.Exists(perTarget))
            {
                source = perTarget;
                return true;
            }
            // linux glibc 目标收敛到 musl 静态件（static-pie 双宿主通吃）；
            // 字面 per-target 命中优先——显式目录仍允许开发覆盖自有 glibc 件。
            if (MuslSibling(target) is { } muslDir &&
                File.Exists(Path.Combine(directory, muslDir, fileName)))
            {
                source = Path.Combine(directory, muslDir, fileName);
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
        if (MuslSibling(target) is { } muslResource &&
            TryExtractEmbedded($"updater/{muslResource}/{fileName}", out source))
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

    /// <summary>
    /// <see cref="TryResolve"/> 的强制变体：更新面已声明而引导件无件可选 = 死更新链——
    /// 静默跳过会产出装得出但永远换不了包的载荷。抛出诊断列出全部探测位。
    /// </summary>
    public static string ResolveOrThrow(UpdateBundleConfiguration update, string target)
    {
        if (TryResolve(update, target, out var source))
        {
            return source;
        }
        var fileName = FileNameFor(target);
        var probe = HasExplicitDirectory(update)
            ? $"in '{update.BootstrapperDirectory}': {target}/{fileName} → (linux glibc 目标) linux-musl-<arch>/{fileName} → (非 win) posix/bundler-updater.sh"
            : $"in Bundler.Core embedded resources: updater/{target}/{fileName} → (linux glibc 目标) updater/linux-musl-<arch>/{fileName} → (非 win) updater/posix/bundler-updater.sh — or configure update.bootstrapperDirectory";
        throw new InvalidOperationException(
            $"BundlerUpdate enabled but no bootstrapper found for '{target}' — probed {probe}; none matched.");
    }

    // linux 非 musl 目标的 musl 姊妹目录名（linux-x86_64 → linux-musl-x86_64）；
    // musl 目标本身、windows/macos 目标返回 null。
    private static string? MuslSibling(string target)
    {
        const string linuxPrefix = "linux-";
        const string muslPrefix = "linux-musl-";
        return target.StartsWith(linuxPrefix, StringComparison.OrdinalIgnoreCase) &&
               !target.StartsWith(muslPrefix, StringComparison.OrdinalIgnoreCase)
            ? muslPrefix + target.Substring(linuxPrefix.Length).ToLowerInvariant()
            : null;
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
        // 共享 /tmp 下同名路径可被其他本地用户预置（squatting，含 symlink 指向他处）：
        // 目录必须 chmod 0700 成功且属主=当前 uid 才可信——不满足即落到不可预测
        // GUID 根（攻击者无法预置不存在的名字），同样先建后验。
        if (!TryPreparePrivateRoot(root))
        {
            root = Path.Combine(
                Path.GetTempPath(), $"bundler-updater-{UserScope()}-{Guid.NewGuid():N}");
            if (!TryPreparePrivateRoot(root))
            {
                throw new InvalidOperationException(
                    $"Could not create a private updater extraction directory under '{Path.GetTempPath()}'.");
            }
        }
        SweepStaleExtractions(root);
        return root;
    }

    private static bool TryPreparePrivateRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return true;
            }
            // stat 跟随 symlink：链接指向他属目录时属主检查照常在目标上失败；
            // chmod 非属主 EPERM 同样落到不信任分支。
            return chmod(root, 0x1C0 /* 0700 */) == 0 &&
                UnixFileTypes.TryGetOwnerUid(root, out var owner) &&
                owner == getuid();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // `.<guid>` 提取目录按进程唯一命名——打包进程死在半路时无清理方，残件
    // 在提取根下无限积累。每次新建前按 mtime 清扫 24h 前的同类目录；删不动
    // 的（被占用/权限）尽力跳过，全路径 best-effort 不挡打包。
    // squatting 降级根 `bundler-updater-<uid>-<guid>` 同样在临时根下按本 uid
    // 前缀一并清扫——持续 squatting 不该让降级根永久积累。
    private static void SweepStaleExtractions(string root)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        try
        {
            var scopePrefix = $"bundler-updater-{UserScope()}-";
            foreach (var fallback in Directory.GetDirectories(
                         Path.GetTempPath(), scopePrefix + "*"))
            {
                TryDeleteIfStale(fallback, cutoff);
            }
        }
        catch (Exception)
        {
        }
        try
        {
            foreach (var directory in Directory.GetDirectories(root, ".*"))
            {
                TryDeleteIfStale(directory, cutoff);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteIfStale(string directory, DateTime cutoff)
    {
        try
        {
            if (FileTree.IsStale(directory, cutoff))
            {
                Directory.Delete(directory, recursive: true);
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
    // 返回值不可吞：chmod 失败=注入件缺执行位=死更新链（applier 拉不起引导件），
    // 把 errno 带进诊断而非静默产出一个跑不动的载荷。
    internal static void Chmod755(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        if (chmod(path, 0x1ED /* 0755 */) != 0)
        {
            throw new InvalidOperationException(
                $"chmod 0755 failed for '{path}' (errno {Marshal.GetLastWin32Error()}) — " +
                "the injected bootstrapper would not be executable.");
        }
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int chmod(string path, ushort mode);
}

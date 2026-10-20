namespace DotNet.Bundler.Core;

/// <summary>
/// CLI 与 MSBuild 入口共享的打包编排：planner 统一预检 → 逐格式扇出校验 →
/// 逐格式容错构建 → UPDATE 发射。入口只提供各自的单格式配置构造、
/// 门面工厂与致命异常判据，编排语义两侧不再各写一遍。
/// </summary>
internal static class BundleOrchestrator
{
    /// <summary>
    /// 预检 + 扇出：先对整个配置跑 planner 聚错，再逐格式校验；宿主门禁
    /// （PlatformNotSupportedException）格式不进预检、以 Bundler=null 入
    /// pending 留构建趟容错。返回聚合校验问题（非空即终止、不再构建）。
    /// </summary>
    internal static List<(string Path, string Message)> PlanFanout(
        BundleConfiguration configuration,
        IReadOnlyList<PackageFormat> formats,
        Func<PackageFormat, BundleConfiguration> singleFormat,
        Func<PackageFormat, IFormatBundler> bundlerFactory,
        List<FormatFanoutItem> pending)
    {
        var issues = new List<(string Path, string Message)>();
        try
        {
            BundlePlanner.Create(configuration);
        }
        catch (BundleValidationException validation)
        {
            // 共享层错误收集后继续跑各格式预检，一次报完。
            issues.AddRange(validation.Issues.Select(issue => (issue.Path, issue.Message)));
        }
        foreach (var format in formats)
        {
            var single = singleFormat(format);
            try
            {
                var bundler = bundlerFactory(format);
                bundler.Validate(single);
                pending.Add(new FormatFanoutItem { Format = format, Configuration = single, Bundler = bundler });
            }
            catch (PlatformNotSupportedException)
            {
                pending.Add(new FormatFanoutItem { Format = format, Configuration = single });
            }
            catch (BundleValidationException validation)
            {
                issues.AddRange(validation.Issues.Select(issue => (issue.Path, issue.Message)));
            }
            catch (Exception exception)
            {
                issues.Add((DisplayName(format), exception.Message));
            }
        }
        return issues;
    }

    /// <summary>
    /// 逐格式容错构建：单格式失败不拖累其余格式，失败记入返回值由入口汇总；
    /// isFatal 判为真的异常（校验拒绝/用法层错误等）不容错、直接向上传播。
    /// </summary>
    internal static async Task<List<(PackageFormat Format, string Reason)>> BuildAllAsync(
        IReadOnlyList<FormatFanoutItem> pending,
        Func<PackageFormat, IFormatBundler> bundlerFactory,
        List<BundleArtifact> artifacts,
        IBundleLogger logger,
        Func<Exception, bool>? isFatal = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<(PackageFormat Format, string Reason)>();
        foreach (var item in pending)
        {
            try
            {
                var produced = await (item.Bundler ?? bundlerFactory(item.Format))
                    .BuildAsync(item.Configuration, cancellationToken).ConfigureAwait(false);
                artifacts.AddRange(produced);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failures.Add((item.Format, exception.Message));
                logger.Log(BundleLogLevel.Warning,
                    $"format '{DisplayName(item.Format)}' failed: {exception.Message}");
            }
        }
        return failures;

        bool IsFatal(Exception exception) =>
            exception is BundleValidationException || (isFatal?.Invoke(exception) ?? false);
    }

    /// <summary>
    /// UPDATE 发射尾段：无产出时发警告不发清单；有产出但全不适配更新时
    /// 另发"清单空"警告。返回新增产物路径（.sig 旁车与通道清单）。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> EmitUpdateAsync(
        BundleConfiguration configuration,
        IReadOnlyList<BundleArtifact> artifacts,
        Func<BundleConfiguration, IReadOnlyList<BundleArtifact>, CancellationToken, Task<IReadOnlyList<string>>> emit,
        Func<PackageFormat, bool> isUpdateAdapted,
        IBundleLogger logger,
        CancellationToken cancellationToken = default)
    {
        var produced = artifacts.Count == 0
            ? Array.Empty<string>()
            : await emit(configuration, artifacts, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
        {
            logger.Log(BundleLogLevel.Warning,
                "update is enabled but no artifacts were produced; " +
                "no update feed was emitted — installed apps keep polling the last published feed.");
        }
        else if (!artifacts.Any(a => isUpdateAdapted(a.Format)))
        {
            logger.Log(BundleLogLevel.Warning,
                "update is enabled but no produced format is update-adapted; " +
                "the emitted feed carries no artifacts — clients read it as 'no update'.");
        }
        return produced;
    }

    /// <summary>格式显示名（targz 为唯一非 ToString 小写特例），警告与 JSON 输出共用。</summary>
    internal static string DisplayName(PackageFormat format) => format switch
    {
        PackageFormat.TarGz => "targz",
        _ => format.ToString().ToLowerInvariant()
    };
}

/// <summary>扇出预检产物：单格式配置 + 预创建门面（null 表示宿主门禁留待构建趟容错）。</summary>
internal sealed class FormatFanoutItem
{
    internal PackageFormat Format { get; init; }
    internal BundleConfiguration Configuration { get; init; } = null!;
    internal IFormatBundler? Bundler { get; init; }
}

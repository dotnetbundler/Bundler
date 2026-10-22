using System.Reflection;
using DotNet.Bundler;
using DotNet.Bundler.MSBuild;

namespace Bundler.UnitTests.MsBuild;

/// <summary>
/// 旋钮覆盖不变量：把「配置模型字段 ↔ MSBuild 任务属性 ↔ buildTransitive
/// 参数接线」三层钉成机械化断言——新增配置字段漏接任务属性、新增任务属性
/// 漏接 targets、targets 参数拼错名，都在这里当场炸，不靠人肉对表。
/// </summary>
public sealed class KnobCoverageTests
{
    private static readonly IReadOnlyList<PropertyInfo> TaskProperties =
        typeof(BundleDesktopApplication)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(prop => prop.CanWrite && prop.SetMethod is { IsPublic: true })
            .ToArray();

    // 每个格式的任务属性前缀与其 BuildFormatConfiguration 返回类型。
    private static readonly (PackageFormat Format, string Section)[] Sections =
    [
        (PackageFormat.Nsis, "nsis"),
        (PackageFormat.Msi, "msi"),
        (PackageFormat.App, "app"),
        (PackageFormat.Dmg, "dmg"),
        (PackageFormat.Pkg, "pkg"),
        (PackageFormat.Deb, "deb"),
        (PackageFormat.Rpm, "rpm"),
        (PackageFormat.AppImage, "appimage"),
        (PackageFormat.AlpineApk, "alpineapk"),
        (PackageFormat.Zip, "archive")
    ];

    // 任务属性名与配置叶名后缀匹配不上的语义映射——例外在这里显式登记，
    // 新例外必须带同样明确的旋钮语义（不是漏接的借口）。
    private static readonly Dictionary<string, string> LeafExceptions = new(StringComparer.Ordinal)
    {
        ["app.bundleDisplayName"] = "MacAppDisplayName",
        ["app.signing.temporaryCertificatePassword"] = "MacAppTemporaryCertificatePasswordEnvironmentVariable",
        ["app.signing.keychainProfile"] = "MacAppNotaryProfile",
        ["app.signing.applePassword"] = "MacAppApplePasswordEnvironmentVariable",
        ["dmg.signing.identity"] = "MacDmgSignIdentity",
        ["dmg.signing.temporaryCertificatePassword"] = "MacDmgTemporaryCertificatePasswordEnvironmentVariable",
        ["pkg.signing.identity"] = "MacPkgSignIdentity",
        ["pkg.signing.temporaryCertificatePassword"] = "MacPkgTemporaryCertificatePasswordEnvironmentVariable",
        ["pkg.signing.keychainProfile"] = "MacPkgNotaryProfile",
        ["pkg.signing.applePassword"] = "MacPkgApplePasswordEnvironmentVariable",
        ["rpm.signing.passphrase"] = "RpmSigningKeyPassphraseEnvironmentVariable",
        ["appimage.signing.passphrase"] = "AppImageSigningKeyPassphraseEnvironmentVariable",
        ["alpineapk.signing.passphrase"] = "AlpineApkSigningKeyPassphraseEnvironmentVariable",
        ["msi.localeFiles"] = "WixLanguageFiles",
        ["nsis.customLanguageFiles"] = "NsisLanguageFiles"
    };

    // 不对应任何任务属性的配置字段——item 级元数据映射或后端内部字段，
    // 同样显式登记。
    private static readonly HashSet<string> UnmappedLeaves = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void TaskProperties_AllReachableFromTargets()
    {
        // 每个公开可写任务属性都必须出现在 targets 的任务调用参数里（`Prop="`）。
        var targets = File.ReadAllText(TargetsPath());
        var invocationStart = targets.IndexOf("DotNet.Bundler.MSBuild.BundleDesktopApplication", StringComparison.Ordinal);
        Assert.True(invocationStart >= 0, "targets 里找不到 BundleDesktopApplication 调用");
        var invocation = targets[invocationStart..];
        foreach (var prop in TaskProperties)
        {
            Assert.Contains(prop.Name + "=\"", invocation);
        }
    }

    [Fact]
    public void TargetsInvocation_OnlyKnownProperties()
    {
        // 反向：targets 传给任务的每个参数名都必须是真实任务属性——
        // 属性改名而 targets 漏改时 MSBuild 以「未知参数」直接拒。
        var targets = File.ReadAllText(TargetsPath());
        var invocationStart = targets.IndexOf(
            "<DotNet.Bundler.MSBuild.BundleDesktopApplication", StringComparison.Ordinal);
        Assert.True(invocationStart >= 0);
        // 只取开始标签自身（含 <Output TaskParameter=.../> 子元素会误抓非属性名）。
        var invocationEnd = targets.IndexOf('>', invocationStart);
        Assert.True(invocationEnd > invocationStart);
        var invocation = targets[invocationStart..invocationEnd];
        var known = TaskProperties.Select(prop => prop.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = System.Text.RegularExpressions.Regex
            .Matches(invocation, @"\s(\w+)=""")
            .Select(match => match.Groups[1].Value)
            .Where(name => !known.Contains(name))
            .ToArray();
        Assert.Empty(unknown);
    }

    [Fact]
    public void SecretProperties_OnlyEnvironmentVariableIndirection()
    {
        // 秘密旋钮只允许 env 名间址——值形态的 Password/Passphrase 属性一个不许有。
        var plain = TaskProperties
            .Select(prop => prop.Name)
            .Where(name =>
                (name.EndsWith("Password", StringComparison.Ordinal) ||
                 name.EndsWith("Passphrase", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(plain);
    }

    [Fact]
    public void ConfigurationLeaves_AllMappedToTaskProperties()
    {
        var task = new BundleDesktopApplication
        {
            ProductName = "p",
            Identifier = "id",
            Version = "1.0",
            BundlerTarget = "linux-x86_64",
            InputDirectory = "in",
            OutputDirectory = "out",
            MainExecutable = "m",
            Formats = "deb"
        };
        var taskNames = TaskProperties.Select(prop => prop.Name).ToArray();
        var missing = new List<string>();
        var sections = Sections
            .Select(pair => (pair.Section, task.BuildFormatConfiguration(pair.Format)!.GetType()))
            .Append(("update", typeof(DotNet.Bundler.UpdateBundleConfiguration)));
        foreach (var (section, configType) in sections)
        {
            foreach (var (path, leaf) in Leaves(configType))
            {
                var fullPath = section + "." + path;
                if (UnmappedLeaves.Contains(fullPath))
                {
                    continue;
                }
                if (LeafExceptions.TryGetValue(fullPath, out var named))
                {
                    Assert.Contains(taskNames, name =>
                        string.Equals(name, named, StringComparison.Ordinal));
                    continue;
                }
                if (!taskNames.Any(name =>
                        name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase)))
                {
                    missing.Add(fullPath);
                }
            }
        }
        Assert.Empty(missing);
    }

    // 叶枚举：递归进嵌套单例配置对象（signing/layout/shortcuts 等），集合与
    // 字典按叶处理（其元素字段走 item 元数据，不是顶层旋钮）。产出 (path, leaf)
    // ——path 用于例外表查准，leaf 用于任务属性名后缀匹配。
    private static IEnumerable<(string Path, string Leaf)> Leaves(Type type, string prefix = "")
    {
        foreach (var prop in type.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var name = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            var propType = prop.PropertyType;
            var nested = Nullable.GetUnderlyingType(propType) ?? propType;
            if (nested.IsClass && nested != typeof(string) &&
                nested.FullName?.StartsWith("DotNet.Bundler.", StringComparison.Ordinal) == true &&
                !typeof(System.Collections.IEnumerable).IsAssignableFrom(nested))
            {
                foreach (var leaf in Leaves(nested, prefix + name + "."))
                {
                    yield return leaf;
                }
                continue;
            }
            yield return (prefix + name, name);
        }
    }

    private static string TargetsPath() =>
        Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive",
            "DotNet.Bundler.MSBuild.targets");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bundler.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
    }
}

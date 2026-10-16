using DotNet.Bundler.MSBuild;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Bundler.UnitTests.MsBuild;

/// <summary>
/// MergeUniversalPayload 任务的输入契约：SourceDirectories/OutputDirectory 必填语义、
/// 合并失败的错误路径（返回 false + LogError）、MergedDirectory 输出与参考目录旋钮。
/// 不经 msbuild 宿主，直接实例化断言。
/// </summary>
public sealed class MergeUniversalPayloadTests : TempBase
{
    private string Dir(string name, params (string File, string Content)[] files)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(path, file), content);
        }
        return path;
    }

    private static MergeUniversalPayload Task(
        IBuildEngine engine, string output, params string[] sources) => new()
    {
        BuildEngine = engine,
        SourceDirectories = sources.Select(s => new TaskItem(s)).ToArray(),
        OutputDirectory = output
    };

    [Fact]
    public void Execute_TwoInputs_MergesAndSetsMergedDirectory()
    {
        var a = Dir("a", ("shared.txt", "same"), ("only-a.txt", "a"));
        var b = Dir("b", ("shared.txt", "same"), ("only-b.txt", "b"));
        var engine = new FakeBuildEngine();
        var output = Path.Combine(Root, "merged");

        var task = Task(engine, output, a, b);
        Assert.True(task.Execute());

        Assert.Equal(Path.GetFullPath(output), task.MergedDirectory);
        Assert.Equal("same", File.ReadAllText(Path.Combine(output, "shared.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(output, "only-a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(output, "only-b.txt")));
        Assert.Contains(engine.Messages, m => m.Contains("Merged universal payload"));
        Assert.Empty(engine.Errors);
    }

    [Fact]
    public void Execute_SingleSource_Fails()
    {
        var engine = new FakeBuildEngine();
        var task = Task(engine, Path.Combine(Root, "merged"), Dir("solo"));

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, e => e.Contains("at least two"));
    }

    [Fact]
    public void Execute_MissingSource_Fails()
    {
        var engine = new FakeBuildEngine();
        var task = Task(engine, Path.Combine(Root, "merged"),
            Dir("a"), Path.Combine(Root, "ghost"));

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public void Execute_NonEmptyOutput_Fails()
    {
        var output = Dir("merged", ("stale.txt", "old"));
        var engine = new FakeBuildEngine();
        var task = Task(engine, output, Dir("a"), Dir("b"));

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, e => e.Contains("not empty"));
    }

    [Fact]
    public void Execute_ConflictingNonMachO_NoReference_Fails()
    {
        // 逐 RID 发布踩出的真场景：同名非 Mach-O 件两侧不同且无参考 → 硬报错不静默取边。
        var a = Dir("a", ("conf.dat", "aaa"));
        var b = Dir("b", ("conf.dat", "bbb"));
        var engine = new FakeBuildEngine();
        var task = Task(engine, Path.Combine(Root, "merged"), a, b);

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, e =>
            e.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Execute_ConflictingNonMachO_ReferenceSupplied_UsesReference()
    {
        var a = Dir("a", ("conf.dat", "aaa"));
        var b = Dir("b", ("conf.dat", "bbb"));
        var reference = Dir("ref", ("conf.dat", "neutral"), ("ref-only.txt", "r"));
        var engine = new FakeBuildEngine();
        var output = Path.Combine(Root, "merged");
        var task = Task(engine, output, a, b);
        task.ReferenceDirectory = reference;

        Assert.True(task.Execute());
        Assert.Equal("neutral", File.ReadAllText(Path.Combine(output, "conf.dat")));
        // 参考发布独有的件也进并集——不被静默丢。
        Assert.Equal("r", File.ReadAllText(Path.Combine(output, "ref-only.txt")));
    }

    /// <summary>最小 IBuildEngine：捕获消息供断言，不跑真构建。</summary>
    private sealed class FakeBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public List<string> Messages { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e.Message ?? "");
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames,
            System.Collections.IDictionary globalProperties,
            System.Collections.IDictionary targetOutputs) => true;
    }
}

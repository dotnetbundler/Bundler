using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class BundlePipelineTests : TempBase
{
    private sealed class FakeBackend(
        TargetOperatingSystem os, PackageFormat format, LibC? libc = null,
        Func<BundleBuildContext, IReadOnlyList<BundleArtifact>>? produce = null) : IBundleBackend
    {
        public PackageFormat Format => format;
        public TargetOperatingSystem OperatingSystem => os;
        public LibC? RequiredLibC => libc;

        public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
            BundleBuildContext context, CancellationToken cancellationToken) =>
            Task.FromResult(produce?.Invoke(context) ??
                (IReadOnlyList<BundleArtifact>)[new BundleArtifact(
                    format, context.Item.Target.Target,
                    Path.Combine(context.Item.OutputDirectory, "out.bin"))]);
    }

    private sealed class RecordingLogger : IBundleLogger
    {
        public List<(BundleLogLevel Level, string Message)> Entries { get; } = [];
        public void Log(BundleLogLevel level, string message) => Entries.Add((level, message));
    }

    private BundleConfiguration ConfigFor(string target, PackageFormat format, string output)
    {
        var input = Temp("bundler-pipe-in");
        var executableName = target.StartsWith("windows", StringComparison.Ordinal)
            ? "Sample.exe" : "Sample";
        File.WriteAllText(Path.Combine(input.FullName, executableName), "x");
        return new BundleConfiguration
        {
            ProductName = "Sample", Identifier = "com.example.sample", Version = "1.0.0",
            OutputDirectory = output,
            Targets = [new BundleTargetConfiguration
            {
                Target = target, InputDirectory = input.FullName, Formats = [format]
            }]
        };
    }

    [Fact]
    public async Task Build_ProducesArtifacts_AndCleansWorkDirectory()
    {
        var output = Temp("bundler-pipe-out");
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context =>
            {
                File.WriteAllText(Path.Combine(context.WorkDirectory, "scratch"), "w");
                var artifact = Path.Combine(context.Item.OutputDirectory, "a.deb");
                File.WriteAllText(artifact, "pkg");
                return [new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, artifact)];
            });
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb,
            Path.Combine(output.FullName, "artifacts"));

        var artifacts = await pipeline.BuildAsync(config, TestContext.Current.CancellationToken);

        Assert.Single(artifacts);
        Assert.EndsWith("a.deb", artifacts[0].Path);
        // .bundler-work 脚手架逐层回收
        Assert.False(Directory.Exists(Path.Combine(config.OutputDirectory, ".bundler-work")));
    }

    [Fact]
    public async Task Build_NoBackend_ThrowsNotSupported()
    {
        var output = Temp("bundler-pipe-out");
        var pipeline = new BundlePipeline([]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.BuildAsync(config, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_DuplicateSameLibC_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new BundlePipeline(
        [
            new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb, LibC.Musl),
            new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb, LibC.Musl)
        ]));
    }

    [Fact]
    public async Task Build_LibCSpecificBackend_PreferredOverGeneric()
    {
        var output = Temp("bundler-pipe-out");
        var used = new List<string>();
        IReadOnlyList<BundleArtifact> Make(BundleBuildContext context, string tag)
        {
            used.Add(tag);
            var path = Path.Combine(context.Item.OutputDirectory, tag + ".apk");
            File.WriteAllText(path, "x");
            return [new BundleArtifact(PackageFormat.AlpineApk, context.Item.Target.Target, path)];
        }

        var pipeline = new BundlePipeline(
        [
            new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.AlpineApk, null,
                c => Make(c, "generic")),
            new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.AlpineApk, LibC.Musl,
                c => Make(c, "musl"))
        ]);
        var config = ConfigFor("linux-musl-x86_64", PackageFormat.AlpineApk, output.FullName);

        await pipeline.BuildAsync(config, TestContext.Current.CancellationToken);
        Assert.Equal(["musl"], used);
    }

    [Fact]
    public async Task Build_BackendReturnsNull_ContractViolation()
    {
        var output = Temp("bundler-pipe-out");
        var backend = new NullReturningBackend();
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.BuildAsync(config, TestContext.Current.CancellationToken));
    }

    private sealed class NullReturningBackend : IBundleBackend
    {
        public PackageFormat Format => PackageFormat.Deb;
        public TargetOperatingSystem OperatingSystem => TargetOperatingSystem.Linux;
        public LibC? RequiredLibC => null;

        public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
            BundleBuildContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return null!;
        }
    }

    [Fact]
    public async Task Build_BackendReturnsMissingPath_ContractViolation()
    {
        var output = Temp("bundler-pipe-out");
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context => [new BundleArtifact(
                PackageFormat.Deb, context.Item.Target.Target,
                Path.Combine(context.Item.OutputDirectory, "missing.deb"))]);
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.BuildAsync(config, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Build_DuplicateArtifactPaths_Rejected()
    {
        var output = Temp("bundler-pipe-out");
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context =>
            {
                var path = Path.Combine(context.Item.OutputDirectory, "same.deb");
                File.WriteAllText(path, "x");
                return
                [
                    new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, path),
                    new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, path)
                ];
            });
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.BuildAsync(config, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Build_Cancellation_Propagates()
    {
        var output = Temp("bundler-pipe-out");
        var pipeline = new BundlePipeline(
            [new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb)]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.BuildAsync(config, cts.Token));
    }

    [Fact]
    public async Task Build_ScaffoldResidueAtTargetLayer_StopsReclaim()
    {
        var output = Temp("bundler-pipe-out");
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context =>
            {
                // 工作目录是 <out>/.bundler-work/<target>/<fmt>/<guid>：
                // 残留写进 <target> 层——<fmt> 空壳照收，<target> 起须停在非空层。
                var residue = Path.GetFullPath(
                    Path.Combine(context.WorkDirectory, "..", "..", "residue.txt"));
                File.WriteAllText(residue, "leftover");
                var artifact = Path.Combine(context.Item.OutputDirectory, "a.deb");
                File.WriteAllText(artifact, "pkg");
                return [new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, artifact)];
            });
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);
        var sibling = Path.Combine(output.FullName, "sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "x");

        await pipeline.BuildAsync(config, TestContext.Current.CancellationToken);

        var scaffoldRoot = Path.Combine(output.FullName, ".bundler-work");
        // 非空层挡住回收：残留与 .bundler-work 根都还在……
        Assert.True(File.Exists(Path.Combine(scaffoldRoot, "linux-x86_64", "residue.txt")));
        Assert.True(Directory.Exists(scaffoldRoot));
        // ……但非空层以下的空壳（<fmt>）仍被回收；
        Assert.False(Directory.Exists(Path.Combine(scaffoldRoot, "linux-x86_64", "deb")));
        // ……且不越界：.bundler-work 之外的兄弟与产物原样未动。
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(output.FullName, "a.deb")));
    }

    [Fact]
    public async Task Build_ScaffoldRootResidue_KeepsRootReclaimsInnerShells()
    {
        var output = Temp("bundler-pipe-out");
        // 上一轮构建留下的 .bundler-work 根层残件——本轮构建不该顺手清掉它。
        var scaffoldRoot = Path.Combine(output.FullName, ".bundler-work");
        Directory.CreateDirectory(scaffoldRoot);
        var stale = Path.Combine(scaffoldRoot, "stale.txt");
        File.WriteAllText(stale, "from a previous run");
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context =>
            {
                var artifact = Path.Combine(context.Item.OutputDirectory, "a.deb");
                File.WriteAllText(artifact, "pkg");
                return [new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, artifact)];
            });
        var pipeline = new BundlePipeline([backend]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await pipeline.BuildAsync(config, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(stale));
        // 本轮脚手架的 <target>/<fmt> 空壳层仍被回收。
        Assert.False(Directory.Exists(Path.Combine(scaffoldRoot, "linux-x86_64")));
        Assert.True(File.Exists(Path.Combine(output.FullName, "a.deb")));
    }

    [Fact]
    public async Task Build_Logs_BuildAndCreatedSequence()
    {
        var output = Temp("bundler-pipe-out");
        var logger = new RecordingLogger();
        var backend = new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb,
            produce: context =>
            {
                var path = Path.Combine(context.Item.OutputDirectory, "a.deb");
                File.WriteAllText(path, "x");
                return [new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target, path)];
            });
        var pipeline = new BundlePipeline([backend], logger);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);

        await pipeline.BuildAsync(config, TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, e => e.Message.Contains("Building Deb"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Created"));
    }
}

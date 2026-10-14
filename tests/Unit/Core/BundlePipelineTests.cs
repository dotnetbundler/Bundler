using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class BundlePipelineTests
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

    private static BundleConfiguration ConfigFor(string target, PackageFormat format, string output)
    {
        var input = Directory.CreateTempSubdirectory("bundler-pipe-in");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
        var pipeline = new BundlePipeline(
            [new FakeBackend(TargetOperatingSystem.Linux, PackageFormat.Deb)]);
        var config = ConfigFor("linux-x86_64", PackageFormat.Deb, output.FullName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.BuildAsync(config, cts.Token));
    }

    [Fact]
    public async Task Build_Logs_BuildAndCreatedSequence()
    {
        var output = Directory.CreateTempSubdirectory("bundler-pipe-out");
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

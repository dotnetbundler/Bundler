// MSBuild 真构建腿：dotnet msbuild 子进程驱动真实 BundlerBundle——
// 原 CliTests.MsBuildDefaultsInferFormatsIdentifierAndVersion（直接 Process.Start dotnet，
// Unit 层零子进程规则不收）。断言 formats/identifier/version 缺省推导与
// eval 期 BundlerTarget 可见性、csproj 体内后设 RID 的 canonical 重算。
using System.Runtime.InteropServices;

public sealed class MsBuildIntegrationFixture : IDisposable
{
    // 惰性创建：宿主门禁在各用例体内 Skip，之后才允许占锁建目录。
    private readonly Lazy<IntegrationWorkspace> _ws = new(() => IntegrationWorkspace.Create(
        "msbuild-integration", "BundlerMsBuildIntegration"));

    public IntegrationWorkspace Ws => _ws.Value;

    public void Dispose()
    {
        if (_ws.IsValueCreated)
        {
            _ws.Value.Dispose();
        }
    }
}

public sealed class MsBuildIntegrationTests : IClassFixture<MsBuildIntegrationFixture>
{
    private IntegrationWorkspace Ws => _fixture.Ws;
    private readonly MsBuildIntegrationFixture _fixture;

    public MsBuildIntegrationTests(MsBuildIntegrationFixture fixture) => _fixture = fixture;

    // 宿主推导目标词：与 CliConfig.HostTarget 同一合同（linux 腿断言用，独立复算防循环遮蔽）。
    static string LinuxHostTarget =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-aarch64" : "linux-x86_64";

    [Fact]
    public void MsBuildDefaultsInferFormatsIdentifierAndVersion()
    {
        // deb 推导只在 glibc 目标词下成立——musl 宿主推导出 linux-musl-* → alpineapk。
        Assert.SkipUnless(TestPlatform.IsLinux && !TestPlatform.IsMusl,
            "glibc linux host only (asserts deb derivation).");
        ExternalTools.Require("dotnet");
        var repo = RepositoryLayout.Root;
        var taskDll = Path.Combine(repo,
            "src", "Bundler.MSBuild", "bin", "Release", "netstandard2.0", "DotNet.Bundler.MSBuild.dll");
        Assert.SkipUnless(File.Exists(taskDll),
            "Bundler.MSBuild not built: " + taskDll);
        var root = Ws.Combine("msbuild-defaults");
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "TestApp"), "x");
        var project = Path.Combine(root, "proj.csproj");
        File.WriteAllText(project, $$"""
            <Project>
              <PropertyGroup>
                <AssemblyName>TestApp</AssemblyName>
                <TargetName>TestApp</TargetName>
                <_BundlerTaskAssembly>{{taskDll}}</_BundlerTaskAssembly>
              </PropertyGroup>
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props")}}" />
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets")}}" />
            </Project>
            """);

        var result = Dotnet.Run(["msbuild", project, "-t:BundlerBundle",
            $"-p:BundlerInputDir={input}", "-p:BundlerTarget=linux-x86_64",
            $"-p:BundlerOutputPath={output}"]);
        ProcessRunner.AssertSuccess(result, "BundlerBundle defaults");
        var log = result.Output;
        Assert.Contains("dev.bundler.testapp", log); // identifier 合成警告
        Assert.Contains("placeholder '0.1.0'", log); // Version 空缺兜底警告
        Assert.Contains(": deb", log); // formats 按 linux 目标推导
        Assert.True(Directory.EnumerateFiles(output, "*.deb").Any(),
            "expected a .deb in output: " + string.Join(",", Directory.EnumerateFiles(output)));

        // SDK 项目免 BundlerTarget：NETCoreSdkPortableRuntimeIdentifier → 宿主推导
        var sdkProject = Path.Combine(root, "sdk.csproj");
        File.WriteAllText(sdkProject, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>TestApp</AssemblyName>
                <TargetName>TestApp</TargetName>
                <_BundlerTaskAssembly>{{taskDll}}</_BundlerTaskAssembly>
              </PropertyGroup>
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props")}}" />
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets")}}" />
              <Target Name="EvalCondHit" BeforeTargets="_BundlerDerive">
                <Message Importance="high" Condition="'$(BundlerTarget)' == '{{LinuxHostTarget}}'" Text="COND-HIT eval BundlerTarget={{LinuxHostTarget}}" />
              </Target>
            </Project>
            """);
        var output2 = Path.Combine(root, "out2");
        var result2 = Dotnet.Run(["msbuild", sdkProject, "-t:BundlerBundle",
            $"-p:BundlerInputDir={input}", $"-p:BundlerOutputPath={output2}"]);
        ProcessRunner.AssertSuccess(result2, "BundlerBundle sdk-derived target");
        var log2 = result2.Output;
        // normal 级推断消息在默认 minimal 详细度不输出——看 high 级 Bundling 行的推导目标
        Assert.Contains($"for {LinuxHostTarget}:", log2);
        // eval 期推导：csproj 内按 $(BundlerTarget) 条件化的配置照常命中
        Assert.Contains("COND-HIT", log2);
        Assert.True(Directory.EnumerateFiles(output2, "*.deb").Any(),
            "expected a .deb in output2: " + string.Join(",", Directory.EnumerateFiles(output2)));

        // csproj 体内后设 RID：canonical 链按最终 BundlerTarget 重算，不残留宿主架构
        var ridProject = Path.Combine(root, "rid.csproj");
        File.WriteAllText(ridProject, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>TestApp</AssemblyName>
                <TargetName>TestApp</TargetName>
                <_BundlerTaskAssembly>{{taskDll}}</_BundlerTaskAssembly>
              </PropertyGroup>
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props")}}" />
              <!-- RID 在 props 导入后才设置：eval 期先按宿主推导，target 期须刷新 -->
              <PropertyGroup>
                <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>
              </PropertyGroup>
              <Import Project="{{Path.Combine(repo, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets")}}" />
              <Target Name="DumpCanon" AfterTargets="_BundlerDerive">
                <Message Importance="high" Text="CANON=$(_BundlerCanonicalTarget)" />
              </Target>
            </Project>
            """);
        var output3 = Path.Combine(root, "out3");
        var result3 = Dotnet.Run(["msbuild", ridProject, "-t:BundlerBundle",
            $"-p:BundlerInputDir={input}", $"-p:BundlerOutputPath={output3}",
            "-p:BundlerFormats=zip"]);
        ProcessRunner.AssertSuccess(result3, "BundlerBundle late-RID canonical recompute");
        var log3 = result3.Output;
        Assert.Contains("CANON=linux-aarch64", log3);
        Assert.True(Directory.EnumerateFiles(output3, "*.zip").Any(),
            "expected a .zip in output3: " + string.Join(",", Directory.EnumerateFiles(output3)));
    }
}

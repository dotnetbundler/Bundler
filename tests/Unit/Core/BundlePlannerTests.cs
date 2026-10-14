using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class BundlePlannerTests
{
    private static BundlePlan Plan(BundleConfiguration config) =>
        BundlePlanner.Create(config, checkFileSystem: false);

    [Fact]
    public void Create_InvalidConfig_ThrowsWithIssues()
    {
        var exception = Assert.Throws<BundleValidationException>(
            () => Plan(new BundleConfiguration()));
        Assert.NotEmpty(exception.Issues);
    }

    [Fact]
    public void Create_DmgWithoutApp_InjectsIntermediateApp()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "macos-aarch64", InputDirectory = "in",
                Formats = [PackageFormat.Dmg]
            }]
        };

        var plan = Plan(config);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(PackageFormat.App, plan.Items[0].Format);
        Assert.True(plan.Items[0].Intermediate);
        Assert.Equal(PackageFormat.Dmg, plan.Items[1].Format);
        Assert.False(plan.Items[1].Intermediate);
    }

    [Fact]
    public void Create_DmgPkgBoth_InjectOnceOrderAppDmgPkg()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "macos-aarch64", InputDirectory = "in",
                // 故意乱序（重复格式本身是校验错，单独用例覆盖）
                Formats = [PackageFormat.Pkg, PackageFormat.Dmg]
            }]
        };

        var plan = Plan(config);
        Assert.Equal(
            [PackageFormat.App, PackageFormat.Dmg, PackageFormat.Pkg],
            plan.Items.Select(i => i.Format).ToArray());
        Assert.True(plan.Items[0].Intermediate);
    }

    [Fact]
    public void Create_ExplicitApp_KeepsSingleNonIntermediate()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "macos-aarch64", InputDirectory = "in",
                Formats = [PackageFormat.App, PackageFormat.Dmg]
            }]
        };

        var plan = Plan(config);
        Assert.Equal(2, plan.Items.Count);
        Assert.False(plan.Items[0].Intermediate);
        Assert.Equal(PackageFormat.App, plan.Items[0].Format);
    }

    [Fact]
    public void Create_OutputLayout_ByFormatFansOut()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = "out", OutputLayout = OutputLayout.ByFormat,
            Targets = [new BundleTargetConfiguration
            {
                Target = "linux-x86_64", InputDirectory = "in",
                Formats = [PackageFormat.Deb, PackageFormat.Rpm]
            }]
        };

        var plan = Plan(config);
        Assert.Equal(Path.Combine("out", "deb"), plan.Items[0].OutputDirectory);
        Assert.Equal(Path.Combine("out", "rpm"), plan.Items[1].OutputDirectory);
    }

    [Fact]
    public void Create_ArtifactNameCollision_Throws()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64", InputDirectory = "in1", Formats = [PackageFormat.Deb]
                },
                new BundleTargetConfiguration
                {
                    Target = "linux-x86_64", InputDirectory = "in2", Formats = [PackageFormat.Deb]
                }
            ]
        };

        Assert.Throws<BundleValidationException>(() => Plan(config));
    }

    [Fact]
    public void Create_MainExecutableDefault_WindowsSuffixesExe()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "windows-x86_64", InputDirectory = "in", Formats = [PackageFormat.Nsis]
            }]
        };

        var plan = Plan(config);
        Assert.Equal("Sample.exe", plan.Items[0].MainExecutable);

        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "linux-x86_64", InputDirectory = "in", Formats = [PackageFormat.Deb]
            }]
        };
        Assert.Equal("Sample", Plan(config).Items[0].MainExecutable);
    }
}

using DotNet.Bundler;

namespace Bundler.UnitTests.Abstractions;

public sealed class BundleConfigurationTests
{
    [Fact]
    public void Defaults_AreDocumentedValues()
    {
        var config = new BundleConfiguration();

        Assert.Equal("", config.ProductName);
        Assert.Equal("", config.Identifier);
        Assert.Equal("", config.Version);
        Assert.Null(config.Publisher);
        Assert.Equal("artifacts", config.OutputDirectory);
        Assert.Equal(OutputLayout.Flat, config.OutputLayout);
        Assert.Empty(config.Icons);
        Assert.Empty(config.Resources);
        Assert.Empty(config.FileAssociations);
        Assert.Empty(config.UrlProtocols);
        Assert.Empty(config.Targets);
        Assert.Null(config.Update);
    }

    [Fact]
    public void NestedRecords_Defaults()
    {
        var update = new UpdateBundleConfiguration();
        Assert.Equal("", update.FeedUrl);
        Assert.Equal("latest", update.Channel);
        Assert.Null(update.SigningKeyFile);
        Assert.Null(update.PublicKey);
        Assert.Null(update.Notes);
        Assert.Null(update.BootstrapperDirectory);

        var resource = new BundleResourceConfiguration();
        Assert.Equal("", resource.Source);
        Assert.Equal("", resource.Destination);

        var keyFile = new KeyFileSigningConfiguration();
        Assert.Null(keyFile.KeyFile);
        Assert.Null(keyFile.Passphrase);

        var association = new BundleFileAssociationConfiguration();
        Assert.Empty(association.Extensions);
        Assert.Null(association.Name);
        Assert.Null(association.Description);
        Assert.Null(association.MimeType);

        var protocol = new BundleUrlProtocolConfiguration();
        Assert.Empty(protocol.Schemes);
        Assert.Null(protocol.Name);

        var target = new BundleTargetConfiguration();
        Assert.Equal("", target.Target);
        Assert.Equal("", target.InputDirectory);
        Assert.Null(target.MainExecutable);
        Assert.Empty(target.SigningFiles);
        Assert.Empty(target.Formats);
    }

    [Fact]
    public void InitSetters_AssignAtConstruction()
    {
        var config = new BundleConfiguration
        {
            ProductName = "B",
            Targets = [new BundleTargetConfiguration { Target = "linux-x86_64", InputDirectory = "in" }]
        };

        Assert.Equal("B", config.ProductName);
        Assert.Single(config.Targets);
        Assert.Equal("linux-x86_64", config.Targets[0].Target);
    }
}

public sealed class ContractSurfaceTests
{
    [Fact]
    public void NullBundleLogger_IsSingleton_AndLogIsNoOp()
    {
        Assert.Same(NullBundleLogger.Instance, NullBundleLogger.Instance);
        var ex = Record.Exception(() => NullBundleLogger.Instance.Log(BundleLogLevel.Error, "msg"));
        Assert.Null(ex);
    }

    [Fact]
    public void PublicContractTypes_ExistWithExpectedShape()
    {
        // 接口形状由编译验证；此处钉住关键签名不漂移。
        Assert.True(typeof(IFormatBundler).IsInterface);
        Assert.True(typeof(IBundleBackend).IsInterface);
        Assert.True(typeof(IBundleLogger).IsInterface);
        Assert.True(typeof(IBundleSigner).IsInterface);
        Assert.Single(typeof(IBundleLogger).GetMethods());
        Assert.Single(typeof(IBundleSigner).GetMethods());
    }

    [Fact]
    public void BundleSigningRequest_CarriesArtifactKind()
    {
        var request = new BundleSigningRequest(
            "p", BundleSigningArtifactKind.Installer, "prod", "windows-x86_64");
        Assert.Equal(BundleSigningArtifactKind.Installer, request.ArtifactKind);
        Assert.Equal(
            [BundleSigningArtifactKind.PayloadExecutable, BundleSigningArtifactKind.PayloadFile,
             BundleSigningArtifactKind.NativeComponent, BundleSigningArtifactKind.Installer,
             BundleSigningArtifactKind.Uninstaller],
            Enum.GetValues<BundleSigningArtifactKind>());
    }

    [Fact]
    public void BundleBuildContext_And_Artifact_Records()
    {
        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb, "in", "app", "out", false);
        var context = new BundleBuildContext(new BundleConfiguration(), item, "/tmp/work", NullBundleLogger.Instance);
        Assert.Same(item, context.Item);

        var artifact = new BundleArtifact(PackageFormat.Deb, "linux-x86_64", "/out/a.deb");
        Assert.Equal(PackageFormat.Deb, artifact.Format);
        Assert.Equal("linux-x86_64", artifact.Target);
    }
}

using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class BundleConfigurationLoaderTests
{
    private static async Task<BundleConfiguration> Load(string json)
    {
        var dir = Directory.CreateTempSubdirectory("bundler-cfg");
        var path = Path.Combine(dir.FullName, "bundler.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        return await BundleConfigurationLoader.LoadAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Load_MinimalJson_RoundTrips()
    {
        var config = await Load("""
            {
              "productName": "Sample",
              "identifier": "com.example.sample",
              "version": "1.0.0",
              "targets": [
                { "target": "linux-x86_64", "inputDirectory": "in", "formats": ["deb"] }
              ]
            }
            """);

        Assert.Equal("Sample", config.ProductName);
        Assert.Single(config.Targets);
        Assert.Equal(PackageFormat.Deb, config.Targets[0].Formats[0]);
        // 相对 inputDirectory 按配置目录归一化
        Assert.True(Path.IsPathRooted(config.Targets[0].InputDirectory));
    }

    [Fact]
    public async Task Load_EnumCamelCase_Parses()
    {
        var config = await Load("""
            {
              "productName": "S", "identifier": "com.example.s", "version": "1.0.0",
              "targets": [{ "target": "windows-x86_64", "inputDirectory": "in",
                            "formats": ["nsis", "msi", "tarGz"] }]
            }
            """);
        Assert.Equal(
            [PackageFormat.Nsis, PackageFormat.Msi, PackageFormat.TarGz],
            config.Targets[0].Formats);
    }

    [Fact]
    public async Task Load_MalformedJson_Throws()
    {
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () =>
            await Load("{ not json"));
    }

    [Fact]
    public async Task Load_EmptyFile_ThrowsInvalidData()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-cfg");
        var path = Path.Combine(dir.FullName, "bundler.json");
        await File.WriteAllTextAsync(path, "   ", TestContext.Current.CancellationToken);
        // 全空白文件在反序列化阶段即 JsonException（"empty" 分支只兜 null 对象）
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => BundleConfigurationLoader.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_MissingFile_Throws()
    {
        await Assert.ThrowsAnyAsync<IOException>(() =>
            BundleConfigurationLoader.LoadAsync(
                Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "bundler.json"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_TrailingCommasAndComments_Tolerated()
    {
        var config = await Load("""
            {
              // comment
              "productName": "S",
              "identifier": "com.example.s",
              "version": "1.0.0",
              "targets": [],
            }
            """);
        Assert.Equal("S", config.ProductName);
    }

    [Fact]
    public void Paths_RelativeResolvedAgainstBase()
    {
        var source = new BundleConfiguration
        {
            ProductName = "S", Identifier = "com.example.s", Version = "1.0.0",
            OutputDirectory = "rel-out",
            LicenseFile = "LICENSE.txt",
            Icons = ["icon.png"],
            Resources = [new BundleResourceConfiguration { Source = "res", Destination = "share" }],
            Targets = [new BundleTargetConfiguration
            {
                Target = "linux-x86_64", InputDirectory = "rel-in",
                SigningFiles = ["lib/x.so"], Formats = [PackageFormat.Deb]
            }],
            Update = new UpdateBundleConfiguration
            {
                FeedUrl = "https://x", SigningKeyFile = "keys/ec.json",
                BootstrapperDirectory = "bootstrap"
            }
        };

        var resolved = BundleConfigurationPaths.Resolve(source, "/cfg/base");

        Assert.True(Path.IsPathRooted(resolved.OutputDirectory));
        Assert.True(Path.IsPathRooted(resolved.Targets[0].InputDirectory));
        Assert.True(Path.IsPathRooted(resolved.Icons[0]));
        Assert.True(Path.IsPathRooted(resolved.Resources[0].Source));
        Assert.Equal("share", resolved.Resources[0].Destination); // 目标侧不解析
        Assert.True(Path.IsPathRooted(resolved.Update!.SigningKeyFile));
        Assert.True(Path.IsPathRooted(resolved.Update!.BootstrapperDirectory));
        Assert.False(Path.IsPathRooted(resolved.Targets[0].SigningFiles[0])); // signingFiles 相对 input 留相对
    }
}

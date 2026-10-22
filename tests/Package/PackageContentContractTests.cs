// 发货包内容契约腿：对本地 dotnet pack 出的 nupkg 直接断言内容清单、
// 依赖图与随包文件——不是"包存在"，是包里确实装着消费者要用的东西。
// nupkg 即 zip，经 System.IO.Compression 打开核对条目与内嵌 nuspec。
// 宿主无关（pack 与 zip 读取都是纯托管腿），不经真装同意闸。
using System.IO.Compression;
using System.Xml.Linq;

public sealed class PackageContentContractTests
{
    private static readonly string[] ExpectedPackageIds =
    [
        "DotNet.Bundler",
        "DotNet.Bundler.Abstractions",
        "DotNet.Bundler.AlpineApk",
        "DotNet.Bundler.AppImage",
        "DotNet.Bundler.Archive",
        "DotNet.Bundler.Cli",
        "DotNet.Bundler.Core",
        "DotNet.Bundler.Deb",
        "DotNet.Bundler.MSBuild",
        "DotNet.Bundler.MacApp",
        "DotNet.Bundler.MacDmg",
        "DotNet.Bundler.MacPkg",
        "DotNet.Bundler.Nsis",
        "DotNet.Bundler.Rpm",
        "DotNet.Bundler.Signing.Windows",
        "DotNet.Bundler.Update",
        "DotNet.Bundler.Updater",
        "DotNet.Bundler.Wix",
        // AOT 换件引导器：无 PackageId 时以程序集名落包。
        "bundler-updater",
    ];

    private static string NupkgPath(string id)
        => Path.Combine(RepositoryPackages.DirectoryPath, $"{id}.{RepositoryPackages.Version}.nupkg");

    private static List<string> Entries(string id)
    {
        using var zip = ZipFile.OpenRead(NupkgPath(id));
        return zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
    }

    private static XDocument Nuspec(string id)
    {
        using var zip = ZipFile.OpenRead(NupkgPath(id));
        var entry = zip.Entries.Single(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    // 内嵌 nuspec 带默认命名空间，按 LocalName 查询。
    private static XElement Meta(XDocument nuspec)
        => nuspec.Descendants().Single(e => e.Name.LocalName == "metadata");

    private static XElement MetaElement(XDocument nuspec, string name)
        => Meta(nuspec).Elements().Single(e => e.Name.LocalName == name);

    [Fact]
    public void PackProducesExactlyTheExpectedPackageSet()
    {
        var version = RepositoryPackages.Version;
        var actual = Directory.EnumerateFiles(RepositoryPackages.DirectoryPath, "*.nupkg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.EndsWith("." + version, StringComparison.Ordinal))
            .Select(n => n![..^(version.Length + 1)])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var expected = ExpectedPackageIds.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryNuspecReportsTheRepositoryPackageVersion()
    {
        foreach (var id in ExpectedPackageIds)
        {
            var version = MetaElement(Nuspec(id), "version").Value;
            Assert.Equal(RepositoryPackages.Version, version);
        }
    }

    [Fact]
    public void MetapackageDependsOnAdapterAndShipsDocs()
    {
        var entries = Entries("DotNet.Bundler");
        Assert.Contains("README.md", entries);
        Assert.Contains("LICENSE", entries);
        Assert.Contains("THIRD-PARTY-NOTICES.md", entries);

        var nuspec = Nuspec("DotNet.Bundler");
        Assert.Equal("true", MetaElement(nuspec, "developmentDependency").Value);
        var dep = Meta(nuspec).Descendants()
            .Single(d => d.Name.LocalName == "dependency" &&
                         d.Attribute("id")!.Value == "DotNet.Bundler.MSBuild");
        Assert.Equal($"[{RepositoryPackages.Version}]", dep.Attribute("version")!.Value);
    }

    [Fact]
    public void MsbuildPackageInjectsBuildTransitiveAndCarriesAllBackendTaskAssemblies()
    {
        var entries = Entries("DotNet.Bundler.MSBuild");

        Assert.Contains("buildTransitive/DotNet.Bundler.MSBuild.props", entries);
        Assert.Contains("buildTransitive/DotNet.Bundler.MSBuild.targets", entries);
        Assert.Contains("tasks/netstandard2.0/DotNet.Bundler.MSBuild.dll", entries);
        Assert.Contains("tasks/netstandard2.0/DotNet.Bundler.MSBuild.deps.json", entries);

        foreach (var assembly in
            new[]
            {
                "Abstractions", "Core", "MacApp", "MacDmg", "MacPkg", "Deb", "Rpm",
                "AppImage", "Archive", "Nsis", "Wix", "Signing.Windows", "AlpineApk",
                "Update",
            })
        {
            Assert.Contains($"tasks/netstandard2.0/DotNet.Bundler.{assembly}.dll", entries);
        }
        Assert.Contains("tasks/netstandard2.0/BouncyCastle.Cryptography.dll", entries);

        // 随包再分发工具的许可义务件不得丢。
        Assert.Contains("licenses/nsis/COPYING", entries);
        Assert.Contains("licenses/nsis-plugin/LICENSE", entries);
        Assert.Contains("licenses/wix/LICENSE.TXT", entries);
    }

    [Fact]
    public void UpdaterPackageShipsMuslDirectPInvokeTransitive()
    {
        // musl static-pie 消费方的 libc 直绑清单——缺席则自更新 spawn 面在
        // Alpine 静态发布里运行期全崩（.dynsym 不可解析 DllImport("libc")）。
        var entries = Entries("DotNet.Bundler.Updater");
        Assert.Contains("buildTransitive/DotNet.Bundler.Updater.targets", entries);
    }

    [Fact]
    public void WixPackageCarriesThirdPartyProvenance()
    {
        var entries = Entries("DotNet.Bundler.Wix");
        Assert.Contains("licenses/wix/LICENSE.TXT", entries);
        Assert.Contains("licenses/wix/SHA256SUMS", entries);
        Assert.Contains("licenses/wix/msi-wix-provenance.md", entries);
        Assert.Contains("licenses/wix/wix3141-source.zip", entries);
    }

    [Fact]
    public void NsisPackageCarriesToolsetLicenses()
    {
        var entries = Entries("DotNet.Bundler.Nsis");
        Assert.Contains("licenses/nsis/COPYING", entries);
        Assert.Contains("licenses/nsis-plugin/LICENSE", entries);
    }

    [Fact]
    public void AppImagePackageCarriesToolsetLicenses()
    {
        var entries = Entries("DotNet.Bundler.AppImage");
        Assert.Contains("licenses/appimagetool/LICENSE-appimagetool", entries);
        Assert.Contains("licenses/appimagetool/LICENSE-runtime", entries);
    }

    [Fact]
    public void CliPackageIsPackedAsDotnetTool()
    {
        var entries = Entries("DotNet.Bundler.Cli");
        Assert.Contains(entries, e => e.StartsWith("tools/net", StringComparison.Ordinal) &&
                                    e.EndsWith("/any/DotnetToolSettings.xml", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("tools/net", StringComparison.Ordinal) &&
                                    e.EndsWith("/any/bundler.dll", StringComparison.Ordinal));
    }
}

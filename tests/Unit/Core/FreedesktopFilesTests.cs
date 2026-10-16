using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class FreedesktopFilesTests : TempBase
{
    private static BundleConfiguration Bundle(
        IReadOnlyList<BundleFileAssociationConfiguration>? associations = null,
        IReadOnlyList<BundleUrlProtocolConfiguration>? protocols = null,
        string? description = null,
        IReadOnlyList<string>? icons = null) => new()
    {
        ProductName = "Sample App",
        Identifier = "com.example.sample",
        Version = "1.0.0",
        Description = description,
        Icons = icons ?? [],
        FileAssociations = associations ?? [],
        UrlProtocols = protocols ?? []
    };

    private static string Desktop(BundleConfiguration bundle,
        string binLink = "/usr/bin/sample",
        string? categories = null,
        bool alwaysEmitIcon = false) =>
        Encoding.UTF8.GetString(FreedesktopFiles.DesktopFileContent(
            bundle, "sample", "/opt/sample", "sample", binLink, categories, "deb", alwaysEmitIcon));

    [Fact]
    public void DesktopFile_Minimal_RequiredKeys()
    {
        var content = Desktop(Bundle());

        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Type=Application", content);
        Assert.Contains("Name=Sample App", content);
        Assert.Contains("Exec=/usr/bin/sample", content);
        Assert.Contains("Terminal=false", content);
        Assert.DoesNotContain("Icon=", content);
        Assert.DoesNotContain("MimeType=", content);
        Assert.DoesNotContain("Categories=", content);
        Assert.DoesNotContain("Comment=", content);
    }

    [Fact]
    public void DesktopFile_NoBinLink_FallsBackToInstallRoot()
    {
        var content = Desktop(Bundle(), binLink: "");
        Assert.Contains("Exec=/opt/sample/sample", content);
    }

    [Fact]
    public void DesktopFile_UrlProtocols_EmitUAndSchemeHandlerMime()
    {
        var content = Desktop(Bundle(
            protocols: [new BundleUrlProtocolConfiguration { Schemes = ["myapp"] }]));
        Assert.Contains("Exec=/usr/bin/sample %u", content);
        Assert.Contains("x-scheme-handler/myapp", content);
    }

    [Fact]
    public void DesktopFile_FileAssociations_EmitFAndMime()
    {
        var content = Desktop(Bundle(
            associations: [new BundleFileAssociationConfiguration
            {
                Extensions = ["myext"], MimeType = "application/x-myext"
            }]));
        Assert.Contains("Exec=/usr/bin/sample %f", content);
        Assert.Contains("MimeType=application/x-myext;", content);
    }

    [Fact]
    public void DesktopFile_Description_FirstLineAsComment()
    {
        var content = Desktop(Bundle(description: "First line.\nSecond line."));
        Assert.Contains("Comment=First line.", content);
    }

    [Fact]
    public void DesktopFile_Categories_ValidatedSemicolonList()
    {
        var content = Desktop(Bundle(), categories: "Utility; Development");
        Assert.Contains("Categories=Utility;Development;", content);

        Assert.Throws<ArgumentException>(() => Desktop(Bundle(), categories: "bad;cat!"));
        Assert.Throws<ArgumentException>(() => Desktop(Bundle(), categories: ";;;"));
    }

    [Fact]
    public void DesktopFile_IconsEmitIconKey_AlwaysEmit()
    {
        Assert.Contains("Icon=sample\n", Desktop(Bundle(icons: ["icon.png"])));
        Assert.Contains("Icon=sample\n", Desktop(Bundle(), alwaysEmitIcon: true));
    }

    [Fact]
    public void DesktopFile_ExecQuoting_SpecialChars()
    {
        var content = Desktop(Bundle(), binLink: "/opt/my app/run");
        Assert.Contains("Exec=\"/opt/my app/run\"", content);
    }

    [Fact]
    public void DesktopFile_MultiLineName_Rejected()
    {
        var bundle = Bundle();
        bundle = new BundleConfiguration
        {
            ProductName = "bad\nname", Identifier = "x", Version = "1"
        };
        Assert.Throws<ArgumentException>(() => Desktop(bundle));
    }

    [Fact]
    public void NormalizeAbsoluteDestination_Canonical()
    {
        Assert.Equal("/etc/app/x.conf",
            FreedesktopFiles.NormalizeAbsoluteDestination("/etc/app/x.conf", "deb"));
    }

    [Theory]
    [InlineData("etc/relative")]
    [InlineData("/")]
    [InlineData("/etc/../x")]
    [InlineData("/etc/./x")]
    [InlineData("/etc/app/")]
    [InlineData("")]
    [InlineData("\\etc\\app\\x.conf")]
    public void NormalizeAbsoluteDestination_Rejects(string destination)
    {
        Assert.Throws<ArgumentException>(
            () => FreedesktopFiles.NormalizeAbsoluteDestination(destination, "deb"));
    }

    [Fact]
    public void Collect_AlwaysEmitsDesktopEntry()
    {
        var entries = FreedesktopFiles.Collect(
            Bundle(), "sample", "/opt/sample", "sample", "/usr/bin/sample",
            new FreedesktopFiles.Options { Format = "deb" });

        Assert.Single(entries);
        Assert.Equal("usr/share/applications/sample.desktop", entries[0].ArchivePath);
        Assert.Contains("[Desktop Entry]", Encoding.UTF8.GetString(entries[0].ReadBytes()));
    }

    [Fact]
    public void Collect_Icon_GoesToHicolorByPngSize()
    {
        var icon = TempPath($"icon-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(icon, TestPng(48, 48));
        try
        {
            var entries = FreedesktopFiles.Collect(
                Bundle(icons: [icon]), "sample", "/opt/sample", "sample", "/usr/bin/sample",
                new FreedesktopFiles.Options { Format = "deb" });

            var iconEntry = entries.Single(e => e.ArchivePath.Contains("hicolor"));
            Assert.Equal("usr/share/icons/hicolor/48x48/apps/sample.png", iconEntry.ArchivePath);
        }
        finally
        {
            File.Delete(icon);
        }
    }

    [Fact]
    public void Collect_IconAt2x_MapsToScaleSlot()
    {
        var icon = TempPath($"icon-{Guid.NewGuid():N}@2x.png");
        File.WriteAllBytes(icon, TestPng(96, 96));
        try
        {
            var entries = FreedesktopFiles.Collect(
                Bundle(icons: [icon]), "sample", "/opt/sample", "sample", "/usr/bin/sample",
                new FreedesktopFiles.Options { Format = "deb" });

            Assert.Equal("usr/share/icons/hicolor/48x48@2/apps/sample.png",
                entries.Single(e => e.ArchivePath.Contains("hicolor")).ArchivePath);
        }
        finally
        {
            File.Delete(icon);
        }
    }

    [Fact]
    public void Collect_NonPngIcon_Rejected()
    {
        var icon = TempPath($"icon-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(icon, [1, 2, 3]);
        try
        {
            Assert.Throws<ArgumentException>(() => FreedesktopFiles.Collect(
                Bundle(icons: [icon]), "sample", "/opt/sample", "sample", "/usr/bin/sample",
                new FreedesktopFiles.Options { Format = "deb" }));
        }
        finally
        {
            File.Delete(icon);
        }
    }

    [Fact]
    public void Collect_Metainfo_StagedVerbatim()
    {
        var metainfo = TempPath($"m-{Guid.NewGuid():N}.xml");
        File.WriteAllText(metainfo, "<component/>");
        try
        {
            var entries = FreedesktopFiles.Collect(
                Bundle(), "sample", "/opt/sample", "sample", "/usr/bin/sample",
                new FreedesktopFiles.Options { Format = "deb", MetainfoFile = metainfo });

            Assert.Contains(entries,
                e => e.ArchivePath == "usr/share/metainfo/sample.metainfo.xml");
        }
        finally
        {
            File.Delete(metainfo);
        }
    }

    /// <summary>构造合法 PNG 头 + IHDR 尺寸段。</summary>
    internal static byte[] TestPng(int width, int height)
    {
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        // 8..11 长度 + 12..15 "IHDR"
        png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
        png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16);
        png[18] = (byte)(width >> 8); png[19] = (byte)width;
        png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16);
        png[22] = (byte)(height >> 8); png[23] = (byte)height;
        return png;
    }
}

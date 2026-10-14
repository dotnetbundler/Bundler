using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Deb;
using DotNet.Bundler.Rpm;
using Bundler.UnitTests.Core;

namespace Bundler.UnitTests.Formats;

/// <summary>
/// deb/rpm/apk 写读器往返：构造最小输入目录 → 写器产包 → 读器解出结构断言字段。
/// 全托管零进程。
/// </summary>
public sealed class PackageRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bundler-ut-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _output;

    public PackageRoundTripTests()
    {
        _input = Path.Combine(_root, "input");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_input);
        File.WriteAllText(Path.Combine(_input, "sample"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(_input, "readme.txt"), "doc\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static BundleConfiguration Bundle() => new()
    {
        ProductName = "Sample",
        Identifier = "com.example.sample",
        Publisher = "Example Co",
        Version = "1.2.3",
        Description = "Round trip fixture",
        Targets = []
    };

    private BundlePlanItem Item(BundleTarget target, PackageFormat format) =>
        new(target, format, _input, "sample", _output, Intermediate: false);

    private static readonly BundleTarget LinuxX64 =
        BundleTarget.Parse("linux-x86_64");

    [Fact]
    public void Deb_WriterReader_RoundTrip()
    {
        var result = DebPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.Deb),
            new DebBundleConfiguration(), NullBundleLogger.Instance);

        Assert.EndsWith(".deb", result.OutputPath);
        Assert.Equal("sample", result.PackageName);
        Assert.Equal("amd64", result.Architecture);
        Assert.True(File.Exists(result.OutputPath + ".sha256"));

        var members = DebPackageReader.ReadAr(result.OutputPath);
        Assert.Equal(3, members.Count);
        Assert.Equal("debian-binary", members[0].Name);
        Assert.Equal("control.tar.gz", members[1].Name);
        Assert.Equal("data.tar.gz", members[2].Name);
        Assert.Equal("2.0\n", Encoding.UTF8.GetString(members[0].Content));

        var control = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content));
        var controlFile = control.Single(e => e.Name.EndsWith("control"));
        var controlText = Encoding.UTF8.GetString(controlFile.Content);
        Assert.Contains("Package: sample", controlText);
        Assert.Contains("Version: 1.2.3", controlText);
        Assert.Contains("Architecture: amd64", controlText);
        Assert.True(control.Any(e => e.Name.EndsWith("md5sums")),
            "control.tar.gz must carry md5sums");

        var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
        var exe = data.Single(e => e.Name.EndsWith("usr/lib/sample/sample"));
        Assert.Equal("#!/bin/sh\necho hi\n", Encoding.UTF8.GetString(exe.Content));
        Assert.Equal(493, exe.Mode); // tar 条目只存权限位：0755
        Assert.True(data.Any(e => e.Name == "usr/bin/sample" || e.Name.EndsWith("/usr/bin/sample")),
            "bin-link symlink must exist in data.tar.gz");
    }

    [Fact]
    public void Rpm_WriterReader_RoundTrip()
    {
        var result = RpmPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.Rpm),
            new RpmBundleConfiguration(), NullBundleLogger.Instance);

        Assert.EndsWith(".rpm", result.OutputPath);
        Assert.Equal("sample", result.PackageName);
        Assert.Equal("x86_64", result.Architecture);
        Assert.True(File.Exists(result.OutputPath + ".sha256"));

        var rpm = RpmPackageReader.Read(result.OutputPath);
        Assert.Equal("sample", rpm.Main.Text(1000));         // NAME
        Assert.Equal("1.2.3", rpm.Main.Text(1001));          // VERSION
        Assert.Equal("x86_64", rpm.Main.Text(1022));         // ARCH
        Assert.Equal("Example Co", rpm.Main.Text(1011));     // VENDOR
        Assert.True(rpm.Payload.Any(e => e.Path.EndsWith("/usr/lib/sample/sample")),
            "payload must contain the main executable");
        Assert.True(rpm.Payload.Any(e => e.Path == "/usr/bin/sample"),
            "payload must contain the bin-link");
    }

    [Fact]
    public void Apk_WriterReader_RoundTrip()
    {
        var result = ApkPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.AlpineApk),
            new AlpineApkBundleConfiguration(), NullBundleLogger.Instance);

        Assert.EndsWith(".apk", result.Path);

        var apk = File.ReadAllBytes(result.Path);
        var streams = ApkPackageReader.SplitGzipStreams(apk);
        Assert.True(streams.Count >= 2, "apk must be at least control+data gzip members");

        var pkginfo = ApkPackageReader.ReadTar(streams[0])
            .Single(e => e.Name == ".PKGINFO");
        var text = Encoding.UTF8.GetString(pkginfo.Content);
        Assert.Contains("pkgname = sample", text);
        Assert.Contains("pkgver = 1.2.3-r0", text);
        Assert.Contains("arch = x86_64", text);

        var data = ApkPackageReader.ReadTar(streams[^1]);
        Assert.True(data.Any(e => e.Name.Contains("usr/lib/sample/sample")),
            "data segment must contain the main executable");
    }

    [Fact]
    public void Apk_SignedWriter_SignatureSegment()
    {
        // 进程内生成临时 RSA 密钥（测试件，不入库）写 PEM，走签名段分支
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var keyPath = Path.Combine(_root, "testkey.pem");
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        var result = ApkPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.AlpineApk),
            new AlpineApkBundleConfiguration
            {
                Signing = new KeyFileSigningConfiguration { KeyFile = keyPath }
            },
            NullBundleLogger.Instance);

        var streams = ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(result.Path));
        Assert.Equal(3, streams.Count); // signature + control + data
        var signSegment = ApkPackageReader.ReadTar(streams[0]);
        Assert.True(signSegment.Any(e => e.Name.StartsWith(".SIGN.RSA.")),
            "signed apk must lead with a .SIGN.RSA.* member");
    }

    [Fact]
    public void Apk_MissingKeyFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ApkPackageWriter.Build(
                Bundle(), Item(LinuxX64, PackageFormat.AlpineApk),
                new AlpineApkBundleConfiguration
                {
                    Signing = new KeyFileSigningConfiguration
                    {
                        KeyFile = Path.Combine(_root, "nope.pem")
                    }
                },
                NullBundleLogger.Instance));
    }
}

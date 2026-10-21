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
public sealed class PackageRoundTripTests : TempBase
{
    private readonly string _input;
    private readonly string _output;
    private readonly string _work;

    public PackageRoundTripTests()
    {
        _input = Path.Combine(Root, "input");
        _output = Path.Combine(Root, "out");
        _work = Path.Combine(Root, "work");
        Directory.CreateDirectory(_input);
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_input, "sample"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(_input, "readme.txt"), "doc\n");
    }


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
            new DebBundleConfiguration(), _work, NullBundleLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.EndsWith(".deb", result.OutputPath);
        Assert.Equal("sample", result.PackageName);
        Assert.Equal("amd64", result.Architecture);

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
            new RpmBundleConfiguration(), _work, NullBundleLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.EndsWith(".rpm", result.OutputPath);
        Assert.Equal("sample", result.PackageName);
        Assert.Equal("x86_64", result.Architecture);

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
    public void Rpm_SymlinkSize_MatchesUtf8ByteCount()
    {
        // 非 ASCII 链接目标：FILESIZES/SIZE 必须与 cpio 体的 UTF-8 字节数一致，
        // 不是 .NET 字符数——回归 Local Review 第7轮发现的字符/字节错位。
        var nonAscii = "数据文件.so";
        File.WriteAllText(Path.Combine(_input, nonAscii), "lib");
        var link = Path.Combine(_input, "link.so");
        try
        {
            File.CreateSymbolicLink(link, nonAscii);
        }
        catch
        {
            Assert.Skip("The host cannot create symlinks; link leg unverifiable.");
        }

        var result = RpmPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.Rpm),
            new RpmBundleConfiguration(), _work, NullBundleLogger.Instance,
            TestContext.Current.CancellationToken);
        var rpm = RpmPackageReader.Read(result.OutputPath);

        var baseNames = rpm.Main.Strings(1117);   // BASENAMES
        var sizes = rpm.Main.Ints(1028);          // FILESIZES
        var index = Array.IndexOf(baseNames, "link.so");
        Assert.True(index >= 0, "link.so must be in the rpm file manifest");
        var expected = Encoding.UTF8.GetByteCount(nonAscii);
        Assert.Equal(expected, sizes[index]);
        // cpio 体字节数与 FILESIZES 必须同源一致
        var payloadEntry = rpm.Payload.Single(e => e.Path.EndsWith("/link.so"));
        Assert.Equal(expected, payloadEntry.Data.Length);
    }

    [Theory]
    [InlineData(42L, 1009, 4)]                       // SIZE / INT32
    [InlineData((long)int.MaxValue, 1009, 4)]        // 边界内仍走 SIZE
    [InlineData((long)int.MaxValue + 1, 5009, 5)]    // 越界切 RPMTAG_LONGSIZE
    [InlineData(3L * int.MaxValue, 5009, 5)]
    public void Rpm_SizeEntry_LongVariantBeyondInt32(long size, int expectedTag, int expectedType)
    {
        // 单件 >2GiB 已在 AddFile 拒，但件数一多总和照样溢出——断言主头换
        // RPM 4.15+ LONGSIZE 变体而非 checked 溢出。
        var entry = RpmPackageWriter.SizeEntry(size);
        Assert.Equal(expectedTag, entry.Tag);
        Assert.Equal(expectedType, entry.Type);
        if (expectedType == 5)
        {
            var decoded = 0L;
            foreach (var b in entry.Data)
            {
                decoded = (decoded << 8) | b;
            }
            Assert.Equal(size, decoded);   // Pack64 大端
        }
    }

    [Fact]
    public void Apk_WriterReader_RoundTrip()
    {
        var result = ApkPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.AlpineApk),
            new AlpineApkBundleConfiguration(), _work, NullBundleLogger.Instance,
            TestContext.Current.CancellationToken);

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
        var keyPath = Path.Combine(Root, "testkey.pem");
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        var result = ApkPackageWriter.Build(
            Bundle(), Item(LinuxX64, PackageFormat.AlpineApk),
            new AlpineApkBundleConfiguration
            {
                Signing = new KeyFileSigningConfiguration { KeyFile = keyPath }
            },
            _work, NullBundleLogger.Instance, TestContext.Current.CancellationToken);

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
                        KeyFile = Path.Combine(Root, "nope.pem")
                    }
                },
                _work, NullBundleLogger.Instance, TestContext.Current.CancellationToken));
    }
}

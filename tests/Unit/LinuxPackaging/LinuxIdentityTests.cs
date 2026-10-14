using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Deb;
using DotNet.Bundler.Rpm;

namespace Bundler.UnitTests.LinuxPackaging;

/// <summary>Linux 包族身份映射：kebab 派生、SemVer→distro 版本、依赖子句解析。</summary>
public sealed class LinuxIdentityTests
{
    // ---- DebName ----

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("My_App 2", "my-app-2")]
    [InlineData("hello.world", "hello.world")]
    [InlineData("  Surround  ", "surround")]
    [InlineData("a!!b", "a-b")]
    public void DebName_Sanitize_KebabCases(string input, string expected)
    {
        Assert.Equal(expected, DebName.Sanitize(input));
    }

    [Fact]
    public void DebName_Sanitize_Underivable_Throws()
    {
        Assert.Throws<ArgumentException>(() => DebName.Sanitize("!!!"));
        Assert.Throws<ArgumentException>(() => DebName.Sanitize("x")); // deb 名单字符太短
    }

    [Theory]
    [InlineData("my-app", true)]
    [InlineData("libx11-6", true)]
    [InlineData("My-App", false)]  // 大写
    [InlineData("-app", false)]   // 必须字母数字起
    [InlineData("a", false)]      // 至少两字符（[a-z0-9][a-z0-9+.-]+）
    [InlineData("my app", false)] // 空格
    public void DebName_Validate(string name, bool ok)
    {
        if (ok) DebName.Validate(name);
        else Assert.Throws<ArgumentException>(() => DebName.Validate(name));
    }

    // ---- DebVersion ----

    [Fact]
    public void DebVersion_Map_PrereleaseBecomesTilde()
    {
        var settings = new DebBundleConfiguration();
        // '~' 让预发布排在正式发布之前——'-' 表达不了这个序。
        Assert.Equal("1.2.3~alpha.1-1", DebVersion.Map("1.2.3-alpha.1", settings));
        Assert.Equal("1.2.3+build.5-1", DebVersion.Map("1.2.3+build.5", settings));
        Assert.Equal("1.2.3-1", DebVersion.Map("1.2.3", settings));
    }

    [Fact]
    public void DebVersion_Map_EpochAndReleaseKnobs()
    {
        Assert.Equal("2:1.2.3-9", DebVersion.Map("1.2.3",
            new DebBundleConfiguration { Epoch = "2", Release = "9" }));
        Assert.Equal("1.2.3", DebVersion.Map("1.2.3",
            new DebBundleConfiguration { Release = "" })); // 空 release 即裸 upstream
    }

    [Fact]
    public void DebVersion_Map_ExplicitVersionWins()
    {
        Assert.Equal("3:9.9.9-4", DebVersion.Map("0.0.1",
            new DebBundleConfiguration { Version = "3:9.9.9-4" }));
        Assert.Throws<ArgumentException>(() => DebVersion.Map("1.0.0",
            new DebBundleConfiguration { Version = "not a version!!" }));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("1a")]
    [InlineData("1.5")]
    public void DebVersion_Map_EpochDigitsOnly(string epoch)
    {
        Assert.Throws<ArgumentException>(() => DebVersion.Map("1.0.0",
            new DebBundleConfiguration { Epoch = epoch }));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("2:1.2.3-4", true)]
    [InlineData("1.2.3~rc1", true)]
    [InlineData("1.2.3+b1-2", true)]
    [InlineData("abc-1", false)]   // upstream 必须数字起
    [InlineData("1.2.3-é", false)]
    [InlineData("1:2:3", false)]   // 多 epoch
    public void DebVersion_Validate(string version, bool ok)
    {
        if (ok) DebVersion.Validate(version);
        else Assert.Throws<ArgumentException>(() => DebVersion.Validate(version));
    }

    [Theory]
    [InlineData("2:1.2.3-4", "1.2.3-4")] // epoch 不进文件名
    [InlineData("1.2.3-4", "1.2.3-4")]
    public void DebVersion_FileNameVersion_DropsEpoch(string version, string expected)
    {
        Assert.Equal(expected, DebVersion.FileNameVersion(version));
    }

    // ---- RpmName ----

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("hello_world", "hello-world")]
    public void RpmName_Sanitize(string input, string expected)
    {
        Assert.Equal(expected, RpmName.Sanitize(input));
    }

    [Theory]
    [InlineData("my-app", true)]
    [InlineData("MyApp", true)]   // rpm 允许大写
    [InlineData("app_1", true)]   // rpm 允许下划线
    [InlineData("my app", false)] // 无空白
    [InlineData("-app", false)]   // 字母数字起
    public void RpmName_Validate(string name, bool ok)
    {
        if (ok) RpmName.Validate(name);
        else Assert.Throws<ArgumentException>(() => RpmName.Validate(name));
    }

    // ---- RpmVersion ----

    [Fact]
    public void RpmVersion_Map_PrereleaseGetsZeroRelease()
    {
        var settings = new RpmBundleConfiguration();
        var plain = RpmVersion.Map("1.2.3", settings);
        Assert.Equal("1.2.3", plain.Version);
        Assert.Equal("1", plain.Release);
        // Fedora 约定：预发布 Release 0.<n>.<label> 排在 "1" 之前。
        var pre = RpmVersion.Map("1.2.3-beta.2", settings);
        Assert.Equal("1.2.3", pre.Version);
        Assert.Equal("0.1.beta.2", pre.Release);
    }

    [Fact]
    public void RpmVersion_Map_BuildMetadataIntoRelease()
    {
        var mapped = RpmVersion.Map("1.2.3+b.5", new RpmBundleConfiguration());
        Assert.Equal("1.2.3", mapped.Version);
        Assert.Equal("1+b.5", mapped.Release);
    }

    [Fact]
    public void RpmVersion_Map_ExplicitAndEpoch()
    {
        var mapped = RpmVersion.Map("1.0.0",
            new RpmBundleConfiguration { Version = "9.9", Release = "42", Epoch = "3" });
        Assert.Equal("9.9", mapped.Version);
        Assert.Equal("42", mapped.Release);
        Assert.Equal(3, mapped.Epoch);
        Assert.Equal("3:9.9-42", RpmVersion.Evr(mapped));
    }

    [Theory]
    [InlineData("v1.2.3")]   // 版本必须数字起
    [InlineData("-beta")]    // 无版本核
    public void RpmVersion_Map_BadSemver_Rejected(string semver)
    {
        Assert.Throws<ArgumentException>(() => RpmVersion.Map(semver, new RpmBundleConfiguration()));
    }

    [Fact]
    public void RpmVersion_Map_DashInFields_Rejected()
    {
        Assert.Throws<ArgumentException>(() => RpmVersion.Map("1.0.0",
            new RpmBundleConfiguration { Version = "1.0-1" }));
        Assert.Throws<ArgumentException>(() => RpmVersion.Map("1.0.0",
            new RpmBundleConfiguration { Release = "1-2" }));
    }

    // ---- RpmDependency.Parse ----

    [Theory]
    [InlineData("libc.so.6", "libc.so.6", 0, "")]
    [InlineData("libfoo >= 1.2", "libfoo", RpmDependency.Greater | RpmDependency.Equal, "1.2")]
    [InlineData("libfoo = 1.2-1", "libfoo", RpmDependency.Equal, "1.2-1")]
    [InlineData("libfoo < 2.0", "libfoo", RpmDependency.Less, "2.0")]
    [InlineData("libfoo <= 2.0", "libfoo", RpmDependency.Less | RpmDependency.Equal, "2.0")]
    [InlineData("libfoo > 1.0", "libfoo", RpmDependency.Greater, "1.0")]
    public void RpmDependency_Parse_ClauseForms(string clause, string name, int flags, string version)
    {
        var parsed = RpmDependency.Parse(clause);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(flags, parsed.Flags);
        Assert.Equal(version, parsed.Version);
    }

    [Fact]
    public void RpmDependency_Parse_RichDeps_PassThrough()
    {
        // rpm ≥4.13 rich deps：整组括号透传、只验配对。
        var parsed = RpmDependency.Parse("(libgcc or libgcc_s1)");
        Assert.Equal("(libgcc or libgcc_s1)", parsed.Name);
        Assert.Equal(0, parsed.Flags);
        Assert.Throws<ArgumentException>(() => RpmDependency.Parse("(a or b"));   // 不配对
        Assert.Throws<ArgumentException>(() => RpmDependency.Parse("(a))"));      // 早闭
        Assert.Throws<ArgumentException>(() => RpmDependency.Parse("()"));        // 空壳
    }

    [Theory]
    [InlineData("libfoo ~ 1.2")]  // 非法算子
    [InlineData("a b c d")]       // 四段
    public void RpmDependency_Parse_Invalid_Rejected(string clause)
    {
        Assert.Throws<ArgumentException>(() => RpmDependency.Parse(clause));
    }

    // ---- ApkIdentity ----

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("a__b", "a-b")] // 折叠连字符
    public void ApkName_Sanitize(string input, string expected)
    {
        Assert.Equal(expected, ApkIdentity.SanitizeName(input));
    }

    [Theory]
    [InlineData("my-app", true)]
    [InlineData("my_app.1+2", true)]
    [InlineData("-app", false)]   // 字母数字起
    [InlineData("app-", false)]   // 字母数字收
    [InlineData("my app", false)]
    public void ApkName_Validate(string name, bool ok)
    {
        if (ok) ApkIdentity.ValidateName(name);
        else Assert.Throws<ArgumentException>(() => ApkIdentity.ValidateName(name));
    }

    [Fact]
    public void ApkVersion_Map_DashBecomesUnderscore()
    {
        // apk 版本里 '-' 切 release 段——上游 '-' 一律映成 '_'。
        Assert.Equal("1.2.3_alpha.1", ApkIdentity.MapVersion("1.2.3-alpha.1"));
        Assert.Equal("1.2.3+b5", ApkIdentity.MapVersion("1.2.3+b5"));
        Assert.Throws<ArgumentException>(() => ApkIdentity.MapVersion(".bad"));
    }

    [Theory]
    [InlineData("aarch64", true)]
    [InlineData("x86_64", true)]
    [InlineData("X86_64", false)]  // 必须小写
    [InlineData("x86-64", false)]  // 只许小写字母/数字/下划线
    public void ApkArch_Validate(string arch, bool ok)
    {
        if (ok) ApkIdentity.ValidateArchitecture(arch);
        else Assert.Throws<ArgumentException>(() => ApkIdentity.ValidateArchitecture(arch));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("42", true)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    public void ApkKnob_NonNegativeInteger(string value, bool ok)
    {
        if (ok) ApkIdentity.ValidateNonNegativeInteger(value, "Release");
        else Assert.Throws<ArgumentException>(() =>
            ApkIdentity.ValidateNonNegativeInteger(value, "Release"));
    }

    [Theory]
    [InlineData("so:libc.musl-x86_64.so.1", true)]
    [InlineData("cmd:foo>=1.2", true)]
    [InlineData("", false)]
    [InlineData("two tokens", false)] // 单 token 字段
    public void ApkKnob_ListEntry(string entry, bool ok)
    {
        if (ok) ApkIdentity.ValidateListEntry(entry, "Depends");
        else Assert.Throws<ArgumentException>(() =>
            ApkIdentity.ValidateListEntry(entry, "Depends"));
    }

    // ---- AppImageIdentity ----

    [Theory]
    [InlineData(null, "My App", "my-app")]      // 空白等非集字符 → '-'
    [InlineData("Explicit.Name", "My App", "explicit.name")]
    [InlineData(null, "My_App++", "my_app")]    // '_'/'+' 属合法集保留，尾缀被 Trim
    public void AppImageName_PackageName(string? knob, string product, string expected)
    {
        Assert.Equal(expected, AppImageIdentity.PackageName(knob, product));
    }

    [Fact]
    public void AppImageName_TooShort_Rejected()
    {
        Assert.Throws<ArgumentException>(() => AppImageIdentity.PackageName(null, "x"));
        Assert.Throws<ArgumentException>(() => AppImageIdentity.PackageName(null, "!!!"));
    }

    [Theory]
    [InlineData("linux-x86_64", "x86_64")]
    [InlineData("linux-aarch64", "aarch64")]
    [InlineData("linux-x86", "i686")]
    public void AppImageArch_Environment(string target, string expected)
    {
        Assert.Equal(expected,
            AppImageIdentity.EnvironmentArchitecture(BundleTarget.Parse(target)));
    }

    [Theory]
    [InlineData("x86_64", "x86_64")]
    [InlineData("amd64", "x86_64")] // amd64 归一
    [InlineData("aarch64", "aarch64")]
    [InlineData("i686", null)]      // i686 无随包 runtime
    [InlineData("armv7", null)]
    public void AppImageArch_NormalizeOverride(string input, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<ArgumentException>(() => AppImageIdentity.NormalizeArchitecture(input));
        }
        else
        {
            Assert.Equal(expected, AppImageIdentity.NormalizeArchitecture(input));
        }
    }
}

using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

/// <summary>
/// binLink 统一命名校验：deb/rpm/apk/AppImage 共用的单文件名规则——
/// 分隔符、空白、引号、展开/替换元字符、控制字符一律拒。
/// </summary>
public sealed class LinuxLinkNameTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("my-app")]
    [InlineData("my_app.sh")]
    [InlineData("a.b-c_9")]
    public void ValidName_PassesThrough(string name) =>
        Assert.Equal(name, LinuxLinkName.Validate(name, "Knob"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Rejected(string? name) =>
        Assert.Throws<ArgumentException>(() => LinuxLinkName.Validate(name, "Knob"));

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a$b")]
    [InlineData("a`b")]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("a&b")]
    [InlineData(" x")]
    [InlineData("x ")]
    public void UnsafeName_Rejected(string name) =>
        Assert.Throws<ArgumentException>(() => LinuxLinkName.Validate(name, "Knob"));

    [Fact]
    public void ControlChar_Rejected() =>
        Assert.Throws<ArgumentException>(() => LinuxLinkName.Validate("a\nb", "Knob"));
}

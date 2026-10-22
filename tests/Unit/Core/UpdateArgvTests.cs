using DotNet.Bundler.Core.Update;

namespace Bundler.UnitTests.Core;

/// <summary>
/// UPDATE argv 拼写单源（Core/Update/UpdateArgv.cs，三工程同源链接）：
/// Join 走逐元双引号 + CommandLineToArgvW 转义规则；ShJoin/ShQuote 走 POSIX
/// 单引号折义。verbatim 断言钉死拼写面——客户端与 AOT 引导件共用同一份字节。
/// </summary>
public sealed class UpdateArgvTests
{
    [Fact]
    public void Join_PlainArgs_EachQuoted()
    {
        Assert.Equal("\"one\" \"two\"", UpdateArgv.Join(["one", "two"]));
    }

    [Fact]
    public void Join_EmptyArg_EmptyQuotes()
    {
        Assert.Equal("\"\" \"x\"", UpdateArgv.Join(["", "x"]));
    }

    [Fact]
    public void Join_EmbeddedQuote_BackslashEscaped()
    {
        // " 前的连续 \ 先翻倍再附 \"——CommandLineToArgvW 引号区内规则。
        Assert.Equal("\"a\\\"b\"", UpdateArgv.Join(["a\"b"]));
        Assert.Equal("\"x\\\\\\\"y\"", UpdateArgv.Join(["x\\\"y"]));
    }

    [Fact]
    public void Join_TrailingBackslashes_Doubled()
    {
        // 串尾紧邻收尾引号的 \ 翻倍防吞；非尾部的 \ 原样。
        Assert.Equal("\"c\\\\\"", UpdateArgv.Join(["c\\"]));
        Assert.Equal("\"d\\\\\\\\\"", UpdateArgv.Join(["d\\\\"]));
        Assert.Equal("\"d\\e\"", UpdateArgv.Join(["d\\e"]));
    }

    [Fact]
    public void Join_Spaces_And_MixedQuoting()
    {
        Assert.Equal(
            "\"a b\" \"\" \"c\\\"d\" \"trail\\\\\"",
            UpdateArgv.Join(["a b", "", "c\"d", "trail\\"]));
    }

    [Fact]
    public void ShQuote_SingleQuotes_Folded()
    {
        Assert.Equal("'x'", UpdateArgv.ShQuote("x"));
        Assert.Equal("''", UpdateArgv.ShQuote(""));
        Assert.Equal("'don'\\''t'", UpdateArgv.ShQuote("don't"));
        Assert.Equal("'a b'", UpdateArgv.ShQuote("a b"));
        // 双引号/反斜杠在单引号区内是字面量。
        Assert.Equal("'a\"b\\c'", UpdateArgv.ShQuote("a\"b\\c"));
    }

    [Fact]
    public void ShJoin_EachQuoted_SpaceJoined()
    {
        Assert.Equal("'a b' 'c' 'don'\\''t'",
            UpdateArgv.ShJoin(["a b", "c", "don't"]));
    }
}

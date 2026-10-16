using DotNet.Bundler.Cli;

namespace Bundler.UnitTests.Cli;

public sealed class CliArgumentsTests
{
    [Fact]
    public void Parse_CommandAndFlags()
    {
        var parsed = CliArguments.Parse(["bundle", "--json", "--quiet", "--strict"]);
        Assert.Equal("bundle", parsed.Command);
        Assert.True(parsed.Json);
        Assert.True(parsed.Quiet);
        Assert.True(parsed.Strict);
    }

    [Fact]
    public void Parse_ValueOption_EqualsAndSeparate()
    {
        var parsed = CliArguments.Parse(
            ["validate", "--target=linux-x86_64", "--formats", "deb"]);
        Assert.Equal("linux-x86_64", parsed.Options["target"]);
        Assert.Equal("deb", parsed.Options["formats"]);
    }

    [Fact]
    public void Parse_RepeatableInputDir_OrderPreserved()
    {
        var parsed = CliArguments.Parse(
            ["bundle", "--input-dir", "a", "--input-dir=b", "--input-dir", "c"]);
        Assert.Equal(["a", "b", "c"], parsed.InputDirectories);
        // 首个值同时进 options 喂配置；其余由 CliProgram 合并
        Assert.Equal("a", parsed.Options["input-dir"]);
    }

    [Fact]
    public void Parse_DuplicateOption_Rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--target", "x", "--target", "y"]));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOption_Rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--frobnicate"]));
        Assert.Contains("Unknown option", ex.Message);
    }

    [Fact]
    public void Parse_FlagWithValue_Rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--quiet=yes"]));
        Assert.Contains("does not take a value", ex.Message);
    }

    [Fact]
    public void Parse_KeyFile_OnlyWithUpdateKeygen()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--key-file", "k.pem"]));

        var parsed = CliArguments.Parse(["update-keygen", "--key-file", "k.pem"]);
        Assert.Equal("k.pem", parsed.Options["key-file"]);
    }

    [Fact]
    public void Parse_QuietVerbose_Conflict()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--quiet", "--verbose"]));
    }

    [Fact]
    public void Parse_BareDashAndPositional_Rejected()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--"]));
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "extra"]));
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "-x"]));
    }

    [Fact]
    public void Parse_FormatKnobOverride_Recognized()
    {
        var parsed = CliArguments.Parse(["bundle", "--deb.vendor=ACME", "--nsis.language=en"]);
        Assert.Equal("ACME", parsed.Options["deb.vendor"]);
        Assert.Equal("en", parsed.Options["nsis.language"]);
    }

    [Fact]
    public void Parse_UnknownFormatSection_Rejected()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--bogus.knob=v"]));
    }

    [Fact]
    public void Parse_MissingValue_Rejected()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["bundle", "--target"]));
    }

    [Fact]
    public void Parse_HelpAndVersion()
    {
        Assert.True(CliArguments.Parse(["--help"]).Help);
        Assert.True(CliArguments.Parse(["-h"]).Help);
        Assert.True(CliArguments.Parse(["--h"]).Help);
        Assert.True(CliArguments.Parse(["--version"]).ShowVersion);
    }

    [Fact]
    public void Parse_EmptyArgs_NoCommand()
    {
        var parsed = CliArguments.Parse([]);
        Assert.Equal("", parsed.Command);
        Assert.False(parsed.Help);
    }

    [Fact]
    public void RequiredOption_Missing_Throws()
    {
        var parsed = CliArguments.Parse(["update-keygen"]);
        Assert.Throws<CliUsageException>(() => parsed.RequiredOption("key-file"));
    }

    [Fact]
    public void OptionalOption_Whitespace_TreatedAsMissing()
    {
        var parsed = CliArguments.Parse(["bundle", "--publisher=  "]);
        Assert.Null(parsed.OptionalOption("publisher"));
        Assert.Throws<CliUsageException>(() => parsed.RequiredOption("publisher"));
    }

    [Fact]
    public void Parse_VerboseEnv_HonoredUnlessQuiet()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_BUNDLER_VERBOSE");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_BUNDLER_VERBOSE", "1");
            Assert.True(CliArguments.Parse(["bundle"]).Verbose);
            Assert.False(CliArguments.Parse(["bundle", "--quiet"]).Verbose);

            Environment.SetEnvironmentVariable("DOTNET_BUNDLER_VERBOSE", "0");
            Assert.False(CliArguments.Parse(["bundle"]).Verbose);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_BUNDLER_VERBOSE", previous);
        }
    }
}

using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class BundleConfigurationValidatorTests
{
    private static IReadOnlyList<ValidationIssue> Validate(
        BundleConfiguration config, bool checkFileSystem = false) =>
        BundleConfigurationValidator.Validate(config, checkFileSystem);

    private static bool HasIssue(IReadOnlyList<ValidationIssue> issues, string pathPrefix) =>
        issues.Any(issue => issue.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

    [Fact]
    public void ValidConfig_Passes()
    {
        Assert.Empty(Validate(ValidConfig.Create()));
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BundleConfigurationValidator.Validate(null!));
    }

    [Theory]
    [InlineData("productName")]
    [InlineData("identifier")]
    [InlineData("version")]
    [InlineData("outputDirectory")]
    public void Required_Missing_Reports(string field)
    {
        var config = ValidConfig.Create();
        config = field switch
        {
            "productName" => new BundleConfiguration
            {
                Identifier = config.Identifier, Version = config.Version,
                OutputDirectory = config.OutputDirectory, Targets = config.Targets
            },
            "identifier" => new BundleConfiguration
            {
                ProductName = config.ProductName, Version = config.Version,
                OutputDirectory = config.OutputDirectory, Targets = config.Targets
            },
            "version" => new BundleConfiguration
            {
                ProductName = config.ProductName, Identifier = config.Identifier,
                OutputDirectory = config.OutputDirectory, Targets = config.Targets
            },
            _ => new BundleConfiguration
            {
                ProductName = config.ProductName, Identifier = config.Identifier,
                Version = config.Version, OutputDirectory = "", Targets = config.Targets
            }
        };

        var issues = Validate(config);
        Assert.Contains(issues, issue => issue.Path == field);
    }

    [Theory]
    [InlineData("com.example.app", false)]
    [InlineData("a.b", false)]
    [InlineData("Com-Example.App-2", false)]
    [InlineData("com.example.", true)]
    [InlineData(".com.example", true)]
    [InlineData("com..example", true)]
    [InlineData("com.ex_ample.app", true)]
    [InlineData("single", true)]
    [InlineData("-com.example", true)]
    public void Identifier_ReverseDnsPattern(string identifier, bool expectIssue)
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = identifier, Version = config.Version,
            OutputDirectory = config.OutputDirectory, Targets = config.Targets
        };
        Assert.Equal(expectIssue, HasIssue(Validate(config), "identifier"));
    }

    [Theory]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3-beta.1", false)]
    [InlineData("1.2.3+build.5", false)]
    [InlineData("0.0.1-rc", false)]
    [InlineData("1.2", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3-", true)]
    public void Version_SemVerLike(string version, bool expectIssue)
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier, Version = version,
            OutputDirectory = config.OutputDirectory, Targets = config.Targets
        };
        Assert.Equal(expectIssue, HasIssue(Validate(config), "version"));
    }

    [Theory]
    [InlineData("https://example.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("ftp://example.com", true)]
    [InlineData("example.com", true)]
    [InlineData("not a url", true)]
    public void Homepage_OnlyHttpHttps(string homepage, bool expectIssue)
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier, Version = config.Version,
            OutputDirectory = config.OutputDirectory, Targets = config.Targets, Homepage = homepage
        };
        Assert.Equal(expectIssue, HasIssue(Validate(config), "homepage"));
    }

    [Fact]
    public void Targets_Empty_Reports()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier, Version = config.Version,
            OutputDirectory = config.OutputDirectory
        };
        Assert.Contains(Validate(config), issue => issue.Path == "targets");
    }

    [Fact]
    public void Target_Unparsable_Reports()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier, Version = config.Version,
            OutputDirectory = config.OutputDirectory,
            Targets = [new BundleTargetConfiguration
            {
                Target = "win-x64", InputDirectory = "input", Formats = [PackageFormat.Nsis]
            }]
        };
        Assert.Contains(Validate(config), issue => issue.Path == "targets[0].target");
    }

    [Fact]
    public void Target_FormatsEmpty_Reports()
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64", InputDirectory = "input"
        });
        Assert.Contains(Validate(config), issue => issue.Path == "targets[0].formats");
    }

    [Fact]
    public void Target_UnsupportedFormat_Reports()
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64", InputDirectory = "input", Formats = [PackageFormat.Nsis]
        });
        Assert.Contains(Validate(config),
            issue => issue.Path == "targets[0].formats" && issue.Message.Contains("Nsis"));
    }

    [Theory]
    [InlineData("   ")]
    public void MainExecutable_Blank_Reports(string executable)
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64", InputDirectory = "input",
            MainExecutable = executable, Formats = [PackageFormat.Deb]
        });
        Assert.Contains(Validate(config),
            issue => issue.Path == "targets[0].mainExecutable" && issue.Message.Contains("blank"));
    }

    [Theory]
    [InlineData("/abs/path/app")]
    [InlineData("../escape")]
    public void MainExecutable_EscapesInput_Reports(string executable)
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64", InputDirectory = "input",
            MainExecutable = executable, Formats = [PackageFormat.Deb]
        });
        Assert.Contains(Validate(config), issue => issue.Path == "targets[0].mainExecutable");
    }

    [Fact]
    public void SigningFiles_RelativeOnly()
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64", InputDirectory = "input",
            Formats = [PackageFormat.Deb],
            SigningFiles = ["lib/a.so", "/abs/evil.so", "../up.so", ""]
        });
        var issues = Validate(config);
        Assert.False(HasIssue(issues, "targets[0].signingFiles[0]"));
        Assert.True(HasIssue(issues, "targets[0].signingFiles[1]"));
        Assert.True(HasIssue(issues, "targets[0].signingFiles[2]"));
        Assert.True(HasIssue(issues, "targets[0].signingFiles[3]"));
    }

    [Fact]
    public void OutputDirectory_InsideInput_Reports()
    {
        var input = Path.Combine(Path.GetTempPath(), "bundler-test-input");
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64",
            InputDirectory = input,
            Formats = [PackageFormat.Deb]
        }, outputDirectory: Path.Combine(input, "artifacts"));
        Assert.Contains(Validate(config, checkFileSystem: true),
            issue => issue.Path == "outputDirectory");
    }

    [Fact]
    public void InputDirectory_Missing_Reports_WhenCheckingFileSystem()
    {
        var config = WithTarget(new BundleTargetConfiguration
        {
            Target = "linux-x86_64",
            InputDirectory = Path.Combine(Path.GetTempPath(), $"bundler-no-such-{Guid.NewGuid():N}"),
            Formats = [PackageFormat.Deb]
        });
        Assert.Contains(Validate(config, checkFileSystem: true),
            issue => issue.Path == "targets[0].inputDirectory");
        Assert.DoesNotContain(Validate(config, checkFileSystem: false),
            i => i.Path.Contains("inputDirectory") && i.Message.Contains("does not exist"));
    }

    [Fact]
    public void Resources_SourceDestinationRequired_NoEscape()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = config.Targets,
            Resources =
            [
                new BundleResourceConfiguration { Source = "", Destination = "ok" },
                new BundleResourceConfiguration { Source = "s", Destination = "" },
                new BundleResourceConfiguration { Source = "s", Destination = "../escape" },
                new BundleResourceConfiguration { Source = "s", Destination = "/abs" },
                new BundleResourceConfiguration { Source = "s", Destination = "C:/rooted" },
                new BundleResourceConfiguration { Source = "s", Destination = "share/readme.txt" }
            ]
        };
        var issues = Validate(config);
        Assert.True(HasIssue(issues, "resources[0].source"));
        Assert.True(HasIssue(issues, "resources[1].targetPath"));
        Assert.True(HasIssue(issues, "resources[2].targetPath"));
        Assert.True(HasIssue(issues, "resources[3].targetPath"));
        Assert.True(HasIssue(issues, "resources[4].targetPath"));
        Assert.False(HasIssue(issues, "resources[5]"));
    }

    [Theory]
    [InlineData("license.txt", false)]
    [InlineData("license.rtf", false)]
    [InlineData("license.md", true)]
    [InlineData("license", true)]
    public void LicenseFile_ExtensionWhitelist(string licenseFile, bool expectIssue)
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = config.Targets, LicenseFile = licenseFile
        };
        Assert.Equal(expectIssue, HasIssue(Validate(config), "licenseFile"));
    }

    [Fact]
    public void FileAssociations_ExtensionPatternAndDedup()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = config.Targets,
            FileAssociations =
            [
                new BundleFileAssociationConfiguration { Extensions = [".myext", "other+2"] },
                new BundleFileAssociationConfiguration { Extensions = ["myext"] },
                new BundleFileAssociationConfiguration { Extensions = ["bad ext!"] },
                new BundleFileAssociationConfiguration { Extensions = [] }
            ]
        };
        var issues = Validate(config);
        Assert.False(HasIssue(issues, "fileAssociations[0]"));
        Assert.Contains(issues, i => i.Path == "fileAssociations[1].extensions[0]" && i.Message.Contains("Duplicate"));
        Assert.Contains(issues, i => i.Path == "fileAssociations[2].extensions[0]");
        Assert.Contains(issues, i => i.Path == "fileAssociations[3].extensions");
    }

    [Fact]
    public void UrlProtocols_SchemePatternAndDedup()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = config.Targets,
            UrlProtocols =
            [
                new BundleUrlProtocolConfiguration { Schemes = ["myapp", "myapp+v2"] },
                new BundleUrlProtocolConfiguration { Schemes = ["MYAPP"] },
                new BundleUrlProtocolConfiguration { Schemes = ["9bad"] },
                new BundleUrlProtocolConfiguration { Schemes = [] }
            ]
        };
        var issues = Validate(config);
        Assert.False(HasIssue(issues, "urlProtocols[0]"));
        Assert.Contains(issues, i => i.Path == "urlProtocols[1].schemes[0]" && i.Message.Contains("Duplicate"));
        Assert.Contains(issues, i => i.Path == "urlProtocols[2].schemes[0]");
        Assert.Contains(issues, i => i.Path == "urlProtocols[3].schemes");
    }

    [Fact]
    public void Update_Null_SkipsValidation()
    {
        Assert.Empty(Validate(ValidConfig.Create()));
    }

    [Fact]
    public void Update_Enabled_RequiresFeedUrlAndSigningKey()
    {
        var config = WithUpdate(new UpdateBundleConfiguration { Channel = "stable" });
        var issues = Validate(config);
        Assert.True(HasIssue(issues, "update.feedUrl"));
        Assert.True(HasIssue(issues, "update.signingKeyFile"));
    }

    [Theory]
    [InlineData("latest", false)]
    [InlineData("stable.2", false)]
    [InlineData("beta_1", false)]
    [InlineData("my-channel", false)]
    [InlineData("bad/channel", true)]
    [InlineData("bad channel", true)]
    [InlineData("-lead", true)]
    public void Update_ChannelFilenameSafe(string channel, bool expectIssue)
    {
        var config = WithUpdate(new UpdateBundleConfiguration
        {
            FeedUrl = "https://example.com/feed", Channel = channel, SigningKeyFile = "key.json"
        });
        Assert.Equal(expectIssue, HasIssue(Validate(config), "update.channel"));
    }

    [Fact]
    public void Update_PublicKey_MustBeP256Point()
    {
        var config = WithUpdate(new UpdateBundleConfiguration
        {
            FeedUrl = "https://example.com/feed",
            SigningKeyFile = "key.json",
            PublicKey = Convert.ToBase64String(new byte[65])
        });
        Assert.True(HasIssue(Validate(config), "update.publicKey"));

        var point = new byte[65];
        point[0] = 0x04;
        config = WithUpdate(new UpdateBundleConfiguration
        {
            FeedUrl = "https://example.com/feed",
            SigningKeyFile = "key.json",
            PublicKey = Convert.ToBase64String(point)
        });
        Assert.False(HasIssue(Validate(config), "update.publicKey"));
    }

    [Fact]
    public void DuplicateOutputs_SameTargetSameFormat_Reports()
    {
        var config = ValidConfig.Create();
        var target = config.Targets[0];
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = [target, new BundleTargetConfiguration
            {
                Target = target.Target, InputDirectory = "other-input",
                Formats = [PackageFormat.Deb]
            }]
        };
        Assert.Contains(Validate(config),
            issue => issue.Path == "targets[1].formats" && issue.Message.Contains("Duplicate"));
    }

    [Fact]
    public void DuplicateOutputs_DmgImplicitlyClaimsAppSlot()
    {
        var config = ValidConfig.Create();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = "macos-aarch64", InputDirectory = "in1",
                    Formats = [PackageFormat.App]
                },
                new BundleTargetConfiguration
                {
                    Target = "macos-aarch64", InputDirectory = "in2",
                    Formats = [PackageFormat.Dmg]
                }
            ]
        };
        // dmg 隐式注入 app 中间项占位 → 与显式 app 撞槽
        Assert.Contains(Validate(config), issue => issue.Message.Contains("Duplicate"));
    }

    private static BundleConfiguration WithTarget(
        BundleTargetConfiguration target, string? outputDirectory = null)
    {
        var config = ValidConfig.Create();
        return new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version,
            OutputDirectory = outputDirectory ?? config.OutputDirectory,
            Targets = [target]
        };
    }

    private static BundleConfiguration WithUpdate(UpdateBundleConfiguration update)
    {
        var config = ValidConfig.Create();
        return new BundleConfiguration
        {
            ProductName = config.ProductName, Identifier = config.Identifier,
            Version = config.Version, OutputDirectory = config.OutputDirectory,
            Targets = config.Targets, Update = update
        };
    }
}

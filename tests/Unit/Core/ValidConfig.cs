using DotNet.Bundler;

namespace Bundler.UnitTests.Core;

/// <summary>通过全部 checkFileSystem=false 校验的最小合法配置。</summary>
internal static class ValidConfig
{
    public static BundleConfiguration Create() => new()
    {
        ProductName = "Sample",
        Identifier = "com.example.sample",
        Version = "1.2.3",
        OutputDirectory = "artifacts",
        Targets =
        [
            new BundleTargetConfiguration
            {
                Target = "linux-x86_64",
                InputDirectory = "input",
                Formats = [PackageFormat.Deb]
            }
        ]
    };
}

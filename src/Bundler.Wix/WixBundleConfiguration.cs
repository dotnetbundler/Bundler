namespace DotNet.Bundler.Wix;

public enum WixInstallScope
{
    CurrentUser,
    PerMachine
}

public sealed class WixBundleConfiguration
{
    public WixInstallScope InstallScope { get; init; } = WixInstallScope.CurrentUser;
    public string? UpgradeCode { get; init; }
    public int Codepage { get; init; } = 1252;
}

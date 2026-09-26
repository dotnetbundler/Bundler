Console.WriteLine($"BundlerMacIntegrationFixture:{string.Join(",", args)}");

// LaunchServices-launched invocations (open <file> / open <scheme>://) cannot reach stdout;
// drop a marker inside the bundle so the integration script can prove the app actually ran.
var contentsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
if (contentsDir.EndsWith("Contents", StringComparison.Ordinal))
{
    File.WriteAllText(Path.Combine(contentsDir, ".launch-marker"), string.Join(",", args));
}

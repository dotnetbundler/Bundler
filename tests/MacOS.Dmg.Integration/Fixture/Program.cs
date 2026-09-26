Console.WriteLine($"BundlerMacDmgIntegrationFixture:{string.Join(",", args)}");

// Mounted-dmg invocations cannot reach stdout; drop a marker inside the bundle copy
// so the integration script can prove the app actually ran from the image.
var contentsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
if (contentsDir.EndsWith("Contents", StringComparison.Ordinal))
{
    try
    {
        File.WriteAllText(Path.Combine(contentsDir, ".launch-marker"), string.Join(",", args));
    }
    catch (IOException)
    {
        // Read-only image mount: the stdout marker already proves the app ran.
    }
}

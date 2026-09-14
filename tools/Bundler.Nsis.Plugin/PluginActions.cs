using NsisPlugin;

namespace DotNet.Bundler.Nsis.Plugin;

public static class PluginActions
{
    [NsisAction]
    public static int SemverCompare(string candidate, string installed)
    {
        if (!SemanticVersion.TryParse(candidate, out var candidateVersion) ||
            !SemanticVersion.TryParse(installed, out var installedVersion))
        {
            // NSIS treats 2 as an unknown comparison and applies the conservative policy.
            return 2;
        }

        return Math.Sign(candidateVersion!.CompareTo(installedVersion));
    }
}

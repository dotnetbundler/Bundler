using System.Diagnostics;
using System.Threading;

namespace DotNet.Bundler.AppImage;

/// <summary>
/// Prepares an isolated GNUPGHOME for <c>appimagetool --sign</c>: the
/// caller-supplied secret key is imported into a throwaway keyring inside the
/// build work directory, and the dedicated gpg-agent is torn down after the
/// build. The passphrase reaches appimagetool through
/// <c>APPIMAGETOOL_SIGN_PASSPHRASE</c> — it never appears on a command line.
/// Host gpg is required only when signing is configured.
/// </summary>
internal static class AppImageSigning
{
    internal sealed class SigningContext : IDisposable
    {
        internal string GnupgHome = "";

        public void Dispose()
        {
            if (GnupgHome.Length == 0 || !Directory.Exists(GnupgHome))
            {
                return;
            }
            try
            {
                AppImageProcessRunner.RunAsync(
                    "gpgconf", ["--kill", "gpg-agent"], GnupgHome,
                    CancellationToken.None,
                    new Dictionary<string, string> { ["GNUPGHOME"] = GnupgHome })
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Agent teardown is best-effort; the work directory is removed
                // wholesale and a leftover agent is bound to this GNUPGHOME.
            }
        }
    }

    internal static SigningContext Prepare(string keyFile, string workDirectory)
    {
        var gnupgHome = Path.Combine(workDirectory, "gnupg");
        Directory.CreateDirectory(gnupgHome);
        // gpg refuses a group/world-readable homedir.
        using (var process = Process.Start(
                   new ProcessStartInfo("chmod") { Arguments = $"700 \"{gnupgHome}\"" }))
        {
            process?.WaitForExit();
        }

        AppImageProcessRunner.RunAsync(
            "gpg", ["--batch", "--yes", "--import", keyFile],
            workDirectory, CancellationToken.None,
            new Dictionary<string, string> { ["GNUPGHOME"] = gnupgHome })
            .GetAwaiter().GetResult();
        return new SigningContext { GnupgHome = gnupgHome };
    }
}

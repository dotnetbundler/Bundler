using System.Threading;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

/// <summary>
/// Prepares an isolated GNUPGHOME for <c>appimagetool --sign</c>: the
/// caller-supplied secret key is imported into a throwaway keyring under the
/// build work directory (<c>.gnupg</c>, sibling of the staged AppDir so it is
/// never packaged; a crashed build leaves it for the pipeline's stale-workdir
/// sweep rather than an orphan in the shared temp root), and the dedicated
/// gpg-agent is torn down and the keyring deleted on Dispose.
/// The passphrase reaches appimagetool through <c>APPIMAGETOOL_SIGN_PASSPHRASE</c>
/// — it never appears on a command line. Host gpg is required only when
/// signing is configured.
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
                ToolRunner.RunAsync(
                    "gpgconf", ["--kill", "gpg-agent"], GnupgHome,
                    CancellationToken.None,
                    new Dictionary<string, string> { ["GNUPGHOME"] = GnupgHome })
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Agent teardown is best-effort — a leftover agent is bound to
                // this GNUPGHOME and dies with it.
            }
            try
            {
                Directory.Delete(GnupgHome, recursive: true);
            }
            catch
            {
                // 私钥环残片不能静默留置——但删目录失败不遮构建主错误。
            }
        }
    }

    internal static SigningContext Prepare(string keyFile, string workDirectory)
    {
        var gnupgHome = Path.Combine(workDirectory, ".gnupg");
        if (Directory.Exists(gnupgHome))
        {
            Directory.Delete(gnupgHome, recursive: true);
        }
        Directory.CreateDirectory(gnupgHome);
        try
        {
            // gpg refuses a group/world-readable homedir.
            // 走同一个带参数转义与退出码检查的 runner——裸拼参数且不看退出码会静默产坏 home。
            ToolRunner.RunAsync(
                "chmod", ["700", gnupgHome], workDirectory, CancellationToken.None)
                .GetAwaiter().GetResult();

            ToolRunner.RunAsync(
                "gpg", ["--batch", "--yes", "--import", keyFile],
                workDirectory, CancellationToken.None,
                new Dictionary<string, string> { ["GNUPGHOME"] = gnupgHome })
                .GetAwaiter().GetResult();
        }
        catch
        {
            // 导入失败时导入了一半的私钥环不能留在盘上。
            try { Directory.Delete(gnupgHome, recursive: true); } catch { }
            throw;
        }
        return new SigningContext { GnupgHome = gnupgHome };
    }
}

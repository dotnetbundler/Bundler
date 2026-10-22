namespace DotNet.Bundler.Core;

/// <summary>
/// Validation for the <c>*BinLink</c> knobs (deb/rpm/apk/AppImage): the link is
/// created under <c>$installRoot/bin</c> (or the AppImage bin dir), so the name
/// must be a single plain file name — no separators, whitespace, quoting, or
/// shell-expanding characters that would survive into maintainer scripts or
/// generated launchers. "<c>none</c>" disables the link; the caller resolves
/// that before invoking this validator.
/// </summary>
internal static class LinuxLinkName
{
    // Characters refused outright: path separators (\ is a separator on Windows
    // hosts and an escape lead-in for shell snippets), whitespace, quoting, and
    // expansion/substitution metacharacters that maintainer scripts or wrapper
    // scripts would act on. Rejected uniformly by all four consumers.
    private static readonly char[] Forbidden =
        { '/', '\\', ' ', '\t', '\'', '"', '$', '`', ';', '|', '&', '<', '>',
          '(', ')', '[', ']', '{', '}', '*', '?', '!', '#', '~', '@', '%', '^', ',', '=', ':' };

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless <paramref name="value"/> is
    /// a safe single file name for a bin link; returns it unchanged otherwise.
    /// </summary>
    internal static string Validate(string? value, string knobName)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException(
                $"{knobName} must name the bin link (a single file name) — " +
                "use \"none\" to disable the link.");
        }
        if (value!.Length != name.Length)
        {
            throw new ArgumentException($"{knobName} must not have leading or trailing whitespace.");
        }
        if (name is "." or "..")
        {
            throw new ArgumentException($"{knobName} must be a plain file name, not '{name}'.");
        }
        var bad = name.IndexOfAny(Forbidden);
        if (bad >= 0 || name.Any(char.IsControl))
        {
            var shown = bad >= 0 ? name[bad] : name.First(char.IsControl);
            throw new ArgumentException(
                $"{knobName} must be a plain file name for the bin link, " +
                $"got '{name}' (forbidden character '{shown}').");
        }
        return name;
    }
}

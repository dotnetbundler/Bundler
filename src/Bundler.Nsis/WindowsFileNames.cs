namespace DotNet.Bundler.Nsis;

// Installer and shortcut paths are Windows-side artifacts: they must be validated
// against Windows file-name rules no matter which host runs the backend.
internal static class WindowsFileNames
{
    private const string InvalidCharacters = "<>:\"/\\|?*";

    internal static bool ContainsInvalidCharacter(string value) =>
        value.IndexOfAny(InvalidCharacters.ToCharArray()) >= 0 || value.Any(char.IsControl);

    internal static string Sanitize(string value) =>
        new(value.Select(character =>
            InvalidCharacters.IndexOf(character) >= 0 || char.IsControl(character) ? '_' : character).ToArray());
}

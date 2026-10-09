namespace DotNet.Bundler.Nsis;

// Installer and shortcut paths are Windows-side artifacts: they must be validated
// against Windows file-name rules no matter which host runs the backend.
internal static class WindowsFileNames
{
    private const string InvalidCharacters = "<>:\"/\\|?*";

    // 设备保留名按"去扩展名的基名"判定（con.txt 同样被 Windows 拒绝）。
    private static readonly string[] ReservedDeviceNames =
        ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5",
         "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4",
         "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    internal static bool ContainsInvalidCharacter(string value) =>
        value.IndexOfAny(InvalidCharacters.ToCharArray()) >= 0 || value.Any(char.IsControl) ||
        IsReservedDeviceName(value);

    internal static bool IsReservedDeviceName(string value)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(value);
        return ReservedDeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    internal static string Sanitize(string value) =>
        new(value.Select(character =>
            InvalidCharacters.IndexOf(character) >= 0 || char.IsControl(character) ? '_' : character).ToArray());
}

namespace DotNet.Bundler.Core;

// Installer/shortcut/package paths are Windows-side artifacts: they must be
// validated against Windows file-name rules no matter which host runs the
// backend — never use Path.GetInvalidFileNameChars() (host-dependent).
internal static class WindowsFileNames
{
    private const string InvalidCharacters = "<>:\"/\\|?*";

    // 设备保留名按首个 '.' 前的基名判定（con.tar.gz 同样被 Windows 拒绝）。
    private static readonly string[] ReservedDeviceNames =
        ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5",
         "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4",
         "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    internal static bool ContainsInvalidCharacter(string value) =>
        value.IndexOfAny(InvalidCharacters.ToCharArray()) >= 0 || value.Any(char.IsControl) ||
        IsReservedDeviceName(value);

    // 单个 Windows 文件名的完整合法性：非法字符、控制字符、设备保留名、尾点/尾空格。
    internal static bool IsInvalidName(string value) =>
        ContainsInvalidCharacter(value) ||
        value.EndsWith(" ", StringComparison.Ordinal) || value.EndsWith(".", StringComparison.Ordinal);

    internal static bool IsReservedDeviceName(string value)
    {
        var name = value.Split('.')[0];
        return ReservedDeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    internal static string Sanitize(string value) =>
        new(value.Select(character =>
            InvalidCharacters.IndexOf(character) >= 0 || char.IsControl(character) ? '_' : character).ToArray());

    // 清洗成可直接落盘的 Windows 名：非法字符→'_'、首尾空白与尾点剥除、
    // 设备保留名/纯点/空结果回落 fallback——"CON"→"CON.exe" 这类 Windows
    // 拒绝创建的名字不会漏出。
    internal static string SafeFileName(string value, string fallback)
    {
        var result = Sanitize(value).Trim().TrimEnd('.', ' ');
        return result.Length == 0 || IsReservedDeviceName(result) ? fallback : result;
    }
}

using System.Text;
using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;
using Bundler.Core.Tools;

namespace Bundler.Core.Backends.Windows;

public sealed class NsisBundleBackend(string compilerPath) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Nsis;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;

    public async Task<BundleArtifact> BuildAsync(
        BundleConfiguration configuration,
        BundlePlanItem item,
        CancellationToken cancellationToken = default)
    {
        if (!System.OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NSIS packages can currently be built only on Windows hosts.");
        }

        var fullCompilerPath = Path.GetFullPath(compilerPath);
        if (!File.Exists(fullCompilerPath))
        {
            throw new FileNotFoundException("makensis.exe was not found.", fullCompilerPath);
        }

        Directory.CreateDirectory(item.OutputDirectory);
        var safeProductName = SafeFileName(configuration.ProductName);
        var installerPath = Path.Combine(
            item.OutputDirectory,
            $"{safeProductName}-{configuration.Version}-setup.exe");
        var workDirectory = Path.Combine(item.OutputDirectory, $".work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            var scriptPath = Path.Combine(workDirectory, "installer.nsi");
            await File.WriteAllTextAsync(
                scriptPath,
                CreateScript(configuration, item, installerPath, safeProductName),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);

            await ProcessRunner.RunAsync(
                fullCompilerPath,
                ["/V2", scriptPath],
                workDirectory,
                cancellationToken);

            if (!File.Exists(installerPath))
            {
                throw new InvalidOperationException("NSIS reported success but did not create the installer.");
            }

            return new BundleArtifact(Format, item.Target.RuntimeIdentifier, installerPath);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
    }

    private static string CreateScript(
        BundleConfiguration configuration,
        BundlePlanItem item,
        string installerPath,
        string safeProductName)
    {
        var publisher = configuration.Publisher ?? configuration.ProductName;
        var uninstallKey = $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{configuration.Identifier}";
        var version = NumericVersion(configuration.Version);

        return $$"""
            Unicode true
            !include "MUI2.nsh"

            !define PRODUCT_NAME "{{Escape(configuration.ProductName)}}"
            !define PRODUCT_VERSION "{{Escape(configuration.Version)}}"
            !define PRODUCT_PUBLISHER "{{Escape(publisher)}}"
            !define PRODUCT_ID "{{Escape(configuration.Identifier)}}"
            !define MAIN_EXECUTABLE "{{Escape(item.MainExecutable)}}"

            Name "${PRODUCT_NAME}"
            OutFile "{{Escape(installerPath)}}"
            InstallDir "$LOCALAPPDATA\Programs\{{Escape(safeProductName)}}"
            RequestExecutionLevel user
            SetCompressor /SOLID lzma
            VIProductVersion "{{version}}"
            VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
            VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
            VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
            VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Installer"

            !insertmacro MUI_PAGE_WELCOME
            !insertmacro MUI_PAGE_INSTFILES
            !insertmacro MUI_PAGE_FINISH
            !insertmacro MUI_UNPAGE_CONFIRM
            !insertmacro MUI_UNPAGE_INSTFILES
            !insertmacro MUI_LANGUAGE "English"

            Section "Install"
              SetShellVarContext current
              SetOutPath "$INSTDIR"
              File /r "{{Escape(Path.Combine(item.InputDirectory, "*"))}}"
              WriteUninstaller "$INSTDIR\Uninstall.exe"
              CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
              CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXECUTABLE}"
              CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXECUTABLE}"
              WriteRegStr HKCU "{{Escape(uninstallKey)}}" "DisplayName" "${PRODUCT_NAME}"
              WriteRegStr HKCU "{{Escape(uninstallKey)}}" "DisplayVersion" "${PRODUCT_VERSION}"
              WriteRegStr HKCU "{{Escape(uninstallKey)}}" "Publisher" "${PRODUCT_PUBLISHER}"
              WriteRegStr HKCU "{{Escape(uninstallKey)}}" "DisplayIcon" "$INSTDIR\${MAIN_EXECUTABLE}"
              WriteRegStr HKCU "{{Escape(uninstallKey)}}" "UninstallString" '$"$INSTDIR\Uninstall.exe$"'
              WriteRegDWORD HKCU "{{Escape(uninstallKey)}}" "NoModify" 1
              WriteRegDWORD HKCU "{{Escape(uninstallKey)}}" "NoRepair" 1
            SectionEnd

            Section "Uninstall"
              SetShellVarContext current
              Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
              RMDir /r "$SMPROGRAMS\${PRODUCT_NAME}"
              DeleteRegKey HKCU "{{Escape(uninstallKey)}}"
              RMDir /r "$INSTDIR"
            SectionEnd
            """;
    }

    private static string Escape(string value) => value
        .Replace("$", "$$", StringComparison.Ordinal)
        .Replace("\"", "$\\\"", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? "Application" : result;
    }

    private static string NumericVersion(string version)
    {
        var components = version.Split(['-', '+'], 2)[0].Split('.');
        return string.Join('.', components.Take(3).Append("0"));
    }
}

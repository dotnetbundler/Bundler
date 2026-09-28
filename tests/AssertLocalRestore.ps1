if (-not ('BundlerShortcutWriter' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out] StringBuilder file, int capacity, IntPtr findData, uint flags);
    void GetIDList(out IntPtr idList);
    void SetIDList(IntPtr idList);
    void GetDescription([Out] StringBuilder name, int capacity);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out] StringBuilder dir, int capacity);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out] StringBuilder args, int capacity);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out] StringBuilder iconPath, int capacity, out int iconIndex);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);
    void IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass { }

public sealed class BundlerShortcutInfo
{
    public string TargetPath;
    public string Arguments;
    public string WorkingDirectory;
    public string IconLocation;
}

public static class BundlerShortcutWriter
{
    public static void Write(string path, string targetPath, string workingDirectory)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(targetPath);
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                link.SetWorkingDirectory(workingDirectory);
            }
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}

public static class BundlerShortcutReader
{
    private const int BufferCapacity = 4096;

    public static BundlerShortcutInfo Read(string path)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(BufferCapacity);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            if (target.Length == 0)
            {
                // StringData 未落盘的目标靠 IDList/LinkInfo 解析兜底。
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            }
            var arguments = new StringBuilder(BufferCapacity);
            link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(BufferCapacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var iconPath = new StringBuilder(BufferCapacity);
            int iconIndex;
            link.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex);
            return new BundlerShortcutInfo
            {
                TargetPath = target.ToString(),
                Arguments = arguments.ToString(),
                WorkingDirectory = workingDirectory.ToString(),
                IconLocation = iconPath.Length == 0 ? "" : iconPath + "," + iconIndex
            };
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
'@
}

function Get-ShellShortcut {
    param([Parameter(Mandatory)][string]$Path)
    return [BundlerShortcutReader]::Read($Path)
}

function Set-ShellShortcut {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$TargetPath,
        [string]$WorkingDirectory = ''
    )
    [BundlerShortcutWriter]::Write($Path, $TargetPath, $WorkingDirectory)
}

function Get-BundlerPackageVersion {
    param([Parameter(Mandatory)][string]$Repository)

    $path = Join-Path $Repository 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Shared local package configuration is missing: $path"
    }
    [xml]$props = Get-Content -LiteralPath $path -Raw
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/BundlerPackageVersion')
    $version = if ($null -ne $versionNode) { [string]$versionNode.InnerText } else { '' }
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "BundlerPackageVersion is missing from $path"
    }
    return $version.Trim()
}

function Assert-LocalBundlerRestore {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Cache,
        [Parameter(Mandatory)][string[]]$RequiredPackages
    )

    $assetsPath = Join-Path (Split-Path -Parent $Project) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Restore assets are missing: $assetsPath" }
    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $expectedSource = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
    $expectedCache = [IO.Path]::GetFullPath($Cache).TrimEnd('\', '/')
    $configuredSources = @($assets.project.restore.sources.PSObject.Properties.Name)
    # 本地源经 RestoreAdditionalProjectSources 追加注入，源列表随宿主机 NuGet 配置变化——
    # 不校验源清单形状，只要求本地源在场；包的真实来源由下方 .nupkg.metadata 逐包核验。
    $sources = @($configuredSources |
        Where-Object { $_ -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://' } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') })
    if (-not ($sources | Where-Object { $_ -eq $expectedSource })) {
        throw "Restore did not use the local package source: $expectedSource"
    }
    $folders = @($assets.packageFolders.PSObject.Properties.Name |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') })
    if ($folders -notcontains $expectedCache) {
        throw "Restore did not use the isolated package cache: $expectedCache"
    }
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    foreach ($name in $RequiredPackages) {
        if ($libraries -notcontains "$name/$PackageVersion") {
            throw "Restored package is missing or has the wrong version: $name/$PackageVersion"
        }
        $packageDir = Join-Path $expectedCache ("{0}\{1}" -f $name.ToLowerInvariant(), $PackageVersion)
        $packagePath = Join-Path $packageDir ("{0}.{1}.nupkg" -f $name.ToLowerInvariant(), $PackageVersion)
        if (-not (Test-Path -LiteralPath $packagePath)) {
            throw "Restored package is missing from the isolated cache: $packagePath"
        }
        $metadataPath = Join-Path $packageDir '.nupkg.metadata'
        if (-not (Test-Path -LiteralPath $metadataPath)) {
            throw "Restored package is missing provenance metadata: $metadataPath"
        }
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $packageOrigin = $metadata.source
        if ([string]::IsNullOrWhiteSpace($packageOrigin) -or
            $packageOrigin -match '^[a-zA-Z][a-zA-Z0-9+.-]*://' -or
            [IO.Path]::GetFullPath($packageOrigin).TrimEnd('\', '/') -ne $expectedSource) {
            throw "Restored package did not come from the local source: $name/$PackageVersion (source: $packageOrigin)"
        }
    }
    $wrongVersions = @($libraries | Where-Object {
        $_ -like 'DotNet.Bundler/*' -or $_ -like 'DotNet.Bundler.*/*'
    } | Where-Object { $_ -notlike "*/$PackageVersion" })
    if ($wrongVersions.Count -gt 0) {
        throw "Restore selected unexpected Bundler versions: $($wrongVersions -join ', ')"
    }
    Write-Host "PASS: local package source and isolated cache contain Bundler $PackageVersion for $Project"
}

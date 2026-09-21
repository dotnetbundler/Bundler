param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Prepare", "Verify")]
    [string]$Phase,

    [string]$InstallerPath,
    [string]$InstallDirectory = "$env:ProgramFiles\DotNet Bundler Reboot Fixture",
    [string]$Identifier = "com.dotnetbundler.integrationfixture",
    [switch]$ConfirmDisposableMachine
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmDisposableMachine) {
    throw "This validation intentionally creates a machine-wide pending delete and requires a reboot. Run only in a disposable Windows VM and pass -ConfirmDisposableMachine."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$fullInstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not $fullInstallDirectory.StartsWith($programFilesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The reboot fixture install directory must remain under Program Files: $fullInstallDirectory"
}

$stateRoot = Join-Path $env:ProgramData "DotNetBundler\reboot-validation"
$stateFile = Join-Path $stateRoot "$Identifier.json"
$transactionDirectory = Join-Path $env:ProgramData "DotNetBundler\transactions\$Identifier"
$uninstallRegistryPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$Identifier"
$pendingRenamePath = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager"

function Get-PendingRenameOperations {
    $value = Get-ItemPropertyValue -LiteralPath $pendingRenamePath -Name "PendingFileRenameOperations" -ErrorAction SilentlyContinue
    if ($null -eq $value) { return @() }
    return @($value)
}

if ($Phase -eq "Prepare") {
    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        throw "Prepare requires -InstallerPath pointing to the perMachine integration fixture installer."
    }
    $fullInstallerPath = [IO.Path]::GetFullPath($InstallerPath)
    if (-not (Test-Path -LiteralPath $fullInstallerPath -PathType Leaf)) {
        throw "Installer not found: $fullInstallerPath"
    }
    if (Test-Path -LiteralPath $stateFile) {
        throw "A reboot validation is already pending: $stateFile"
    }

    $install = Start-Process -FilePath $fullInstallerPath -ArgumentList "/S /D=$fullInstallDirectory" -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "Fixture install failed with exit code $($install.ExitCode)."
    }

    $lockedFile = Join-Path $fullInstallDirectory "docs\license.txt"
    $uninstaller = Join-Path $fullInstallDirectory "Uninstall.exe"
    if (-not (Test-Path -LiteralPath $lockedFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "The installed reboot fixture is incomplete."
    }

    # 直接启动安装目录中的 NSIS 卸载器时，外层临时 launcher 不传播实际卸载进程
    # 的退出码。显式复制并使用 `_?=`，才能验证真正的 3010，同时避免锁住原卸载器。
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $directUninstaller = Join-Path $stateRoot "direct-uninstaller.exe"
    Copy-Item -LiteralPath $uninstaller -Destination $directUninstaller
    $lock = [IO.File]::Open($lockedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $uninstall = Start-Process -FilePath $directUninstaller -ArgumentList "/S /DELETEAPPDATA _?=$fullInstallDirectory" -Wait -PassThru
    }
    finally {
        $lock.Dispose()
        Remove-Item -LiteralPath $directUninstaller -Force -ErrorAction SilentlyContinue
    }

    if ($uninstall.ExitCode -ne 3010) {
        throw "Locked-file uninstall returned $($uninstall.ExitCode), expected 3010. Do not edit PendingFileRenameOperations to force this test."
    }
    $pending = Get-PendingRenameOperations
    if (-not ($pending | Where-Object { $_ -like "*$fullInstallDirectory*" })) {
        throw "Exit code 3010 was returned, but no product-owned path was found in PendingFileRenameOperations."
    }

    [pscustomobject]@{
        InstallDirectory = $fullInstallDirectory
        Identifier = $Identifier
        PreparedAtUtc = [DateTime]::UtcNow.ToString("O")
    } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8

    Write-Host "PASS prepare: a genuine locked-file delete returned 3010. Reboot this disposable VM, then run the Verify phase with the same identifier."
    return
}

if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
    throw "Reboot validation state not found: $stateFile"
}
$state = Get-Content -Raw -LiteralPath $stateFile | ConvertFrom-Json
if (-not [string]::Equals([IO.Path]::GetFullPath($state.InstallDirectory).TrimEnd('\'), $fullInstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The saved reboot validation directory does not match -InstallDirectory."
}
if (Test-Path -LiteralPath $fullInstallDirectory) {
    throw "The product directory still exists after reboot: $fullInstallDirectory"
}
if (Test-Path -LiteralPath $transactionDirectory) {
    throw "A transaction journal survived the reboot-required uninstall: $transactionDirectory"
}
if (Test-Path -LiteralPath $uninstallRegistryPath) {
    throw "The uninstall registry key survived reboot-required uninstall: $uninstallRegistryPath"
}
if (Get-PendingRenameOperations | Where-Object { $_ -like "*$fullInstallDirectory*" }) {
    throw "Product-owned pending rename entries survived reboot."
}

Remove-Item -LiteralPath $stateFile -Force
if ((Get-ChildItem -LiteralPath $stateRoot -Force).Count -eq 0) {
    Remove-Item -LiteralPath $stateRoot
}
Write-Host "PASS verify: Windows completed the product-owned pending delete after reboot."

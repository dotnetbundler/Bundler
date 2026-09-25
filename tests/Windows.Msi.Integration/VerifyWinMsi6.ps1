param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion,
    [switch]$ConfirmDisposableVm,
    [switch]$ConfirmLocalInstall
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsiTestSupport.ps1')
Assert-MsiTestHost $ConfirmDisposableVm.IsPresent $ConfirmLocalInstall.IsPresent

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($PackageVersion)) { $PackageVersion = Get-BundlerPackageVersion -Repository $repository }
$runId = [guid]::NewGuid().ToString('N')
$identifier = "com.example.bundler.msi.features.$runId"
$root = Join-Path $env:TEMP "Bundler-Msi-WinMsi6-$runId"
$packages = Join-Path $root 'packages'
$nuget = Join-Path $root 'nuget'
$install = Join-Path $env:LOCALAPPDATA "BundlerTests\msi6-$runId"
$installer = $null
$codes = @()
$baselinePath = [Environment]::GetEnvironmentVariable('Path', 'User')
$unknown = Join-Path $install 'user-created.txt'
$unknownCreated = $false

function Assert-ProductState([string]$Code, [bool]$Expected) {
    $state = $installer.ProductState($Code)
    if ($Expected -and $state -ne 5) { throw "Expected installed product $Code; state $state" }
    if (-not $Expected -and $state -ne -1) { throw "Expected absent product $Code; state $state" }
}

function Invoke-LoggedMsi([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $root "$Name.log"
    $code = Invoke-Msi ($Arguments + @('/qn', '/norestart', '/L*v', ('"' + $log + '"')))
    Write-Host "$Name exit code: $code; log: $log"
    return $code
}

function Read-MsiColumn($database, [string]$query) {
    $view = $database.OpenView($query)
    $values = [Collections.Generic.List[string]]::new()
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) { $values.Add($record.StringData(1)) }
    }
    finally { $null = $view.Close() }
    return $values.ToArray()
}

$defaultInstall = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
if ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $defaultInstall)) {
    throw "Test installation already exists: $install / $defaultInstall"
}
New-Item -ItemType Directory -Path $root, $packages | Out-Null
try {
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration `
        -PackageVersion $PackageVersion -PackageDirectory $packages
    $msis = @()
    foreach ($version in @('1.0.0', '1.1.0')) {
        $fixtureDir = Join-Path $root "fixture-$version"
        $project = Copy-MsiTestFixture -Source (Join-Path $PSScriptRoot 'Fixture') -Destination $fixtureDir
        Restore-MsiTestFixture -Project $project -PackageDirectory $packages -PackageCache $nuget `
            -PackageVersion $PackageVersion -RuntimeIdentifier 'win-x64'
        $output = Join-Path $root "output-$version"
        dotnet publish $project -c $Configuration --no-restore `
            "-p:BundlerPackageSource=$packages" "-p:RestorePackagesPath=$nuget" `
            "-p:BundlerPackageVersion=$PackageVersion" '-p:RuntimeIdentifier=win-x64' `
            "-p:BundlerOutputPath=$output" "-p:BundlerIdentifier=$identifier" `
            "-p:BundlerVersion=$version" '-p:MsiLifecycleTest=true' `
            '-p:BundlerWixInstallDirectorySelection=true' '-p:BundlerWixStartMenuShortcut=true' `
            '-p:BundlerWixDesktopShortcut=true' '-p:BundlerWixAddToPath=true' `
            '-p:BundlerWixUninstallShortcut=true' '-p:BundlerWixLaunchAfterInstall=true' `
            "-p:BundlerWixBannerBitmap=$(Join-Path $fixtureDir 'Assets\banner.bmp')" `
            "-p:BundlerWixDialogBitmap=$(Join-Path $fixtureDir 'Assets\dialog.bmp')"
        if ($LASTEXITCODE -ne 0) { throw "WIN-MSI-6 fixture publish failed for $version" }
        $msis += (Join-Path $output "win-x64\msi\Bundler MSI Smoke-$version.msi")
    }
    foreach ($path in $msis) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing MSI: $path" }
    }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $v1 = $msis[0]
    $v2 = $msis[1]
    $code1 = Get-MsiProperty $installer $v1 'ProductCode'
    $code2 = Get-MsiProperty $installer $v2 'ProductCode'
    $codes = @($code1, $code2)
    Assert-ProductState $code1 $false

    $database = $installer.OpenDatabase($v1, 0)
    $dialogs = @(Read-MsiColumn $database 'SELECT Dialog FROM Dialog')
    foreach ($expectedDialog in @('WelcomeDlg', 'InstallDirDlg', 'BrowseDlg', 'InvalidDirDlg',
            'VerifyReadyDlg', 'ExitDialog', 'MaintenanceWelcomeDlg', 'MaintenanceTypeDlg')) {
        if ($dialogs -notcontains $expectedDialog) {
            throw "WIN-MSI-6 MSI is missing dialog $expectedDialog"
        }
    }
    if ($dialogs -contains 'LicenseAgreementDlg') {
        throw 'A license-free MSI must not embed an empty license dialog.'
    }
    $sequence = @(Read-MsiColumn $database 'SELECT Action FROM InstallExecuteSequence')
    $conditions = @(Read-MsiColumn $database 'SELECT Condition FROM InstallExecuteSequence')
    if ($sequence -notcontains 'BundlerInstallDirScope' -or
        @($conditions -match 'LocalAppDataFolder' -and $_ -match 'INSTALLFOLDER').Count -eq 0) {
        throw 'The silent install-directory scope check is missing from the execute sequence.'
    }
    $envNames = @(Read-MsiColumn $database 'SELECT Name FROM Environment')
    $envValues = @(Read-MsiColumn $database 'SELECT Value FROM Environment')
    if ($envNames -notcontains '=-PATH' -or @($envValues -match '^\[~\];\[INSTALLFOLDER\]$').Count -ne 1) {
        throw 'The MSI PATH feature must append only the product directory.'
    }
    if (@(Read-MsiColumn $database 'SELECT Name FROM Shortcut').Count -ne 3 -or
        @(Read-MsiColumn $database 'SELECT Arguments FROM Shortcut' |
            Where-Object { $_ -match 'ProductCode' }).Count -ne 1) {
        throw 'Expected start menu, desktop, and managed uninstall shortcuts.'
    }
    $binaries = @(Read-MsiColumn $database 'SELECT Name FROM Binary')
    if ($binaries -notcontains 'WixUI_Bmp_Banner' -or $binaries -notcontains 'WixUI_Bmp_Dialog') {
        throw 'Brand bitmaps were not embedded into the MSI.'
    }
    if ((Get-MsiProperty $installer $v1 'WIXUI_INSTALLDIR') -ne 'INSTALLFOLDER' -or
        (Get-MsiProperty $installer $v1 'ARPNOMODIFY') -ne '1' -or
        (Get-MsiProperty $installer $v1 'ARPCONTACT') -ne 'Bundler Tests') {
        throw 'WIN-MSI-6 MSI properties are incomplete.'
    }

    $rejectRoot = Invoke-LoggedMsi 'reject-root' @('/i', ('"' + $v1 + '"'), "INSTALLFOLDER=`"$env:LOCALAPPDATA`"")
    if ($rejectRoot -ne 1603) { throw "Installing into the allowed root itself returned $rejectRoot instead of 1603." }
    $rejectOutside = Invoke-LoggedMsi 'reject-outside' @('/i', ('"' + $v1 + '"'), 'INSTALLFOLDER="C:\Program Files\Common Files\Forbidden"')
    if ($rejectOutside -ne 1603 -or
        -not (Select-String -LiteralPath (Join-Path $root 'reject-outside.log') `
            -Pattern 'installation folder must be a subfolder' -Quiet)) {
        throw "An out-of-root silent INSTALLFOLDER was not rejected: $rejectOutside"
    }
    Assert-ProductState $code1 $false

    if ((Invoke-LoggedMsi 'install-custom' @('/i', ('"' + $v1 + '"'), "INSTALLFOLDER=`"$install`"")) -ne 0) {
        throw 'Silent install into a custom in-root directory failed.'
    }
    Assert-ProductState $code1 $true
    if (-not (Test-Path -LiteralPath (Join-Path $install 'BundlerMsiSmoke.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $install 'docs\marker.txt'))) {
        throw 'Payload did not land in the chosen installation directory.'
    }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($baselinePath -and -not $userPath.StartsWith($baselinePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The MSI PATH feature overwrote unrelated user PATH entries.'
    }
    if ($userPath -notmatch [regex]::Escape(";$install") -and $userPath -ne "$baselinePath;$install") {
        throw 'The product directory was not appended to the user PATH.'
    }
    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$identifier"
    if (-not (Test-Path -LiteralPath (Join-Path $startMenu 'Bundler MSI Smoke.lnk')) -or
        -not (Test-Path -LiteralPath (Join-Path $startMenu 'Uninstall Bundler MSI Smoke.lnk'))) {
        throw 'Start menu shortcuts were not created.'
    }
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Bundler MSI Smoke (' + $identifier + ').lnk')
    if (-not (Test-Path -LiteralPath $desktopShortcut)) { throw "Desktop shortcut is missing: $desktopShortcut" }
    $arp = $null
    foreach ($hive in @('HKCU:', 'HKLM:')) {
        $candidate = "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\$code1"
        if (Test-Path -LiteralPath $candidate) {
            $arp = Get-ItemProperty -LiteralPath $candidate
            break
        }
    }
    if (-not $arp -or ($arp.InstallLocation -ne "$install\" -and $arp.InstallLocation -ne $install) -or
        -not $arp.Contact) {
        throw 'Add/Remove Programs metadata is missing the install location or contact.'
    }
    Set-Content -LiteralPath $unknown -Value 'preserve user data'
    $unknownCreated = $true

    if ((Invoke-LoggedMsi 'upgrade-v2' @('/i', ('"' + $v2 + '"'))) -ne 0) {
        throw 'Upgrade over a custom install directory failed.'
    }
    Assert-ProductState $code1 $false
    Assert-ProductState $code2 $true
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v2-only.txt')) -or
        -not (Test-Path -LiteralPath $unknown) -or
        (Test-Path -LiteralPath $defaultInstall)) {
        throw 'Upgrade did not restore the previously chosen install directory.'
    }

    if ((Invoke-LoggedMsi 'repair-v2' @('/fomus', ('"' + $v2 + '"'))) -ne 0) {
        throw 'Repair on the custom directory install failed.'
    }
    Assert-ProductState $code2 $true

    if ((Invoke-LoggedMsi 'uninstall-v2' @('/x', $code2)) -ne 0) { throw 'Uninstall failed.' }
    Assert-ProductState $code2 $false
    $afterPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($afterPath -match [regex]::Escape($install)) {
        throw 'Uninstall left the product PATH entry behind.'
    }
    if ($baselinePath -and $afterPath -ne $baselinePath) {
        throw 'Uninstall did not restore the pre-install user PATH.'
    }
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'Uninstall left the desktop shortcut.' }
    if (Test-Path -LiteralPath $startMenu) { throw 'Uninstall left the start menu folder.' }
    if ((Test-Path -LiteralPath (Join-Path $install 'BundlerMsiSmoke.exe')) -or
        -not (Test-Path -LiteralPath $unknown)) {
        throw 'Uninstall did not preserve only the unknown user file.'
    }
    Write-Host 'PASS: custom install directory with silent rejection, PATH append/restore, shortcuts, ARP metadata, upgrade restore, repair, and clean uninstall.'
}
finally {
    if ($installer) {
        foreach ($code in $codes) {
            try {
                if ($installer.ProductState($code) -ne -1) {
                    $result = Invoke-LoggedMsi "cleanup-$code" @('/x', $code)
                    if ($result -ne 0) { Write-Warning "Cleanup of $code failed with $result; inspect $root" }
                }
            }
            catch { Write-Warning "Cleanup inspection failed for $code; inspect $root`: $_" }
        }
    }
    if ($unknownCreated -and (Test-Path -LiteralPath $unknown)) { Remove-Item -LiteralPath $unknown -Force }
    foreach ($dir in @($install, $defaultInstall)) {
        if ((Test-Path -LiteralPath $dir) -and
            -not (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $dir
        }
    }
    $leftover = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Bundler MSI Smoke (' + $identifier + ').lnk')
    if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Force }
    $leftoverMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$identifier"
    if (Test-Path -LiteralPath $leftoverMenu) { Remove-Item -LiteralPath $leftoverMenu -Recurse -Force }
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -ne $baselinePath) {
        [Environment]::SetEnvironmentVariable('Path', $baselinePath, 'User')
        Write-Warning 'User PATH differed from the baseline after cleanup; restored manually.'
    }
    Write-Host "WIN-MSI-6 logs and packages retained: $root"
    if ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $defaultInstall)) {
        throw "WIN-MSI-6 install directories remain: $install / $defaultInstall"
    }
}

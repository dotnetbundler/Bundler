param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '0.1.0-alpha.37',
    [switch]$ConfirmDisposableVm,
    [switch]$ConfirmLocalInstall
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsiTestSupport.ps1')
Assert-MsiTestHost $ConfirmDisposableVm.IsPresent $ConfirmLocalInstall.IsPresent

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureSource = Join-Path $PSScriptRoot 'Fixture'
$id = [guid]::NewGuid().ToString('N')
$identifier = "com.example.bundler.msi.lifecycle.$id"
$extension = "bmsi$id"
$scheme = "bmsi-$id"
$root = Join-Path $env:TEMP "Bundler-Msi-Lifecycle-$id"
$packages = Join-Path $root 'packages'
$nuget = Join-Path $root 'nuget'
$fixtureDirectory = Join-Path $root 'msbuild-fixture'
$install = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
$unknown = Join-Path $install 'user-created.txt'
$startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) "Programs\$identifier\Bundler MSI Smoke.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "Bundler MSI Smoke ($identifier).lnk"
$fileKey = "HKCU:\Software\Classes\.$extension"
$schemeKey = "HKCU:\Software\Classes\$scheme"
$progId = "$identifier.win-x64.file.$extension"
$urlProgId = "$identifier.win-x64.url.$scheme"
$capabilities = "HKCU:\Software\DotNetBundler\Products\$identifier\win-x64\Capabilities"
$registered = 'HKCU:\Software\RegisteredApplications'
$productCodes = @()
$installer = $null
$unknownCreated = $false

function Read-ProductCode([string]$Path) {
    return Get-MsiProperty $installer $Path 'ProductCode'
}
function Assert-Installed([string]$Code, [bool]$Expected) {
    $state = $installer.ProductState($Code)
    if ($Expected -and $state -ne 5) { throw "Expected installed product $Code; state is $state" }
    if (-not $Expected -and $state -ne -1) { throw "Expected absent product $Code; state is $state" }
}
function Assert-Shortcut([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Shortcut is missing: $Path" }
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($Path).TargetPath
    if ($target -ne (Join-Path $install 'BundlerMsiSmoke.exe')) { throw "Shortcut target is wrong: $target" }
}
function Assert-Registrations {
    $expectedCommand = '"' + (Join-Path $install 'BundlerMsiSmoke.exe') + '" "%1"'
    if ((Get-Item -LiteralPath "HKCU:\Software\Classes\$progId\shell\open\command").GetValue('') -ne $expectedCommand -or
        (Get-Item -LiteralPath "HKCU:\Software\Classes\$urlProgId\shell\open\command").GetValue('') -ne $expectedCommand) {
        throw 'File or URL handler command is not safely quoted or does not target the installed executable.'
    }
    if ((Get-ItemPropertyValue -LiteralPath "$fileKey\OpenWithProgids" -Name $progId) -ne '') {
        throw 'File OpenWithProgids registration is wrong.'
    }
    if ((Get-ItemPropertyValue -LiteralPath "$capabilities\FileAssociations" -Name ".$extension") -ne $progId) {
        throw 'File capability registration is wrong.'
    }
    if ((Get-ItemPropertyValue -LiteralPath "$capabilities\MIMEAssociations" -Name 'application/x-bundler-msi-lifecycle') -ne $progId) {
        throw 'MIME capability registration is wrong.'
    }
    if ((Get-ItemPropertyValue -LiteralPath "$capabilities\UrlAssociations" -Name $scheme) -ne $urlProgId) {
        throw 'URL capability registration is wrong.'
    }
    if ((Get-ItemPropertyValue -LiteralPath $registered -Name "$identifier.win-x64") -ne
        "Software\DotNetBundler\Products\$identifier\win-x64\Capabilities") {
        throw 'RegisteredApplications entry is wrong.'
    }
    if ((Get-Item -LiteralPath $fileKey).GetValue('') -ne 'Other.Test.Owner') {
        throw 'MSI changed an existing file default.'
    }
    if ((Get-Item -LiteralPath $schemeKey).GetValue('') -ne 'Other URL owner') {
        throw 'MSI changed an existing protocol owner.'
    }
}

if (Test-Path -LiteralPath $install) { throw "Test install path already exists: $install" }
if (Test-Path -LiteralPath $fileKey) { throw "Test extension already exists: $fileKey" }
if (Test-Path -LiteralPath $schemeKey) { throw "Test scheme already exists: $schemeKey" }
if (Test-Path -LiteralPath $startMenu) { throw "Test Start Menu path already exists: $startMenu" }
if (Test-Path -LiteralPath $desktop) { throw "Test desktop path already exists: $desktop" }
New-Item -ItemType Directory -Force -Path $root, $packages | Out-Null
try {
    New-Item -Path $fileKey, $schemeKey -Force | Out-Null
    Set-Item -LiteralPath $fileKey -Value 'Other.Test.Owner'
    Set-Item -LiteralPath $schemeKey -Value 'Other URL owner'
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration -PackageVersion $PackageVersion -PackageDirectory $packages
    Push-Location $repository
    try {
        foreach ($version in @('1.0.0', '1.1.0')) {
            $fixture = Copy-MsiTestFixture -Source $fixtureSource -Destination "$fixtureDirectory-$version"
            Restore-MsiTestFixture -Project $fixture -PackageDirectory $packages -PackageCache $nuget -PackageVersion $PackageVersion
            $output = Join-Path $root "output-$version"
            dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packages `
                -p:RestorePackagesPath=$nuget -p:BundlerPackageVersion=$PackageVersion `
                -p:BundlerOutputPath=$output -p:BundlerIdentifier=$identifier -p:BundlerVersion=$version `
                -p:MsiLifecycleTest=true -p:BundlerTestExtension=$extension -p:BundlerTestScheme=$scheme `
                -p:BundlerWixStartMenuShortcut=true -p:BundlerWixDesktopShortcut=true
            if ($LASTEXITCODE -ne 0) { throw "Fixture publish failed for $version." }
        }
        $fixture = Copy-MsiTestFixture -Source $fixtureSource -Destination "$fixtureDirectory-variant"
        Restore-MsiTestFixture -Project $fixture -PackageDirectory $packages -PackageCache $nuget -PackageVersion $PackageVersion
        $variantOutput = Join-Path $root 'output-variant'
        dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packages `
            -p:RestorePackagesPath=$nuget -p:BundlerPackageVersion=$PackageVersion `
            -p:BundlerOutputPath=$variantOutput -p:BundlerIdentifier=$identifier -p:BundlerVersion=1.1.0 `
            -p:MsiLifecycleTest=true -p:MsiVariantTest=true -p:BundlerTestExtension=$extension -p:BundlerTestScheme=$scheme `
            -p:BundlerWixStartMenuShortcut=true -p:BundlerWixDesktopShortcut=true
        if ($LASTEXITCODE -ne 0) { throw 'Variant fixture publish failed.' }
    }
    finally { Pop-Location }
    $v1 = Join-Path $root 'output-1.0.0\win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
    $v2 = Join-Path $root 'output-1.1.0\win-x64\msi\Bundler MSI Smoke-1.1.0.msi'
    $variant = Join-Path $root 'output-variant\win-x64\msi\Bundler MSI Smoke-1.1.0.msi'
    if (-not (Test-Path -LiteralPath $v1) -or -not (Test-Path -LiteralPath $v2) -or
        -not (Test-Path -LiteralPath $variant)) { throw 'All MSI test packages must be built.' }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $code1 = Read-ProductCode $v1
    $code2 = Read-ProductCode $v2
    $variantCode = Read-ProductCode $variant
    $productCodes = @($code1, $code2)
    Write-Host "Read ProductCodes: v1=[$code1], v2=[$code2]"
    if ($code1 -eq $code2) { throw 'Major upgrade did not change ProductCode.' }
    if ($variantCode -ne $code2) { throw 'Same-version variant must retain ProductCode to exercise the collision guard.' }
    Assert-Installed $code1 $false
    Assert-Installed $code2 $false
    Write-Host "MSI v1 SHA-256: $((Get-FileHash -LiteralPath $v1 -Algorithm SHA256).Hash)"
    Write-Host "MSI v2 SHA-256: $((Get-FileHash -LiteralPath $v2 -Algorithm SHA256).Hash)"
    Write-Host "MSI variant SHA-256: $((Get-FileHash -LiteralPath $variant -Algorithm SHA256).Hash)"
    Write-Host "Fixture identifier: $identifier; v1=$code1; v2=$code2"
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "Windows: $($os.Caption), build $($os.BuildNumber), $($os.OSArchitecture)"

    $result = Invoke-Msi @('/i', ('"' + $v1 + '"'), '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root 'install-v1.log') + '"'))
    if ($result -ne 0) { throw "v1 install failed: $result" }
    Assert-Installed $code1 $true
    Assert-Shortcut $startMenu
    Assert-Shortcut $desktop
    Assert-Registrations
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v1-only.txt'))) { throw 'v1 payload is missing.' }
    Set-Content -LiteralPath $unknown -Value 'preserve user data'
    $unknownCreated = $true

    $result = Invoke-Msi @('/i', ('"' + $v2 + '"'), '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root 'upgrade-v2.log') + '"'))
    if ($result -ne 0) { throw "v2 upgrade failed: $result" }
    Assert-Installed $code1 $false
    Assert-Installed $code2 $true
    if (Test-Path -LiteralPath (Join-Path $install 'docs\v1-only.txt')) { throw 'Upgrade retained the old managed payload.' }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v2-only.txt'))) { throw 'Upgrade lost the new managed payload.' }
    if (-not (Test-Path -LiteralPath $unknown)) { throw 'Upgrade removed unknown user data.' }
    Assert-Shortcut $startMenu
    Assert-Shortcut $desktop
    Assert-Registrations

    $result = Invoke-Msi @('/i', ('"' + $variant + '"'), '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root 'same-version-variant.log') + '"'))
    if ($result -ne 1638) { throw "Same-version different-content MSI returned $result instead of 1638." }
    Assert-Installed $code2 $true
    if (Test-Path -LiteralPath (Join-Path $install 'docs\variant-only.txt')) { throw 'Rejected variant installed its payload.' }
    Assert-Registrations

    $result = Invoke-Msi @('/i', ('"' + $v1 + '"'), '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root 'downgrade.log') + '"'))
    if ($result -ne 1603 -or
        -not (Select-String -LiteralPath (Join-Path $root 'downgrade.log') -Pattern 'A newer version of Bundler MSI Smoke is already installed' -Quiet)) {
        throw "Downgrade did not fail for the expected newer-version condition; exit code $result."
    }
    Assert-Installed $code1 $false
    Assert-Installed $code2 $true
    Assert-Registrations
    $result = Invoke-Msi @('/x', $code2, '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root 'uninstall-v2.log') + '"'))
    if ($result -ne 0) { throw "v2 uninstall failed: $result" }
    Assert-Installed $code2 $false
    if (Test-Path -LiteralPath $startMenu) { throw 'Uninstall left Start Menu shortcut.' }
    if (Test-Path -LiteralPath $desktop) { throw 'Uninstall left desktop shortcut.' }
    if (Test-Path -LiteralPath $capabilities) { throw 'Uninstall left application capabilities.' }
    if (Test-Path -LiteralPath "HKCU:\Software\Classes\$progId") { throw 'Uninstall left file ProgID.' }
    if (Test-Path -LiteralPath "HKCU:\Software\Classes\$urlProgId") { throw 'Uninstall left URL ProgID.' }
    if (-not (Test-Path -LiteralPath $unknown)) { throw 'Uninstall removed unknown user data.' }
    if (Test-Path -LiteralPath (Join-Path $install 'BundlerMsiSmoke.exe')) { throw 'Uninstall left the managed executable.' }
    if (Test-Path -LiteralPath (Join-Path $install 'docs\v2-only.txt')) { throw 'Uninstall left managed v2 payload.' }
    Write-Host 'PASS: current-user major upgrade, same-version variant and downgrade rejection, desktop registrations, shortcut lifecycle, and user data preservation.'
}
finally {
    if ($installer) {
        foreach ($code in $productCodes) {
            if (-not $code) { continue }
            try {
                if ($installer.ProductState($code) -ne -1) {
                    $cleanup = Invoke-Msi @('/x', $code, '/qn', '/norestart', '/L*v', ('"' + (Join-Path $root "cleanup-$code.log") + '"'))
                    if ($cleanup -ne 0) { Write-Warning "Cleanup of $code failed with $cleanup; inspect $root" }
                }
            }
            catch { Write-Warning "Cleanup inspection failed for $code; inspect $root`: $_" }
        }
    }
    if ($unknownCreated -and (Test-Path -LiteralPath $unknown)) { Remove-Item -LiteralPath $unknown -Force }
    if ((Test-Path -LiteralPath $install) -and -not (Get-ChildItem -LiteralPath $install -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $install
    }
    if (Test-Path -LiteralPath $fileKey) { Remove-Item -LiteralPath $fileKey -Recurse -Force }
    if (Test-Path -LiteralPath $schemeKey) { Remove-Item -LiteralPath $schemeKey -Recurse -Force }
    Write-Host "MSI lifecycle logs retained: $root"
    if (Test-Path -LiteralPath $install) { throw "MSI lifecycle install directory remains after cleanup: $install" }
}

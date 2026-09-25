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
$identifier = "com.example.bundler.msi.i18n.$runId"
$root = Join-Path $env:TEMP "Bundler-Msi-WinMsi7-$runId"
$packages = Join-Path $root 'packages'
$nuget = Join-Path $root 'nuget'
$installer = $null
$codes = @()

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

$defaultInstallEn = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
$defaultInstallJa = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64-ja-jp"
if ((Test-Path -LiteralPath $defaultInstallEn) -or (Test-Path -LiteralPath $defaultInstallJa)) {
    throw "Test installation already exists: $defaultInstallEn / $defaultInstallJa"
}
New-Item -ItemType Directory -Path $root, $packages | Out-Null
try {
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration `
        -PackageVersion $PackageVersion -PackageDirectory $packages
    $fixtureDir = Join-Path $root 'fixture'
    $project = Copy-MsiTestFixture -Source (Join-Path $PSScriptRoot 'Fixture') -Destination $fixtureDir
    Restore-MsiTestFixture -Project $project -PackageDirectory $packages -PackageCache $nuget `
        -PackageVersion $PackageVersion -RuntimeIdentifier 'win-x64'
    $output = Join-Path $root 'output'
    dotnet publish $project -c $Configuration --no-restore `
        "-p:BundlerPackageSource=$packages" "-p:RestorePackagesPath=$nuget" `
        "-p:BundlerPackageVersion=$PackageVersion" '-p:RuntimeIdentifier=win-x64' `
        "-p:BundlerOutputPath=$output" "-p:BundlerIdentifier=$identifier" `
        '-p:BundlerVersion=1.0.0' '-p:BundlerWixLanguages=en-US%3Bja-JP' `
        '-p:BundlerWixStartMenuShortcut=true'
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-7 fixture publish failed' }

    $apiSource = Join-Path $repository 'tests\Msi.Api.PackageFixture'
    $apiDir = Join-Path $root 'api-fixture'
    New-Item -ItemType Directory -Path $apiDir | Out-Null
    foreach ($name in @('Msi.Api.PackageFixture.csproj', 'Program.cs')) {
        Copy-Item -LiteralPath (Join-Path $apiSource $name) -Destination (Join-Path $apiDir $name)
    }
    Copy-Item -LiteralPath (Join-Path $repository 'Bundler.LocalPackages.props') -Destination $apiDir
    $apiProject = Join-Path $apiDir 'Msi.Api.PackageFixture.csproj'
    dotnet restore $apiProject "-p:BundlerPackageSource=$packages" `
        "-p:RestorePackagesPath=$nuget" "-p:BundlerPackageVersion=$PackageVersion"
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-7 API fixture restore failed.' }
    Assert-LocalBundlerRestore -Project $apiProject -PackageVersion $PackageVersion -Source $packages `
        -Cache $nuget -RequiredPackages @('DotNet.Bundler.Wix', 'DotNet.Bundler.Core', 'DotNet.Bundler.Abstractions')
    $apiOutput = Join-Path $root 'api-output'
    dotnet run --project $apiProject -c $Configuration --no-restore "-p:RestorePackagesPath=$nuget" `
        "-p:BundlerPackageSource=$packages" "-p:BundlerPackageVersion=$PackageVersion" `
        -- $apiOutput (Join-Path $root 'api-tools') win-x64 1.0.0 1.0.0 false "en-US;de-DE"
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-7 direct API build failed' }
    if (-not (Test-Path -LiteralPath (Join-Path $apiOutput 'artifacts\win-x64\msi\MSI API Package Fixture-1.0.0.msi')) -or
        -not (Test-Path -LiteralPath (Join-Path $apiOutput 'artifacts\win-x64\msi\MSI API Package Fixture-1.0.0-de-de.msi'))) {
        throw 'The direct API did not produce one MSI per requested language.'
    }

    $msiEn = Join-Path $output 'win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
    $msiJa = Join-Path $output 'win-x64\msi\Bundler MSI Smoke-1.0.0-ja-jp.msi'
    foreach ($path in @($msiEn, $msiJa)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing localized MSI: $path" }
    }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $codeEn = Get-MsiProperty $installer $msiEn 'ProductCode'
    $codeJa = Get-MsiProperty $installer $msiJa 'ProductCode'
    $codes = @($codeEn, $codeJa)
    if ($codeEn -eq $codeJa -or
        (Get-MsiProperty $installer $msiEn 'UpgradeCode') -eq (Get-MsiProperty $installer $msiJa 'UpgradeCode')) {
        throw 'Localized MSI outputs must keep isolated product identities.'
    }
    if ((Get-MsiProperty $installer $msiJa 'ProductLanguage') -ne '1041' -or
        (Get-MsiProperty $installer $msiEn 'ProductLanguage') -ne '1033') {
        throw 'Localized MSI product languages are incorrect.'
    }
    Assert-ProductState $codeEn $false
    Assert-ProductState $codeJa $false

    if ((Invoke-LoggedMsi 'install-en' @('/i', ('"' + $msiEn + '"'))) -ne 0) {
        throw 'English MSI silent install failed.'
    }
    if ((Invoke-LoggedMsi 'install-ja' @('/i', ('"' + $msiJa + '"'))) -ne 0) {
        throw 'Japanese MSI silent install failed.'
    }
    Assert-ProductState $codeEn $true
    Assert-ProductState $codeJa $true
    if (-not (Test-Path -LiteralPath (Join-Path $defaultInstallEn 'BundlerMsiSmoke.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $defaultInstallJa 'BundlerMsiSmoke.exe'))) {
        throw 'Per-language MSI payloads did not land in isolated install directories.'
    }
    $menuEn = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$identifier\Bundler MSI Smoke.lnk"
    $menuJa = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$identifier-ja-jp\Bundler MSI Smoke-ja-jp.lnk"
    if (-not (Test-Path -LiteralPath $menuEn) -or -not (Test-Path -LiteralPath $menuJa)) {
        throw 'Per-language start menu shortcuts are missing.'
    }

    if ((Invoke-LoggedMsi 'uninstall-ja' @('/x', $codeJa)) -ne 0) { throw 'Japanese MSI uninstall failed.' }
    Assert-ProductState $codeJa $false
    Assert-ProductState $codeEn $true
    if ((Invoke-LoggedMsi 'uninstall-en' @('/x', $codeEn)) -ne 0) { throw 'English MSI uninstall failed.' }
    Assert-ProductState $codeEn $false
    if ((Test-Path -LiteralPath $defaultInstallEn) -or (Test-Path -LiteralPath $defaultInstallJa)) {
        throw 'Per-language MSI uninstall left install directories behind.'
    }
    Write-Host 'PASS: one publish produced isolated English and Japanese MSIs that install, coexist, and uninstall independently.'
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
    foreach ($dir in @($defaultInstallEn, $defaultInstallJa)) {
        if ((Test-Path -LiteralPath $dir) -and
            -not (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $dir
        }
    }
    foreach ($menu in @("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$identifier",
            "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$identifier-ja-jp")) {
        if (Test-Path -LiteralPath $menu) { Remove-Item -LiteralPath $menu -Recurse -Force }
    }
    Write-Host "WIN-MSI-7 logs and packages retained: $root"
    if ((Test-Path -LiteralPath $defaultInstallEn) -or (Test-Path -LiteralPath $defaultInstallJa)) {
        throw "WIN-MSI-7 install directories remain: $defaultInstallEn / $defaultInstallJa"
    }
}

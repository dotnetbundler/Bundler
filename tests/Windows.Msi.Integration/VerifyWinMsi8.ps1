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
$identifier = "com.example.bundler.msi.ext.$runId"
$root = Join-Path $env:TEMP "Bundler-Msi-WinMsi8-$runId"
$packages = Join-Path $root 'packages'
$nuget = Join-Path $root 'nuget'
$installer = $null
$codes = @()

function Invoke-LoggedMsi([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $root "$Name.log"
    $code = Invoke-Msi ($Arguments + @('/qn', '/norestart', '/L*v', ('"' + $log + '"')))
    Write-Host "$Name exit code: $code; log: $log"
    return $code
}

$defaultInstall = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
$expertInstall = Join-Path $env:LOCALAPPDATA 'BundlerMsi8Api'
$registryKey = 'HKCU:\Software\BundlerTests\WinMsi8'
foreach ($probe in @($defaultInstall, $expertInstall, $registryKey)) {
    if (Test-Path -LiteralPath $probe) { throw "Test state already exists: $probe" }
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
        '-p:BundlerVersion=1.0.0' '-p:MsiExtensionTest=true' `
        '-p:BundlerWixExtensionIdPrefix=Ext.'
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-8 regular-extension fixture publish failed' }
    $msi = Join-Path $output 'win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
    if (-not (Test-Path -LiteralPath $msi)) { throw "Missing extended MSI: $msi" }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $code = Get-MsiProperty $installer $msi 'ProductCode'
    $codes = @($code)
    if ($installer.ProductState($code) -ne -1) { throw "Product already installed: $code" }

    if ((Invoke-LoggedMsi 'install-regular' @('/i', ('"' + $msi + '"'))) -ne 0) {
        throw 'Regular-mode extended MSI silent install failed.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $defaultInstall 'extension-marker.txt'))) {
        throw 'The extension fragment file did not land in the install directory.'
    }
    if ((Get-ItemProperty -LiteralPath $registryKey -Name Installed -ErrorAction SilentlyContinue).Installed -ne 'yes') {
        throw 'The extension fragment registry value is missing.'
    }
    if ((Invoke-LoggedMsi 'uninstall-regular' @('/x', $code)) -ne 0) {
        throw 'Regular-mode extended MSI uninstall failed.'
    }
    if ((Test-Path -LiteralPath (Join-Path $defaultInstall 'extension-marker.txt')) -or
        (Test-Path -LiteralPath $registryKey)) {
        throw 'Extension payload was not removed on uninstall.'
    }
    Write-Host 'PASS: regular-mode extension fragment installed and uninstalled managed content.'

    # Expert mode: a caller-supplied template consuming Bundler.* identity vars.
    $apiSource = Join-Path $repository 'tests\Msi.Api.PackageFixture'
    $apiDir = Join-Path $root 'api-fixture'
    New-Item -ItemType Directory -Path $apiDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Standalone\Msi.Api.PackageFixture.csproj') `
        -Destination (Join-Path $apiDir 'Msi.Api.PackageFixture.csproj')
    Copy-Item -LiteralPath (Join-Path $apiSource 'Program.cs') -Destination (Join-Path $apiDir 'Program.cs')
    Copy-Item -LiteralPath (Join-Path $repository 'Bundler.LocalPackages.props') -Destination $apiDir
    $apiProject = Join-Path $apiDir 'Msi.Api.PackageFixture.csproj'
    dotnet restore $apiProject "-p:BundlerPackageSource=$packages" `
        "-p:RestorePackagesPath=$nuget" "-p:BundlerPackageVersion=$PackageVersion"
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-8 API fixture restore failed.' }
    Assert-LocalBundlerRestore -Project $apiProject -PackageVersion $PackageVersion -Source $packages `
        -Cache $nuget -RequiredPackages @('DotNet.Bundler.Wix', 'DotNet.Bundler.Core', 'DotNet.Bundler.Abstractions')

    $apiOutput = Join-Path $root 'api-output'
    $template = Join-Path $root 'expert-template.wxs'
    $fixturePayload = [IO.Path]::Combine($apiOutput, 'publish', 'ApiFixture.exe').Replace('\', '/')
    Set-Content -LiteralPath $template -Value (
        '<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">' +
        '<Product Id="$(var.Bundler.ProductCode)" Name="$(var.Bundler.ProductName)"' +
        ' Language="$(var.Bundler.ProductLanguage)" Version="$(var.Bundler.ProductVersion)"' +
        ' Manufacturer="$(var.Bundler.Manufacturer)" UpgradeCode="$(var.Bundler.UpgradeCode)"' +
        ' Codepage="$(var.Bundler.Codepage)">' +
        '<Package InstallerVersion="500" Compressed="yes" InstallScope="$(var.Bundler.InstallScope)"/>' +
        '<Media Id="1" Cabinet="app.cab" EmbedCab="yes"/>' +
        '<MajorUpgrade Schedule="afterInstallInitialize" AllowSameVersionUpgrades="no"' +
        ' DowngradeErrorMessage="!(loc.BundlerDowngradeErrorMessage)"/>' +
        '<Property Id="MSIINSTALLPERUSER" Value="1"/>' +
        '<Directory Id="TARGETDIR" Name="SourceDir"><Directory Id="LocalAppDataFolder">' +
        '<Directory Id="INSTALLFOLDER" Name="BundlerMsi8Api"/></Directory></Directory>' +
        '<DirectoryRef Id="INSTALLFOLDER">' +
        '<Component Id="Expert.App" Guid="{7c4a5f2e-9b31-4d68-8a2f-6e1c5d9a0b7e}">' +
        '<File Source="' + $fixturePayload + '"/>' +
        '<RemoveFolder Id="Expert.RemoveFolder" On="uninstall"/>' +
        '<RegistryValue Root="HKCU" Key="Software\BundlerTests\WinMsi8Expert" Name="Mark"' +
        ' Type="string" Value="expert" KeyPath="yes"/>' +
        '</Component></DirectoryRef>' +
        '<Feature Id="Complete" Title="$(var.Bundler.ProductName)" Level="1">' +
        '<ComponentRef Id="Expert.App"/></Feature>' +
        '</Product></Wix>')
    dotnet run --project $apiProject -c $Configuration --no-restore "-p:RestorePackagesPath=$nuget" `
        "-p:BundlerPackageSource=$packages" "-p:BundlerPackageVersion=$PackageVersion" `
        -- $apiOutput (Join-Path $root 'api-tools') win-x64 1.0.0 1.0.0 false "en-US" $template
    if ($LASTEXITCODE -ne 0) { throw 'WIN-MSI-8 expert template build failed' }
    $expertMsi = Join-Path $apiOutput 'artifacts\win-x64\msi\MSI API Package Fixture-1.0.0.msi'
    if (-not (Test-Path -LiteralPath $expertMsi)) { throw "Missing expert MSI: $expertMsi" }
    $expertCode = Get-MsiProperty $installer $expertMsi 'ProductCode'
    $codes += $expertCode
    if ($installer.ProductState($expertCode) -ne -1) { throw "Expert product already installed: $expertCode" }
    if ((Invoke-LoggedMsi 'install-expert' @('/i', ('"' + $expertMsi + '"'))) -ne 0) {
        throw 'Expert-mode MSI silent install failed.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $expertInstall 'ApiFixture.exe'))) {
        throw 'The expert template payload did not land.'
    }
    if ((Invoke-LoggedMsi 'uninstall-expert' @('/x', $expertCode)) -ne 0) {
        throw 'Expert-mode MSI uninstall failed.'
    }
    if (Test-Path -LiteralPath $expertInstall) {
        throw 'The expert template install directory was left behind.'
    }
    Write-Host 'PASS: expert-mode template produced a Bundler-identity MSI that installs and uninstalls.'
    Write-Host 'PASS: WIN-MSI-8 real integration; artifacts retained at' $root
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
    foreach ($dir in @($defaultInstall, $expertInstall)) {
        if ((Test-Path -LiteralPath $dir) -and
            -not (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $dir
        }
    }
    if (Test-Path -LiteralPath $registryKey) { Remove-Item -LiteralPath $registryKey -Force }
    foreach ($menu in @("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$identifier")) {
        if (Test-Path -LiteralPath $menu) { Remove-Item -LiteralPath $menu -Recurse -Force }
    }
    Write-Host "WIN-MSI-8 logs and packages retained: $root"
    if ((Test-Path -LiteralPath $defaultInstall) -or (Test-Path -LiteralPath $expertInstall) -or
        (Test-Path -LiteralPath $registryKey)) {
        throw "WIN-MSI-8 test state remains."
    }
}

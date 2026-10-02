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
$fixtureSource = Join-Path $PSScriptRoot 'Fixture'
$apiFixtureSource = Join-Path $repository 'tests\Msi.Api.PackageFixture'
$sessionId = [guid]::NewGuid().ToString('N')
$identifier = "com.example.bundler.msi.smoke.$sessionId"
$sessionRoot = Join-Path $env:TEMP "Bundler-Msi-Smoke-$sessionId"
$packageDirectory = Join-Path $sessionRoot 'packages'
$outputDirectory = Join-Path $sessionRoot 'output'
$nugetDirectory = Join-Path $sessionRoot 'nuget'
$apiFixtureDirectory = Join-Path $sessionRoot 'api-fixture'
$apiFixture = Join-Path $apiFixtureDirectory 'Msi.Api.PackageFixture.csproj'
$apiOutput = Join-Path $sessionRoot 'api-output'
$fixtureDirectory = Join-Path $sessionRoot 'msbuild-fixture'
$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
$mainExecutable = Join-Path $installDirectory 'BundlerMsiSmoke.exe'
$resourceFile = Join-Path $installDirectory 'docs\marker.txt'
$unknownFile = Join-Path $installDirectory 'user-created.txt'
$installLog = Join-Path $sessionRoot 'install.log'
$uninstallLog = Join-Path $sessionRoot 'uninstall.log'
$installed = $false
$createdUnknownFile = $false
$msi = $null
$productCode = $null
if (Test-Path -LiteralPath $installDirectory) {
    throw "The test product path already exists; refusing to touch existing data: $installDirectory"
}
New-Item -ItemType Directory -Force -Path $sessionRoot, $packageDirectory | Out-Null

try {
    $fixture = Copy-MsiTestFixture -Source $fixtureSource -Destination $fixtureDirectory
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration -PackageVersion $PackageVersion -PackageDirectory $packageDirectory
    Restore-MsiTestFixture -Project $fixture -PackageDirectory $packageDirectory -PackageCache $nugetDirectory -PackageVersion $PackageVersion
    Push-Location $repository
    try {
        dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packageDirectory `
            -p:RestorePackagesPath=$nugetDirectory `
            -p:BundlerPackageVersion=$PackageVersion -p:BundlerOutputPath=$outputDirectory -p:BundlerIdentifier=$identifier
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture publish failed.' }

        New-Item -ItemType Directory -Path $apiFixtureDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Standalone\Msi.Api.PackageFixture.csproj') -Destination $apiFixture
        Copy-Item -LiteralPath (Join-Path $apiFixtureSource 'Program.cs') -Destination (Join-Path $apiFixtureDirectory 'Program.cs')
        Copy-Item -LiteralPath (Join-Path $repository 'Bundler.LocalPackages.props') `
            -Destination (Join-Path $apiFixtureDirectory 'Bundler.LocalPackages.props')
        dotnet restore $apiFixture -p:RestorePackagesPath=$nugetDirectory `
            -p:BundlerPackageSource=$packageDirectory -p:BundlerPackageVersion=$PackageVersion
        if ($LASTEXITCODE -ne 0) { throw 'Standalone MSI API package fixture restore failed.' }
        Assert-LocalBundlerRestore -Project $apiFixture -PackageVersion $PackageVersion -Source $packageDirectory `
            -Cache $nugetDirectory -RequiredPackages @('DotNet.Bundler.Wix', 'DotNet.Bundler.Core', 'DotNet.Bundler.Abstractions')
        dotnet run --project $apiFixture -c $Configuration --no-restore -p:RestorePackagesPath=$nugetDirectory `
            -p:BundlerPackageSource=$packageDirectory -p:BundlerPackageVersion=$PackageVersion `
            -- $apiOutput (Join-Path $sessionRoot 'api-tools')
        if ($LASTEXITCODE -ne 0) { throw 'Standalone MSI API package fixture failed.' }
    }
    finally { Pop-Location }

    $apiMsi = Join-Path $apiOutput 'artifacts\win-x64\msi\MSI API Package Fixture-1.0.0.msi'
    if (-not (Test-Path -LiteralPath $apiMsi)) { throw "Standalone MSI API did not produce an MSI: $apiMsi" }
    $apiInstaller = New-Object -ComObject WindowsInstaller.Installer
    if ((Get-MsiProperty $apiInstaller $apiMsi 'ProductName') -ne 'MSI API Package Fixture') {
        throw 'Standalone MSI API package created an unexpected product.'
    }
    Write-Host "Standalone MSI API SHA-256: $((Get-FileHash -LiteralPath $apiMsi -Algorithm SHA256).Hash)"

    $msi = Join-Path $outputDirectory 'win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
    if (-not (Test-Path -LiteralPath $msi)) { throw "MSI was not produced: $msi" }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $productCode = Get-MsiProperty $installer $msi 'ProductCode'
    if ([guid]$productCode -eq [guid]::Empty -or $installer.ProductState($productCode) -ne -1) {
        throw "The test ProductCode is invalid or already registered: $productCode"
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $msi).Hash
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "MSI SHA-256: $hash"
    Write-Host "Fixture identifier: $identifier; ProductCode: $productCode"
    Write-Host "Windows: $($os.Caption), build $($os.BuildNumber), $($os.OSArchitecture)"

    $installed = $true
    $installCode = Invoke-Msi @('/i', ('"' + $msi + '"'), '/qn', '/norestart', '/L*v', ('"' + $installLog + '"'))
    if ($installCode -ne 0) { throw "MSI install failed with $installCode. See $installLog" }
    if (-not (Test-Path -LiteralPath $mainExecutable)) { throw 'Installed executable is missing.' }
    if (-not (Test-Path -LiteralPath $resourceFile) -or
        (Get-Content -Raw -LiteralPath $resourceFile).Trim() -ne 'Bundler MSI integration resource') {
        throw 'Installed resource file is missing or has unexpected content.'
    }
    if ($installer.ProductState($productCode) -ne 5) { throw 'Windows Installer did not register the installed product.' }
    if ($installer.ProductInfo($productCode, 'ProductName') -ne 'Bundler MSI Smoke') {
        throw 'Windows Installer registered an unexpected product name.'
    }
    if ($installer.ProductInfo($productCode, 'VersionString') -ne '1.0.0') {
        throw 'Windows Installer registered an unexpected product version.'
    }
    Set-Content -LiteralPath $unknownFile -Value 'keep user data'
    $createdUnknownFile = $true

    $uninstallCode = Invoke-Msi @('/x', $productCode, '/qn', '/norestart', '/L*v', ('"' + $uninstallLog + '"'))
    if ($uninstallCode -ne 0) { throw "MSI uninstall failed with $uninstallCode. See $uninstallLog" }
    if ($installer.ProductState($productCode) -ne -1) { throw 'Windows Installer still registers the uninstalled product.' }
    $installed = $false
    if (Test-Path -LiteralPath $mainExecutable) { throw 'MSI uninstall left the managed executable.' }
    if (Test-Path -LiteralPath $resourceFile) { throw 'MSI uninstall left the managed resource file.' }
    if (-not (Test-Path -LiteralPath $unknownFile)) { throw 'MSI uninstall deleted a user-created file.' }
    Write-Host 'PASS: real Windows MSI install and uninstall preserved unknown user data.'
}
finally {
    $cleanupCode = 0
    if ($installed -and $productCode -and (Test-Path -LiteralPath $msi) -and
        $installer.ProductState($productCode) -ne -1) {
        $cleanupCode = Invoke-Msi @('/x', $productCode, '/qn', '/norestart', '/L*v', ('"' + (Join-Path $sessionRoot 'cleanup-uninstall.log') + '"'))
    }
    if ($createdUnknownFile -and (Test-Path -LiteralPath $unknownFile)) {
        Remove-Item -LiteralPath $unknownFile -Force
    }
    if ((Test-Path -LiteralPath $installDirectory) -and
        -not (Get-ChildItem -LiteralPath $installDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $installDirectory
    }
    Write-Host "MSI smoke logs and packages retained for review: $sessionRoot"
    if ($cleanupCode -ne 0) { throw "MSI cleanup failed with $cleanupCode; inspect $sessionRoot" }
    if (Test-Path -LiteralPath $installDirectory) {
        throw "MSI test installation directory remains after cleanup: $installDirectory"
    }
}

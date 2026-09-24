param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '0.1.0-alpha.34',
    [switch]$ConfirmDisposableVm,
    [switch]$ConfirmLocalInstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..\AssertLocalRestore.ps1')
if ($ConfirmDisposableVm -eq $ConfirmLocalInstall) {
    throw 'Pass exactly one of -ConfirmDisposableVm or -ConfirmLocalInstall.'
}
if ($ConfirmDisposableVm) {
    $computer = Get-CimInstance Win32_ComputerSystem
    if ("$($computer.Manufacturer) $($computer.Model)" -notmatch '(?i)(virtual|vmware|qemu|kvm|hyper-v|parallels|xen)') {
        throw 'The host does not identify as a virtual machine; MSI installation was refused.'
    }
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixture = Join-Path $PSScriptRoot 'Fixture\BundlerMsiSmoke.csproj'
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

function Invoke-Msi([string[]]$Arguments) {
    $process = Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}

try {
    Push-Location $repository
    try {
        dotnet pack Bundler.slnx -c $Configuration -o $packageDirectory -p:BundlerPackageVersion=$PackageVersion
        if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
        foreach ($name in @('DotNet.Bundler', 'DotNet.Bundler.Wix', 'DotNet.Bundler.MSBuild')) {
            if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory "$name.$PackageVersion.nupkg"))) {
                throw "Expected local package is missing: $name/$PackageVersion"
            }
        }
        $wixPackage = [IO.Compression.ZipFile]::OpenRead((Join-Path $packageDirectory "DotNet.Bundler.Wix.$PackageVersion.nupkg"))
        try {
            $entries = @($wixPackage.Entries | ForEach-Object FullName)
            foreach ($entry in @('lib/netstandard2.0/DotNet.Bundler.Wix.dll', 'licenses/wix/LICENSE.TXT',
                    'licenses/wix/wix3141-source.zip', 'licenses/wix/SHA256SUMS', 'THIRD-PARTY-NOTICES.md')) {
                if ($entries -notcontains $entry) { throw "Standalone MSI backend package is missing $entry" }
            }
        }
        finally { $wixPackage.Dispose() }
        dotnet restore $fixture -p:BundlerPackageSource=$packageDirectory -p:RestorePackagesPath=$nugetDirectory `
            -p:BundlerPackageVersion=$PackageVersion
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture restore failed.' }
        Assert-LocalBundlerRestore -Project $fixture -PackageVersion $PackageVersion -Source $packageDirectory `
            -Cache $nugetDirectory -RequiredPackages @('DotNet.Bundler', 'DotNet.Bundler.MSBuild', 'DotNet.Bundler.Wix')
        dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packageDirectory `
            -p:RestorePackagesPath=$nugetDirectory `
            -p:BundlerPackageVersion=$PackageVersion -p:BundlerOutputPath=$outputDirectory -p:BundlerIdentifier=$identifier
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture publish failed.' }

        New-Item -ItemType Directory -Path $apiFixtureDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $apiFixtureSource 'Msi.Api.PackageFixture.csproj') -Destination $apiFixture
        Copy-Item -LiteralPath (Join-Path $apiFixtureSource 'Program.cs') -Destination (Join-Path $apiFixtureDirectory 'Program.cs')
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
    $apiDatabase = $apiInstaller.OpenDatabase($apiMsi, 0)
    $apiView = $apiDatabase.OpenView("SELECT Value FROM Property WHERE Property = 'ProductName'")
    $apiView.Execute()
    $apiRecord = $apiView.Fetch()
    if ($null -eq $apiRecord -or $apiRecord.StringData(1) -ne 'MSI API Package Fixture') {
        throw 'Standalone MSI API package created an unexpected product.'
    }
    $apiView.Close()
    Write-Host "Standalone MSI API SHA-256: $((Get-FileHash -LiteralPath $apiMsi -Algorithm SHA256).Hash)"

    $msi = Join-Path $outputDirectory 'win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
    if (-not (Test-Path -LiteralPath $msi)) { throw "MSI was not produced: $msi" }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($msi, 0)
    $view = $database.OpenView("SELECT Value FROM Property WHERE Property = 'ProductCode'")
    $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) { throw 'MSI has no ProductCode.' }
    $productCode = $record.StringData(1)
    $view.Close()
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

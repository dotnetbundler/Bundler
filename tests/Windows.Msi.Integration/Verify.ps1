param(
    [string]$Configuration = 'Release',
    [switch]$ConfirmDisposableVm,
    [switch]$ConfirmLocalInstall
)

$ErrorActionPreference = 'Stop'
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
$sessionId = [guid]::NewGuid().ToString('N')
$identifier = "com.example.bundler.msi.smoke.$sessionId"
$sessionRoot = Join-Path $env:TEMP "Bundler-Msi-Smoke-$sessionId"
$packageDirectory = Join-Path $sessionRoot 'packages'
$outputDirectory = Join-Path $sessionRoot 'output'
$nugetDirectory = Join-Path $sessionRoot 'nuget'
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
        dotnet pack Bundler.slnx -c $Configuration -o $packageDirectory
        if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
        dotnet restore $fixture --source $packageDirectory -p:RestorePackagesPath=$nugetDirectory
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture restore failed.' }
        dotnet publish $fixture -c $Configuration --no-restore -p:RestorePackagesPath=$nugetDirectory -p:BundlerOutputPath=$outputDirectory -p:BundlerIdentifier=$identifier
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture publish failed.' }
    }
    finally { Pop-Location }

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

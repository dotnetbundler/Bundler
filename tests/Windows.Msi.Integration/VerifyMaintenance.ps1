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
$id = [guid]::NewGuid().ToString('N')
$identifier = "com.example.bundler.msi.maintenance.$id"
$root = Join-Path $env:TEMP "Bundler-Msi-Maintenance-$id"
$packages = Join-Path $root 'packages'
$cache = Join-Path $root 'nuget'
$fixtureDirectory = Join-Path $root 'fixture'
$output = Join-Path $root 'output'
$install = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64"
$executable = Join-Path $install 'BundlerMsiSmoke.exe'
$resource = Join-Path $install 'docs\marker.txt'
$unknown = Join-Path $install 'user-created.txt'
$msi = Join-Path $output 'win-x64\msi\Bundler MSI Smoke-1.0.0.msi'
$faultMsi = Join-Path $root 'injected-failure.msi'
$installer = $null
$productCode = $null
$installed = $false
$chineseInstalled = $false
$chineseCode = $null
$unknownCreated = $false

if (Test-Path -LiteralPath $install) { throw "Test install path already exists: $install" }
New-Item -ItemType Directory -Path $root, $packages | Out-Null
try {
    $fixture = Copy-MsiTestFixture -Source (Join-Path $PSScriptRoot 'Fixture') -Destination $fixtureDirectory
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration -PackageVersion $PackageVersion -PackageDirectory $packages
    Restore-MsiTestFixture -Project $fixture -PackageDirectory $packages -PackageCache $cache -PackageVersion $PackageVersion
    dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packages `
        -p:RestorePackagesPath=$cache -p:BundlerPackageVersion=$PackageVersion `
        -p:BundlerOutputPath=$output -p:BundlerIdentifier=$identifier
    if ($LASTEXITCODE -ne 0) { throw 'MSI maintenance fixture publish failed.' }
    if (-not (Test-Path -LiteralPath $msi)) { throw "MSI was not produced: $msi" }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $productCode = Get-MsiProperty $installer $msi 'ProductCode'
    if ($installer.ProductState($productCode) -ne -1) { throw "Test product already registered: $productCode" }
    Write-Host "MSI SHA-256: $((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash)"
    Write-Host "Fixture identifier: $identifier; ProductCode: $productCode"
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "Windows: $($os.Caption), build $($os.BuildNumber), $($os.OSArchitecture)"

    $damaged = Join-Path $root 'damaged.msi'
    Set-Content -LiteralPath $damaged -Value 'invalid MSI test fixture' -Encoding Ascii
    $damagedCode = Invoke-Msi @('/i', ('"' + $damaged + '"'), '/qn', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'damaged.log') + '"'))
    if ($damagedCode -notin @(1619, 1620) -or $installer.ProductState($productCode) -ne -1 -or
        (Test-Path -LiteralPath $install)) {
        throw "A damaged MSI returned unexpected code $damagedCode or left product state."
    }

    # Only the disposable copy receives a test-only deferred failure action.
    # It runs after InstallFiles in the native execution script, causing rollback.
    Copy-Item -LiteralPath $msi -Destination $faultMsi
    $database = $installer.OpenDatabase($faultMsi, 1)
    $view = $database.OpenView("SELECT Sequence FROM InstallExecuteSequence WHERE Action = 'InstallFiles'")
    $null = $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) { throw 'InstallFiles action is absent from the fixture.' }
    $failureSequence = $record.IntegerData(1) + 1
    $null = $view.Close()
    $view = $database.OpenView('CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(64), `Target` CHAR(255) LOCALIZABLE PRIMARY KEY `Action`)')
    $null = $view.Execute()
    $null = $view.Close()
    $view = $database.OpenView("INSERT INTO ``CustomAction`` (``Action``, ``Type``, ``Source``, ``Target``) VALUES ('BundlerTestFail', 1058, 'TARGETDIR', '[SystemFolder]cmd.exe /c exit /b 17')")
    $null = $view.Execute()
    $null = $view.Close()
    $view = $database.OpenView("INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('BundlerTestFail', 'NOT Installed', $failureSequence)")
    $null = $view.Execute()
    $null = $view.Close()
    $null = $database.Commit()
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    $view = $null
    $record = $null
    $database = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    $failLog = Join-Path $root 'injected-failure.log'
    $failure = Invoke-Msi @('/i', ('"' + $faultMsi + '"'), '/qn', '/norestart', '/L*v', ('"' + $failLog + '"'))
    if ($failure -ne 1603 -or -not (Select-String -LiteralPath $failLog -Pattern 'BundlerTestFail' -Quiet) -or
        -not (Select-String -LiteralPath $failLog -Pattern 'Executing op: FileCopy' -Quiet)) {
        throw "Injected MSI failure returned $failure without executing the expected action. See $failLog"
    }
    if ($installer.ProductState($productCode) -ne -1 -or (Test-Path -LiteralPath $executable) -or
        (Test-Path -LiteralPath $resource)) {
        throw 'Failed MSI installation retained product registration or managed files.'
    }

    $installed = $true
    $passiveInstall = Invoke-Msi @('/i', ('"' + $msi + '"'), '/passive', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'passive-install.log') + '"'))
    if ($passiveInstall -ne 0 -or $installer.ProductState($productCode) -ne 5 -or
        -not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $resource)) {
        throw "Passive MSI installation failed with $passiveInstall."
    }
    Set-Content -LiteralPath $unknown -Value 'preserve user data'
    $unknownCreated = $true
    Remove-Item -LiteralPath $resource
    $repair = Invoke-Msi @('/fomus', ('"' + $msi + '"'), '/qn', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'quiet-repair.log') + '"'))
    if ($repair -ne 0 -or -not (Test-Path -LiteralPath $resource) -or
        -not (Test-Path -LiteralPath $unknown) -or $installer.ProductState($productCode) -ne 5) {
        throw "Quiet MSI repair failed with $repair or changed unmanaged data."
    }

    $license = Join-Path $root 'application-license.rtf'
    Set-Content -LiteralPath $license -Value '{\rtf1\ansi Test-only application license.}' -Encoding Ascii
    $chineseOutput = Join-Path $root 'output-zh'
    dotnet publish $fixture -c $Configuration --no-restore -p:BundlerPackageSource=$packages `
        -p:RestorePackagesPath=$cache -p:BundlerPackageVersion=$PackageVersion `
        -p:BundlerOutputPath=$chineseOutput -p:BundlerIdentifier=$identifier `
        -p:BundlerWixLanguage=zh-CN "-p:BundlerLicenseFile=$license"
    if ($LASTEXITCODE -ne 0) { throw 'Localized MSI maintenance fixture publish failed.' }
    $chineseMsi = Join-Path $chineseOutput 'win-x64\msi\Bundler MSI Smoke-1.0.0-zh-cn.msi'
    $chineseInstall = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x64-zh-cn"
    if (-not (Test-Path -LiteralPath $chineseMsi) -or (Test-Path -LiteralPath $chineseInstall)) {
        throw 'Chinese MSI was missing or its independent install path already exists.'
    }
    $chineseCode = Get-MsiProperty $installer $chineseMsi 'ProductCode'
    if ((Get-MsiProperty $installer $chineseMsi 'ProductLanguage') -ne '2052' -or
        $chineseCode -eq $productCode) { throw 'Chinese MSI language or identity is incorrect.' }
    $chineseInstalled = $true
    $chineseResult = Invoke-Msi @('/i', ('"' + $chineseMsi + '"'), '/qn', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'chinese-install.log') + '"'))
    if ($chineseResult -ne 0 -or $installer.ProductState($chineseCode) -ne 5 -or
        -not (Test-Path -LiteralPath (Join-Path $chineseInstall 'BundlerMsiSmoke.exe')) -or
        $installer.ProductState($productCode) -ne 5) {
        throw "Chinese MSI did not install independently: $chineseResult"
    }
    $chineseRemove = Invoke-Msi @('/x', $chineseCode, '/qn', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'chinese-uninstall.log') + '"'))
    if ($chineseRemove -ne 0 -or $installer.ProductState($chineseCode) -ne -1 -or
        (Test-Path -LiteralPath $chineseInstall) -or $installer.ProductState($productCode) -ne 5) {
        throw "Chinese MSI did not uninstall without affecting English: $chineseRemove"
    }
    $chineseInstalled = $false

    $uninstall = Invoke-Msi @('/x', $productCode, '/passive', '/norestart', '/L*v',
        ('"' + (Join-Path $root 'passive-uninstall.log') + '"'))
    if ($uninstall -ne 0 -or $installer.ProductState($productCode) -ne -1 -or
        (Test-Path -LiteralPath $executable) -or (Test-Path -LiteralPath $resource) -or
        -not (Test-Path -LiteralPath $unknown)) {
        throw "Passive MSI uninstall failed with $uninstall or changed unmanaged data."
    }
    $installed = $false
    Write-Host "PASS: damaged MSI rejection ($damagedCode), deferred failure rollback, passive install/uninstall, quiet repair, language isolation, and user data preservation."
}
finally {
    if ($chineseInstalled -and $installer -and $chineseCode -and $installer.ProductState($chineseCode) -ne -1) {
        $cleanupZh = Invoke-Msi @('/x', $chineseCode, '/qn', '/norestart', '/L*v',
            ('"' + (Join-Path $root 'cleanup-chinese.log') + '"'))
        if ($cleanupZh -ne 0) { throw "Chinese MSI cleanup failed with $cleanupZh; inspect $root" }
    }
    if ($installed -and $installer -and $productCode -and $installer.ProductState($productCode) -ne -1) {
        $cleanup = Invoke-Msi @('/x', $productCode, '/qn', '/norestart', '/L*v',
            ('"' + (Join-Path $root 'cleanup-uninstall.log') + '"'))
        if ($cleanup -ne 0) { throw "MSI cleanup failed with $cleanup; inspect $root" }
    }
    if ($unknownCreated -and (Test-Path -LiteralPath $unknown)) { Remove-Item -LiteralPath $unknown -Force }
    if ((Test-Path -LiteralPath $install) -and
        -not (Get-ChildItem -LiteralPath $install -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $install
    }
    Write-Host "MSI maintenance logs and packages retained for review: $root"
    if (Test-Path -LiteralPath $install) { throw "MSI maintenance install directory remains: $install" }
    if ($chineseInstall -and (Test-Path -LiteralPath $chineseInstall)) {
        throw "Chinese MSI maintenance install directory remains: $chineseInstall"
    }
}

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
$identifier = "com.example.bundler.msi.x86.$runId"
$root = Join-Path $env:TEMP "Bundler-Msi-WinMsi5-$runId"
$packages = Join-Path $root 'packages'
$nuget = Join-Path $root 'nuget'
$install = Join-Path $env:LOCALAPPDATA "Programs\$identifier-x86"
$unknown = Join-Path $install 'user-created.txt'
$installer = $null
$codes = @()
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

if (Test-Path -LiteralPath $install) { throw "Test installation already exists: $install" }
New-Item -ItemType Directory -Path $root, $packages | Out-Null
try {
    Pack-MsiTestPackages -Repository $repository -Configuration $Configuration -PackageVersion $PackageVersion -PackageDirectory $packages
    $cases = @(
        @{ Name = 'v1-reject'; AppVersion = '1.0.0'; MsiVersion = '1.0.0'; Allow = 'false' },
        @{ Name = 'v2-mapped'; AppVersion = '1.1.0-beta.1'; MsiVersion = '1.1.0'; Allow = 'false' },
        @{ Name = 'v1-allow'; AppVersion = '1.0.0'; MsiVersion = '1.0.0'; Allow = 'true' },
        @{ Name = 'v2-collision'; AppVersion = '1.1.0-beta.2'; MsiVersion = '1.1.0'; Allow = 'false' }
    )
    foreach ($case in $cases) {
        $name = $case.Name
        $project = Copy-MsiTestFixture -Source (Join-Path $PSScriptRoot 'Fixture') `
            -Destination (Join-Path $root "fixture-$name")
        Restore-MsiTestFixture -Project $project -PackageDirectory $packages -PackageCache $nuget `
            -PackageVersion $PackageVersion -RuntimeIdentifier 'win-x86'
        $output = Join-Path $root "output-$name"
        dotnet publish $project -c $Configuration --no-restore `
            "-p:BundlerPackageSource=$packages" "-p:RestorePackagesPath=$nuget" `
            "-p:BundlerPackageVersion=$PackageVersion" '-p:RuntimeIdentifier=win-x86' `
            "-p:BundlerOutputPath=$output" "-p:BundlerIdentifier=$identifier" `
            "-p:BundlerVersion=$($case.AppVersion)" "-p:BundlerWixMsiVersion=$($case.MsiVersion)" `
            "-p:BundlerWixAllowDowngrades=$($case.Allow)" '-p:MsiLifecycleTest=true'
        if ($LASTEXITCODE -ne 0) { throw "x86 MSBuild fixture publish failed for $name" }
        $case.MsiPath = Join-Path $output "win-x86\msi\Bundler MSI Smoke-$($case.MsiVersion).msi"
        if (-not (Test-Path -LiteralPath $case.MsiPath)) { throw "Missing MSI: $($case.MsiPath)" }
    }

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
    if ($LASTEXITCODE -ne 0) { throw 'x86 API fixture restore failed.' }
    Assert-LocalBundlerRestore -Project $apiProject -PackageVersion $PackageVersion -Source $packages `
        -Cache $nuget -RequiredPackages @('DotNet.Bundler.Wix', 'DotNet.Bundler.Core', 'DotNet.Bundler.Abstractions')
    $apiOutput = Join-Path $root 'api-output'
    dotnet run --project $apiProject -c $Configuration --no-restore "-p:RestorePackagesPath=$nuget" `
        "-p:BundlerPackageSource=$packages" "-p:BundlerPackageVersion=$PackageVersion" `
        -- $apiOutput (Join-Path $root 'api-tools') win-x86 2.0.0-beta.1 1.8.4 false
    if ($LASTEXITCODE -ne 0) { throw 'x86 standalone backend API fixture failed.' }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $apiMsi = Join-Path $apiOutput 'artifacts\win-x86\msi\MSI API Package Fixture-1.8.4.msi'
    if (-not (Test-Path -LiteralPath $apiMsi) -or
        (Get-MsiProperty $installer $apiMsi 'ProductVersion') -ne '1.8.4') {
        throw 'Standalone API did not produce the mapped x86 MSI.'
    }
    foreach ($case in $cases) {
        if ((Get-MsiProperty $installer $case.MsiPath 'ProductVersion') -ne $case.MsiVersion) {
            throw "Wrong mapped MSI ProductVersion for $($case.Name)"
        }
        Write-Host "$($case.Name) MSI SHA-256: $((Get-FileHash -LiteralPath $case.MsiPath -Algorithm SHA256).Hash)"
    }
    Write-Host "API MSI SHA-256: $((Get-FileHash -LiteralPath $apiMsi -Algorithm SHA256).Hash)"
    $v1 = $cases[0].MsiPath
    $v2 = $cases[1].MsiPath
    $v1Allow = $cases[2].MsiPath
    $v2Collision = $cases[3].MsiPath
    $code1 = Get-MsiProperty $installer $v1 'ProductCode'
    $code2 = Get-MsiProperty $installer $v2 'ProductCode'
    $codes = @($code1, $code2)
    if ($code1 -eq $code2 -or (Get-MsiProperty $installer $v1Allow 'ProductCode') -ne $code1) {
        throw 'MSI version identity or same-version ProductCode is incorrect.'
    }
    if ((Get-MsiProperty $installer $v2Collision 'ProductCode') -ne $code2) {
        throw 'Two application versions mapped to one MSI version must share ProductCode for collision rejection.'
    }
    Assert-ProductState $code1 $false
    Assert-ProductState $code2 $false
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "Windows: $($os.Caption), build $($os.BuildNumber), $($os.OSArchitecture)"
    Write-Host "Fixture identifier: $identifier; v1=$code1; v2=$code2"

    if ((Invoke-LoggedMsi 'install-v1' @('/i', ('"' + $v1 + '"'))) -ne 0) { throw 'x86 v1 install failed.' }
    Assert-ProductState $code1 $true
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v1-only.txt'))) { throw 'x86 v1 payload is missing.' }
    $reg32 = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
    try {
        $componentKey = $reg32.OpenSubKey("Software\DotNetBundler\Products\$identifier\win-x86\Components")
        try {
            if (-not $componentKey -or -not $componentKey.GetValue('DefinitionHash')) {
                throw 'x86 component registration is absent from the 32-bit registry view.'
            }
        }
        finally { if ($componentKey) { $componentKey.Dispose() } }
    }
    finally { $reg32.Dispose() }
    Set-Content -LiteralPath $unknown -Value 'preserve user data'
    $unknownCreated = $true

    if ((Invoke-LoggedMsi 'upgrade-v2' @('/i', ('"' + $v2 + '"'))) -ne 0) { throw 'Mapped x86 v2 upgrade failed.' }
    Assert-ProductState $code1 $false
    Assert-ProductState $code2 $true
    if (Test-Path -LiteralPath (Join-Path $install 'docs\v1-only.txt')) { throw 'Upgrade retained v1 payload.' }
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v2-only.txt'))) { throw 'Upgrade lost mapped v2 payload.' }
    if (-not (Test-Path -LiteralPath $unknown)) { throw 'Upgrade removed unknown user data.' }

    $collisionCode = Invoke-LoggedMsi 'mapped-version-collision' @('/i', ('"' + $v2Collision + '"'))
    if ($collisionCode -ne 1638) { throw "Mapped same-version collision returned $collisionCode instead of 1638." }
    Assert-ProductState $code2 $true

    $rejectCode = Invoke-LoggedMsi 'downgrade-rejected' @('/i', ('"' + $v1 + '"'))
    if ($rejectCode -ne 1603 -or
        -not (Select-String -LiteralPath (Join-Path $root 'downgrade-rejected.log') `
            -Pattern 'A newer version of Bundler MSI Smoke is already installed' -Quiet)) {
        throw "Default x86 downgrade was not rejected as expected: $rejectCode"
    }
    Assert-ProductState $code2 $true

    if ((Invoke-LoggedMsi 'downgrade-allowed' @('/i', ('"' + $v1Allow + '"'))) -ne 0) {
        throw 'Explicitly allowed x86 downgrade failed.'
    }
    Assert-ProductState $code1 $true
    Assert-ProductState $code2 $false
    if (-not (Test-Path -LiteralPath (Join-Path $install 'docs\v1-only.txt')) -or
        (Test-Path -LiteralPath (Join-Path $install 'docs\v2-only.txt')) -or
        -not (Test-Path -LiteralPath $unknown)) {
        throw 'Downgrade did not replace managed files while preserving user data.'
    }

    if ((Invoke-LoggedMsi 'uninstall-v1' @('/x', $code1)) -ne 0) { throw 'x86 uninstall failed.' }
    Assert-ProductState $code1 $false
    if (-not (Test-Path -LiteralPath $unknown) -or
        (Test-Path -LiteralPath (Join-Path $install 'BundlerMsiSmoke.exe'))) {
        throw 'Uninstall did not preserve only the unknown user file.'
    }
    Write-Host 'PASS: package-only x86 API/MSBuild, mapped MSI version, rejected and allowed downgrade, and user data ownership.'
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
    if ((Test-Path -LiteralPath $install) -and
        -not (Get-ChildItem -LiteralPath $install -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $install
    }
    Write-Host "WIN-MSI-5 logs and packages retained: $root"
    if (Test-Path -LiteralPath $install) { throw "WIN-MSI-5 install directory remains: $install" }
}

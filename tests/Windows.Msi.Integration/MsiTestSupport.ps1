$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..\AssertLocalRestore.ps1')

function Assert-MsiTestHost([bool]$DisposableVm, [bool]$LocalInstall) {
    if ($DisposableVm -eq $LocalInstall) {
        throw 'Pass exactly one of -ConfirmDisposableVm or -ConfirmLocalInstall.'
    }
    if ($DisposableVm) {
        $computer = Get-CimInstance Win32_ComputerSystem
        if ("$($computer.Manufacturer) $($computer.Model)" -notmatch '(?i)(virtual|vmware|qemu|kvm|hyper-v|parallels|xen)') {
            throw 'The host does not identify as a virtual machine; MSI installation was refused.'
        }
    }
}

function Pack-MsiTestPackages(
    [string]$Repository, [string]$Configuration, [string]$PackageVersion, [string]$PackageDirectory
) {
    Push-Location $Repository
    try {
        & dotnet pack Bundler.slnx -c $Configuration -o $PackageDirectory "-p:BundlerPackageVersion=$PackageVersion"
        if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
    }
    finally { Pop-Location }
    foreach ($name in @('DotNet.Bundler', 'DotNet.Bundler.Wix', 'DotNet.Bundler.MSBuild')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory "$name.$PackageVersion.nupkg"))) {
            throw "Expected local package is missing: $name/$PackageVersion"
        }
    }
    $wixPackage = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory "DotNet.Bundler.Wix.$PackageVersion.nupkg"))
    try {
        $entries = @($wixPackage.Entries | ForEach-Object FullName)
        foreach ($entry in @('lib/netstandard2.0/DotNet.Bundler.Wix.dll', 'licenses/wix/LICENSE.TXT',
                'licenses/wix/wix3141-source.zip', 'licenses/wix/msi-wix-provenance.md',
                'licenses/wix/SHA256SUMS', 'THIRD-PARTY-NOTICES.md')) {
            if ($entries -notcontains $entry) { throw "Standalone MSI backend package is missing $entry" }
        }
        $sources = @{
            'licenses/wix/LICENSE.TXT' = 'third_party/wix/LICENSE.TXT'
            'licenses/wix/wix3141-source.zip' = 'third_party/wix/wix3141-source.zip'
            'licenses/wix/msi-wix-provenance.md' = 'third_party/wix/msi-wix-provenance.md'
            'licenses/wix/SHA256SUMS' = 'third_party/wix/SHA256SUMS'
            'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
        }
        foreach ($name in $sources.Keys) {
            $stream = $wixPackage.GetEntry($name).Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            $expectedHash = (Get-FileHash -LiteralPath (Join-Path $Repository $sources[$name]) -Algorithm SHA256).Hash
            if ($actualHash -ne $expectedHash) { throw "MSI package redistribution file differs from repository source: $name" }
        }
    }
    finally { $wixPackage.Dispose() }
}

function Copy-MsiTestFixture([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to replace an existing test fixture: $Destination" }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($name in @('BundlerMsiSmoke.csproj', 'Program.cs')) {
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $Destination $name)
    }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    Copy-Item -LiteralPath (Join-Path $repository 'Bundler.LocalPackages.props') `
        -Destination (Join-Path $Destination 'Bundler.LocalPackages.props')
    Copy-Item -LiteralPath (Join-Path $Source 'Assets') -Destination (Join-Path $Destination 'Assets') -Recurse
    return Join-Path $Destination 'BundlerMsiSmoke.csproj'
}

function Restore-MsiTestFixture(
    [string]$Project, [string]$PackageDirectory, [string]$PackageCache, [string]$PackageVersion,
    [string]$RuntimeIdentifier = ''
) {
    $properties = @("-p:BundlerPackageSource=$PackageDirectory", "-p:RestorePackagesPath=$PackageCache",
        "-p:BundlerPackageVersion=$PackageVersion")
    if ($RuntimeIdentifier) { $properties += "-p:RuntimeIdentifier=$RuntimeIdentifier" }
    & dotnet restore $Project @properties
    if ($LASTEXITCODE -ne 0) { throw "MSI fixture restore failed: $Project" }
    Assert-LocalBundlerRestore -Project $Project -PackageVersion $PackageVersion -Source $PackageDirectory `
        -Cache $PackageCache -RequiredPackages @('DotNet.Bundler', 'DotNet.Bundler.MSBuild', 'DotNet.Bundler.Wix')
}

function Get-MsiProperty($Installer, [string]$Path, [string]$Name) {
    $database = $Installer.OpenDatabase($Path, 0)
    $view = $database.OpenView("SELECT Value FROM Property WHERE Property = '$Name'")
    try {
        $null = $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "MSI property $Name is missing: $Path" }
        return $record.StringData(1)
    }
    finally { $null = $view.Close() }
}

function Invoke-Msi([string[]]$Arguments) {
    return (Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList $Arguments `
        -Wait -PassThru -WindowStyle Hidden).ExitCode
}

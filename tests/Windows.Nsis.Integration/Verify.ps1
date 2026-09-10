param(
    [string]$Configuration = "Release",
    [string]$PackageVersion = "0.1.0-alpha.11"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$integrationRoot = Join-Path $repositoryRoot "artifacts\windows-nsis-integration"
$packageDirectory = Join-Path $repositoryRoot "artifacts\packages"
$packagePath = Join-Path $packageDirectory "DotNet.Bundler.$PackageVersion.nupkg"
$fixtureProject = Join-Path $PSScriptRoot "Fixture\BundlerIntegrationFixture.csproj"
$packageCache = Join-Path $integrationRoot "packages"
$bundleOutput = Join-Path $integrationRoot "bundle"
$perMachineBundleOutput = Join-Path $integrationRoot "bundle-per-machine"
$bothBundleOutput = Join-Path $integrationRoot "bundle-both"
$testIcon = Join-Path $integrationRoot "test-installer.ico"
$testHeaderImage = Join-Path $integrationRoot "test-header.bmp"
$testSidebarImage = Join-Path $integrationRoot "test-sidebar.bmp"
$installRoot = Join-Path $integrationRoot "安装 目录"
$installDirectory = Join-Path $installRoot "Bundler Integration Fixture"
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\Bundler Integration Fixture"
$identifier = "com.dotnetbundler.integrationfixture"
$productName = "Bundler Integration Fixture"
$registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$identifier"
$roamingData = Join-Path $env:APPDATA $identifier
$localData = Join-Path $env:LOCALAPPDATA $identifier
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "$productName.lnk"
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$productName"
$startMenuShortcut = Join-Path $startMenuDirectory "$productName.lnk"
$fixtureProcess = $null
$hookMarkers = @("preinstall", "postinstall", "preuninstall", "postuninstall") | ForEach-Object { Join-Path $env:TEMP "DotNetBundler-$_.txt" }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-UnderIntegrationRoot([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $integrationRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the integration root: $fullPath"
    }
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Process failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-WindowsExecutable([string]$FilePath, [string]$ArgumentLine) {
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentLine -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode)`: $FilePath $ArgumentLine"
    }
}

function Build-FixtureBundle([string]$InstallMode, [string]$OutputPath) {
    Invoke-Native "dotnet" @(
        "publish", $fixtureProject, "-c", $Configuration, "--force",
        "-p:BundlerPackageVersion=$PackageVersion",
        "-p:BundlerPackageSource=$packageDirectory",
        "-p:BundlerIntegrationOutput=$OutputPath",
        "-p:BundlerTestIcon=$testIcon",
        "-p:BundlerTestHeaderImage=$testHeaderImage",
        "-p:BundlerTestSidebarImage=$testSidebarImage",
        "-p:BundlerNsisInstallMode=$InstallMode",
        "-p:RestorePackagesPath=$packageCache"
    )
}

function Wait-For([scriptblock]$Condition, [string]$Message, [int]$TimeoutSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    }
    throw $Message
}

function Remove-TestState {
    if ($null -ne $script:fixtureProcess -and -not $script:fixtureProcess.HasExited) {
        Stop-Process -Id $script:fixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath -Recurse -Force }
    if (Test-Path -LiteralPath $desktopShortcut) { Remove-Item -LiteralPath $desktopShortcut -Force }
    if (Test-Path -LiteralPath $startMenuShortcut) { Remove-Item -LiteralPath $startMenuShortcut -Force }
    if (Test-Path -LiteralPath $startMenuDirectory) { Remove-Item -LiteralPath $startMenuDirectory -Force }
    foreach ($path in @($installDirectory, $installRoot)) {
        Assert-UnderIntegrationRoot $path
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if ([IO.Path]::GetFileName($defaultInstallDirectory) -ne $productName) {
        throw "Refusing to remove an unexpected default install path: $defaultInstallDirectory"
    }
    if (Test-Path -LiteralPath $defaultInstallDirectory) {
        Remove-Item -LiteralPath $defaultInstallDirectory -Recurse -Force
    }
    foreach ($path in @($roamingData, $localData)) {
        if ([IO.Path]::GetFileName($path) -ne $identifier) {
            throw "Refusing to remove an unexpected application-data path: $path"
        }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

try {
    Assert-True (Test-Path -LiteralPath $packagePath) "Package not found: $packagePath"
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        foreach ($requiredEntry in @(
            "buildTransitive/DotNet.Bundler.props",
            "buildTransitive/DotNet.Bundler.targets",
            "tasks/netstandard2.0/Bundler.Core.dll",
            "tasks/netstandard2.0/DotNet.Bundler.MSBuild.dll",
            "templates/nsis/installer.nsi",
            "templates/nsis/languages/English.nsh",
            "templates/nsis/languages/SimpChinese.nsh",
            "tools/nsis/nsis-3.12.zip"
        )) {
            Assert-True ($entries -contains $requiredEntry) "NuGet package is missing $requiredEntry"
        }
        Assert-True (-not ($entries | Where-Object { $_ -like "tools/net8.0/*" })) "NuGet package contains the removed net8 CLI driver."
    }
    finally {
        $archive.Dispose()
    }
    foreach ($path in $hookMarkers) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    New-Item -ItemType Directory -Path $integrationRoot -Force | Out-Null
    Assert-UnderIntegrationRoot $packageCache
    if (Test-Path -LiteralPath $packageCache) {
        Remove-Item -LiteralPath $packageCache -Recurse -Force
    }
    Remove-TestState

    $nsisArchivePath = Join-Path $repositoryRoot "third_party\nsis\nsis-3.12.zip"
    $nsisArchive = [IO.Compression.ZipFile]::OpenRead($nsisArchivePath)
    try {
        $iconEntry = $nsisArchive.GetEntry("nsis-3.12/Contrib/Graphics/Icons/modern-install.ico")
        Assert-True ($null -ne $iconEntry) "Bundled NSIS archive does not contain the integration-test icon."
        $inputStream = $iconEntry.Open()
        $outputStream = [IO.File]::Create($testIcon)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }

        foreach ($asset in @(
            @{ Entry = "nsis-3.12/Contrib/Graphics/Header/nsis3-grey.bmp"; Path = $testHeaderImage },
            @{ Entry = "nsis-3.12/Contrib/Graphics/Wizard/nsis3-grey.bmp"; Path = $testSidebarImage }
        )) {
            $entry = $nsisArchive.GetEntry($asset.Entry)
            Assert-True ($null -ne $entry) "Bundled NSIS archive does not contain $($asset.Entry)."
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Create($asset.Path)
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally {
        $nsisArchive.Dispose()
    }

    Build-FixtureBundle "currentUser" $bundleOutput
    Build-FixtureBundle "perMachine" $perMachineBundleOutput
    Build-FixtureBundle "both" $bothBundleOutput

    $installer = Join-Path $bundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    Assert-True (Test-Path -LiteralPath $installer) "Installer was not created: $installer"
    Assert-True (Test-Path -LiteralPath (Join-Path $perMachineBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Per-machine installer was not created."
    Assert-True (Test-Path -LiteralPath (Join-Path $bothBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Both-scope installer was not created."

    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    $installedExecutable = Join-Path $installDirectory "BundlerIntegrationFixture.exe"
    $uninstaller = Join-Path $installDirectory "Uninstall.exe"
    Wait-For { Test-Path -LiteralPath $installedExecutable } "Installed executable is missing."
    Assert-True (Test-Path -LiteralPath $installedExecutable) "Installed executable is missing."
    Assert-True (Test-Path -LiteralPath $uninstaller) "Uninstaller is missing."
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "docs\license.txt")) "Configured external resource is missing."
    Assert-True (Test-Path -LiteralPath $registryPath) "Uninstall registry entry is missing."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "Comments") -eq "Disposable Windows NSIS integration-test fixture.") "Description metadata is missing from the uninstall registry entry."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "URLInfoAbout") -eq "https://example.com/dotnet-bundler-fixture") "Homepage metadata is missing from the uninstall registry entry."
    Assert-True (Test-Path -LiteralPath $desktopShortcut) "Desktop shortcut is missing."
    Assert-True (Test-Path -LiteralPath $startMenuShortcut) "Start Menu shortcut is missing."
    Assert-True (Test-Path -LiteralPath $hookMarkers[0]) "Pre-install hook did not run."
    Assert-True (Test-Path -LiteralPath $hookMarkers[1]) "Post-install hook did not run."

    $script:fixtureProcess = Start-Process -FilePath $installedExecutable -ArgumentList "--wait" -PassThru
    Start-Sleep -Milliseconds 500
    Assert-True (-not $script:fixtureProcess.HasExited) "Fixture process did not remain running."
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    $script:fixtureProcess.Refresh()
    Assert-True $script:fixtureProcess.HasExited "Reinstall did not close the running application."

    $runtimeData = Join-Path $installDirectory "runtime-created.db"
    Set-Content -LiteralPath $runtimeData -Value "preserve" -Encoding UTF8
    New-Item -ItemType Directory -Path $roamingData -Force | Out-Null
    New-Item -ItemType Directory -Path $localData -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $roamingData "settings.json") -Value "{}" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $localData "cache.bin") -Value "cache" -Encoding UTF8
    Invoke-WindowsExecutable $uninstaller "/S"
    Wait-For { -not (Test-Path -LiteralPath $installedExecutable) } "Packaged executable survived uninstall."
    Assert-True (Test-Path -LiteralPath $runtimeData) "Default uninstall should preserve runtime-created program data."
    Assert-True (-not (Test-Path -LiteralPath $installedExecutable)) "Packaged executable survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory "docs\license.txt"))) "External resource survived uninstall."
    Assert-True (Test-Path -LiteralPath $roamingData) "Default uninstall removed roaming application data."
    Assert-True (Test-Path -LiteralPath $localData) "Default uninstall removed local application data."
    Assert-True (-not (Test-Path -LiteralPath $registryPath)) "Uninstall registry entry survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "Desktop shortcut survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $startMenuShortcut)) "Start Menu shortcut survived uninstall."
    Assert-True (Test-Path -LiteralPath $hookMarkers[2]) "Pre-uninstall hook did not run."
    Assert-True (Test-Path -LiteralPath $hookMarkers[3]) "Post-uninstall hook did not run."

    Remove-Item -LiteralPath $runtimeData -Force
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    Wait-For { Test-Path -LiteralPath (Join-Path $installDirectory "Uninstall.exe") } "Second install did not complete."
    Set-Content -LiteralPath (Join-Path $installDirectory "runtime-created.db") -Value "delete" -Encoding UTF8
    New-Item -ItemType Directory -Path $roamingData -Force | Out-Null
    New-Item -ItemType Directory -Path $localData -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $roamingData "settings.json") -Value "{}" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $localData "cache.bin") -Value "cache" -Encoding UTF8
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "DELETEAPPDATA did not remove the complete program directory."
    Assert-True (-not (Test-Path -LiteralPath $installDirectory)) "DELETEAPPDATA did not remove the complete program directory."
    Assert-True (-not (Test-Path -LiteralPath $roamingData)) "DELETEAPPDATA did not remove roaming application data."
    Assert-True (-not (Test-Path -LiteralPath $localData)) "DELETEAPPDATA did not remove local application data."

    Write-Host "PASS Windows NSIS install/uninstall integration"
}
finally {
    Remove-TestState
}

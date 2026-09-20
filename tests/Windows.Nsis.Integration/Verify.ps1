param(
    [string]$Configuration = "Release",
    [string]$PackageVersion = "0.1.0-alpha.19"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$integrationRoot = Join-Path $repositoryRoot "artifacts\windows-nsis-integration"
$packageDirectory = Join-Path $repositoryRoot "artifacts\packages"
$packagePath = Join-Path $packageDirectory "DotNet.Bundler.$PackageVersion.nupkg"
$msbuildPackagePath = Join-Path $packageDirectory "DotNet.Bundler.MSBuild.$PackageVersion.nupkg"
$nsisPackagePath = Join-Path $packageDirectory "DotNet.Bundler.Nsis.$PackageVersion.nupkg"
$signingPackagePath = Join-Path $packageDirectory "DotNet.Bundler.Signing.Windows.$PackageVersion.nupkg"
$fixtureProject = Join-Path $PSScriptRoot "Fixture\BundlerIntegrationFixture.csproj"
$legacyMsiProject = Join-Path $PSScriptRoot "LegacyMsiFixture\LegacyMsiFixture.wixproj"
$legacyMsiPath = Join-Path $PSScriptRoot "LegacyMsiFixture\bin\$Configuration\LegacyMsiFixture.msi"
$apiFixtureProject = Join-Path $repositoryRoot "tests\Nsis.Api.PackageFixture\Nsis.Api.PackageFixture.csproj"
$packageCache = Join-Path $integrationRoot "packages"
$bundleOutput = Join-Path $integrationRoot "bundle"
$perMachineBundleOutput = Join-Path $integrationRoot "bundle-per-machine"
$bothBundleOutput = Join-Path $integrationRoot "bundle-both"
$upgradeBundleOutput = Join-Path $integrationRoot "bundle-upgrade"
$allowedDowngradeBundleOutput = Join-Path $integrationRoot "bundle-allowed-downgrade"
$legacyMsiProductMigrationBundleOutput = Join-Path $integrationRoot "bundle-legacy-msi-product-migration"
$legacyMsiUpgradeMigrationBundleOutput = Join-Path $integrationRoot "bundle-legacy-msi-upgrade-migration"
$signedBundleOutput = Join-Path $integrationRoot "bundle-signed"
$noShortcutDefaultsBundleOutput = Join-Path $integrationRoot "bundle-no-shortcut-defaults"
$directMsBuildOutput = Join-Path $integrationRoot "bundle-direct-msbuild"
$testIcon = Join-Path $integrationRoot "test-installer.ico"
$testHeaderImage = Join-Path $integrationRoot "test-header.bmp"
$testSidebarImage = Join-Path $integrationRoot "test-sidebar.bmp"
$installRoot = Join-Path $integrationRoot "安装 目录"
$installDirectory = Join-Path $installRoot "Bundler Integration Fixture"
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\Bundler Integration Fixture"
$identifier = "com.dotnetbundler.integrationfixture"
$productName = "Bundler Integration Fixture"
$legacyMsiProductCode = "{1D1A6B03-2BDA-4D18-B12C-574145D9CFA0}"
$legacyMsiUpgradeCode = "{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}"
$legacyMsiInstallDirectory = Join-Path $env:LOCALAPPDATA "Bundler Legacy MSI Fixture"
$registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$identifier"
$fileExtensionRegistryPath = "HKCU:\Software\Classes\.dbfixture"
$fileProgId = "$identifier.File.dbfixture.1"
$fileProgIdRegistryPath = "HKCU:\Software\Classes\$fileProgId"
$urlSchemeRegistryPath = "HKCU:\Software\Classes\bundlerfixture"
$urlProgIdRegistryPath = "HKCU:\Software\Classes\$identifier.Url.bundlerfixture.1"
$capabilitiesRegistryPath = "HKCU:\Software\$identifier\Capabilities"
$registeredApplicationsRegistryPath = "HKCU:\Software\RegisteredApplications"
$deepLinkMarker = Join-Path $env:TEMP "DotNetBundler-deep-link.txt"
$commandLineMarker = Join-Path $env:TEMP "DotNetBundler-command-line.txt"
$roamingData = Join-Path $env:APPDATA $identifier
$localData = Join-Path $env:LOCALAPPDATA $identifier
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "$productName.lnk"
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DotNet Bundler Integration"
$startMenuShortcut = Join-Path $startMenuDirectory "$productName.lnk"
$legacyStartMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Legacy Bundler Fixture"
$legacyStartMenuShortcut = Join-Path $legacyStartMenuDirectory "Legacy Bundler Fixture.lnk"
$fixtureProcess = $null
$testCertificateThumbprint = $null
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

function Invoke-MsiExec([string]$ArgumentLine, [int[]]$AllowedExitCodes = @(0, 3010)) {
    $process = Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList $ArgumentLine -Wait -PassThru
    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        throw "msiexec failed with exit code $($process.ExitCode): $ArgumentLine"
    }
}

function Get-ShortcutInfo([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $folder = Split-Path -Parent $Path
    $name = Split-Path -Leaf $Path
    $shellApplication = New-Object -ComObject Shell.Application
    $item = $shellApplication.NameSpace($folder).ParseName($name)
    [pscustomobject]@{
        TargetPath = $shortcut.TargetPath
        Arguments = $shortcut.Arguments
        WorkingDirectory = $shortcut.WorkingDirectory
        IconLocation = $shortcut.IconLocation
        AppUserModelId = $item.ExtendedProperty("System.AppUserModel.ID")
    }
}

function Set-TestShortcut([string]$Path, [string]$TargetPath) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = Split-Path -Parent $TargetPath
    $shortcut.Save()
}

function Build-FixtureBundle(
    [string]$InstallMode,
    [string]$OutputPath,
    [string]$PackageId = "DotNet.Bundler",
    [string]$ApplicationVersion = "1.0.0",
    [bool]$AllowDowngrades = $false,
    [string]$LegacyMsiProductCodes = "",
    [string]$LegacyMsiUpgradeCodes = "",
    [string]$SigningCertificateThumbprint = "",
    [bool]$ShortcutDesktop = $true,
    [bool]$ShortcutStartMenu = $true
) {
    Invoke-Native "dotnet" @(
        "publish", $fixtureProject, "-c", $Configuration, "--force",
        "-p:BundlerPackageVersion=$PackageVersion",
        "-p:Version=$ApplicationVersion",
        "-p:BundlerIntegrationPackageId=$PackageId",
        "-p:BundlerPackageSource=$packageDirectory",
        "-p:BundlerIntegrationOutput=$OutputPath",
        "-p:BundlerTestIcon=$testIcon",
        "-p:BundlerTestHeaderImage=$testHeaderImage",
        "-p:BundlerTestSidebarImage=$testSidebarImage",
        "-p:BundlerNsisInstallMode=$InstallMode",
        "-p:BundlerNsisAllowDowngrades=$AllowDowngrades",
        "-p:BundlerNsisLegacyMsiProductCodes=$LegacyMsiProductCodes",
        "-p:BundlerNsisLegacyMsiUpgradeCodes=$LegacyMsiUpgradeCodes",
        "-p:BundlerWindowsSigningCertificateThumbprint=$SigningCertificateThumbprint",
        "-p:BundlerNsisShortcutDesktop=$ShortcutDesktop",
        "-p:BundlerNsisShortcutStartMenu=$ShortcutStartMenu",
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
    foreach ($path in @($fileProgIdRegistryPath, $urlSchemeRegistryPath, $urlProgIdRegistryPath, $capabilitiesRegistryPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if (Test-Path -LiteralPath $fileExtensionRegistryPath) {
        Remove-ItemProperty -LiteralPath (Join-Path $fileExtensionRegistryPath "OpenWithProgids") -Name $fileProgId -ErrorAction SilentlyContinue
        if ((Get-ChildItem -LiteralPath $fileExtensionRegistryPath -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $fileExtensionRegistryPath -Recurse -Force
        }
    }
    Remove-ItemProperty -LiteralPath $registeredApplicationsRegistryPath -Name $identifier -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $deepLinkMarker) { Remove-Item -LiteralPath $deepLinkMarker -Force }
    if (Test-Path -LiteralPath $commandLineMarker) { Remove-Item -LiteralPath $commandLineMarker -Force }
    if (Test-Path -LiteralPath $desktopShortcut) { Remove-Item -LiteralPath $desktopShortcut -Force }
    if (Test-Path -LiteralPath $startMenuShortcut) { Remove-Item -LiteralPath $startMenuShortcut -Force }
    if (Test-Path -LiteralPath $startMenuDirectory) { Remove-Item -LiteralPath $startMenuDirectory -Force }
    if (Test-Path -LiteralPath $legacyStartMenuShortcut) { Remove-Item -LiteralPath $legacyStartMenuShortcut -Force }
    if (Test-Path -LiteralPath $legacyStartMenuDirectory) { Remove-Item -LiteralPath $legacyStartMenuDirectory -Force }
    Invoke-MsiExec "/x $legacyMsiProductCode /qn /norestart" @(0, 1605, 3010)
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
    if ([IO.Path]::GetFileName($legacyMsiInstallDirectory) -ne "Bundler Legacy MSI Fixture") {
        throw "Refusing to remove an unexpected legacy MSI path: $legacyMsiInstallDirectory"
    }
    if (Test-Path -LiteralPath $legacyMsiInstallDirectory) {
        Remove-Item -LiteralPath $legacyMsiInstallDirectory -Recurse -Force
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
    Assert-True (Test-Path -LiteralPath $msbuildPackagePath) "Package not found: $msbuildPackagePath"
    Assert-True (Test-Path -LiteralPath $nsisPackagePath) "Package not found: $nsisPackagePath"
    Assert-True (Test-Path -LiteralPath $signingPackagePath) "Package not found: $signingPackagePath"
    $archive = [IO.Compression.ZipFile]::OpenRead($msbuildPackagePath)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        foreach ($requiredEntry in @(
            "buildTransitive/DotNet.Bundler.MSBuild.props",
            "buildTransitive/DotNet.Bundler.MSBuild.targets",
            "tasks/netstandard2.0/DotNet.Bundler.Abstractions.dll",
            "tasks/netstandard2.0/DotNet.Bundler.Core.dll",
            "tasks/netstandard2.0/DotNet.Bundler.Nsis.dll",
            "tasks/netstandard2.0/DotNet.Bundler.Signing.Windows.dll",
            "tasks/netstandard2.0/DotNet.Bundler.MSBuild.dll",
            "licenses/nsis/COPYING",
            "licenses/nsis-plugin/LICENSE"
        )) {
            Assert-True ($entries -contains $requiredEntry) "NuGet package is missing $requiredEntry"
        }
        Assert-True (-not ($entries | Where-Object { $_ -like "tools/net8.0/*" })) "NuGet package contains the removed net8 CLI driver."
    }
    finally {
        $archive.Dispose()
    }
    $nsisPackage = [IO.Compression.ZipFile]::OpenRead($nsisPackagePath)
    try {
        $entries = @($nsisPackage.Entries | ForEach-Object FullName)
        Assert-True ($entries -contains "lib/netstandard2.0/DotNet.Bundler.Nsis.dll") "NSIS API package is missing its netstandard2.0 assembly."
        Assert-True ($entries -contains "licenses/nsis/COPYING") "NSIS API package is missing the upstream NSIS license."
        Assert-True ($entries -contains "licenses/nsis-plugin/LICENSE") "NSIS API package is missing the NsisPlugin license."
    }
    finally {
        $nsisPackage.Dispose()
    }
    $signingPackage = [IO.Compression.ZipFile]::OpenRead($signingPackagePath)
    try {
        $entries = @($signingPackage.Entries | ForEach-Object FullName)
        Assert-True ($entries -contains "lib/netstandard2.0/DotNet.Bundler.Signing.Windows.dll") "Windows signing API package is missing its netstandard2.0 assembly."
    }
    finally {
        $signingPackage.Dispose()
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

    $apiOutput = Join-Path $integrationRoot "standalone-api"
    Invoke-Native "dotnet" @(
        "run", "--project", $apiFixtureProject, "-c", $Configuration,
        "-p:BundlerPackageVersion=$PackageVersion",
        "-p:BundlerPackageSource=$packageDirectory",
        "-p:RestorePackagesPath=$packageCache",
        "--", $apiOutput, (Join-Path $integrationRoot "shared-tools")
    )
    $apiInstaller = Join-Path $apiOutput "artifacts\win-x64\nsis\NSIS API Package Fixture-1.0.0-setup.exe"
    Assert-True (Test-Path -LiteralPath $apiInstaller) "Standalone NSIS API package did not create its installer."

    $nsisArchivePath = Join-Path $repositoryRoot "third_party\nsis\nsis-toolset-3.12-r1.zip"
    $nsisArchive = [IO.Compression.ZipFile]::OpenRead($nsisArchivePath)
    try {
        $iconEntry = $nsisArchive.GetEntry("common/Contrib/Graphics/Icons/modern-install.ico")
        Assert-True ($null -ne $iconEntry) "Bundled NSIS archive does not contain the integration-test icon."
        $inputStream = $iconEntry.Open()
        $outputStream = [IO.File]::Create($testIcon)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }

        foreach ($asset in @(
            @{ Entry = "common/Contrib/Graphics/Header/nsis3-grey.bmp"; Path = $testHeaderImage },
            @{ Entry = "common/Contrib/Graphics/Wizard/nsis3-grey.bmp"; Path = $testSidebarImage }
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

    Invoke-Native "dotnet" @("build", $legacyMsiProject, "-c", $Configuration)
    Assert-True (Test-Path -LiteralPath $legacyMsiPath) "Legacy MSI fixture was not created."

    Build-FixtureBundle "currentUser" $bundleOutput
    Build-FixtureBundle "currentUser" $directMsBuildOutput "DotNet.Bundler.MSBuild"
    Build-FixtureBundle "perMachine" $perMachineBundleOutput
    Build-FixtureBundle "both" $bothBundleOutput
    Build-FixtureBundle "currentUser" $upgradeBundleOutput "DotNet.Bundler" "1.1.0"
    Build-FixtureBundle "currentUser" $allowedDowngradeBundleOutput "DotNet.Bundler" "1.0.0" $true
    Build-FixtureBundle "currentUser" $legacyMsiProductMigrationBundleOutput "DotNet.Bundler" "1.0.0" $false $legacyMsiProductCode ""
    Build-FixtureBundle "currentUser" $legacyMsiUpgradeMigrationBundleOutput "DotNet.Bundler" "1.0.0" $false "" $legacyMsiUpgradeCode

    # 使用当前用户证书存储区验证 MSBuild 参数映射以及安装器、卸载器的双重签名。
    $testCertificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=DotNet.Bundler disposable integration certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter ([DateTime]::Now.AddDays(1))
    $testCertificateThumbprint = $testCertificate.Thumbprint
    Build-FixtureBundle "currentUser" $signedBundleOutput "DotNet.Bundler" "1.0.0" $false "" "" $testCertificateThumbprint
    Build-FixtureBundle "currentUser" $noShortcutDefaultsBundleOutput "DotNet.Bundler" "1.0.0" $false "" "" "" $false $false

    $installer = Join-Path $bundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $upgradeInstaller = Join-Path $upgradeBundleOutput "win-x64\nsis\$productName-1.1.0-setup.exe"
    $allowedDowngradeInstaller = Join-Path $allowedDowngradeBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $legacyMsiProductMigrationInstaller = Join-Path $legacyMsiProductMigrationBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $legacyMsiUpgradeMigrationInstaller = Join-Path $legacyMsiUpgradeMigrationBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $signedInstaller = Join-Path $signedBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $noShortcutDefaultsInstaller = Join-Path $noShortcutDefaultsBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    Assert-True (Test-Path -LiteralPath $installer) "Installer was not created: $installer"
    Assert-True (Test-Path -LiteralPath (Join-Path $directMsBuildOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Direct MSBuild package installer was not created."
    Assert-True (Test-Path -LiteralPath (Join-Path $perMachineBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Per-machine installer was not created."
    Assert-True (Test-Path -LiteralPath (Join-Path $bothBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Both-scope installer was not created."
    Assert-True (Test-Path -LiteralPath $upgradeInstaller) "Upgrade installer was not created."
    Assert-True (Test-Path -LiteralPath $allowedDowngradeInstaller) "Allowed-downgrade installer was not created."
    Assert-True (Test-Path -LiteralPath $legacyMsiProductMigrationInstaller) "ProductCode migration installer was not created."
    Assert-True (Test-Path -LiteralPath $legacyMsiUpgradeMigrationInstaller) "UpgradeCode migration installer was not created."
    Assert-True (Test-Path -LiteralPath $noShortcutDefaultsInstaller) "Shortcut-default fixture installer was not created."
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $signedInstaller).SignerCertificate.Thumbprint -eq $testCertificateThumbprint) "The final installer does not contain the expected Authenticode certificate."
    Invoke-WindowsExecutable $signedInstaller "/S /D=$installDirectory"
    $signedUninstaller = Join-Path $installDirectory "Uninstall.exe"
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $signedUninstaller).SignerCertificate.Thumbprint -eq $testCertificateThumbprint) "The installed uninstaller does not contain the expected Authenticode certificate."
    Invoke-WindowsExecutable $signedUninstaller "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Signed installer test cleanup did not finish."

    # /ARGS 没有 /R 时属于调用错误，必须稳定返回 3 且不能写入载荷。
    $invalidArgumentsProcess = Start-Process -FilePath $installer -ArgumentList "/S /ARGS orphaned /D=$installDirectory" -Wait -PassThru
    Assert-True ($invalidArgumentsProcess.ExitCode -eq 3) "Invalid /ARGS usage did not return exit code 3."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe"))) "Invalid command-line arguments unexpectedly installed the application."

    # /P 只显示进度且自动关闭；/NS 在首次安装时不创建任何快捷方式。
    Invoke-WindowsExecutable $installer "/P /NS /D=$installDirectory"
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe")) "Passive installation did not write the application payload."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "/NS unexpectedly created a desktop shortcut."
    Assert-True (-not (Test-Path -LiteralPath $startMenuShortcut)) "/NS unexpectedly created a Start Menu shortcut."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/P /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Passive-mode test cleanup did not finish."

    # 包配置可以默认取消两种快捷方式；这与 /NS 独立，适用于无人值守的用户选择策略。
    Invoke-WindowsExecutable $noShortcutDefaultsInstaller "/S /D=$installDirectory"
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "Disabled desktop shortcut default was ignored."
    Assert-True (-not (Test-Path -LiteralPath $startMenuShortcut)) "Disabled Start Menu shortcut default was ignored."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Shortcut-default test cleanup did not finish."

    # 分别按 ProductCode 和 UpgradeCode 迁移同一个一次性 MSI，验证两种精确标识路径。
    Invoke-MsiExec "/i `"$legacyMsiPath`" /qn /norestart"
    Assert-True (Test-Path -LiteralPath (Join-Path $legacyMsiInstallDirectory "legacy-payload.txt")) "Legacy MSI fixture was not installed."
    Invoke-WindowsExecutable $legacyMsiProductMigrationInstaller "/S /D=$installDirectory"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $legacyMsiInstallDirectory "legacy-payload.txt"))) "ProductCode migration did not remove the legacy MSI payload."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "ProductCode migration test cleanup did not finish."

    Invoke-MsiExec "/i `"$legacyMsiPath`" /qn /norestart"
    Assert-True (Test-Path -LiteralPath (Join-Path $legacyMsiInstallDirectory "legacy-payload.txt")) "Legacy MSI fixture was not reinstalled."
    Invoke-WindowsExecutable $legacyMsiUpgradeMigrationInstaller "/S /D=$installDirectory"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $legacyMsiInstallDirectory "legacy-payload.txt"))) "UpgradeCode migration did not remove the legacy MSI payload."
    $installedExecutable = Join-Path $installDirectory "BundlerIntegrationFixture.exe"
    $uninstaller = Join-Path $installDirectory "Uninstall.exe"
    Wait-For { Test-Path -LiteralPath $installedExecutable } "Installed executable is missing."
    Assert-True (Test-Path -LiteralPath $installedExecutable) "Installed executable is missing."
    Assert-True (Test-Path -LiteralPath $uninstaller) "Uninstaller is missing."
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "docs\license.txt")) "Configured external resource is missing."
    Assert-True (Test-Path -LiteralPath $registryPath) "Uninstall registry entry is missing."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "Comments") -eq "Disposable Windows NSIS integration-test fixture.") "Description metadata is missing from the uninstall registry entry."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "URLInfoAbout") -eq "https://example.com/dotnet-bundler-fixture") "Homepage metadata is missing from the uninstall registry entry."
    Assert-True ((Get-ItemPropertyValue -LiteralPath (Join-Path $fileExtensionRegistryPath "OpenWithProgids") -Name $fileProgId) -eq "") "File association was not registered as an Open With candidate."
    Assert-True (Test-Path -LiteralPath $fileProgIdRegistryPath) "Application-specific file ProgID is missing."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $capabilitiesRegistryPath -Name "ApplicationName") -eq $productName) "Default-app capabilities are missing."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registeredApplicationsRegistryPath -Name $identifier) -eq "Software\$identifier\Capabilities") "RegisteredApplications entry is missing."
    Assert-True ((Get-ItemPropertyValue -LiteralPath (Join-Path $urlSchemeRegistryPath "shell\open\command") -Name "(default)") -eq "`"$installedExecutable`" `"%1`"") "Deep-link command is missing or not quoted."
    Assert-True (Test-Path -LiteralPath $urlProgIdRegistryPath) "Application-specific URL ProgID is missing."
    Start-Process -FilePath "bundlerfixture:integration-value"
    Wait-For { Test-Path -LiteralPath $deepLinkMarker } "Registered deep link did not launch the installed application."
    Assert-True ((Get-Content -Raw -LiteralPath $deepLinkMarker) -eq "bundlerfixture:integration-value") "Installed application received the wrong deep-link argument."
    Assert-True (Test-Path -LiteralPath $desktopShortcut) "Desktop shortcut is missing."
    Assert-True (Test-Path -LiteralPath $startMenuShortcut) "Start Menu shortcut is missing."
    $desktopShortcutInfo = Get-ShortcutInfo $desktopShortcut
    Assert-True ($desktopShortcutInfo.TargetPath -eq $installedExecutable) "Desktop shortcut target is incorrect."
    Assert-True ($desktopShortcutInfo.Arguments -eq '--shortcut-mode "hello world"') "Desktop shortcut arguments are incorrect."
    Assert-True ($desktopShortcutInfo.WorkingDirectory -eq (Join-Path $installDirectory "docs")) "Desktop shortcut working directory is incorrect."
    Assert-True ($desktopShortcutInfo.IconLocation.StartsWith($installedExecutable, [StringComparison]::OrdinalIgnoreCase)) "Desktop shortcut icon is incorrect."
    Assert-True ($desktopShortcutInfo.AppUserModelId -eq "com.dotnetbundler.integrationfixture.desktop") "Desktop shortcut AppUserModelID is incorrect."
    $startMenuShortcutInfo = Get-ShortcutInfo $startMenuShortcut
    Assert-True ($startMenuShortcutInfo.TargetPath -eq $installedExecutable) "Start Menu shortcut target is incorrect."
    Assert-True ($startMenuShortcutInfo.AppUserModelId -eq "com.dotnetbundler.integrationfixture.desktop") "Start Menu shortcut AppUserModelID is incorrect."
    Assert-True (Test-Path -LiteralPath $hookMarkers[0]) "Pre-install hook did not run."
    Assert-True (Test-Path -LiteralPath $hookMarkers[1]) "Post-install hook did not run."

    $script:fixtureProcess = Start-Process -FilePath $installedExecutable -ArgumentList "--wait" -PassThru
    Start-Sleep -Milliseconds 500
    Assert-True (-not $script:fixtureProcess.HasExited) "Fixture process did not remain running."
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    $script:fixtureProcess.Refresh()
    Assert-True $script:fixtureProcess.HasExited "Reinstall did not close the running application."

    # 自动更新原位覆盖并保留运行时数据及用户删除快捷方式的选择；/R 与 /ARGS
    # 会在成功后以桌面用户身份启动应用。
    $upgradePreservedData = Join-Path $installDirectory "upgrade-preserved.db"
    Set-Content -LiteralPath $upgradePreservedData -Value "preserve" -Encoding UTF8
    # 模拟旧产品名和旧主程序名；更新应安全迁移该快捷方式并刷新全部属性。
    $legacyExecutable = Join-Path $installDirectory "LegacyFixture.exe"
    Copy-Item -LiteralPath $installedExecutable -Destination $legacyExecutable
    Set-TestShortcut $legacyStartMenuShortcut $legacyExecutable
    Remove-Item -LiteralPath $startMenuShortcut -Force
    Remove-Item -LiteralPath $desktopShortcut -Force
    Invoke-WindowsExecutable $upgradeInstaller "/UPDATE /R /ARGS=--protocol-marker `"hello world`" /D=$installDirectory"
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Upgrade did not record the new semantic version."
    Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Upgrade removed runtime-created program data."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "/UPDATE recreated a shortcut that the user had removed."
    Assert-True (Test-Path -LiteralPath $startMenuShortcut) "/UPDATE did not migrate the legacy Start Menu shortcut."
    Assert-True (-not (Test-Path -LiteralPath $legacyStartMenuShortcut)) "/UPDATE left the legacy shortcut behind."
    $migratedShortcutInfo = Get-ShortcutInfo $startMenuShortcut
    Assert-True ($migratedShortcutInfo.TargetPath -eq $installedExecutable) "Migrated shortcut still targets the legacy executable."
    Assert-True ($migratedShortcutInfo.Arguments -eq '--shortcut-mode "hello world"') "Migrated shortcut arguments were not refreshed."
    Assert-True ($migratedShortcutInfo.AppUserModelId -eq "com.dotnetbundler.integrationfixture.desktop") "Migrated shortcut AppUserModelID was not refreshed."
    Wait-For { Test-Path -LiteralPath $commandLineMarker } "/R did not start the installed application."
    $forwardedArguments = @(Get-Content -LiteralPath $commandLineMarker)
    Assert-True ($forwardedArguments.Count -eq 2) "/ARGS did not preserve the expected argument count."
    Assert-True ($forwardedArguments[0] -eq "--protocol-marker" -and $forwardedArguments[1] -eq "hello world") "/ARGS changed the forwarded application arguments."

    # 默认禁止降级；静默模式必须在不修改现有安装的情况下返回失败。
    $downgradeProcess = Start-Process -FilePath $installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($downgradeProcess.ExitCode -eq 4) "A blocked downgrade did not return exit code 4."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Blocked downgrade modified the installed version."
    Assert-True (Test-Path -LiteralPath $installedExecutable) "Blocked downgrade removed the installed application."

    # 明确允许降级的安装器沿用先卸载后替换的路径，并继续保留运行时创建的应用数据。
    Invoke-WindowsExecutable $allowedDowngradeInstaller "/S /D=$installDirectory"
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.0.0") "Allowed silent downgrade did not install the requested version."
    Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Allowed downgrade removed runtime-created program data."

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
    Assert-True (-not (Test-Path -LiteralPath $fileProgIdRegistryPath)) "File ProgID survived uninstall."
    Assert-True ($null -eq (Get-ItemPropertyValue -LiteralPath (Join-Path $fileExtensionRegistryPath "OpenWithProgids") -Name $fileProgId -ErrorAction SilentlyContinue)) "Open With registration survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $urlSchemeRegistryPath)) "Owned deep-link registration survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $urlProgIdRegistryPath)) "URL ProgID survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $capabilitiesRegistryPath)) "Default-app capabilities survived uninstall."
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
    # 模拟另一应用在卸载前接管协议，验证卸载器不会删除新的所有者。
    Set-Item -LiteralPath (Join-Path $urlSchemeRegistryPath "shell\open\command") -Value '"C:\OtherApp\Other.exe" "%1"'
    # 同样接管两个同名快捷方式；卸载器必须按实际目标判定所有权，而不是按名称删除。
    $foreignShortcutTarget = Join-Path $env:WINDIR "System32\notepad.exe"
    Set-TestShortcut $desktopShortcut $foreignShortcutTarget
    Set-TestShortcut $startMenuShortcut $foreignShortcutTarget
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "DELETEAPPDATA did not remove the complete program directory."
    Assert-True (-not (Test-Path -LiteralPath $installDirectory)) "DELETEAPPDATA did not remove the complete program directory."
    Assert-True (-not (Test-Path -LiteralPath $roamingData)) "DELETEAPPDATA did not remove roaming application data."
    Assert-True (-not (Test-Path -LiteralPath $localData)) "DELETEAPPDATA did not remove local application data."
    Assert-True (Test-Path -LiteralPath $urlSchemeRegistryPath) "Uninstall removed a deep-link protocol owned by another application."
    Assert-True (Test-Path -LiteralPath $desktopShortcut) "Uninstall removed a same-name desktop shortcut owned by another application."
    Assert-True (Test-Path -LiteralPath $startMenuShortcut) "Uninstall removed a same-name Start Menu shortcut owned by another application."
    Assert-True ((Get-ShortcutInfo $desktopShortcut).TargetPath -eq $foreignShortcutTarget) "Uninstall changed the foreign desktop shortcut."
    Assert-True ((Get-ShortcutInfo $startMenuShortcut).TargetPath -eq $foreignShortcutTarget) "Uninstall changed the foreign Start Menu shortcut."

    Write-Host "PASS Windows NSIS install/uninstall integration"
}
finally {
    Remove-TestState
    if ($null -ne $testCertificateThumbprint) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$testCertificateThumbprint" -Force -ErrorAction SilentlyContinue
    }
}

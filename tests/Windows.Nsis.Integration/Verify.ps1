param(
    [string]$Configuration = "Release",
    [string]$PackageVersion = "0.1.0-alpha.32",
    [switch]$CleanupOnly
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
$rollbackFailureBundleOutput = Join-Path $integrationRoot "bundle-rollback-failure"
$transactionSnapshotFailureBundleOutput = Join-Path $integrationRoot "bundle-transaction-snapshot-failure"
$transactionActivationFailureBundleOutput = Join-Path $integrationRoot "bundle-transaction-activation-failure"
$payloadRestoreFailureBundleOutput = Join-Path $integrationRoot "bundle-payload-restore-failure"
$registryRestoreFailureBundleOutput = Join-Path $integrationRoot "bundle-registry-restore-failure"
$journalCleanupFailureBundleOutput = Join-Path $integrationRoot "bundle-journal-cleanup-failure"
$shortcutPersistenceFailureBundleOutput = Join-Path $integrationRoot "bundle-shortcut-persistence-failure"
$registryPersistenceFailureBundleOutput = Join-Path $integrationRoot "bundle-registry-persistence-failure"
$commitCleanupFailureBundleOutput = Join-Path $integrationRoot "bundle-commit-cleanup-failure"
$interruptedBundleOutput = Join-Path $integrationRoot "bundle-interrupted"
$rebootRequiredBundleOutput = Join-Path $integrationRoot "bundle-reboot-required"
$allowedDowngradeBundleOutput = Join-Path $integrationRoot "bundle-allowed-downgrade"
$legacyMsiProductMigrationBundleOutput = Join-Path $integrationRoot "bundle-legacy-msi-product-migration"
$legacyMsiUpgradeMigrationBundleOutput = Join-Path $integrationRoot "bundle-legacy-msi-upgrade-migration"
$signedBundleOutput = Join-Path $integrationRoot "bundle-signed"
$noShortcutDefaultsBundleOutput = Join-Path $integrationRoot "bundle-no-shortcut-defaults"
$failingUninstallBundleOutput = Join-Path $integrationRoot "bundle-failing-uninstall-forward"
$interruptedUninstallBundleOutput = Join-Path $integrationRoot "bundle-interrupted-uninstall-forward"
$directMsBuildOutput = Join-Path $integrationRoot "bundle-direct-msbuild"
$unicodeBundleOutput = Join-Path $integrationRoot "bundle-unicode"
$testIcon = Join-Path $integrationRoot "test-installer.ico"
$testHeaderImage = Join-Path $integrationRoot "test-header.bmp"
$testSidebarImage = Join-Path $integrationRoot "test-sidebar.bmp"
$failingInstallerHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-postinstall.nsh"
$failingTransactionSnapshotHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-transaction-snapshot.nsh"
$failingTransactionActivationHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-transaction-activation.nsh"
$failingPayloadRestoreHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-payload-restore.nsh"
$failingRegistryRestoreHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-registry-restore.nsh"
$failingJournalCleanupHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-journal-cleanup.nsh"
$failingShortcutPersistenceHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-shortcut-persistence.nsh"
$failingRegistryPersistenceHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-registry-persistence.nsh"
$failingCommitCleanupHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-commit-cleanup.nsh"
$abortingInstallerHooks = Join-Path $PSScriptRoot "Fixture\Assets\aborting-postinstall.nsh"
$rebootingInstallerHooks = Join-Path $PSScriptRoot "Fixture\Assets\rebooting-postinstall.nsh"
$failingUninstallHooks = Join-Path $PSScriptRoot "Fixture\Assets\failing-postuninstall-forward.nsh"
$interruptedUninstallHooks = Join-Path $PSScriptRoot "Fixture\Assets\interrupted-postuninstall-forward.nsh"
$installRoot = Join-Path $integrationRoot "安装 目录"
$installDirectory = Join-Path $installRoot "Bundler Integration Fixture"
$externalFixtureDirectory = Join-Path $integrationRoot "same-name-external-process"
$reparseOutsideDirectory = Join-Path $integrationRoot "reparse-outside"
$defaultInstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\Bundler Integration Fixture"
$identifier = "com.dotnetbundler.integrationfixture"
$productName = "Bundler Integration Fixture"
$unicodeProductName = "多言語テスト应用"
$unicodeIdentifier = "com.dotnetbundler.localizationfixture"
$unicodeDescription = "Unicode 元数据と説明"
$unicodeInstallRoot = Join-Path $integrationRoot "多语言 安装目录"
$unicodeInstallDirectory = Join-Path $unicodeInstallRoot "应用"
$unicodeRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$unicodeIdentifier"
$unicodeLanguageRegistryPath = "HKCU:\Software\$unicodeIdentifier"
$unicodeStartMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\多语言 开始菜单"
$unicodeStartMenuShortcut = Join-Path $unicodeStartMenuDirectory "$unicodeProductName.lnk"
$fallbackLanguage = if ((Get-UICulture).Name.StartsWith("ja", [StringComparison]::OrdinalIgnoreCase)) { "Korean" } else { "Japanese" }
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
$interruptedHookMarker = Join-Path $env:TEMP "DotNetBundler-interrupted-postinstall.txt"
$failingUninstallHookMarker = Join-Path $env:TEMP "DotNetBundler-failing-postuninstall-forward.once"
$interruptedUninstallHookMarker = Join-Path $env:TEMP "DotNetBundler-interrupted-postuninstall-forward.once"
$roamingData = Join-Path $env:APPDATA $identifier
$localData = Join-Path $env:LOCALAPPDATA $identifier
$transactionDirectory = Join-Path $env:LOCALAPPDATA "DotNetBundler\transactions\$identifier"
$journalTamperFile = Join-Path $integrationRoot "journal-tamper-sentinel.txt"
$journalTamperRegistryPath = "HKCU:\Software\DotNetBundler\JournalTamperSentinel"
$committedTransactionDirectory = "$transactionDirectory.committed"
$uninstallTransactionDirectory = "$transactionDirectory.uninstall"
$committedUninstallTransactionDirectory = "$uninstallTransactionDirectory.committed"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "$productName.lnk"
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DotNet Bundler Integration"
$startMenuShortcut = Join-Path $startMenuDirectory "$productName.lnk"
$legacyStartMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Legacy Bundler Fixture"
$legacyStartMenuShortcut = Join-Path $legacyStartMenuDirectory "Legacy Bundler Fixture.lnk"
$fixtureProcess = $null
$externalFixtureProcess = $null
$testCertificateThumbprint = $null
$directUninstallerCopies = [Collections.Generic.List[string]]::new()
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

function Start-DirectUninstaller(
    [string]$UninstallerPath,
    [string]$InstallPath,
    [string]$ArgumentLine,
    [switch]$Wait
) {
    # NSIS 从安装目录启动卸载器时会先创建一个临时 launcher；launcher 不传播真正
    # 卸载进程的退出码。测试显式复制并传入 `_?=`，既控制实际进程，也与产品恢复路径一致。
    $copyPath = Join-Path $integrationRoot "direct-uninstaller-$([Guid]::NewGuid().ToString('N')).exe"
    Assert-UnderIntegrationRoot $copyPath
    Copy-Item -LiteralPath $UninstallerPath -Destination $copyPath
    $directUninstallerCopies.Add($copyPath)
    $arguments = "$ArgumentLine _?=$InstallPath"
    if ($Wait) {
        return Start-Process -FilePath $copyPath -ArgumentList $arguments -Wait -PassThru
    }
    return Start-Process -FilePath $copyPath -ArgumentList $arguments -PassThru
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

function Set-RegistrySnapshotSubKey([string]$Path, [string]$SubKey) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $input = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.BinaryReader]::new($input)
    try {
        $kind = $reader.ReadByte()
        $root = $reader.ReadString()
        $view = $reader.ReadInt32()
        $null = $reader.ReadString()
        $tail = $reader.ReadBytes([int]($input.Length - $input.Position))
    }
    finally {
        $reader.Dispose()
        $input.Dispose()
    }

    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([byte]$kind)
        $writer.Write([string]$root)
        $writer.Write([int]$view)
        $writer.Write($SubKey)
        $writer.Write($tail)
        $writer.Flush()
        [IO.File]::WriteAllBytes($Path, $output.ToArray())
    }
    finally {
        $writer.Dispose()
        $output.Dispose()
    }
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
    [bool]$ShortcutStartMenu = $true,
    [string]$InstallerHooks = "",
    [string]$ProductName = "Bundler Integration Fixture",
    [string]$Identifier = "com.dotnetbundler.integrationfixture",
    [string]$Languages = "English;SimpChinese",
    [bool]$DisplayLanguageSelector = $true,
    [string]$Description = "Disposable Windows NSIS integration-test fixture.",
    [string]$ShortcutArguments = '--shortcut-mode "hello world"',
    [string]$ShortcutStartMenuFolder = "DotNet Bundler Integration"
) {
    if ([string]::IsNullOrWhiteSpace($InstallerHooks)) {
        $InstallerHooks = Join-Path $PSScriptRoot "Fixture\Assets\installer-hooks.nsh"
    }
    $escapedLanguages = $Languages.Replace(";", "%3B")
    $escapedShortcutArguments = $ShortcutArguments.Replace("%", "%25").Replace(";", "%3B").Replace('"', "%22")
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
        "-p:BundlerIntegrationInstallerHooks=$InstallerHooks",
        "-p:BundlerNsisShortcutDesktop=$ShortcutDesktop",
        "-p:BundlerNsisShortcutStartMenu=$ShortcutStartMenu",
        "-p:BundlerIntegrationProductName=$ProductName",
        "-p:BundlerIntegrationIdentifier=$Identifier",
        "-p:BundlerIntegrationLanguages=$escapedLanguages",
        "-p:BundlerIntegrationDisplayLanguageSelector=$DisplayLanguageSelector",
        "-p:BundlerIntegrationDescription=$Description",
        "-p:BundlerIntegrationShortcutArguments=$escapedShortcutArguments",
        "-p:BundlerIntegrationShortcutStartMenuFolder=$ShortcutStartMenuFolder",
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
    if ($null -ne $script:externalFixtureProcess -and -not $script:externalFixtureProcess.HasExited) {
        Stop-Process -Id $script:externalFixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($path in $directUninstallerCopies) {
        Assert-UnderIntegrationRoot $path
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $directUninstallerCopies.Clear()
    if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath -Recurse -Force }
    if (Test-Path -LiteralPath $journalTamperRegistryPath) { Remove-Item -LiteralPath $journalTamperRegistryPath -Recurse -Force }
    if (Test-Path -LiteralPath $unicodeRegistryPath) { Remove-Item -LiteralPath $unicodeRegistryPath -Recurse -Force }
    if (Test-Path -LiteralPath $unicodeLanguageRegistryPath) { Remove-Item -LiteralPath $unicodeLanguageRegistryPath -Recurse -Force }
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
    if (Test-Path -LiteralPath $interruptedHookMarker) { Remove-Item -LiteralPath $interruptedHookMarker -Force }
    if (Test-Path -LiteralPath $failingUninstallHookMarker) { Remove-Item -LiteralPath $failingUninstallHookMarker -Force }
    if (Test-Path -LiteralPath $interruptedUninstallHookMarker) { Remove-Item -LiteralPath $interruptedUninstallHookMarker -Force }
    foreach ($path in $hookMarkers) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    if (Test-Path -LiteralPath $desktopShortcut) { Remove-Item -LiteralPath $desktopShortcut -Force }
    if (Test-Path -LiteralPath $startMenuShortcut) { Remove-Item -LiteralPath $startMenuShortcut -Force }
    if (Test-Path -LiteralPath $startMenuDirectory) { Remove-Item -LiteralPath $startMenuDirectory -Force }
    if (Test-Path -LiteralPath $legacyStartMenuShortcut) { Remove-Item -LiteralPath $legacyStartMenuShortcut -Force }
    if (Test-Path -LiteralPath $legacyStartMenuDirectory) { Remove-Item -LiteralPath $legacyStartMenuDirectory -Force }
    if (Test-Path -LiteralPath $unicodeStartMenuShortcut) { Remove-Item -LiteralPath $unicodeStartMenuShortcut -Force }
    if (Test-Path -LiteralPath $unicodeStartMenuDirectory) { Remove-Item -LiteralPath $unicodeStartMenuDirectory -Force }
    foreach ($path in @($transactionDirectory, $committedTransactionDirectory, $uninstallTransactionDirectory, $committedUninstallTransactionDirectory)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Invoke-MsiExec "/x $legacyMsiProductCode /qn /norestart" @(0, 1605, 3010)
    foreach ($path in @($installDirectory, $installRoot, $externalFixtureDirectory, $reparseOutsideDirectory, $unicodeInstallRoot)) {
        Assert-UnderIntegrationRoot $path
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Assert-UnderIntegrationRoot $journalTamperFile
    if (Test-Path -LiteralPath $journalTamperFile) { Remove-Item -LiteralPath $journalTamperFile -Force }
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
    if ([IO.Path]::GetFileName($transactionDirectory) -ne $identifier -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($transactionDirectory)) -ne "transactions") {
        throw "Refusing to remove an unexpected transaction path: $transactionDirectory"
    }
    foreach ($path in @($transactionDirectory, $uninstallTransactionDirectory)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

if ($CleanupOnly) {
    Remove-TestState
    Write-Host "PASS Windows NSIS integration state cleanup"
    return
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
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $rollbackFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingInstallerHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $transactionSnapshotFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingTransactionSnapshotHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $transactionActivationFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingTransactionActivationHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $payloadRestoreFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingPayloadRestoreHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $registryRestoreFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingRegistryRestoreHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $journalCleanupFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingJournalCleanupHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $shortcutPersistenceFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingShortcutPersistenceHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $registryPersistenceFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingRegistryPersistenceHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $commitCleanupFailureBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $failingCommitCleanupHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $interruptedBundleOutput -ApplicationVersion "1.2.0" -InstallerHooks $abortingInstallerHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $rebootRequiredBundleOutput -ApplicationVersion "1.0.0" -InstallerHooks $rebootingInstallerHooks
    Build-FixtureBundle "currentUser" $allowedDowngradeBundleOutput "DotNet.Bundler" "1.0.0" $true
    Build-FixtureBundle "currentUser" $legacyMsiProductMigrationBundleOutput "DotNet.Bundler" "1.0.0" $false $legacyMsiProductCode ""
    Build-FixtureBundle "currentUser" $legacyMsiUpgradeMigrationBundleOutput "DotNet.Bundler" "1.0.0" $false "" $legacyMsiUpgradeCode

    # 使用当前用户证书存储区验证 MSBuild 参数映射以及 payload、插件、卸载器、安装器签名链路。
    $testCertificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=DotNet.Bundler disposable integration certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter ([DateTime]::Now.AddDays(1))
    $testCertificateThumbprint = $testCertificate.Thumbprint
    Build-FixtureBundle "currentUser" $signedBundleOutput "DotNet.Bundler" "1.0.0" $false "" "" $testCertificateThumbprint
    Build-FixtureBundle "currentUser" $noShortcutDefaultsBundleOutput "DotNet.Bundler" "1.0.0" $false "" "" "" $false $false
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $failingUninstallBundleOutput -InstallerHooks $failingUninstallHooks
    Build-FixtureBundle -InstallMode "currentUser" -OutputPath $interruptedUninstallBundleOutput -InstallerHooks $interruptedUninstallHooks
    Build-FixtureBundle `
        -InstallMode "currentUser" `
        -OutputPath $unicodeBundleOutput `
        -ProductName $unicodeProductName `
        -Identifier $unicodeIdentifier `
        -Languages $fallbackLanguage `
        -DisplayLanguageSelector $false `
        -Description $unicodeDescription `
        -ShortcutDesktop $false `
        -ShortcutStartMenu $true `
        -ShortcutArguments '--表示モード "你好 世界"' `
        -ShortcutStartMenuFolder "多语言 开始菜单"

    $installer = Join-Path $bundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $upgradeInstaller = Join-Path $upgradeBundleOutput "win-x64\nsis\$productName-1.1.0-setup.exe"
    $rollbackFailureInstaller = Join-Path $rollbackFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $transactionSnapshotFailureInstaller = Join-Path $transactionSnapshotFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $transactionActivationFailureInstaller = Join-Path $transactionActivationFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $payloadRestoreFailureInstaller = Join-Path $payloadRestoreFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $registryRestoreFailureInstaller = Join-Path $registryRestoreFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $journalCleanupFailureInstaller = Join-Path $journalCleanupFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $shortcutPersistenceFailureInstaller = Join-Path $shortcutPersistenceFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $registryPersistenceFailureInstaller = Join-Path $registryPersistenceFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $commitCleanupFailureInstaller = Join-Path $commitCleanupFailureBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $interruptedInstaller = Join-Path $interruptedBundleOutput "win-x64\nsis\$productName-1.2.0-setup.exe"
    $rebootRequiredInstaller = Join-Path $rebootRequiredBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $allowedDowngradeInstaller = Join-Path $allowedDowngradeBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $legacyMsiProductMigrationInstaller = Join-Path $legacyMsiProductMigrationBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $legacyMsiUpgradeMigrationInstaller = Join-Path $legacyMsiUpgradeMigrationBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $signedInstaller = Join-Path $signedBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $noShortcutDefaultsInstaller = Join-Path $noShortcutDefaultsBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $failingUninstallInstaller = Join-Path $failingUninstallBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $interruptedUninstallInstaller = Join-Path $interruptedUninstallBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe"
    $unicodeInstaller = Join-Path $unicodeBundleOutput "win-x64\nsis\$unicodeProductName-1.0.0-setup.exe"
    Assert-True (Test-Path -LiteralPath $installer) "Installer was not created: $installer"
    Assert-True (Test-Path -LiteralPath (Join-Path $directMsBuildOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Direct MSBuild package installer was not created."
    Assert-True (Test-Path -LiteralPath (Join-Path $perMachineBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Per-machine installer was not created."
    Assert-True (Test-Path -LiteralPath (Join-Path $bothBundleOutput "win-x64\nsis\$productName-1.0.0-setup.exe")) "Both-scope installer was not created."
    Assert-True (Test-Path -LiteralPath $upgradeInstaller) "Upgrade installer was not created."
    Assert-True (Test-Path -LiteralPath $rollbackFailureInstaller) "Rollback failure-injection installer was not created."
    Assert-True (Test-Path -LiteralPath $transactionSnapshotFailureInstaller) "Transaction-snapshot failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $transactionActivationFailureInstaller) "Transaction-activation failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $payloadRestoreFailureInstaller) "Payload-restore failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $registryRestoreFailureInstaller) "Registry-restore failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $journalCleanupFailureInstaller) "Journal-cleanup failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $shortcutPersistenceFailureInstaller) "Shortcut-persistence failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $registryPersistenceFailureInstaller) "Registry-persistence failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $commitCleanupFailureInstaller) "Commit-cleanup failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $interruptedInstaller) "Interrupted-install fixture was not created."
    Assert-True (Test-Path -LiteralPath $rebootRequiredInstaller) "Reboot-required fixture installer was not created."
    Assert-True (Test-Path -LiteralPath $allowedDowngradeInstaller) "Allowed-downgrade installer was not created."
    Assert-True (Test-Path -LiteralPath $legacyMsiProductMigrationInstaller) "ProductCode migration installer was not created."
    Assert-True (Test-Path -LiteralPath $legacyMsiUpgradeMigrationInstaller) "UpgradeCode migration installer was not created."
    Assert-True (Test-Path -LiteralPath $noShortcutDefaultsInstaller) "Shortcut-default fixture installer was not created."
    Assert-True (Test-Path -LiteralPath $failingUninstallInstaller) "Forward-uninstall failure fixture was not created."
    Assert-True (Test-Path -LiteralPath $interruptedUninstallInstaller) "Interrupted forward-uninstall fixture was not created."
    Assert-True (Test-Path -LiteralPath $unicodeInstaller) "Unicode localization fixture installer was not created."
    Invoke-WindowsExecutable $unicodeInstaller "/S /D=$unicodeInstallDirectory"
    $unicodeExecutable = Join-Path $unicodeInstallDirectory "BundlerIntegrationFixture.exe"
    $unicodeUninstaller = Join-Path $unicodeInstallDirectory "Uninstall.exe"
    Assert-True (Test-Path -LiteralPath $unicodeExecutable) "$fallbackLanguage-only installer did not fall back and install when the system UI language was not selected."
    Assert-True (Test-Path -LiteralPath $unicodeUninstaller) "Unicode install path does not contain the uninstaller."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $unicodeRegistryPath -Name "DisplayName") -eq $unicodeProductName) "Unicode product name was not preserved in uninstall metadata."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $unicodeRegistryPath -Name "Comments") -eq $unicodeDescription) "Unicode description was not preserved in uninstall metadata."
    Assert-True (Test-Path -LiteralPath $unicodeStartMenuShortcut) "Unicode Start Menu shortcut was not created."
    $unicodeShortcut = Get-ShortcutInfo $unicodeStartMenuShortcut
    Assert-True ($unicodeShortcut.TargetPath -eq $unicodeExecutable) "Unicode shortcut target is incorrect."
    Assert-True ($unicodeShortcut.Arguments -eq '--表示モード "你好 世界"') "Unicode shortcut arguments were not preserved."
    Invoke-WindowsExecutable $unicodeUninstaller "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $unicodeInstallDirectory) } "Unicode localization fixture cleanup did not finish."
    Assert-True (-not (Test-Path -LiteralPath $unicodeRegistryPath)) "Unicode uninstall metadata survived uninstall."
    Assert-True (-not (Test-Path -LiteralPath $unicodeStartMenuShortcut)) "Unicode Start Menu shortcut survived uninstall."
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $signedInstaller).SignerCertificate.Thumbprint -eq $testCertificateThumbprint) "The final installer does not contain the expected Authenticode certificate."
    Invoke-WindowsExecutable $signedInstaller "/S /D=$installDirectory"
    $signedPayload = Join-Path $installDirectory "BundlerIntegrationFixture.exe"
    $signedUninstaller = Join-Path $installDirectory "Uninstall.exe"
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $signedPayload).SignerCertificate.Thumbprint -eq $testCertificateThumbprint) "The installed main executable does not contain the expected Authenticode certificate."
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $signedUninstaller).SignerCertificate.Thumbprint -eq $testCertificateThumbprint) "The installed uninstaller does not contain the expected Authenticode certificate."
    Invoke-WindowsExecutable $signedUninstaller "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Signed installer test cleanup did not finish."

    # post-uninstall Hook 失败时，已开始的删除不伪装成可回滚；保留卸载 journal
    # 和恢复卸载器。下次安装器必须先完成旧卸载，再安装新载荷。
    Invoke-WindowsExecutable $failingUninstallInstaller "/S /D=$installDirectory"
    $failingUninstaller = Join-Path $installDirectory "Uninstall.exe"
    $failingUninstall = Start-DirectUninstaller $failingUninstaller $installDirectory "/S /DELETEAPPDATA" -Wait
    Assert-True ($failingUninstall.ExitCode -eq 2) "Injected post-uninstall failure did not return exit code 2."
    Assert-True (Test-Path -LiteralPath $uninstallTransactionDirectory) "Failed uninstall did not preserve its active forward journal."
    Assert-True (Test-Path -LiteralPath (Join-Path $uninstallTransactionDirectory "recovery-uninstaller.exe")) "Failed uninstall did not preserve its recovery uninstaller."
    Assert-True (Test-Path -LiteralPath $registryPath) "Failed uninstall removed the protected recovery anchor too early."
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe")) "Installer did not continue after completing a failed uninstall."
    Assert-True (-not (Test-Path -LiteralPath $uninstallTransactionDirectory)) "Installer recovery did not clean the failed-uninstall journal."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Failed-uninstall recovery cleanup did not finish."

    # 在 post-uninstall 中断整个进程树，模拟无法进入 un.onUninstFailed 的崩溃。
    # 下次安装启动应执行 journal 副本，幂等完成旧删除后再继续安装。
    Invoke-WindowsExecutable $interruptedUninstallInstaller "/S /D=$installDirectory"
    $interruptedUninstaller = Join-Path $installDirectory "Uninstall.exe"
    $interruptedUninstall = Start-DirectUninstaller $interruptedUninstaller $installDirectory "/S /DELETEAPPDATA"
    Wait-For { Test-Path -LiteralPath $interruptedUninstallHookMarker } "Interrupted-uninstall fixture did not reach its post-uninstall hook."
    $taskkill = Start-Process -FilePath "$env:WINDIR\System32\taskkill.exe" -ArgumentList "/PID $($interruptedUninstall.Id) /T /F" -Wait -PassThru -WindowStyle Hidden
    Assert-True ($taskkill.ExitCode -eq 0) "Could not terminate the interrupted-uninstall process tree."
    $interruptedUninstall.WaitForExit()
    Assert-True ($interruptedUninstall.ExitCode -ne 0) "Interrupted-uninstall fixture unexpectedly returned success."
    Assert-True (Test-Path -LiteralPath $uninstallTransactionDirectory) "Interrupted uninstall did not preserve its active forward journal."
    Assert-True (Test-Path -LiteralPath $registryPath) "Interrupted uninstall removed the protected recovery anchor too early."
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe")) "Installer did not continue after completing an interrupted uninstall."
    Assert-True (-not (Test-Path -LiteralPath $uninstallTransactionDirectory)) "Installer recovery did not clean the interrupted-uninstall journal."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Interrupted-uninstall recovery cleanup did not finish."

    # SetRebootFlag 模拟一个已经成功排入系统队列的外部操作。安装事务必须先提交并清理
    # journal，然后返回 3010；即使传入 /R，也不能在重启前启动应用。
    $rebootRequiredProcess = Start-Process -FilePath $rebootRequiredInstaller -ArgumentList "/S /R /ARGS=--protocol-marker reboot-required /D=$installDirectory" -Wait -PassThru
    Assert-True ($rebootRequiredProcess.ExitCode -eq 3010) "Reboot-required install did not return exit code 3010."
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe")) "Reboot-required install did not commit its payload."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.0.0") "Reboot-required install did not commit its registry state."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Reboot-required install left an active transaction journal."
    Assert-True (-not (Test-Path -LiteralPath $commandLineMarker)) "Reboot-required install launched the application before reboot."
    Invoke-WindowsExecutable (Join-Path $installDirectory "Uninstall.exe") "/S /DELETEAPPDATA"
    Wait-For { -not (Test-Path -LiteralPath $installDirectory) } "Reboot-required fixture cleanup did not finish."

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

    Copy-Item -LiteralPath $installDirectory -Destination $externalFixtureDirectory -Recurse
    $externalFixtureExecutable = Join-Path $externalFixtureDirectory "BundlerIntegrationFixture.exe"
    $script:externalFixtureProcess = Start-Process -FilePath $externalFixtureExecutable -ArgumentList "--wait" -PassThru
    $script:fixtureProcess = Start-Process -FilePath $installedExecutable -ArgumentList "--wait" -PassThru
    Start-Sleep -Milliseconds 500
    Assert-True (-not $script:fixtureProcess.HasExited) "Fixture process did not remain running."
    Assert-True (-not $script:externalFixtureProcess.HasExited) "Same-name external fixture process did not remain running."
    Invoke-WindowsExecutable $installer "/S /D=$installDirectory"
    $script:fixtureProcess.Refresh()
    $script:externalFixtureProcess.Refresh()
    Assert-True $script:fixtureProcess.HasExited "Reinstall did not close the running application."
    Assert-True (-not $script:externalFixtureProcess.HasExited) "Reinstall closed a same-name process outside the installation directory."
    Stop-Process -Id $script:externalFixtureProcess.Id -Force
    $script:externalFixtureProcess.WaitForExit()

    # 安装快照和 journal 都必须拒绝重解析点，且不能跟随 junction 修改外部目录。
    New-Item -ItemType Directory -Path $reparseOutsideDirectory -Force | Out-Null
    $reparseSentinel = Join-Path $reparseOutsideDirectory "sentinel.txt"
    Set-Content -LiteralPath $reparseSentinel -Value "outside-owned" -Encoding UTF8
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($transactionDirectory)) -Force | Out-Null
    New-Item -ItemType Junction -Path $transactionDirectory -Target $reparseOutsideDirectory | Out-Null
    $journalReparseProcess = Start-Process -FilePath $installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($journalReparseProcess.ExitCode -eq 2) "A transaction-journal junction did not fail safely."
    Assert-True ((Get-Content -Raw -LiteralPath $reparseSentinel).Trim() -eq "outside-owned") "Transaction validation followed a journal junction."
    [IO.Directory]::Delete($transactionDirectory)

    $payloadJunction = Join-Path $installDirectory "linked-outside"
    New-Item -ItemType Junction -Path $payloadJunction -Target $reparseOutsideDirectory | Out-Null
    $payloadReparseProcess = Start-Process -FilePath $upgradeInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($payloadReparseProcess.ExitCode -eq 2) "An installed-payload junction did not fail before snapshot mutation."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.0.0") "Reparse-point rejection changed the installed version."
    Assert-True ((Get-Content -Raw -LiteralPath $reparseSentinel).Trim() -eq "outside-owned") "Install snapshot followed a payload junction."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Reparse-point snapshot failure left a transaction journal."
    [IO.Directory]::Delete($payloadJunction)
    Remove-Item -LiteralPath $reparseOutsideDirectory -Recurse -Force

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

    # 非主程序载荷可能被其他进程锁定，Restart Manager 不一定能识别或关闭其所有者。
    # 静默安装必须返回失败并保留可恢复 journal，不能跳过该文件后误报成功。
    $lockedPayload = Join-Path $installDirectory "docs\license.txt"
    [IO.File]::WriteAllText($lockedPayload, "locked-payload-sentinel")
    $lockedPayloadStream = [IO.File]::Open($lockedPayload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $lockedPayloadProcess = Start-Process -FilePath $upgradeInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
        Assert-True ($lockedPayloadProcess.ExitCode -eq 2) "A locked payload file did not return exit code 2."
        Assert-True (Test-Path -LiteralPath $transactionDirectory) "A failed locked-payload rollback did not preserve its active journal."
        Assert-True ([IO.File]::ReadAllText($lockedPayload) -eq "locked-payload-sentinel") "A locked payload file was unexpectedly replaced."
    }
    finally {
        $lockedPayloadStream.Dispose()
    }

    # 释放锁后，下一次启动必须先恢复事务；随后旧版本安装器应按正常版本策略阻止降级。
    $lockedPayloadRecovery = Start-Process -FilePath $installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($lockedPayloadRecovery.ExitCode -eq 4) "Locked-payload recovery did not continue to the normal downgrade policy."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Locked-payload recovery did not restore the installed version."
    Assert-True ([IO.File]::ReadAllText($lockedPayload) -eq "locked-payload-sentinel") "Locked-payload recovery did not restore the original file."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Locked-payload recovery did not clean up the active journal."

    # 通过仅限测试 Fixture 的宏分别在快捷方式和注册表持久化边界设置 error flag。
    # 两条路径都必须返回 2，并恢复相同的旧载荷、版本、快捷方式和 journal 状态。
    $prePersistenceFailureHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash
    $prePersistenceFailureShortcut = Get-ShortcutInfo $startMenuShortcut
    foreach ($failureFixture in @(
        [pscustomobject]@{ Name = "shortcut"; Installer = $shortcutPersistenceFailureInstaller },
        [pscustomobject]@{ Name = "registry"; Installer = $registryPersistenceFailureInstaller }
    )) {
        $persistenceFailure = Start-Process -FilePath $failureFixture.Installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
        Assert-True ($persistenceFailure.ExitCode -eq 2) "Injected $($failureFixture.Name) persistence failure did not return exit code 2."
        Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Injected $($failureFixture.Name) persistence failure did not restore the installed version."
        Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $prePersistenceFailureHash) "Injected $($failureFixture.Name) persistence failure did not restore the executable."
        Assert-True ((Get-ShortcutInfo $startMenuShortcut).TargetPath -eq $prePersistenceFailureShortcut.TargetPath) "Injected $($failureFixture.Name) persistence failure did not restore the Start Menu shortcut."
        Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "Injected $($failureFixture.Name) persistence failure recreated the removed desktop shortcut."
        Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Injected $($failureFixture.Name) persistence failure left an active journal."
    }

    # Begin 之后的快照写入失败和快照完成后的激活失败都发生在修改旧状态之前。
    # 失败必须删除未激活 journal，且不能改变旧载荷、注册表或快捷方式。
    $preActivationFailureHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash
    foreach ($failureFixture in @(
        [pscustomobject]@{ Name = "snapshot"; Installer = $transactionSnapshotFailureInstaller },
        [pscustomobject]@{ Name = "activation"; Installer = $transactionActivationFailureInstaller }
    )) {
        $preActivationFailure = Start-Process -FilePath $failureFixture.Installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
        Assert-True ($preActivationFailure.ExitCode -eq 2) "Injected transaction $($failureFixture.Name) failure did not return exit code 2."
        Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Injected transaction $($failureFixture.Name) failure changed the installed version."
        Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $preActivationFailureHash) "Injected transaction $($failureFixture.Name) failure changed the executable."
        Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Injected transaction $($failureFixture.Name) failure lost runtime-created data."
        Assert-True (Test-Path -LiteralPath $startMenuShortcut) "Injected transaction $($failureFixture.Name) failure removed the Start Menu shortcut."
        Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "Injected transaction $($failureFixture.Name) failure recreated the removed desktop shortcut."
        Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Injected transaction $($failureFixture.Name) failure left an inactive journal."
    }

    # 激活后分别在载荷恢复前、载荷恢复后且注册表恢复前、以及全部恢复后的
    # journal 清理点失败一次。每次都必须保留 active journal，下次启动可重入地完成恢复。
    $preRecoveryFailureHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash
    foreach ($failureFixture in @(
        [pscustomobject]@{ Name = "payload"; Installer = $payloadRestoreFailureInstaller; ExpectedVersionAfterFailure = "1.2.0"; PayloadRestored = $false },
        [pscustomobject]@{ Name = "registry"; Installer = $registryRestoreFailureInstaller; ExpectedVersionAfterFailure = "1.2.0"; PayloadRestored = $true },
        [pscustomobject]@{ Name = "cleanup"; Installer = $journalCleanupFailureInstaller; ExpectedVersionAfterFailure = "1.1.0"; PayloadRestored = $true }
    )) {
        $recoveryFailure = Start-Process -FilePath $failureFixture.Installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
        Assert-True ($recoveryFailure.ExitCode -eq 2) "Injected $($failureFixture.Name)-recovery failure did not return exit code 2."
        Assert-True (Test-Path -LiteralPath $transactionDirectory) "Injected $($failureFixture.Name)-recovery failure did not preserve its active journal."
        Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq $failureFixture.ExpectedVersionAfterFailure) "Injected $($failureFixture.Name)-recovery failure stopped at the wrong registry checkpoint."
        if ($failureFixture.PayloadRestored) {
            Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $preRecoveryFailureHash) "Injected $($failureFixture.Name)-recovery failure stopped before restoring the payload."
        }
        else {
            Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne $preRecoveryFailureHash) "Injected $($failureFixture.Name)-recovery failure unexpectedly restored the payload."
        }

        Invoke-WindowsExecutable $upgradeInstaller "/S /D=$installDirectory"
        Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Startup recovery after $($failureFixture.Name) failure did not restore the installed version."
        Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $preRecoveryFailureHash) "Startup recovery after $($failureFixture.Name) failure did not restore the executable."
        Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Startup recovery after $($failureFixture.Name) failure lost runtime-created data."
        Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Startup recovery after $($failureFixture.Name) failure did not clean the active journal."
    }

    # Hook 到达 post-install 后等待；测试进程从外部终止整个安装器进程树，模拟外层监督
    # 进程也无法进入 .onInstFailed 的崩溃。active journal 应保留，下一次安装启动时必须
    # 先恢复 1.1.0，再开始新的事务。
    $preInterruptedExecutableHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash
    $interruptedProcess = Start-Process -FilePath $interruptedInstaller -ArgumentList "/S /UPDATE /D=$installDirectory" -PassThru
    Wait-For { Test-Path -LiteralPath $interruptedHookMarker } "Interrupted-install fixture did not reach its post-install hook."
    $taskkill = Start-Process -FilePath "$env:WINDIR\System32\taskkill.exe" -ArgumentList "/PID $($interruptedProcess.Id) /T /F" -Wait -PassThru -WindowStyle Hidden
    Assert-True ($taskkill.ExitCode -eq 0) "Could not terminate the interrupted-install process tree."
    $interruptedProcess.WaitForExit()
    Assert-True ($interruptedProcess.ExitCode -ne 0) "Interrupted-install fixture unexpectedly returned success."
    Assert-True (Test-Path -LiteralPath $transactionDirectory) "Interrupted install did not preserve its active journal."

    # journal 只保存快照数据，不授予恢复目标。篡改快捷方式路径或注册表子键时，安装器必须
    # 在恢复任何产品状态前拒绝整个 journal，并且不得触碰清单外的 sentinel。
    [IO.File]::WriteAllText($journalTamperFile, "outside-file-owned")
    $fileSnapshot = Get-ChildItem -LiteralPath (Join-Path $transactionDirectory "files") -Directory | Select-Object -First 1
    Assert-True ($null -ne $fileSnapshot) "Interrupted journal did not contain a file snapshot."
    $pathFile = Join-Path $fileSnapshot.FullName "path.txt"
    $originalSnapshotPath = [IO.File]::ReadAllText($pathFile)
    [IO.File]::WriteAllText($pathFile, $journalTamperFile)
    $tamperedFileRecovery = Start-Process -FilePath $upgradeInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($tamperedFileRecovery.ExitCode -eq 2) "A tampered file recovery target did not fail safely."
    Assert-True ([IO.File]::ReadAllText($journalTamperFile) -eq "outside-file-owned") "Recovery modified a file outside the installer manifest."
    Assert-True (Test-Path -LiteralPath $transactionDirectory) "A rejected file target did not preserve the active journal."
    [IO.File]::WriteAllText($pathFile, $originalSnapshotPath)

    New-Item -Path $journalTamperRegistryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $journalTamperRegistryPath -Name "Sentinel" -Value "outside-registry-owned" -PropertyType String -Force | Out-Null
    $registrySnapshot = Get-ChildItem -LiteralPath (Join-Path $transactionDirectory "registry") -Filter "key-*.bin" -File | Select-Object -First 1
    Assert-True ($null -ne $registrySnapshot) "Interrupted journal did not contain a registry-key snapshot."
    $originalRegistrySnapshot = [IO.File]::ReadAllBytes($registrySnapshot.FullName)
    Set-RegistrySnapshotSubKey $registrySnapshot.FullName "Software\DotNetBundler\JournalTamperSentinel"
    $tamperedRegistryRecovery = Start-Process -FilePath $upgradeInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($tamperedRegistryRecovery.ExitCode -eq 2) "A tampered registry recovery target did not fail safely."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $journalTamperRegistryPath -Name "Sentinel") -eq "outside-registry-owned") "Recovery modified a registry key outside the installer manifest."
    Assert-True (Test-Path -LiteralPath $transactionDirectory) "A rejected registry target did not preserve the active journal."
    [IO.File]::WriteAllBytes($registrySnapshot.FullName, $originalRegistrySnapshot)

    Invoke-WindowsExecutable $upgradeInstaller "/S /D=$installDirectory"
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Startup recovery did not restore the previous installed version."
    Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $preInterruptedExecutableHash) "Startup recovery did not restore the previous executable."
    Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Startup recovery lost runtime-created program data."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Startup recovery did not clean up the active journal."

    # 在新版载荷、注册表、关联和快捷方式均已写入后让 post-install Hook 失败；
    # 安装事务必须恢复 1.1.0 的完整可观察状态并清理 journal。
    $preRollbackExecutableHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash
    $preRollbackShortcut = Get-ShortcutInfo $startMenuShortcut
    $rollbackProcess = Start-Process -FilePath $rollbackFailureInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($rollbackProcess.ExitCode -eq 2) "Injected post-install failure did not return exit code 2."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.1.0") "Rollback did not restore the installed version."
    Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $preRollbackExecutableHash) "Rollback did not restore the previous executable."
    Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Rollback lost runtime-created program data."
    Assert-True ((Get-ShortcutInfo $startMenuShortcut).TargetPath -eq $preRollbackShortcut.TargetPath) "Rollback did not restore the previous Start Menu shortcut."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Committed rollback journal was not cleaned up."

    # commit 先原子重命名 transaction 目录，再尽力删除已提交快照。Fixture 的内部标记
    # 确定性保留该目录；安装仍应成功，下一次启动只清理 `.committed`，不能回滚新版。
    $commitCleanupProcess = Start-Process -FilePath $commitCleanupFailureInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($commitCleanupProcess.ExitCode -eq 0) "Commit cleanup failure incorrectly failed the completed installation."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.2.0") "Commit cleanup failure rolled back the committed version."
    Assert-True (Test-Path -LiteralPath $committedTransactionDirectory) "Commit cleanup failure did not preserve the committed journal for later cleanup."
    Assert-True (-not (Test-Path -LiteralPath $transactionDirectory)) "Commit cleanup failure left an active transaction directory."
    Assert-True (Test-Path -LiteralPath $upgradePreservedData) "Commit cleanup failure lost runtime-created program data."

    $commitCleanupRecovery = Start-Process -FilePath $upgradeInstaller -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($commitCleanupRecovery.ExitCode -eq 4) "Committed-journal cleanup did not continue to the normal downgrade policy."
    Assert-True (-not (Test-Path -LiteralPath $committedTransactionDirectory)) "The next installer start did not clean the committed journal."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.2.0") "Committed-journal cleanup changed the installed version."

    # 默认禁止降级；静默模式必须在不修改现有安装的情况下返回失败。
    $downgradeProcess = Start-Process -FilePath $installer -ArgumentList "/S /D=$installDirectory" -Wait -PassThru
    Assert-True ($downgradeProcess.ExitCode -eq 4) "A blocked downgrade did not return exit code 4."
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryPath -Name "DisplayVersion") -eq "1.2.0") "Blocked downgrade modified the installed version."
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
    New-Item -ItemType Directory -Path $reparseOutsideDirectory -Force | Out-Null
    $reparseSentinel = Join-Path $reparseOutsideDirectory "uninstall-sentinel.txt"
    Set-Content -LiteralPath $reparseSentinel -Value "outside-owned" -Encoding UTF8
    $uninstallPayloadJunction = Join-Path $installDirectory "uninstall-linked-outside"
    New-Item -ItemType Junction -Path $uninstallPayloadJunction -Target $reparseOutsideDirectory | Out-Null
    $unsafeUninstall = Start-DirectUninstaller $uninstaller $installDirectory "/S" -Wait
    Assert-True ($unsafeUninstall.ExitCode -eq 2) "Uninstall did not reject an installed-payload junction."
    Assert-True (Test-Path -LiteralPath $installedExecutable) "Rejected uninstall modified the installed payload."
    Assert-True ((Get-Content -Raw -LiteralPath $reparseSentinel).Trim() -eq "outside-owned") "Uninstall followed an installed-payload junction."
    Assert-True (-not (Test-Path -LiteralPath $uninstallTransactionDirectory)) "Rejected uninstall left a forward journal."
    [IO.Directory]::Delete($uninstallPayloadJunction)
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
    $appDataJunction = Join-Path $roamingData "linked-outside"
    New-Item -ItemType Junction -Path $appDataJunction -Target $reparseOutsideDirectory | Out-Null
    $unsafeDeleteAppData = Start-DirectUninstaller (Join-Path $installDirectory "Uninstall.exe") $installDirectory "/S /DELETEAPPDATA" -Wait
    Assert-True ($unsafeDeleteAppData.ExitCode -eq 2) "DELETEAPPDATA did not reject an application-data junction."
    Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory "BundlerIntegrationFixture.exe")) "Rejected DELETEAPPDATA modified the installation."
    Assert-True ((Get-Content -Raw -LiteralPath $reparseSentinel).Trim() -eq "outside-owned") "DELETEAPPDATA followed an application-data junction."
    Assert-True (-not (Test-Path -LiteralPath $uninstallTransactionDirectory)) "Rejected DELETEAPPDATA left a forward journal."
    [IO.Directory]::Delete($appDataJunction)
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
    Remove-Item -LiteralPath $reparseOutsideDirectory -Recurse -Force

    Write-Host "PASS Windows NSIS install/uninstall integration"
}
finally {
    Remove-TestState
    if ($null -ne $testCertificateThumbprint) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$testCertificateThumbprint" -Force -ErrorAction SilentlyContinue
    }
}

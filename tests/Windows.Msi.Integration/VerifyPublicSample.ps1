param(
    [string]$Configuration = 'Release',
    [string]$PackageVersion
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sample = Join-Path $repository 'samples\HelloMsiApp\HelloMsiApp.csproj'
$source = Join-Path $repository 'artifacts\packages'
$root = Join-Path $env:TEMP ('Bundler-Msi-PublicSample-' + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $root 'packages'
$installer = New-Object -ComObject WindowsInstaller.Installer
. (Join-Path $repository 'tests\AssertLocalRestore.ps1')
if ([string]::IsNullOrWhiteSpace($PackageVersion)) { $PackageVersion = Get-BundlerPackageVersion -Repository $repository }

foreach ($name in @('DotNet.Bundler', 'DotNet.Bundler.MSBuild', 'DotNet.Bundler.Wix')) {
    $package = Join-Path $source "$name.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $package)) {
        throw "Build the current local packages before validating the public MSI sample: $package"
    }
}

function Read-MsiColumn($database, [string]$query) {
    $view = $database.OpenView($query)
    $values = [Collections.Generic.List[string]]::new()
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) { $values.Add($record.StringData(1)) }
    }
    finally { $null = $view.Close() }
    return $values.ToArray()
}

function Read-MsiProperties($database) {
    $view = $database.OpenView('SELECT * FROM Property')
    $properties = @{}
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) { $properties[$record.StringData(1)] = $record.StringData(2) }
    }
    finally { $null = $view.Close() }
    return $properties
}

function Read-MsiRegistry($database) {
    $view = $database.OpenView('SELECT * FROM Registry')
    $entries = [Collections.Generic.List[string]]::new()
    try {
        $null = $view.Execute()
        while ($record = $view.Fetch()) {
            $entries.Add("$($record.StringData(3))|$($record.StringData(4))|$($record.StringData(5))")
        }
    }
    finally { $null = $view.Close() }
    return $entries.ToArray()
}

$variants = @(
    @{ Name = 'english-user'; Language = 'en-US'; Scope = 'currentUser'; ProductLanguage = '1033'; AllUsers = '';
       FileName = 'Hello MSI App-1.0.0.msi' },
    @{ Name = 'chinese-user'; Language = 'zh-CN'; Scope = 'currentUser'; ProductLanguage = '2052'; AllUsers = '';
       FileName = 'Hello MSI App-1.0.0-zh-cn.msi' },
    @{ Name = 'english-machine'; Language = 'en-US'; Scope = 'perMachine'; ProductLanguage = '1033'; AllUsers = '1';
       FileName = 'Hello MSI App-1.0.0.msi' }
)
$productCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$upgradeCodes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

Push-Location $repository
try {
    foreach ($variant in $variants) {
        $output = Join-Path $root $variant.Name
        $properties = @(
            "-p:RestorePackagesPath=$cache",
            "-p:HelloMsiAppLanguage=$($variant.Language)",
            "-p:HelloMsiAppInstallScope=$($variant.Scope)",
            "-p:BundlerOutputPath=$output"
        )
        & dotnet restore $sample @properties --force -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Sample restore failed: $($variant.Name)" }
        Assert-LocalBundlerRestore -Project $sample -PackageVersion $PackageVersion -Source $source `
            -Cache $cache -RequiredPackages @('DotNet.Bundler', 'DotNet.Bundler.MSBuild', 'DotNet.Bundler.Wix')
        & dotnet publish $sample -c $Configuration --no-restore @properties -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Sample publish failed: $($variant.Name)" }

        $msi = Join-Path $output (Join-Path 'win-x64\msi' $variant.FileName)
        if (-not (Test-Path -LiteralPath $msi)) { throw "Sample MSI is missing: $msi" }
        $database = $installer.OpenDatabase($msi, 0)
        $msiProperties = Read-MsiProperties $database
        $allUsers = [string]$msiProperties['ALLUSERS']
        if ($msiProperties.ProductName -ne 'Hello MSI App' -or
            $msiProperties.ProductVersion -ne '1.0.0' -or
            $msiProperties.ProductLanguage -ne $variant.ProductLanguage -or
            $allUsers -ne $variant.AllUsers -or
            $msiProperties.ARPPRODUCTICON -ne 'ProductIcon' -or
            $msiProperties.ARPURLINFOABOUT -ne 'https://github.com/dotnetbundler') {
            throw "Sample MSI properties differ from the documented contract: $msi"
        }
        if (-not $productCodes.Add($msiProperties.ProductCode) -or
            -not $upgradeCodes.Add($msiProperties.UpgradeCode)) {
            throw "MSI language and scope variants must have separate identities: $msi"
        }

        $files = @(Read-MsiColumn $database 'SELECT FileName FROM File')
        foreach ($expected in @('demo.hellomsi', 'Readme.txt', 'open-link.cmd', 'HelloMsiApp.exe')) {
            if (@($files -match ([regex]::Escape($expected) + '$')).Count -eq 0) {
                throw "Sample MSI is missing $expected : $msi"
            }
        }
        $dialogs = @(Read-MsiColumn $database 'SELECT Dialog FROM Dialog')
        foreach ($expectedDialog in @('WelcomeDlg', 'LicenseAgreementDlg', 'InstallDirDlg',
                'VerifyReadyDlg', 'ExitDialog', 'MaintenanceWelcomeDlg', 'MaintenanceTypeDlg')) {
            if ($dialogs -notcontains $expectedDialog) {
                throw "Sample MSI is missing custom UI dialog $expectedDialog : $msi"
            }
        }
        if ($dialogs -contains 'WelcomeEulaDlg') {
            throw "Sample MSI should use the custom dialog set, not WixUI_Minimal: $msi"
        }
        if (@(Read-MsiColumn $database 'SELECT Name FROM Icon') -notcontains 'ProductIcon' -or
            @(Read-MsiColumn $database 'SELECT Name FROM Shortcut').Count -ne 3) {
            throw "Sample MSI is missing its icon or shortcuts: $msi"
        }
        $expectedPathName = if ($variant.Scope -eq 'perMachine') { '=-*PATH' } else { '=-PATH' }
        $environment = @(Read-MsiColumn $database 'SELECT Name FROM Environment')
        $environmentValues = @(Read-MsiColumn $database 'SELECT Value FROM Environment')
        if ($environment -notcontains $expectedPathName -or
            @($environmentValues -match 'INSTALLFOLDER').Count -ne 1 -or
            @($environmentValues -match '^\[~\];').Count -ne 1) {
            throw "Sample MSI PATH entry must append only the product directory: $msi"
        }
        $binaries = @(Read-MsiColumn $database 'SELECT Name FROM Binary')
        if ($binaries -notcontains 'WixUI_Bmp_Banner' -or $binaries -notcontains 'WixUI_Bmp_Dialog') {
            throw "Sample MSI is missing its brand bitmaps: $msi"
        }
        if ([string]$msiProperties['WIXUI_INSTALLDIR'] -ne 'INSTALLFOLDER' -or
            [string]$msiProperties['ARPNOMODIFY'] -ne '1' -or
            -not [string]$msiProperties['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'].Contains('[ProductName]')) {
            throw "Sample MSI is missing directory-selection or launch-checkbox properties: $msi"
        }
        $registry = @(Read-MsiRegistry $database)
        if (@($registry -match 'Classes\\\.hellomsi\\OpenWithProgids').Count -eq 0 -or
            @($registry -match 'Capabilities\\MIMEAssociations\|application/x-hellomsi').Count -eq 0 -or
            @($registry -match 'Capabilities\\UrlAssociations\|hello-msi').Count -eq 0) {
            throw "Sample MSI is missing candidate file/URL registrations: $msi"
        }
        $hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
        Write-Host "PASS $($variant.Name): $msi SHA-256=$hash"
    }
}
finally { Pop-Location }

Write-Host "PASS public MSI sample; artifacts retained at $root. No MSI was installed."

function Get-BundlerPackageVersion {
    param([Parameter(Mandatory)][string]$Repository)

    $path = Join-Path $Repository 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Shared local package configuration is missing: $path"
    }
    [xml]$props = Get-Content -LiteralPath $path -Raw
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/BundlerPackageVersion')
    $version = if ($null -ne $versionNode) { [string]$versionNode.InnerText } else { '' }
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "BundlerPackageVersion is missing from $path"
    }
    return $version.Trim()
}

function Assert-LocalBundlerRestore {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Cache,
        [Parameter(Mandatory)][string[]]$RequiredPackages
    )

    $assetsPath = Join-Path (Split-Path -Parent $Project) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Restore assets are missing: $assetsPath" }
    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $expectedSource = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
    $expectedCache = [IO.Path]::GetFullPath($Cache).TrimEnd('\', '/')
    $configuredSources = @($assets.project.restore.sources.PSObject.Properties.Name)
    # 开发规则要求 RestoreSources 保留公共 NuGet 源作 runtime pack 回退；
    # 这里只保证不引入其他远程源，包的真实来源由下方 .nupkg.metadata 核验。
    $unexpectedSources = @($configuredSources | Where-Object {
        $_ -match '^[a-zA-Z][a-zA-Z0-9+.-]*://' -and $_ -ne 'https://api.nuget.org/v3/index.json'
    })
    if ($unexpectedSources.Count -gt 0) {
        throw "Restore unexpectedly used a network source: $($unexpectedSources -join ', ')"
    }
    $sources = @($configuredSources |
        Where-Object { $_ -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://' } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') })
    if (-not ($sources | Where-Object { $_ -eq $expectedSource })) {
        throw "Restore did not use the local package source: $expectedSource"
    }
    $folders = @($assets.packageFolders.PSObject.Properties.Name |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') })
    if ($folders -notcontains $expectedCache) {
        throw "Restore did not use the isolated package cache: $expectedCache"
    }
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    foreach ($name in $RequiredPackages) {
        if ($libraries -notcontains "$name/$PackageVersion") {
            throw "Restored package is missing or has the wrong version: $name/$PackageVersion"
        }
        $packageDir = Join-Path $expectedCache ("{0}\{1}" -f $name.ToLowerInvariant(), $PackageVersion)
        $packagePath = Join-Path $packageDir ("{0}.{1}.nupkg" -f $name.ToLowerInvariant(), $PackageVersion)
        if (-not (Test-Path -LiteralPath $packagePath)) {
            throw "Restored package is missing from the isolated cache: $packagePath"
        }
        $metadataPath = Join-Path $packageDir '.nupkg.metadata'
        if (-not (Test-Path -LiteralPath $metadataPath)) {
            throw "Restored package is missing provenance metadata: $metadataPath"
        }
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $packageOrigin = $metadata.source
        if ([string]::IsNullOrWhiteSpace($packageOrigin) -or
            $packageOrigin -match '^[a-zA-Z][a-zA-Z0-9+.-]*://' -or
            [IO.Path]::GetFullPath($packageOrigin).TrimEnd('\', '/') -ne $expectedSource) {
            throw "Restored package did not come from the local source: $name/$PackageVersion (source: $packageOrigin)"
        }
    }
    $wrongVersions = @($libraries | Where-Object {
        $_ -like 'DotNet.Bundler/*' -or $_ -like 'DotNet.Bundler.*/*'
    } | Where-Object { $_ -notlike "*/$PackageVersion" })
    if ($wrongVersions.Count -gt 0) {
        throw "Restore selected unexpected Bundler versions: $($wrongVersions -join ', ')"
    }
    Write-Host "PASS: local package source and isolated cache contain Bundler $PackageVersion for $Project"
}

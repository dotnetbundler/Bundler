param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory "..\.."))
$project = Join-Path $projectDirectory "Bundler.Nsis.Plugin.csproj"
$publishedPlugin = Join-Path $projectDirectory "bin\$Configuration\net10.0\win-x86\publish\DotNetBundlerNsis.dll"
$embeddedPlugin = Join-Path $repositoryRoot "third_party\nsis\plugins\x86-unicode\DotNetBundlerNsis.dll"

# The plug-in is built separately so ordinary Bundler builds retain their .NET 8 SDK baseline.
dotnet publish $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Native AOT plug-in publish failed with exit code $LASTEXITCODE." }

# Only the native DLL is shipped; PDB and intermediate Native AOT files stay under bin/obj.
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($embeddedPlugin)) -Force | Out-Null
Copy-Item -LiteralPath $publishedPlugin -Destination $embeddedPlugin -Force
Get-FileHash -LiteralPath $embeddedPlugin -Algorithm SHA256

param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory "..\.."))
$project = Join-Path $projectDirectory "Bundler.Nsis.Plugin.csproj"
$publishedPlugin = Join-Path $projectDirectory "bin\$Configuration\net10.0\win-x86\publish\DotNetBundlerNsis.dll"
$embeddedPlugin = Join-Path $repositoryRoot "third_party\nsis\plugins\x86-unicode\DotNetBundlerNsis.dll"

# 插件单独构建，使 Bundler 的常规构建仍以 .NET 8 SDK 为基线。
dotnet publish $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Native AOT plug-in publish failed with exit code $LASTEXITCODE." }

# 只发布原生 DLL；PDB 和 Native AOT 中间文件保留在 bin/obj 下。
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($embeddedPlugin)) -Force | Out-Null
Copy-Item -LiteralPath $publishedPlugin -Destination $embeddedPlugin -Force
Get-FileHash -LiteralPath $embeddedPlugin -Algorithm SHA256

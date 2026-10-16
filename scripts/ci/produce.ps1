# fixture 产件 + sha256 清单（Windows）：pwsh -File produce.ps1 [-OutDir <dir>]
# verify 流水线联合交换的生产侧：产物+SHA256SUMS 打成上传 artifact，
# 异机消费腿按清单验同一件再真装真跑（消费机不见构建树）。
param([string]$OutDir = "")
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path "$PSScriptRoot/../..")
$ROOT = (Get-Location).Path
if (-not $OutDir) { $OutDir = "$ROOT/artifacts/ci/produce" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OUT = (Resolve-Path $OutDir).Path
$FIX = "$ROOT/tests/Fixtures"
$RID = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }

# fixture 经 Bundler.ProjectReference.targets 走源内 buildTransitive——
# UsingTask 需任务程序集先在 bin/Release，否则报 MSB4062。
dotnet build "$ROOT/src/Bundler.MSBuild/Bundler.MSBuild.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "task build rc=$LASTEXITCODE" }

dotnet publish "$FIX/Nsis/Fixture/BundlerNsisIntegrationFixture.csproj" -c Release -r $RID `
    -p:BundlerIntegrationOutput="$OUT/nsis" --nologo
if ($LASTEXITCODE -ne 0) { throw "nsis produce rc=$LASTEXITCODE" }

# MSI 产包走 MsiPackage fixture（消费本地 nupkg——先打仓根 src 包；
# 包落 $OUT 外，不进交换清单）。
$PKGDIR = "$ROOT/artifacts/ci/packages"
New-Item -ItemType Directory -Path $PKGDIR -Force | Out-Null
$srcProjects = Get-ChildItem "$ROOT/src/*/*.csproj"
if ($srcProjects.Count -eq 0) { throw "src/*/*.csproj 零命中——pack 落空" }
foreach ($p in $srcProjects) {
    dotnet pack $p.FullName -c Release -o $PKGDIR --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack $($p.Name) rc=$LASTEXITCODE" }
}
dotnet publish "$FIX/MsiPackage/Fixture/BundlerMsiSmoke.csproj" -c Release -r $RID `
    -p:BundlerOutputPath="$OUT/msi" -p:BundlerPackageSource="$PKGDIR" --nologo
if ($LASTEXITCODE -ne 0) { throw "msi produce rc=$LASTEXITCODE" }

dotnet publish "$FIX/Archive/BundlerArchiveIntegrationFixture.csproj" -c Release -r $RID `
    -p:BundlerIntegrationOutput="$OUT/archive" --nologo
if ($LASTEXITCODE -ne 0) { throw "archive produce rc=$LASTEXITCODE" }

$sums = Get-ChildItem $OUT -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($OUT.Length).TrimStart('\','/') -replace '\\','/'
        "{0}  ./{1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $rel
    }
$sums | Sort-Object | Set-Content "$OUT/SHA256SUMS" -Encoding ascii
Write-Host "=== 产件清单 ==="; Get-Content "$OUT/SHA256SUMS" | Select-Object -First 40

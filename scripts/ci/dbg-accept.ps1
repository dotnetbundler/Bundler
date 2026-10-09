# dbg-accept 驱动（一次性验收）：只跑本轮新增腿——windows-11-arm 的 arm64-matrix 升/修腿与 pinned-items 任务栏 pin。
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path "$PSScriptRoot/../..")
$ROOT = (Get-Location).Path
$OUT = "$ROOT/artifacts/ci"; New-Item -ItemType Directory -Path $OUT -Force | Out-Null
$failedLegs = @()

"=== 构建"
dotnet build Bundler.slnx -c Release --nologo
dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo

"=== fixture 产包（win-arm64）"
$FIX = "tests/Bundler.IntegrationTests/Fixtures"
dotnet publish "$FIX/Nsis/Fixture/BundlerNsisIntegrationFixture.csproj" -c Release -r win-arm64 -p:BundlerIntegrationOutput="$OUT/nsis" --nologo
dotnet publish "tests/Bundler.LocalPackagesTests/Fixtures/Msi/Fixture/BundlerMsiSmoke.csproj" -c Release -r win-arm64 -p:BundlerIntegrationOutput="$OUT/msi" --nologo
$nsis = Get-ChildItem "$OUT/nsis" -Filter *.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$msi  = Get-ChildItem "$OUT/msi"  -Filter *.msi -Recurse | Select-Object -First 1 -ExpandProperty FullName

pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '$ROOT/tests/Special/win/arm64-matrix.ps1' -InstallerPaths @('$nsis','$msi')"
if ($LASTEXITCODE -eq 0) { Write-Host "[LEG-PASS] arm64-matrix(升/修)" } else { Write-Host "[LEG-FAIL] arm64-matrix"; $failedLegs += "arm64-matrix" }

pwsh -NoProfile -ExecutionPolicy Bypass -File "$ROOT/tests/Special/win/pinned-items.ps1" -InstallerPath $nsis -InstallDirName "Bundler Integration Fixture" -TaskbarPin
if ($LASTEXITCODE -eq 0) { Write-Host "[LEG-PASS] pinned-items(taskbar)" } else { Write-Host "[LEG-FAIL] pinned-items"; $failedLegs += "pinned-items" }

if ($failedLegs.Count -gt 0) { Write-Error ("失败腿: " + ($failedLegs -join ", ")) }
"=== 全绿"

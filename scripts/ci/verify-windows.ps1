# 发布前验证驱动（Windows：x64 + arm64 runner）。
# 段序：构建 → 全量 dotnet test → fixture 产包 → Special 自动腿。
# 残留纪律：产包与证据落 artifacts/ci/（job 结束由 runner 回收）；disposable-vm-faults 排最后
# （它会留 PendingFileRenameOperations 到下轮重启——CI runner 用完即弃，接受）。
#requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path "$PSScriptRoot/../..")
$ROOT = (Get-Location).Path
$OUT = "$ROOT/artifacts/ci"
New-Item -ItemType Directory -Path $OUT -Force | Out-Null
$IS_ARM = $env:PROCESSOR_ARCHITECTURE -eq "ARM64"
$RID = if ($IS_ARM) { "win-arm64" } else { "win-x64" }
function Say($m) { Write-Host "=== $m" }

Say "1/4 构建+打包"
dotnet build Bundler.slnx -c Release --nologo
dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo

Say "2/4 全量测试（四工程）"
# MTP+xUnit3 下 `dotnet test` 发现不到用例——直跑测试程序集（tests/README.md 同款）
foreach ($P in "Bundler.Tests", "Bundler.ApiTests", "Bundler.IntegrationTests", "Bundler.LocalPackagesTests") {
    dotnet "tests/$P/bin/Release/net10.0/$P.dll"
    if ($LASTEXITCODE -ne 0) { throw "$P 失败 rc=$LASTEXITCODE" }
}

Say "3/4 fixture 产包（喂 Special 腿）"
$FIX = "tests/Bundler.IntegrationTests/Fixtures"
dotnet publish "$FIX/Nsis/Fixture/BundlerNsisIntegrationFixture.csproj" -c Release -r $RID `
    -p:BundlerIntegrationOutput="$OUT/nsis" --nologo
# MSI 产包走 LocalPackages fixture（仓内唯一 MSI 生产工程，消费仓根刚打的 nupkg）
dotnet publish "tests/Bundler.LocalPackagesTests/Fixtures/Msi/Fixture/BundlerMsiSmoke.csproj" -c Release -r $RID `
    -p:BundlerOutputPath="$OUT/msi" --nologo

$nsis = Get-ChildItem "$OUT/nsis" -Filter *.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$msi  = Get-ChildItem "$OUT/msi"  -Filter *.msi -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $nsis) { throw "nsis 产包缺位" }
if (-not $msi)  { throw "msi 产包缺位" }

Say "4/4 Special 自动腿（凭证/公网/人工/真重启类仍排除）"
$SP = "tests/Special"
$failedLegs = @()
function Run-Leg($name, $script, $argList) {
    pwsh -NoProfile -ExecutionPolicy Bypass -File $script @argList
    if ($LASTEXITCODE -eq 0) { Write-Host "[LEG-PASS] $name" }
    else { Write-Host "[LEG-FAIL] $name"; $script:failedLegs += $name }
}

Run-Leg "pinned-items"        "$SP/win/pinned-items.ps1"        @("-InstallerPath", $nsis)
Run-Leg "mountvol-full-volume" "$SP/win/mountvol-full-volume.ps1" @()
if ($IS_ARM) {
    Run-Leg "arm64-matrix"    "$SP/win/arm64-matrix.ps1"        @("-InstallerPaths", @($nsis, $msi))
}
# 留 PendingFileRename 的故障注入腿排最后
Run-Leg "disposable-vm-faults" "$SP/win/disposable-vm-faults.ps1" @("-InstallerPath", $nsis, "-ConfirmDisposableMachine")

if ($failedLegs.Count -gt 0) {
    Write-Error ("失败腿: " + ($failedLegs -join ", "))
}
Say "全绿"

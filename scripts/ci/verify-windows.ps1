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
# IntegrationTests 逐类跑并落进度文件：超时被砍时 artifacts 能指认在途类；-longRunning 打印挂起用例名
foreach ($P in "Bundler.Tests", "Bundler.ApiTests", "Bundler.IntegrationTests", "Bundler.LocalPackagesTests") {
    $dll = "tests/$P/bin/Release/net10.0/$P.dll"
    if ($P -eq "Bundler.IntegrationTests") {
        $classes = dotnet $dll -list classes | Where-Object { $_ -match '^\w[\w.]*$' }
        if (-not $classes -or $classes.Count -eq 0) {
            throw "-list classes 无输出：集成测试可能整段蒸发"
        }
        foreach ($c in $classes) {
            $c = $c.Trim()
            "[$([datetime]::UtcNow.ToString('o'))] BEGIN $c" | Out-File "$OUT/test-progress.log" -Append
            Write-Host "[CLASS-BEGIN] $c"
            dotnet $dll -class $c -longRunning 300
            $rc = $LASTEXITCODE
            "[$([datetime]::UtcNow.ToString('o'))] END   $c rc=$rc" | Out-File "$OUT/test-progress.log" -Append
            Write-Host "[CLASS-END] $c rc=$rc"
            if ($rc -ne 0) { throw "$P/$c 失败 rc=$rc" }
        }
    } else {
        dotnet $dll -longRunning 300
        if ($LASTEXITCODE -ne 0) { throw "$P 失败 rc=$LASTEXITCODE" }
    }
}

Say "3/4 fixture 产包（喂 Special 腿）"
$FIX = "tests/Bundler.IntegrationTests/Fixtures"
dotnet publish "$FIX/Nsis/Fixture/BundlerNsisIntegrationFixture.csproj" -c Release -r $RID `
    -p:BundlerIntegrationOutput="$OUT/nsis" --nologo
# MSI 产包走 LocalPackages fixture（仓内唯一 MSI 生产工程，消费仓根刚打的 nupkg）
dotnet publish "tests/Bundler.LocalPackagesTests/Fixtures/Msi/Fixture/BundlerMsiSmoke.csproj" -c Release -r $RID `
    -p:BundlerOutputPath="$OUT/msi" -p:BundlerPackageSource="$ROOT/artifacts/packages" --nologo

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

Run-Leg "pinned-items"        "$SP/win/pinned-items.ps1"        @("-InstallerPath", $nsis, "-InstallDirName", "Bundler Integration Fixture", "-TaskbarPin")
Run-Leg "mountvol-full-volume" "$SP/win/mountvol-full-volume.ps1" @()
if ($IS_ARM) {
    # pwsh -File 下 string[] 参数只吃首个 token（其余会位置绑定到下一参数）——改 -Command 真表达式传数组
    pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '$ROOT/tests/Special/win/arm64-matrix.ps1' -InstallerPaths @('$nsis','$msi')"
    if ($LASTEXITCODE -eq 0) { Write-Host "[LEG-PASS] arm64-matrix" }
    else { Write-Host "[LEG-FAIL] arm64-matrix"; $failedLegs += "arm64-matrix" }
}
# 留 PendingFileRename 的故障注入腿排最后
Run-Leg "disposable-vm-faults" "$SP/win/disposable-vm-faults.ps1" @("-InstallerPath", $nsis, "-ConfirmDisposableMachine")

if ($failedLegs.Count -gt 0) {
    Write-Error ("失败腿: " + ($failedLegs -join ", "))
}
Say "全绿"

# 定向测试腿驱动（Windows）：pwsh -File run-suite.ps1 -Suite <unit|api|integration|localpackages|all>
# 跑法与 verify-windows.ps1 §2 同款：直跑测试 dll（MTP+xUnit3，`dotnet test` 发现不到用例），
# IntegrationTests 逐类跑并落 artifacts/ci/test-progress.log 指认在途类。
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("unit", "api", "integration", "localpackages", "all")]
    [string]$Suite
)
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path "$PSScriptRoot/../..")
$ROOT = (Get-Location).Path
$OUT = "$ROOT/artifacts/ci"
New-Item -ItemType Directory -Path $OUT -Force | Out-Null

$projects = switch ($Suite) {
    "unit"          { @("Bundler.Tests") }
    "api"           { @("Bundler.ApiTests") }
    "integration"   { @("Bundler.IntegrationTests") }
    "localpackages" { @("Bundler.LocalPackagesTests") }
    "all"           { @("Bundler.Tests", "Bundler.ApiTests", "Bundler.IntegrationTests", "Bundler.LocalPackagesTests") }
}

# LocalPackagesTests 消费本地 nupkg——先打仓根包再跑（tests/README 前置条件）。
if ($projects -contains "Bundler.LocalPackagesTests") {
    dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack rc=$LASTEXITCODE" }
}

foreach ($P in $projects) {
    $dll = "tests/$P/bin/Release/net10.0/$P.dll"
    if ($P -eq "Bundler.IntegrationTests") {
        $listOut = dotnet $dll -list classes
        if ($LASTEXITCODE -ne 0) { throw "-list classes 退出码非零（$LASTEXITCODE），判失败" }
        $classes = $listOut | Where-Object { $_ -match '^\w[\w.]*$' }
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

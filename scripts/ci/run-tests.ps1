# 测试腿唯一驱动（Windows）：pwsh -File run-tests.ps1 -Suite <unit|api|integration|package|all>
# 测试程序集自宿主运行（MTP+xUnit3，`dotnet test` 发现不到用例）——直跑 dll；
# IntegrationTests 逐类跑并落 artifacts/ci/test-progress.log 指认在途类；
# -longRunning 300 打印挂起用例名。
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("unit", "api", "integration", "package", "all")]
    [string]$Suite
)
$ErrorActionPreference = "Stop"
Set-Location (Resolve-Path "$PSScriptRoot/../..")
$ROOT = (Get-Location).Path
$OUT = "$ROOT/artifacts/ci"
New-Item -ItemType Directory -Path $OUT -Force | Out-Null

$projects = switch ($Suite) {
    "unit"        { @("Unit/Bundler.UnitTests") }
    "api"         { @("Api/Bundler.ApiTests") }
    "integration" { @("Integration/Bundler.IntegrationTests") }
    "package"     { @("Package/Bundler.PackageTests") }
    "all"         { @("Unit/Bundler.UnitTests", "Api/Bundler.ApiTests", "Integration/Bundler.IntegrationTests", "Package/Bundler.PackageTests") }
}

# PackageTests 消费本地 nupkg——先打仓根 src 包再跑（tests/README 前置条件）。
# 只打 src/*/*.csproj：连测试工程一起 pack 会向运行中进程的 bin/ 写文件。
if ($projects -contains "Package/Bundler.PackageTests") {
    $srcProjects = Get-ChildItem "src/*/*.csproj"
    if ($srcProjects.Count -eq 0) { throw "src/*/*.csproj 零命中——pack 落空" }
    foreach ($p in $srcProjects) {
        dotnet pack $p.FullName -c Release -o artifacts/packages --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack $($p.Name) rc=$LASTEXITCODE" }
    }
}

foreach ($P in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($P)
    $dll = "tests/$P/bin/Release/net10.0/$name.dll"
    if ($P -eq "Integration/Bundler.IntegrationTests") {
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
            if ($rc -ne 0) { throw "$name/$c 失败 rc=$rc" }
        }
    } else {
        dotnet $dll -longRunning 300
        if ($LASTEXITCODE -ne 0) { throw "$name 失败 rc=$LASTEXITCODE" }
    }
}

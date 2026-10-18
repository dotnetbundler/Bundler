# 测试腿唯一驱动（Windows）：pwsh -File run-tests.ps1 -Suite <unit|api|integration|package|all>
# 测试程序集自宿主（MTP+xUnit3）——直跑 dll 而非 dotnet test，以逐类调度、
# 落 artifacts/ci/test-progress.log 指认在途类；-longRunning 300 打印挂起用例名。
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

# 挂死插桩：BUNDLER_TOOL_TRACE 指文件时，后台 powershell Get-Content -Wait 把 trace
# 流进本 job 日志（-NoNewWindow 继承控制台，job 被 timeout 硬杀也能看到最后在途 spawn）。
$tailProc = $null
if ($env:BUNDLER_TOOL_TRACE) {
    New-Item -ItemType File -Force -Path $env:BUNDLER_TOOL_TRACE | Out-Null
    $tailProc = Start-Process -PassThru -NoNewWindow -FilePath "powershell" `
        -ArgumentList "-NoProfile","-Command","Get-Content -Wait -Tail 0 -Path `"$env:BUNDLER_TOOL_TRACE`""
}

# 进程快照采样：每 60s 一份进程清单落 process-snapshots.log 并 tail 进 job 日志——
# 挂死复现时 trace 指认最后在途 spawn，快照指认进程树（挂起、孤儿子树），常开不挂 env。
$snapPath = "$OUT/process-snapshots.log"
New-Item -ItemType File -Force -Path $snapPath | Out-Null
$snapJob = Start-Job -ScriptBlock {
    param($path)
    while ($true) {
        "===== $([datetime]::UtcNow.ToString('o')) =====" | Out-File -Append $path
        Get-CimInstance Win32_Process |
            Select-Object ProcessId, ParentProcessId, Name, ExecutablePath |
            Format-Table -AutoSize | Out-String | Out-File -Append $path
        Start-Sleep -Seconds 60
    }
} -ArgumentList $snapPath
$snapTail = Start-Process -PassThru -NoNewWindow -FilePath "powershell" `
    -ArgumentList "-NoProfile","-Command","Get-Content -Wait -Tail 0 -Path `"$snapPath`""
try {
foreach ($P in $projects) {
    $layer, $name = $P -split '/', 2
    $dll = "tests/$layer/bin/Release/net10.0/$name.dll"
    if (-not (Test-Path $dll)) { throw "测试程序集不存在：$dll（先 dotnet build）" }
    if ($name -eq "Bundler.IntegrationTests") {
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
            if ($rc -ne 0) { throw "$c 失败 rc=$rc" }
        }
    } else {
        dotnet $dll -longRunning 300
        if ($LASTEXITCODE -ne 0) { throw "$name 失败 rc=$LASTEXITCODE" }
    }
}
} finally {
    if ($tailProc) { Stop-Process -Id $tailProc.Id -Force -ErrorAction SilentlyContinue }
    if ($snapTail) { Stop-Process -Id $snapTail.Id -Force -ErrorAction SilentlyContinue }
    if ($snapJob) { Stop-Job $snapJob; Remove-Job $snapJob -Force -ErrorAction SilentlyContinue }
}

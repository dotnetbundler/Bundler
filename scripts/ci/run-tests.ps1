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

# 挂死插桩：trace 与快照只落文件不刷 job 日志（日常日志保持干净）——由 test.yml 上传
# 工件、需要时下载取证。残余盲区：job 被 timeout 硬杀时上传步不执行；#130 有界等待
# 已把挂死收敛为步内失败，该场景仅剩非 ToolRunner 面的卡死。
if ($env:BUNDLER_TOOL_TRACE) {
    New-Item -ItemType File -Force -Path $env:BUNDLER_TOOL_TRACE | Out-Null
}

# 进程快照采样：每 60s 一份过滤后进程清单落 process-snapshots.log。清单按名称/命令行
# 白名单过滤（测试/工具/打包工具/宿主壳），全量表数千行/份的噪音裁到数十行；常开不挂 env。
$snapPath = "$OUT/process-snapshots.log"
New-Item -ItemType File -Force -Path $snapPath | Out-Null
$snapJob = Start-Job -ScriptBlock {
    param($path)
    $keep = 'dotnet|Bundler|hdiutil|codesign|security|pkgbuild|productbuild|productsign|pkgutil|makensis|notarytool|ditto|xar|lipo|otool|spctl|candle|light\.exe|heat|msiexec|sudo|docker|conhost|taskkill|MsMpEng|smartscreen|TrustedInstaller|TiWorker|powershell|pwsh|cmd\.exe|tail'
    while ($true) {
        "===== $([datetime]::UtcNow.ToString('o')) =====" | Out-File -Append $path
        # Job runspace 无控制台、缓冲宽 120——Out-String -Width 防 ExecutablePath/CommandLine 截断。
        Get-CimInstance Win32_Process |
            Where-Object { $_.Name -match $keep -or $_.CommandLine -match $keep } |
            Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine |
            Format-Table -AutoSize | Out-String -Width 4096 | Out-File -Append $path
        Start-Sleep -Seconds 60
    }
} -ArgumentList $snapPath
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
    if ($snapJob) { Remove-Job $snapJob -Force -ErrorAction SilentlyContinue }
}

# 联合交换消费腿（Windows）：pwsh -File consume-windows.ps1 -Dir <produce-artifact-dir>
# 在未见构建树的机器上：SHA256SUMS 验同一件 → NSIS/MSI/zip 真装真跑真卸 → 清场。
#requires -RunAsAdministrator
param([Parameter(Mandatory = $true)][string]$Dir)
$ErrorActionPreference = "Stop"
$DIR = (Resolve-Path $Dir).Path
$SUMS = "$DIR/SHA256SUMS"
if (-not (Test-Path $SUMS)) { throw "缺 SHA256SUMS：$DIR" }

# 清单校验（格式 "sha256  ./relpath"，与 produce.ps1 生成约定一致）
$bad = @()
Get-Content $SUMS | ForEach-Object {
    if ($_ -match '^([0-9a-f]{64})  \./(.+)$') {
        $actual = (Get-FileHash "$DIR/$($Matches[2])" -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $Matches[1]) { $bad += $Matches[2] }
    }
}
if ($bad.Count -gt 0) { throw "清单校验失败: $($bad -join ', ')" }

# 应收件存在性对账：本宿主 produce 腿应收 nsis/msi/archive 三件——缺件即判败。
$missing = @("nsis", "msi", "archive") | Where-Object { -not (Test-Path "$DIR/$_") }
if ($missing.Count -gt 0) { throw "产件目录缺失: $($missing -join ', ')" }

$WORK = "$env:RUNNER_TEMP/bundler-consume"
New-Item -ItemType Directory -Force $WORK | Out-Null
$failedLegs = @()
function Leg($name, [scriptblock]$body) {
    try { & $body; Write-Host "[LEG-PASS] $name" }
    catch { Write-Host "[LEG-FAIL] $name :: $_"; $script:failedLegs += $name }
}

# NSIS：/S /D=<dir> 真装 → 主程序真跑 → Uninstall.exe /S 真卸
$nsis = Get-ChildItem "$DIR/nsis" -Filter *.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($nsis) {
    Leg "nsis" {
        $inst = "$WORK/nsis-inst"
        $p = Start-Process $nsis.FullName -ArgumentList "/S", "/D=$inst" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "installer rc=$($p.ExitCode)" }
        $exe = "$inst/BundlerIntegrationFixture.exe"
        if (-not (Test-Path $exe)) { throw "装后主程序缺位：$exe" }
        $out = & $exe ci-marker
        if ($out -notmatch "DotNet.Bundler integration fixture") { throw "marker 输出异常：$out" }
        # NSIS 卸载器原地直启是 launcher 语义不透传真实 rc——复制出去 + _?= 拿真退出码
        # （与 tests/Integration 的 DirectUninstall 同手法；rc=3010 需重启同样算成功）。
        $uninst = "$inst/Uninstall.exe"
        $copy = "$WORK/nsis-uninstall.exe"
        Copy-Item $uninst $copy
        $p = Start-Process $copy -ArgumentList "/S", "/DELETEAPPDATA", "_?=$inst" -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "uninstaller rc=$($p.ExitCode)" }
        if (Test-Path $exe) { throw "卸后主程序仍在：$exe" }
    }
}

# MSI：msiexec 静默真装 → 主程序真跑 → /x 真卸
$msi = Get-ChildItem "$DIR/msi" -Filter *.msi -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msi) {
    Leg "msi" {
        $log = "$WORK/msi-install.log"
        $p = Start-Process msiexec.exe -ArgumentList "/i", "`"$($msi.FullName)`"", "/qn", "/norestart", "/L*v", "`"$log`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec /i rc=$($p.ExitCode) log=$log" }
        $exe = Get-ChildItem "$env:LOCALAPPDATA/Programs" -Filter "*.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*Bundler*Smoke*" -or $_.FullName -like "*bundler*msi*" } |
            Select-Object -First 1
        if (-not $exe) { $exe = Get-ChildItem "${env:ProgramFiles}", "${env:ProgramFiles(x86)}" -Filter "BundlerMsiSmoke*.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $exe) { throw "装后找不到主程序" }
        $out = & $exe.FullName
        if ($LASTEXITCODE -ne 0) { throw "主程序 rc=$LASTEXITCODE" }
        $p = Start-Process msiexec.exe -ArgumentList "/x", "`"$($msi.FullName)`"", "/qn", "/norestart" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec /x rc=$($p.ExitCode)" }
        if (Test-Path $exe.FullName) { throw "卸后主程序仍在：$($exe.FullName)" }
    }
}

# zip 归档：解开直接跑
$zip = Get-ChildItem "$DIR/archive" -Filter *.zip -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($zip) {
    Leg "archive-zip" {
        $t = "$WORK/archive"
        Expand-Archive $zip.FullName -DestinationPath $t -Force
        $exe = Get-ChildItem $t -Filter "BundlerArchiveIntegrationFixture*.exe" -Recurse | Select-Object -First 1
        if (-not $exe) { throw "归档内无主程序" }
        $out = & $exe.FullName ci-marker
        if ($out -notmatch "BundlerArchiveIntegrationFixture:ci-marker") { throw "marker 输出异常：$out" }
    }
}

if ($failedLegs.Count -gt 0) { Write-Error ("失败腿: " + ($failedLegs -join ", ")) }
Write-Host "=== consume-windows 全绿 ==="

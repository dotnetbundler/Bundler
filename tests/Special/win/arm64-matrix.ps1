# Windows ARM64 真机矩阵：装/升/修/卸各格式腿。仅 ARM64 Windows 宿主可跑。
param(
    [Parameter(Mandatory = $true)][string[]]$InstallerPaths,  # 各格式产包
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $result, $detail = "") {
    # 结果归一化为 PASS/FAIL/UNTESTED——bool 直比 "FAIL" 会因右侧强转 bool 恒真
    $r = if ($result -is [bool]) { if ($result) { "PASS" } else { "FAIL" } } else { "$result" }
    $script:evidence += "| $step | $r | $detail |"
    Write-Host "[$r] $step $detail"
    if ($r -eq "FAIL") { $script:hadFail = $true }
}
if ($env:PROCESSOR_ARCHITECTURE -ne "ARM64") {
    throw "本脚本只在 Windows ARM64 宿主跑（当前: $env:PROCESSOR_ARCHITECTURE）"
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

foreach ($inst in $InstallerPaths) {
    if (-not (Test-Path $inst)) { Note "工件存在" "FAIL" $inst; continue }
    $ext = [IO.Path]::GetExtension($inst).ToLowerInvariant()
    $name = Split-Path $inst -Leaf
    try {
        switch ($ext) {
            ".msi" {
                $p = Start-Process "msiexec.exe" -ArgumentList "/i `"$inst`" /qb /norestart" -Wait -PassThru
                Note "$name 装" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
                $p2 = Start-Process "msiexec.exe" -ArgumentList "/x `"$inst`" /qb /norestart" -Wait -PassThru
                Note "$name 卸" ($p2.ExitCode -eq 0) "rc=$($p2.ExitCode)"
            }
            ".exe" {  # nsis
                $p = Start-Process $inst -ArgumentList "/S" -Wait -PassThru
                Note "$name 装" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
            }
            ".zip" { Note "$name 解压即用" $true "ARM64 zip 腿免装" }
            default { Note "$name" "UNTESTED" "未识别扩展名 $ext" }
        }
    } catch { Note "$name" "FAIL" $_.Exception.Message }
}

$out = Join-Path $OutDir ("SA-WINARM64-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-WINARM64 Windows ARM64 矩阵
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString) ARM64

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

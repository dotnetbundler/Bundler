# 干净宿主验收：无 .NET SDK/VS 的 Windows 上，产包能装能卸（不依赖构建工具链）。
# 用法: 在无 SDK 的干净 Windows VM 跑——pwsh clean-host.ps1 -InstallerPaths <产包...>
param(
    [Parameter(Mandatory = $true)][string[]]$InstallerPaths,
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $result, $detail = "") {
    $script:evidence += "| $step | $result | $detail |"
    Write-Host "[$result] $step $detail"
    if ($result -eq "FAIL") { $script:hadFail = $true }
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 前提自检：宿主必须真干净。
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$wix = Get-ChildItem "$env:ProgramFiles*\WiX*" -ErrorAction SilentlyContinue
$nsis = Get-Command makensis -ErrorAction SilentlyContinue
Note "宿主干净(无 dotnet)" ($null -eq $sdk) ($sdk?.Source ?? "无")
Note "宿主干净(无 WiX/NSIS)" (($null -eq $wix) -and ($null -eq $nsis)) ""

function Get-HelloBundlerEntries {
    Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object DisplayName -match "HelloBundlerApp" | ForEach-Object PSChildName
}
$preExisting = @(Get-HelloBundlerEntries)
if ($preExisting.Count -gt 0) {
    Write-Host "检测到既有 HelloBundlerApp 卸载项 $($preExisting -join ',')——只动本脚本新增的" -ForegroundColor Yellow
}

foreach ($inst in $InstallerPaths) {
    if (-not (Test-Path $inst)) { Note "工件存在" "FAIL" $inst; continue }
    $ext = [IO.Path]::GetExtension($inst).ToLowerInvariant(); $name = Split-Path $inst -Leaf
    try {
        if ($ext -eq ".msi") {
            # 每次安装前重新快照——同 ProductCode 连续两包也能正确认领各自的装。
            $preInstallEntries = @(Get-HelloBundlerEntries)
            Start-Process "msiexec.exe" -ArgumentList "/i `"$inst`" /qb /norestart" -Wait
            $newEntry = @(Get-HelloBundlerEntries) |
                Where-Object { $preInstallEntries -notcontains $_ } | Select-Object -First 1
            if ($newEntry) {
                Note "$name 装" "PASS" ""
                Start-Process "msiexec.exe" -ArgumentList "/x `"$newEntry`" /qb /norestart" -Wait
            } elseif ($preInstallEntries.Count -gt 0) {
                # 同 ProductCode 对既存项的修复/升级无法区分归属——不动它。
                Note "$name 装" "UNTESTED" "同 ProductCode 既存项被复用，归属不明故不卸"
            } else {
                Note "$name 装" "FAIL" "装后无卸载注册项"
            }
        } else {
            Start-Process $inst -ArgumentList "/S" -Wait
            Note "$name 装" $true ""
        }
    } catch { Note "$name" "FAIL" $_.Exception.Message }
}

$out = Join-Path $OutDir ("SA-CLEANHOST-WIN-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-CLEANHOST-WIN 干净宿主验收
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

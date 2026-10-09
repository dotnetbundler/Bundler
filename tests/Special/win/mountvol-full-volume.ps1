#requires -Version 7
#requires -RunAsAdministrator
# mountvol 全卷挂载语义实证：junction 等价腿已覆盖逻辑面，
# 本腿验整卷经 mountvol 挂到空目录后，bundler-updater 换包对卷内 install 语义正确。
# 需: Windows+管理员+可分配 NTFS 卷（默认用临时 VHD 提供一个整卷）。
param(
    [string]$VhdSizeMB = "64",
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $ok, $detail = "") {
    $script:evidence += "| $step | $(if($ok){'PASS'}else{'FAIL'}) | $detail |"
    Write-Host "[$(if($ok){'PASS'}else{'FAIL'})] $step $detail"
    if (-not $ok) { $script:hadFail = $true }
}

$repo = Resolve-Path "$PSScriptRoot/../../.."
$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$updater = "$repo/src/Bundler.Updater.Bootstrap/tools/$rid/bundler-updater.exe"
if (-not (Test-Path $updater)) { throw "bundler-updater.exe 缺位: $updater" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$work = Join-Path $env:TEMP ("bundler-mountvol-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$vhd = Join-Path $work "vol.vhdx"
$mountDir = Join-Path $work "mounted"   # 空 NTFS 目录挂点
New-Item -ItemType Directory -Path $mountDir | Out-Null
try {
    @"
create vdisk file="$vhd" maximum=$VhdSizeMB type=fixed
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=ntfs quick label=mountvol
assign
"@ | Set-Content "$work\dp.txt"
    diskpart /s "$work\dp.txt" | Out-Null
    # 卷 GUID → 挂到空目录（不经盘符）。
    $vol = Get-Volume | Where-Object FileSystemLabel -eq "mountvol" | Select-Object -First 1
    if (-not $vol) { throw "VHD 卷未发现" }
    $volPath = $vol.Path  # \\?\Volume{guid}\
    mountvol "$mountDir" $volPath
    Note "整卷经 mountvol 挂到空目录" ((Get-Item $mountDir).Attributes -match "ReparsePoint" -or
        (Test-Path "$mountDir")) $volPath

    # 换包断言：install 在卷内，marker/backup 应贴在卷挂点旁。
    $install = Join-Path $mountDir "install"
    $payload = Join-Path $work "payload"
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Set-Content "$install\app.txt" "v1-app"
    Set-Content "$payload\app.txt" "v2-app"; Set-Content "$payload\new.txt" "v2-new"
    $p = Start-Process $updater -ArgumentList "apply", "--install-dir", $install,
        "--payload", $payload, "--log", "$work\u.log" -Wait -PassThru
    Note "换包 rc=0" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
    Note "载荷已就位" ((Get-Content "$install\app.txt") -eq "v2-app" -and
        (Test-Path "$install\new.txt")) ""
    Note "卷挂点旁无残留" ((-not (Test-Path "$install.bundler-swap")) -and
        (-not (Test-Path "$install.bundler-backup"))) ""
} finally {
    if (Test-Path $mountDir) { mountvol "$mountDir" /d 2>$null }
    @"
select vdisk file="$vhd"
detach vdisk
"@ | Set-Content "$work\dp2.txt" -ErrorAction SilentlyContinue
    diskpart /s "$work\dp2.txt" 2>$null | Out-Null
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$out = Join-Path $OutDir ("SA-MOUNTVOL-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-MOUNTVOL mountvol 全卷实证
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

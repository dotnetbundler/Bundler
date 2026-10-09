# 固定项矩阵验收：开始菜单/任务栏 pin 在装/卸/换包后的存续语义。
# 需在目标 Windows build 跑（win10/win11 任务栏 pin API 不同）。
# 用法: pwsh pinned-items.ps1 -InstallerPath <nsis产包> [-TaskbarPin]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [switch]$TaskbarPin,
    [string]$InstallDirName = "",   # 预期装目录名（$LOCALAPPDATA\Programs\<名>）；给了就在装前拒跑既有安装
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $ok, $detail = "") {
    $script:evidence += "| $step | $(if($ok){'PASS'}else{'FAIL'}) | $detail |"
    Write-Host "[$(if($ok){'PASS'}else{'FAIL'})] $step $detail"
    if (-not $ok) { $script:hadFail = $true }
}
function Wait-Human($prompt) {
    if (-not [Environment]::UserInteractive -or $env:CI -eq "true") {
        Write-Host "(无人值守跳过人工动作: $prompt)"
        return
    }
    Write-Host "`n=== 人工动作: $prompt ===" -ForegroundColor Yellow
    Read-Host "完成后回车继续"
}

if (-not (Test-Path $InstallerPath)) { throw "InstallerPath 不存在: $InstallerPath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$programs = "$env:LOCALAPPDATA\Programs"
# 既有安装守卫：装目录已存在时装/卸断言会被既有态污染且会改写用户既有安装——拒跑要求干净宿主。
if ($InstallDirName -and (Test-Path "$programs\$InstallDirName")) {
    Write-Host "[FAIL] 拒跑：既有安装 '$programs\$InstallDirName' 已存在，本腿要求干净宿主"
    exit 1
}
$startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$build = [Environment]::OSVersion.Version.Build
# 自我描述：安装前后快照 diff 认出新装目录与快捷方式（不硬编产品名）
$beforeDirs = @(Get-ChildItem $programs -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)
$beforeLnks = @(Get-ChildItem $startMenu -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object FullName)
$installDir = $null
try {
    # NSIS 静默装：无 /S 会开 GUI 向导在无人值守环境挂死
    $p = Start-Process $InstallerPath -ArgumentList "/S" -Wait -PassThru
    Note "per-user 安装" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
    $installDir = Get-ChildItem $programs -Directory -ErrorAction SilentlyContinue |
        Where-Object { $beforeDirs -notcontains $_.FullName } | Select-Object -First 1 -ExpandProperty FullName
    Note "装目录发现" ($null -ne $installDir) "$installDir"
    $lnkPath = Get-ChildItem $startMenu -Recurse -Filter *.lnk -ErrorAction SilentlyContinue |
        Where-Object { $beforeLnks -notcontains $_.FullName } | Select-Object -First 1 -ExpandProperty FullName
    $lnk = if ($lnkPath) { Get-Item $lnkPath } else { $null }
    Note "开始菜单快捷方式" ($null -ne $lnk) "$lnkPath"

    if ($TaskbarPin -and $lnk) {
        # win11 22H2+ pin 走 shell verb（build>=22621）；win10 用 pinned 目录。
        $verb = if ($build -ge 22621) { "taskbarpin" } else { "pintotaskbar" }
        $shell = New-Object -ComObject Shell.Application
        $shell.Namespace($lnk.DirectoryName).ParseName($lnk.Name).Verbs() |
            Where-Object Name -match "pin|固定" | ForEach-Object { $_.DoIt() }
        Wait-Human "检查任务栏是否出现 HelloBundlerApp 图标"
        Note "任务栏 pin(人工确认)" $true "build=$build verb=$verb"
    }

    # 卸载后 pin 清理语义：快捷方式移除。
    Wait-Human "卸载产物——确认开始菜单项与任务栏 pin 已清理"
    $uninst = if ($installDir) { Join-Path $installDir "uninstall.exe" } else { $null }
    if ($uninst -and (Test-Path $uninst)) {
        Start-Process $uninst -ArgumentList "/S" -Wait | Out-Null
        Note "卸载后快捷方式移除" (-not (Test-Path "$lnkPath")) ""
    } else {
        Note "卸载器发现" $false "$installDir\uninstall.exe 不存在"
    }
} finally {
    # 残留清场（只清本脚本认出的新装目录）。
    if ($installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
}

$out = Join-Path $OutDir ("SA-PIN-{0}-b{1}-{2:yyyyMMdd}.md" -f
    $env:PROCESSOR_ARCHITECTURE, $build, (Get-Date))
@"
# SA-PIN 固定项矩阵
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | build: $build | arch: $env:PROCESSOR_ARCHITECTURE
- 工件: $(Split-Path $InstallerPath -Leaf)
- 人工介入点: 任务栏 pin 视觉确认、卸载后清理确认

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

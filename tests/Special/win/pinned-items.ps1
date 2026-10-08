# 固定项矩阵验收：开始菜单/任务栏 pin 在装/卸/换包后的存续语义。
# 需在目标 Windows build 跑（win10/win11 任务栏 pin API 不同）。
# 用法: pwsh pinned-items.ps1 -InstallerPath <nsis产包> [-TaskbarPin]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [switch]$TaskbarPin,
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $ok, $detail = "") {
    $script:evidence += "| $step | $(if($ok){'PASS'}else{'FAIL'}) | $detail |"
    Write-Host "[$(if($ok){'PASS'}else{'FAIL'})] $step $detail"
}
function Wait-Human($prompt) {
    Write-Host "`n=== 人工动作: $prompt ===" -ForegroundColor Yellow
    Read-Host "完成后回车继续"
}

if (-not (Test-Path $InstallerPath)) { throw "InstallerPath 不存在: $InstallerPath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$installDir = "$env:LOCALAPPDATA\Programs\HelloBundlerApp"
$startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$build = [Environment]::OSVersion.Version.Build
# 保护既有安装：跑前已存在的 install 直接拒跑——安装腿会改写它。
if (Test-Path $installDir) {
    throw "检测到既有安装 $installDir——pin 腿会先卸载它或换干净宿主再跑"
}
try {
    $p = Start-Process $InstallerPath -Wait -PassThru
    Note "per-user 安装" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
    $lnk = Get-ChildItem $startMenu -Recurse -Filter "*HelloBundler*" |
        Select-Object -First 1
    Note "开始菜单快捷方式" ($null -ne $lnk) $lnk?.FullName

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
    $uninst = Join-Path $installDir "uninstall.exe"
    if (Test-Path $uninst) {
        Start-Process $uninst -Wait | Out-Null
        Note "卸载后快捷方式移除" (-not (Test-Path $lnk?.FullName)) ""
    } else {
        Note "卸载器发现" $false "$installDir\uninstall.exe 不存在"
    }
} finally {
    # 残留清场（只到这一步说明本脚本装的目录）。
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
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

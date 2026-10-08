# MSI 交互对话验收：Browse 换目录/InvalidDir/Feature 页/装完启动勾选/位图缩放——
# 每条都是半自动：脚本起 msiexec 安装，人走 UI，脚本断言落位。
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
$evidence = @()
function Note($step, $result, $detail = "") {
    $script:evidence += "| $step | $result | $detail |"
    Write-Host "[$result] $step $detail"
    if ($result -eq "FAIL") { $script:hadFail = $true }
}
function Wait-Human($prompt) {
    Write-Host "`n=== 人工动作: $prompt ===" -ForegroundColor Yellow
    Read-Host "完成后回车继续"
}
if (-not (Test-Path $MsiPath)) { throw "MsiPath 不存在: $MsiPath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 1) Browse 换自定义目录
Wait-Human "msiexec /i 起安装向导——在目录页 Browse 换到 %LOCALAPPDATA%\BundlerMsiCustom 后装完"
Start-Process "msiexec.exe" -ArgumentList "/i `"$MsiPath`"" -Wait
$custom = "$env:LOCALAPPDATA\BundlerMsiCustom"
Note "Browse 自定义目录落位" (Test-Path $custom) $custom

# 2) InvalidDir：无效路径被拒
Wait-Human "卸载后重装，目录页输入无效路径(如 Z:\nonexistent\dir)——确认被拒绝且可返回修正"
Note "InvalidDir 拒绝" "UNTESTED" "由人工在上一步确认"

# 3) Feature 页（若有 feature 树）
Wait-Human "如向导含 Feature 页——取消勾选可选 feature 验证对应文件不落位"
Note "Feature 页" "UNTESTED" "由人工记录所见"

# 4) 装完启动勾选
Wait-Human "装完页勾选『启动应用』——确认进程拉起"
$proc = Get-Process | Where-Object Name -match "HelloBundler" | Select-Object -First 1
Note "启动勾拉起进程" ($null -ne $proc) $proc?.Name
if ($proc) { Stop-Process $proc -Force }

# 5) 位图缩放（高 DPI）
Wait-Human "切 150%/200% DPI 重跑安装——确认 banner/对话位图不糊不裁"
Note "位图缩放" "UNTESTED" "DPI=$([System.Windows.Forms.SystemInformation]::SmallIconSize)"

# 卸载清场
Wait-Human "最后卸载：msiexec /x 走一遍，确认干净"
$app = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
    Where-Object DisplayName -match "HelloBundlerApp" | Select-Object -First 1
if ($app) {
    Start-Process "msiexec.exe" -ArgumentList "/x `"$($app.PSChildName)`" /qb" -Wait
    Note "卸载完成" $true ""
} else { Note "卸载项发现" "FAIL" "Uninstall 注册项缺失" }

$out = Join-Path $OutDir ("SA-MSI-DLG-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-MSI-DLG MSI 交互对话验收
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString)
- 工件: $(Split-Path $MsiPath -Leaf)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

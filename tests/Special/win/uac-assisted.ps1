# UAC 伴随验收：per-machine NSIS/MSI 真装真卸走真 UAC 弹窗，断言 ProgramFiles 落位、
# HKLM 卸载项、非管理员工具目录拒写。半自动——脚本暂停等人工点 UAC。
# 必须从非提权终端跑：安装器内部的 UAC 弹窗才是验收对象；脚本自身若已提权，
# UAC 不弹、ACL 探针也会以管理员身份直接写入成功。
# 用法: pwsh uac-assisted.ps1 -InstallerPath <产包路径> [-Format nsis|msi] [-OutDir <证据目录>]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [ValidateSet("nsis", "msi")][string]$Format = "nsis",
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

if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole("Administrator")) {
    throw "请从非提权（标准用户）终端运行——提权终端下 UAC 不弹、ACL 探针失效。"
}
if (-not (Test-Path $InstallerPath)) { throw "InstallerPath 不存在: $InstallerPath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$work = Join-Path $env:TEMP ("bundler-uac-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    Note "工件" "PASS" "$InstallerPath sha256=$((Get-FileHash $InstallerPath).SHA256.Substring(0,16))"

    # 1) per-machine 真装：installer 内部以 UAC 提权（nsis 需非提权发起以走真弹窗）。
    Wait-Human "将以标准用户方式运行安装器——UAC 弹窗出现时请批准"
    $install = Start-Process $InstallerPath -Wait -PassThru
    Note "安装器退出码" $([string]($install.ExitCode -eq 0)) "rc=$($install.ExitCode)"

    $installDir = "$env:ProgramFiles\HelloBundlerApp"
    Note "ProgramFiles 落位" $([string](Test-Path $installDir)) $installDir
    $reg = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    $entry = Get-ChildItem $reg | Where-Object {
        (Get-ItemProperty $_.PSPath).DisplayName -match "HelloBundlerApp" }
    Note "HKLM 卸载注册项" $([string]($null -ne $entry)) ($entry?.PSChildName ?? "")

    # 2) 非提权写工具目录应被拒（ACL 证据）。
    $probe = Join-Path $installDir "__probe.tmp"
    $denied = $false
    try { [IO.File]::WriteAllText($probe, "x") } catch { $denied = $true }
    Note "非提权写被拒(ACL)" $([string]$denied) "WriteAllText 抛 UnauthorizedAccess"
    if (Test-Path $probe) { Remove-Item $probe -Force }

    # 3) 卸载走真 UAC。
    Wait-Human "将运行卸载器——UAC 弹窗请批准；界面若要求确认请按默认"
    $uninst = if ($Format -eq "msi") {
        $app = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" |
            Where-Object DisplayName -match "HelloBundlerApp"
        Start-Process "msiexec.exe" -ArgumentList "/x `"$($app.PSChildName)`" /qb" -Wait -PassThru
    } else {
        $uninstPath = Join-Path $installDir "uninstall.exe"
        Start-Process $uninstPath -Wait -PassThru
    }
    Note "卸载器退出码" $([string]($uninst.ExitCode -eq 0)) "rc=$($uninst.ExitCode)"
    Note "目录已清" $([string](-not (Test-Path $installDir))) $installDir
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$out = Join-Path $OutDir ("SA-UAC-{0}-{1:yyyyMMdd}.md" -f $env:PROCESSOR_ARCHITECTURE, (Get-Date))
@"
# SA-UAC UAC 伴随安装验收
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC
- 宿主: $([Environment]::OSVersion.VersionString) $env:PROCESSOR_ARCHITECTURE
- 格式: $Format | 工件: $(Split-Path $InstallerPath -Leaf)
- 人工介入点: UAC 弹窗批准（安装+卸载各一）

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }

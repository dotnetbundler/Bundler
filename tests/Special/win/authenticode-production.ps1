# SA-P-01 Authenticode 生产签名验收：真证书 signtool 签名+RFC3161 时间戳+SmartScreen 观察。
# 需: Windows+生产证书（env AUTHENTICODE_PFX / AUTHENTICODE_THUMBPRINT 二选一）+ 公网(SmartScreen 观察)。
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
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
    if (-not [Environment]::UserInteractive -or $env:CI -eq "true") {
        Write-Host "(无人值守跳过人工动作: $prompt)"
        return
    }
    Write-Host "`n=== 人工动作: $prompt ===" -ForegroundColor Yellow
    Read-Host "完成后回车继续"
}
if (-not (Test-Path $InstallerPath)) { throw "InstallerPath 不存在: $InstallerPath" }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse `
    -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object FullName -match "x64" | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $signtool) { throw "signtool 未发现（需 Windows SDK）" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$work = Join-Path $env:TEMP ("bundler-sign-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$signed = Join-Path $work (Split-Path $InstallerPath -Leaf)
try {
    Copy-Item $InstallerPath $signed
    # 证书先经 PowerShell 入库（密码走 SecureString 参数不进进程命令行），
    # signtool 一律按 thumbprint 从 store 签——密码永不上进程列表。
    $thumbprint = $env:AUTHENTICODE_THUMBPRINT
    $importedCert = $null
    if (-not $thumbprint -and $env:AUTHENTICODE_PFX) {
        $pw = if ($env:AUTHENTICODE_PFX_PASSWORD) {
            ConvertTo-SecureString -String $env:AUTHENTICODE_PFX_PASSWORD -AsPlainText -Force
        } else {
            Read-Host "PFX 密码" -AsSecureString
        }
        $importedCert = Import-PfxCertificate -FilePath $env:AUTHENTICODE_PFX `
            -CertStoreLocation Cert:\CurrentUser\My -Password $pw  # 不加 -Exportable
        $thumbprint = $importedCert.Thumbprint
    }
    if (-not $thumbprint) {
        throw "凭证缺失: 设 AUTHENTICODE_THUMBPRINT（推荐）或 AUTHENTICODE_PFX(+PASSWORD)"
    }
    try {
        $p = Start-Process $signtool -ArgumentList @(
            "sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256",
            "/sha1", $thumbprint, $signed) -Wait -PassThru -NoNewWindow
        Note "生产签名" ($p.ExitCode -eq 0) "rc=$($p.ExitCode)"
    } finally {
        if ($importedCert) {
            Remove-Item "Cert:\CurrentUser\My\$($importedCert.Thumbprint)" -Force `
                -ErrorAction SilentlyContinue
        }
    }
    $sig = Get-AuthenticodeSignature $signed
    Note "签名有效" ($sig.Status -eq "Valid") "status=$($sig.Status) subject=$($sig.SignerCertificate.Subject)"
    Note "RFC3161 时间戳" ($null -ne $sig.TimeStamperCertificate) "ts=$($sig.TimeStamperCertificate?.Subject)"

    Wait-Human "把签名包拷到一台真实 Windows 机器双击运行——记录 SmartScreen 是否拦截/警告文本"
    Note "SmartScreen 观察" "UNTESTED" "人工记录: 拦截?警告文案?"
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$out = Join-Path $OutDir ("SA-P-01-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-P-01 Authenticode 生产签名
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString)
- 工件: $(Split-Path $InstallerPath -Leaf) sha256=$((Get-FileHash $InstallerPath).SHA256.Substring(0,16))
- 人工介入点: SmartScreen 观察

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }
